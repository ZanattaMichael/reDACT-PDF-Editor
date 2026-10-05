using PdfEditor.Core.Pdf;

namespace PdfEditor.Core;

/// <summary>
/// Expands redaction regions to hide how long the removed text was. A black box whose width tracks
/// the redacted text leaks its length — enough, with the surrounding context, to often reconstruct a
/// short redaction. Widening the box breaks that side channel; because the expanded box is used for
/// both the content removal and the drawn rectangle, no live content is ever left hidden under it.
/// </summary>
public static class RedactionBox
{
    /// <summary>How much to widen a redaction box to obscure the removed text's length.</summary>
    public enum LengthObfuscation
    {
        /// <summary>Box matches the selection exactly (leaks length).</summary>
        None,
        /// <summary>Round the width up to a coarse grid, so only a bucketed length shows.</summary>
        Quantize,
        /// <summary>Extend the box across the full page width, so no length shows at all.</summary>
        FullLine,
    }

    // Grid the Quantize mode rounds widths up to (1 inch), and the margin FullLine leaves at the edges.
    private const float Grid = 72f;
    private const float Margin = 18f;

    // The least a quantized box may grow. Rounding a raw width up to the grid can widen it by
    // almost nothing — a 71pt run lands on 72 — leaving a box that still traces the text and
    // reports its length, which is the mode's whole reason to exist.
    private const float MinWiden = Grid / 2f;

    /// <summary>
    /// Prepares the boxes for a given privacy intensity (0–3): 0 leaves them exact; 1 merges
    /// adjacent boxes on a line (so two redactions split by a space read as one, hiding the word
    /// count and boundaries); 2 also rounds each width up to a grid; 3 extends them across the full
    /// line. Merging and widening both drive the removal too, so nothing live hides under a box.
    /// </summary>
    public static List<RectRegion> Prepare(byte[] pdf, IReadOnlyList<RectRegion> regions,
        int intensity, string? password = null)
    {
        intensity = Math.Clamp(intensity, 0, 3);
        var prepared = intensity >= 1 ? MergeAdjacent(regions) : regions.ToList();
        var mode = intensity switch
        {
            2 => LengthObfuscation.Quantize,
            3 => LengthObfuscation.FullLine,
            _ => LengthObfuscation.None,
        };
        return Expand(pdf, prepared, mode, password);
    }

    /// <summary>
    /// Merges redaction boxes that sit on the same line of the same page with only a small gap
    /// between them into a single box spanning both (and the whitespace between). The gap threshold
    /// is tied to the box height, so ordinary inter-word spaces bridge but boxes far apart — which
    /// could have un-redacted content between them — do not.
    /// </summary>
    public static List<RectRegion> MergeAdjacent(IReadOnlyList<RectRegion> regions)
    {
        var result = new List<RectRegion>();
        foreach (var pageGroup in regions.GroupBy(r => r.Page))
            result.AddRange(MergePage(pageGroup.ToList()));
        return result;
    }

    /// <summary>Repeatedly folds mergeable pairs on one page together until none remain.</summary>
    private static List<RectRegion> MergePage(List<RectRegion> boxes)
    {
        while (TryMergeOnePair(boxes)) { /* keep folding */ }
        return boxes;
    }

    /// <summary>Merges the first mergeable pair found (in place) and reports whether one was merged.</summary>
    private static bool TryMergeOnePair(List<RectRegion> boxes)
    {
        for (int i = 0; i < boxes.Count; i++)
        {
            for (int j = i + 1; j < boxes.Count; j++)
            {
                if (!CanMerge(boxes[i], boxes[j])) continue;
                boxes[i] = Union(boxes[i], boxes[j]);
                boxes.RemoveAt(j);
                return true;
            }
        }
        return false;
    }

    private static bool CanMerge(RectRegion a, RectRegion b)
    {
        float aTop = a.Y + a.Height, bTop = b.Y + b.Height;
        float verticalOverlap = Math.Min(aTop, bTop) - Math.Max(a.Y, b.Y);
        if (verticalOverlap <= 0.5f * Math.Min(a.Height, b.Height)) return false; // not the same line
        float aRight = a.X + a.Width, bRight = b.X + b.Width;
        float gap = a.X <= b.X ? b.X - aRight : a.X - bRight; // negative when they already overlap
        return gap <= 1.5f * Math.Max(a.Height, b.Height);
    }

    private static RectRegion Union(RectRegion a, RectRegion b)
    {
        float x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
        float right = Math.Max(a.X + a.Width, b.X + b.Width);
        float top = Math.Max(a.Y + a.Height, b.Y + b.Height);
        return new RectRegion(a.Page, x, y, right - x, top - y);
    }

    /// <summary>
    /// Returns copies of <paramref name="regions"/> widened per <paramref name="mode"/>. Regions on
    /// a page that does not exist are passed through unchanged (the redactor validates those).
    /// </summary>
    public static List<RectRegion> Expand(byte[] pdf, IReadOnlyList<RectRegion> regions,
        LengthObfuscation mode, string? password = null)
    {
        if (mode == LengthObfuscation.None) return regions.ToList();

        var doc = PdfIo.OpenReadOnly(pdf, password);
        int total = doc.PageCount;
        var expanded = new List<RectRegion>(regions.Count);
        foreach (var r in regions)
        {
            if (r.Page < 1 || r.Page > total) { expanded.Add(r); continue; }
            var crop = doc.GetPage(r.Page).CropBox;
            expanded.Add(mode == LengthObfuscation.FullLine ? FullLine(r, crop) : Quantize(r, crop));
        }
        return expanded;
    }

    /// <summary>The box keeps its vertical band but spans the printable page width.</summary>
    private static RectRegion FullLine(RectRegion r, PdfRect crop)
    {
        float x = crop.Left + Margin;
        float width = Math.Max(1f, crop.Width - 2 * Margin);
        return new RectRegion(r.Page, x, r.Y, width, r.Height);
    }

    /// <summary>
    /// Rounds the box width up to the next grid step, so only a bucketed length shows. The rounded
    /// box always still covers the original marked area and stays within the page: it keeps the
    /// original left edge, and only slides left when a box near the right margin would otherwise run
    /// off the page (which previously clamped the width *below* the original, leaving text exposed).
    /// </summary>
    private static RectRegion Quantize(RectRegion r, PdfRect crop)
    {
        float left = crop.Left, right = crop.Right;
        float pageWidth = Math.Max(r.Width, right - left);
        // Quantizing r.Width + MinWiden rather than r.Width keeps the bucketing — widths within one
        // band still collapse onto a single value — while guaranteeing the box clears the text by at
        // least half a grid step, so it can never come back hugging the glyphs it covers.
        float width = Math.Clamp(MathF.Ceiling((r.Width + MinWiden) / Grid) * Grid, r.Width, pageWidth);
        // min(r.X, right - width) slides the box left just enough to fit; clamping to [left, r.X]
        // keeps it on the page while guaranteeing the original stays fully covered.
        float x = Math.Clamp(Math.Min(r.X, right - width), left, r.X);
        return new RectRegion(r.Page, x, r.Y, width, r.Height);
    }
}
