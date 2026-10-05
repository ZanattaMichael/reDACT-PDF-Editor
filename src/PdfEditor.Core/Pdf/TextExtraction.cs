using System.Text;
using System.Text.RegularExpressions;

namespace PdfEditor.Core.Pdf;

/// <summary>
/// Where a piece of text sits on its line: the direction it runs in, its distance from the
/// origin across that direction (which line it is on) and along it (where on the line).
/// <para>
/// Lines are identified by the perpendicular distance truncated to a whole point, and words are
/// separated where the gap between two pieces exceeds half a space — the conventions PDF text
/// extractors have long used to rebuild reading order from positioned glyphs.
/// </para>
/// </summary>
internal sealed class TextChunkLocation
{
    /// <summary>How far a zero-length mark (a combining accent) may sit off its base's line.</summary>
    private const float DiacriticalMarksAllowedVerticalDeviation = 2;

    public TextChunkLocation(Point2 start, Point2 end, double charSpaceWidth)
    {
        Start = start;
        End = end;
        CharSpaceWidth = charSpaceWidth;
        double ox = end.X - start.X, oy = end.Y - start.Y;
        double length = Math.Sqrt(ox * ox + oy * oy);
        if (length == 0)
        {
            ox = 1;
            oy = 0;
        }
        else
        {
            ox /= length;
            oy /= length;
        }
        OrientationX = ox;
        OrientationY = oy;
        OrientationMagnitude = (int)(Math.Atan2(oy, ox) * 1000);
        // The z component of (start - origin) x orientation: the signed distance of the line from the origin.
        DistPerpendicular = (int)(start.X * oy - start.Y * ox);
        DistParallelStart = (float)(ox * start.X + oy * start.Y);
        DistParallelEnd = (float)(ox * end.X + oy * end.Y);
    }

    public Point2 Start { get; }
    public Point2 End { get; }
    public double CharSpaceWidth { get; }
    public double OrientationX { get; }
    public double OrientationY { get; }
    public int OrientationMagnitude { get; }
    public int DistPerpendicular { get; }
    public float DistParallelStart { get; }
    public float DistParallelEnd { get; }

    public bool IsZeroLength => Start == End;

    public bool SameLine(TextChunkLocation other)
    {
        if (OrientationMagnitude != other.OrientationMagnitude) return false;
        int diff = DistPerpendicular - other.DistPerpendicular;
        if (diff == 0) return true;
        return Math.Abs(diff) <= DiacriticalMarksAllowedVerticalDeviation && (IsZeroLength || other.IsZeroLength);
    }

    /// <summary>The gap from the end of <paramref name="other"/> to the start of this.</summary>
    public float DistanceFromEndOf(TextChunkLocation other) => DistParallelStart - other.DistParallelEnd;

    public bool IsAtWordBoundary(TextChunkLocation previous)
    {
        // A zero-length chunk is a mark character; a space before it would split it from its base.
        if (IsZeroLength || previous.IsZeroLength) return false;
        float distance = DistanceFromEndOf(previous);
        if (distance < 0)
        {
            distance = previous.DistanceFromEndOf(this);
            if (distance < 0) return false; // overlapping chunks are one word
        }
        return distance > CharSpaceWidth / 2.0;
    }

    /// <summary>Reading order: by direction, then line, then position along the line.</summary>
    public static int Compare(TextChunkLocation a, TextChunkLocation b)
    {
        if (ReferenceEquals(a, b)) return 0;
        int result = a.OrientationMagnitude.CompareTo(b.OrientationMagnitude);
        if (result != 0) return result;
        int perpendicular = a.DistPerpendicular - b.DistPerpendicular;
        if (perpendicular != 0) return perpendicular;
        return a.DistParallelStart.CompareTo(b.DistParallelStart);
    }
}

/// <summary>A run of text and where it is.</summary>
internal sealed record TextChunk(string Text, TextChunkLocation Location);

/// <summary>
/// Rebuilds a page's text in reading order from the positioned runs a content stream draws:
/// runs are sorted into lines, a space is inserted where the gap between two runs on a line is
/// wider than half a space, and a newline between lines.
/// </summary>
internal sealed class LocationTextExtraction : IContentListener
{
    private readonly List<TextChunk> _chunks = new();

    public void OnText(TextRenderInfo info)
    {
        var baseline = info.Baseline;
        if (info.Rise != 0)
        {
            // Super- and subscripts belong to the line they are raised from.
            var shift = Matrix.Translation(0, -info.Rise);
            var s = shift.Transform(baseline.Start.X, baseline.Start.Y);
            var e = shift.Transform(baseline.End.X, baseline.End.Y);
            baseline = new LineSegment(new Point2(s.X, s.Y), new Point2(e.X, e.Y));
        }
        _chunks.Add(new TextChunk(info.Text, new TextChunkLocation(baseline.Start, baseline.End, info.SingleSpaceWidth)));
    }

    public string GetText()
    {
        var sorted = SortWithMarks(_chunks);
        var sb = new StringBuilder();
        TextChunk? last = null;
        foreach (var chunk in sorted)
        {
            if (last != null)
            {
                if (chunk.Location.SameLine(last.Location))
                {
                    if (chunk.Location.IsAtWordBoundary(last.Location) && !chunk.Text.StartsWith(' ') && !last.Text.EndsWith(' '))
                        sb.Append(' ');
                }
                else
                {
                    sb.Append('\n');
                }
            }
            sb.Append(chunk.Text);
            last = chunk;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Sorts chunks into reading order, keeping zero-length chunks — combining marks, and empty
    /// strings — next to the chunk they sit inside rather than letting the sort scatter them. A
    /// zero-length chunk that sits inside no other stays where the sort puts it; it still matters,
    /// because it separates its neighbours ("Hello" "" "World" reads as one word).
    /// </summary>
    private static List<TextChunk> SortWithMarks(List<TextChunk> chunks)
    {
        var before = new Dictionary<TextChunk, List<TextChunk>>(ReferenceEqualityComparer.Instance);
        var after = new Dictionary<TextChunk, List<TextChunk>>(ReferenceEqualityComparer.Instance);
        var toSort = new List<TextChunk>();
        for (int m = 0; m < chunks.Count; m++)
        {
            var mark = chunks[m];
            if (!mark.Location.IsZeroLength)
            {
                toSort.Add(mark);
                continue;
            }
            bool attached = false;
            for (int b = 0; b < chunks.Count && !attached; b++)
            {
                if (b == m || chunks[b].Location.IsZeroLength || !ContainsMark(chunks[b].Location, mark.Location)) continue;
                Attach(m < b ? before : after, chunks[b], mark);
                attached = true;
            }
            if (!attached) toSort.Add(mark);
        }
        var sorted = toSort.OrderBy(c => c.Location, Comparer<TextChunkLocation>.Create(TextChunkLocation.Compare)).ToList();
        if (before.Count == 0 && after.Count == 0) return sorted;
        var result = new List<TextChunk>(chunks.Count);
        foreach (var chunk in sorted)
        {
            if (before.TryGetValue(chunk, out var b)) result.AddRange(b);
            result.Add(chunk);
            if (after.TryGetValue(chunk, out var a)) result.AddRange(a);
        }
        return result;
    }

    private static void Attach(Dictionary<TextChunk, List<TextChunk>> marks, TextChunk to, TextChunk mark)
    {
        if (!marks.TryGetValue(to, out var list)) marks[to] = list = new List<TextChunk>();
        list.Add(mark);
    }

    private static bool ContainsMark(TextChunkLocation baseLocation, TextChunkLocation mark) =>
        baseLocation.Start.X <= mark.Start.X && baseLocation.End.X >= mark.End.X
        && Math.Abs(baseLocation.DistPerpendicular - mark.DistPerpendicular) <= 2;

    /// <summary>The text of a page, in reading order.</summary>
    public static string ExtractPage(PdfPage page)
    {
        var listener = new LocationTextExtraction();
        new ContentProcessor(listener).ProcessPage(page);
        return listener.GetText();
    }
}

/// <summary>A match of a text search: the matched text and one rectangle per line it spans.</summary>
internal sealed record TextLocation(string Text, PdfRect Rect);

/// <summary>
/// Finds a regular expression in a page's text and reports where each match is drawn. The page
/// text is rebuilt glyph by glyph in reading order (as <see cref="LocationTextExtraction"/> does),
/// keeping a map from every character back to the glyph that drew it, so a match maps to the
/// union of its glyphs' boxes — one rectangle per line.
/// </summary>
internal sealed class RegexTextLocator : IContentListener
{
    private readonly Regex _pattern;
    private readonly List<(GlyphRenderInfo Glyph, TextChunkLocation Location, PdfRect Box)> _glyphs = new();

    public RegexTextLocator(Regex pattern) => _pattern = pattern;

    public void OnText(TextRenderInfo info)
    {
        double space = info.SingleSpaceWidth;
        foreach (var glyph in info.Glyphs)
        {
            var baseline = glyph.Baseline;
            _glyphs.Add((glyph, new TextChunkLocation(baseline.Start, baseline.End, space), glyph.BoundingBox));
        }
    }

    public List<TextLocation> GetLocations()
    {
        var sorted = _glyphs.OrderBy(g => g.Location, Comparer<TextChunkLocation>.Create(TextChunkLocation.Compare)).ToList();

        // Build the text with a map from character index to glyph index.
        var sb = new StringBuilder();
        var indexMap = new Dictionary<int, int>();
        for (int i = 0; i < sorted.Count; i++)
        {
            var (glyph, location, _) = sorted[i];
            if (i > 0)
            {
                var previous = sorted[i - 1];
                if (location.SameLine(previous.Location))
                {
                    if (location.IsAtWordBoundary(previous.Location) && !glyph.Text.StartsWith(' ') && !glyph.Text.EndsWith(' '))
                        sb.Append(' ');
                }
                else
                {
                    sb.Append('\n');
                }
            }
            foreach (char _ in glyph.Text)
            {
                indexMap[sb.Length] = i;
                sb.Append(_);
            }
        }

        string text = sb.ToString();
        var results = new List<TextLocation>();
        foreach (Match match in _pattern.Matches(text))
        {
            if (match.Length == 0) continue;
            int? start = StartIndex(indexMap, match.Index, text.Length);
            int? end = EndIndex(indexMap, match.Index + match.Length - 1);
            if (start is not int s || end is not int e || s > e) continue;
            foreach (var rect in LineRectangles(sorted, s, e))
                results.Add(new TextLocation(match.Value, rect));
        }
        // Stable, position-ordered output (bottom to top, then left to right), duplicates removed —
        // two glyphs drawn on top of each other (a ligature, a fake-bold overprint) give one hit.
        return results
            .OrderBy(r => r.Rect.Y).ThenBy(r => r.Rect.X)
            .Distinct()
            .ToList();
    }

    private static int? StartIndex(Dictionary<int, int> map, int index, int length)
    {
        while (!map.ContainsKey(index) && index < length) index++;
        return map.TryGetValue(index, out int i) ? i : null;
    }

    private static int? EndIndex(Dictionary<int, int> map, int index)
    {
        while (!map.ContainsKey(index) && index >= 0) index--;
        return map.TryGetValue(index, out int i) ? i : null;
    }

    private static IEnumerable<PdfRect> LineRectangles(
        List<(GlyphRenderInfo Glyph, TextChunkLocation Location, PdfRect Box)> glyphs, int from, int to)
    {
        int prev = from;
        int curr = from;
        while (curr <= to)
        {
            while (curr <= to && glyphs[curr].Location.SameLine(glyphs[prev].Location)) curr++;
            var rect = glyphs[prev].Box;
            for (int i = prev + 1; i < curr; i++) rect = rect.Union(glyphs[i].Box);
            yield return rect;
            prev = curr;
        }
    }
}
