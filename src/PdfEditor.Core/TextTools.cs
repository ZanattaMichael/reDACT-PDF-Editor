using System.Text;
using System.Text.RegularExpressions;
using PdfEditor.Core.Pdf;
using PdfEditor.Core.Pdf.Fonts;

namespace PdfEditor.Core;

/// <summary>
/// Text discovery and in-place text editing. Editing works by truly removing the
/// original text operators from the content stream (via <see cref="ContentStreamEditor"/>)
/// and stamping replacement text into the same region.
/// </summary>
public static class TextTools
{
    /// <summary>Returns the text inside a region plus its dominant font size and style.</summary>
    public static RegionText GetTextInRegion(byte[] pdf, RectRegion region, string? password = null)
    {
        var doc = PdfIo.OpenReadOnly(pdf, password);
        var rect = new PdfRect(region.X, region.Y, region.Width, region.Height);
        var chunks = CollectChunks(doc, region.Page).Where(c => ContainsCenter(rect, c.BBox)).ToList();
        string dominantFont = chunks
            .Where(c => !string.IsNullOrEmpty(c.FontName))
            .GroupBy(c => c.FontName, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault()?.Key ?? "";
        // Take the size from the run the region is mostly made of, so a single stray large glyph
        // cannot decide the size for a paragraph — and so size and family describe the same run.
        var sizing = chunks.Where(c => string.Equals(c.FontName, dominantFont, StringComparison.Ordinal)).ToList();
        if (sizing.Count == 0) sizing = chunks;
        float size = sizing.Count == 0 ? 12f : MathF.Round(sizing.Max(c => c.FontSize), 2);
        var (family, bold, italic) = DetectFont(dominantFont);
        return new RegionText(AssembleText(chunks), size, family, bold, italic, dominantFont);
    }

    /// <summary>
    /// Replaces the text inside a region: original text operators are removed from the
    /// file and the new text is laid out inside the same rectangle, in the requested font,
    /// size, style, and colour (all optional — omitted values fall back to what was there).
    /// </summary>
    public static EditResult ReplaceTextInRegion(byte[] pdf, RectRegion region, string newText,
        float? fontSize = null, string? fontFamily = null, bool? bold = null, bool? italic = null,
        string? colorHex = null, string? password = null)
    {
        var found = GetTextInRegion(pdf, region, password);
        float size = fontSize ?? found.FontSize;
        // Family and style fall back to the run being replaced, as the summary above promises. They
        // used to default to plain Helvetica instead, so any caller that named a size but no face
        // silently reset bold Times body copy to regular Helvetica (#29).
        string stampFont = ResolveFont(fontFamily ?? found.FontFamily,
            bold ?? found.Bold, italic ?? found.Italic);

        var removed = Redactor.RemoveContent(pdf, new[] { region }, password,
            RemovalKindFor(pdf, region, password));
        // Baseline-anchored (wrap: false) so the replacement lands on the original text's baseline,
        // in-line with the words around it, rather than being laid out top-down in a box and drifting
        // below the line (#96). Move and find & replace already stamp this way.
        var stamped = StampText(removed.Pdf, region, newText, size, password,
            wrap: false, fontName: stampFont, color: ParseColor(colorHex));

        var warnings = new List<string>(removed.Warnings);
        if (DescribeSubstitution(found.SourceFont, stampFont) is { } note) warnings.Add(note);
        return new EditResult(stamped, warnings);
    }

    /// <summary>
    /// Reports, in words, when replacement text will not be set in the font the original was set
    /// in. Only the standard-14 faces can be stamped today, so editing a run in any other font is a
    /// silent change to how the document looks — exactly the kind of quiet substitution a user needs
    /// told about rather than left to notice. Returns null when the original face is reproduced.
    /// </summary>
    /// <remarks>
    /// Reusing the embedded program itself is issue #34; widening the stampable set is #28. Until
    /// then the honest thing is to say what was swapped for what.
    /// </remarks>
    internal static string? DescribeSubstitution(string? sourceFont, string stampFont)
    {
        string original = StripSubsetPrefix(sourceFont);
        if (original.Length == 0) return null;                                   // nothing detected
        if (string.Equals(original, stampFont, StringComparison.OrdinalIgnoreCase)) return null;
        return $"The original font '{original}' cannot be embedded by the editor, so the replacement "
            + $"text was substituted with '{stampFont}'. Spacing and letterforms will differ.";
    }

    /// <summary>Drops the six-letter subset tag PDF writers prepend (e.g. <c>ABCDEF+Calibri</c>).</summary>
    internal static string StripSubsetPrefix(string? fontName)
    {
        string name = (fontName ?? "").Trim();
        return name.Length > 7 && name[6] == '+' ? name[7..] : name;
    }

    /// <summary>
    /// Decides how much an edit is allowed to remove from a region, from what the text in it is.
    /// <para>
    /// Text drawn in rendering mode 3 paints nothing, so if that is what the region holds, the words
    /// the user is looking at are not this text at all — they are pixels in the page image, and this
    /// is the invisible OCR layer a searchable scan carries over them. Removing only the layer would
    /// leave the old words on screen with the replacement stamped across them, so the pixels have to
    /// be erased too.
    /// </para>
    /// <para>
    /// Anything else is ordinary text that really does draw itself: removing it is enough, and the
    /// image under the region is a letterhead or watermark that must survive the edit.
    /// </para>
    /// </summary>
    private static ContentKinds RemovalKindFor(byte[] pdf, RectRegion region, string? password)
    {
        var doc = PdfIo.OpenReadOnly(pdf, password);
        var rect = new PdfRect(region.X, region.Y, region.Width, region.Height);
        var chunks = CollectChunks(doc, region.Page).Where(c => ContainsCenter(rect, c.BBox)).ToList();
        // "All of it", not "any of it": one stray invisible glyph among visible text is not a scan,
        // and erasing the picture behind real text is the more destructive way to be wrong.
        return chunks.Count > 0 && chunks.TrueForAll(c => c.Invisible)
            ? ContentKinds.TextAndPixelsBeneath
            : ContentKinds.TextOnly;
    }

    /// <summary>
    /// Moves the text found in <paramref name="source"/> by (<paramref name="dx"/>,
    /// <paramref name="dy"/>) in PDF user space: the original text is removed and re-stamped at the
    /// shifted position, preserving its detected font, size, and style. A no-op if the region holds
    /// no text.
    /// </summary>
    public static EditResult MoveText(byte[] pdf, RectRegion source, float dx, float dy, string? password = null)
    {
        var found = GetTextInRegion(pdf, source, password);
        if (string.IsNullOrWhiteSpace(found.Text)) return EditResult.Of(pdf);

        var removed = Redactor.RemoveContent(pdf, new[] { source }, password, ContentKinds.TextOnly);
        var dest = new RectRegion(source.Page, source.X + dx, source.Y + dy, source.Width, source.Height);
        var stamped = StampText(removed.Pdf, dest, found.Text, found.FontSize, password,
            fontName: ResolveFont(found.FontFamily, found.Bold, found.Italic), wrap: false);
        return new EditResult(stamped, removed.Warnings);
    }

    /// <summary>
    /// Adds new text on top of the page inside <paramref name="region"/> (wrapped to its width),
    /// without touching any existing content. Used by the "add text anywhere" tool.
    /// </summary>
    public static EditResult AddText(byte[] pdf, RectRegion region, string text, float fontSize,
        string? fontFamily = null, bool bold = false, bool italic = false,
        string? colorHex = null, string? password = null)
    {
        // Lay the text out from the region's top-left to the page edge rather than confining it to
        // the box. A click places a fixed 240x26 default box and defaults the size to its height, so
        // any caption that does not fit used to be clipped and *lost* — "STAMPED CAPTION" became
        // "STAMPED CAPTIO" (#86 family; the same clipping ReplaceTextInRegion fixed in #29). Adding
        // text must never silently drop characters; the trade is that a caption longer than the box
        // extends past its right edge instead of wrapping inside it.
        var stamped = StampText(pdf, region, text, fontSize, password,
            fontName: ResolveFont(fontFamily, bold, italic), color: ParseColor(colorHex),
            confineToRegion: false);
        return EditResult.Of(stamped);
    }

    /// <summary>Maps a family name (helvetica/times/courier) + style to a standard-14 PDF font.</summary>
    internal static string ResolveFont(string? family, bool bold, bool italic)
    {
        switch ((family ?? "helvetica").Trim().ToLowerInvariant())
        {
            case "times":
            case "serif":
                return bold && italic ? StandardFonts.TimesBoldItalic
                    : bold ? StandardFonts.TimesBold
                    : italic ? StandardFonts.TimesItalic
                    : StandardFonts.TimesRoman;
            case "courier":
            case "mono":
            case "monospace":
                return bold && italic ? StandardFonts.CourierBoldOblique
                    : bold ? StandardFonts.CourierBold
                    : italic ? StandardFonts.CourierOblique
                    : StandardFonts.Courier;
            default: // helvetica / sans-serif
                return bold && italic ? StandardFonts.HelveticaBoldOblique
                    : bold ? StandardFonts.HelveticaBold
                    : italic ? StandardFonts.HelveticaOblique
                    : StandardFonts.Helvetica;
        }
    }

    /// <summary>Best-effort read of a font's PostScript name into family + bold/italic flags.</summary>
    internal static (string Family, bool Bold, bool Italic) DetectFont(string? postScriptName)
    {
        string n = (postScriptName ?? "").ToLowerInvariant();
        string family =
            n.Contains("times") || n.Contains("serif") || n.Contains("georgia") || n.Contains("roman") || n.Contains("minion") ? "times"
            : n.Contains("courier") || n.Contains("mono") || n.Contains("consol") ? "courier"
            : "helvetica";
        bool bold = n.Contains("bold") || n.Contains("black") || n.Contains("heavy") || n.Contains("semibold");
        bool italic = n.Contains("italic") || n.Contains("oblique");
        return (family, bold, italic);
    }

    internal static PdfColor? ParseColor(string? hex) => PdfColor.FromHex(hex);

    /// <summary>
    /// Finds every occurrence of a phrase across the document, honouring the match mode and case
    /// sensitivity in <paramref name="options"/> (default: case-insensitive, anywhere in a word).
    /// </summary>
    public static IReadOnlyList<TextMatch> FindText(byte[] pdf, string phrase, string? password = null,
        SearchOptions? options = null)
    {
        if (string.IsNullOrEmpty(phrase)) return Array.Empty<TextMatch>();
        var pattern = new Regex(BuildSearchPattern(phrase, options ?? new SearchOptions()),
            RegexOptions.None, TimeSpan.FromSeconds(5));
        var doc = PdfIo.OpenReadOnly(pdf, password);
        var matches = new List<TextMatch>();
        for (int p = 1; p <= doc.PageCount; p++)
        {
            var locator = new RegexTextLocator(pattern);
            var page = doc.GetPage(p);
            PdfIo.Guarded($"searching page {p}", () =>
            {
                PdfStructureGuard.EnsureFormXObjectsTerminate(page);
                new ContentProcessor(locator).ProcessPage(page);
            });
            foreach (var location in locator.GetLocations())
            {
                var r = location.Rect;
                matches.Add(new TextMatch(p, location.Text, r.X, r.Y, r.Width, r.Height));
            }
        }
        return matches;
    }

    /// <summary>Builds the regex for a search: escapes the phrase, applies the match mode and case flag.</summary>
    internal static string BuildSearchPattern(string phrase, SearchOptions options)
    {
        string core = System.Text.RegularExpressions.Regex.Escape(phrase);
        // \b is a word boundary: "starts with" anchors the left, "ends with" the right, "whole word" both.
        string pattern = options.Mode switch
        {
            TextMatchMode.StartsWith => $@"\b{core}",
            TextMatchMode.EndsWith => $@"{core}\b",
            TextMatchMode.WholeWord => $@"\b{core}\b",
            _ => core, // Contains (anywhere, including within a word)
        };
        return options.CaseSensitive ? pattern : "(?i)" + pattern;
    }

    /// <summary>Replaces every occurrence of a phrase document-wide. Returns the count replaced.</summary>
    public static (EditResult Result, int Count) ReplaceAll(byte[] pdf, string phrase, string replacement,
        string? password = null)
    {
        // Find & replace stays an exact, case-sensitive substring match (its long-standing behaviour).
        var matches = FindText(pdf, phrase, password, new SearchOptions(TextMatchMode.Contains, CaseSensitive: true));
        if (matches.Count == 0) return (EditResult.Of(pdf), 0);

        var warnings = new List<string>();
        byte[] current = pdf;

        // Measure each match's real type size and face on the original document, before anything is
        // removed. m.Height is the ascender-to-descender box, not the em (it is 0.79–0.93 of it), so
        // stamping at m.Height re-set every replacement 7–21% too small; and passing no font stamped
        // it all in Helvetica whatever the original was (#86). GetTextInRegion recovers both via the
        // same #84 machinery ReplaceTextInRegion uses.
        var styles = matches.Select(m =>
            GetTextInRegion(pdf, new RectRegion(m.Page, m.X, m.Y, m.Width, m.Height), password)).ToList();

        // Inset each match rect slightly so glyphs of adjacent words that merely touch
        // the boundary are not removed with it.
        var regions = matches.Select(m => new RectRegion(m.Page,
            m.X + 0.2f, m.Y + 0.2f, Math.Max(0.1f, m.Width - 0.4f), Math.Max(0.1f, m.Height - 0.4f))).ToList();
        var removed = Redactor.RemoveContent(current, regions, password, ContentKinds.TextOnly);
        warnings.AddRange(removed.Warnings);
        current = removed.Pdf;

        var substitutions = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            var style = styles[i];
            var region = new RectRegion(m.Page, m.X, m.Y, m.Width, m.Height);
            string stampFont = ResolveFont(style.FontFamily, style.Bold, style.Italic);
            current = StampText(current, region, replacement, style.FontSize, password,
                wrap: false, fontName: stampFont);
            // Report a face that could not be reproduced once, not once per occurrence.
            if (DescribeSubstitution(style.SourceFont, stampFont) is { } note) substitutions.Add(note);
        }
        warnings.AddRange(substitutions);
        return (new EditResult(current, warnings), matches.Count);
    }

    /// <summary>
    /// A layout box starting at the region's top-left and running to the page's right and bottom
    /// edges, so replacement text longer than what it replaces has somewhere to go instead of being
    /// clipped away. It can now overlap whatever follows on the line — reflowing the rest of the
    /// paragraph is not something this editor can do — but showing the text in the wrong place beats
    /// dropping characters without a word.
    /// </summary>
    private static PdfRect ToPageEdge(PdfPage page, RectRegion region)
    {
        var size = page.MediaBox;
        float top = region.Y + region.Height;
        return new PdfRect(region.X, size.Bottom,
            Math.Max(1f, size.Right - region.X),
            Math.Max(1f, top - size.Bottom));
    }

    /// <param name="confineToRegion">
    /// Whether the wrapped text must fit inside <paramref name="region"/>. True for "add text",
    /// where the region is a box the user dragged and wrapping to it is the point. False when
    /// replacing existing text, where the region is only the measured bounding box of the words
    /// being replaced: confining the layout to it silently swallowed any replacement longer than
    /// the original, because a line that does not fit the box is dropped
    /// ("HELLO" replaced by "WORLD" came out as "WORL").
    /// </param>
    private static byte[] StampText(byte[] pdf, RectRegion region, string text, float fontSize,
        string? password, bool wrap = true, string? fontName = null,
        PdfColor? color = null, bool confineToRegion = true)
    {
        var doc = PdfIo.Open(pdf, password);
        var page = doc.GetPage(region.Page);
        var font = PdfFont.Standard(fontName ?? StandardFonts.Helvetica);
        // Draw in the page's default user space so the stamped text isn't thrown off by a
        // leftover transform the page content leaves active (e.g. Chrome / Google Docs exports).
        var name = PdfResources.Add(page.GetOrCreateResources(), PdfName.Font, "F", font.Dictionary!);
        var canvas = new ContentBuilder();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (wrap)
        {
            var box = confineToRegion ? new PdfRect(region.X, region.Y, region.Width, region.Height)
                : ToPageEdge(page, region);
            WrappedParagraph(canvas, font, name, fontSize, color, box, lines);
        }
        else
        {
            // Baseline-anchored stamp (find & replace, edit, move). The region's bottom is the
            // descent line of the text that was there, so the baseline sits one descender-depth
            // above it; drawing there keeps the new text in-line with the surrounding words
            // instead of letting a layout's leading push it below the line (#96).
            // Wrapping is deliberately not applied — a replaced run extends along its own line
            // rather than reflowing onto the next one, which would collide with the line below.
            float baseline = region.Y + fontSize * 0.21f; // approximate descender share
            canvas.BeginText().Font(name, fontSize);
            if (color is { } c) canvas.FillColor(c);
            canvas.MoveText(region.X, baseline);
            // Honour explicit line breaks the caller typed, one baseline-spaced line each. The
            // leading is only emitted when there is a second line to place, so the common
            // single-line edit stays a plain Td/Tj with nothing extra in the stream.
            canvas.ShowText(font.Encode(lines[0]));
            if (lines.Length > 1)
            {
                canvas.Leading(fontSize * 1.15f);
                for (int i = 1; i < lines.Length; i++) canvas.NextLineShowText(font.Encode(lines[i]));
            }
            canvas.EndText();
        }
        PdfContentGuard.DrawInDefaultUserSpace(page, canvas.ToArray());
        return PdfIo.Save(doc);
    }

    /// <summary>
    /// Lays <paramref name="paragraphs"/> out top-down inside <paramref name="box"/>, wrapping at
    /// spaces (and inside a word only when the word alone is wider than the box). Lines that do not
    /// fit above the box's bottom are not drawn. The line pitch is 1.05 × the face's normal line
    /// height (1.2 × ascender-to-descender), and the first baseline sits one normal ascent below
    /// the top — the spacing this tool has always used for added text.
    /// </summary>
    private static void WrappedParagraph(ContentBuilder canvas, PdfFont font, PdfName name, float size,
        PdfColor? color, PdfRect box, IEnumerable<string> paragraphs)
    {
        double normal = 1.2 * (font.Ascent - font.Descent) / 1000.0 * size;
        double leading = 1.05 * normal;
        double firstBaseline = box.Top - (1.2 * font.Ascent / 1000.0 * size + (leading - normal) / 2);

        var lines = new List<string>();
        foreach (var paragraph in paragraphs) lines.AddRange(Wrap(font, paragraph, size, box.Width));

        canvas.BeginText().Font(name, size);
        if (color is { } c) canvas.FillColor(c);
        double y = firstBaseline;
        double previousY = 0;
        bool first = true;
        foreach (var line in lines)
        {
            if (y < box.Bottom) break;
            if (first) canvas.MoveText(box.Left, y);
            else canvas.MoveText(0, y - previousY);
            canvas.ShowText(font.Encode(line));
            previousY = y;
            first = false;
            y -= leading;
        }
        canvas.EndText();
    }

    private static IEnumerable<string> Wrap(PdfFont font, string paragraph, float size, float width)
    {
        if (paragraph.Length == 0)
        {
            yield return "";
            yield break;
        }
        var current = new StringBuilder();
        foreach (var word in paragraph.Split(' '))
        {
            string candidate = current.Length == 0 ? word : current + " " + word;
            if (font.MeasureText(candidate, size) <= width || current.Length == 0 && font.MeasureText(word, size) <= width)
            {
                current.Clear().Append(candidate);
                continue;
            }
            if (current.Length > 0)
            {
                yield return current.ToString();
                current.Clear();
            }
            foreach (var line in BreakWord(font, word, size, width, current))
                yield return line;
        }
        if (current.Length > 0) yield return current.ToString();
    }

    /// <summary>
    /// Breaks a word wider than the box on its own between characters. Every full line is returned;
    /// the last, partial one is left in <paramref name="current"/> for the next word to join.
    /// </summary>
    private static IEnumerable<string> BreakWord(PdfFont font, string word, float size, float width, StringBuilder current)
    {
        foreach (char ch in word)
        {
            if (current.Length > 0 && font.MeasureText(current.ToString() + ch, size) > width)
            {
                yield return current.ToString();
                current.Clear();
            }
            current.Append(ch);
        }
    }

    /// <summary>
    /// Returns each run of text on a page with its bounding box in PDF user space. Used to build
    /// the viewer's selectable text layer. Runs (not individual glyphs) keep the layer light.
    /// </summary>
    public static IReadOnlyList<TextSpan> GetTextSpans(byte[] pdf, int page, string? password = null)
    {
        var doc = PdfIo.OpenReadOnly(pdf, password);
        if (page < 1 || page > doc.PageCount) return Array.Empty<TextSpan>();
        var spans = new List<TextSpan>();
        var pdfPage = doc.GetPage(page);
        PdfIo.Guarded($"reading the text layout of page {page}", () =>
        {
            PdfStructureGuard.EnsureFormXObjectsTerminate(pdfPage);
            new ContentProcessor(new SpanListener(spans)).ProcessPage(pdfPage);
        });
        return spans;
    }

    private sealed class SpanListener : IContentListener
    {
        private readonly List<TextSpan> _spans;
        public SpanListener(List<TextSpan> spans) => _spans = spans;

        public void OnText(TextRenderInfo info)
        {
            string text = info.Text;
            if (string.IsNullOrWhiteSpace(text)) return;
            var asc = info.AscentLine;
            var desc = info.DescentLine;
            float x0 = (float)desc.Start.X, x1 = (float)desc.End.X;
            float yBottom = (float)desc.Start.Y, yTop = (float)asc.Start.Y;
            float minX = Math.Min(x0, x1), maxX = Math.Max(x0, x1);
            if (maxX <= minX || yTop <= yBottom) return; // skip zero-area / vertical runs
            _spans.Add(new TextSpan(text, minX, yBottom, maxX - minX, yTop - yBottom));
        }
    }

    // ------------------------------------------------------------ extraction

    private sealed record Chunk(string Text, PdfRect BBox, float FontHeight, float FontSize,
        string FontName, bool Invisible);
    /// <summary>
    /// Recovers the type size a run was set in from the height of its transformed
    /// ascender-to-descender box.
    /// <para>
    /// The box is <em>not</em> the em: it spans only <c>(ascender - descender) / 1000</c> of it —
    /// 0.925 for Helvetica, 0.900 for Times, 0.786 for Courier. Treating the box height as the font
    /// size (which this code did until #29) re-stamped every edited run 7–21% smaller than the
    /// original, and because each edit re-measured its own undersized output the error compounded:
    /// three passes over 24pt Courier left it under 12pt.
    /// </para>
    /// <para>
    /// Working back from the box rather than from the <c>Tf</c> operand is deliberate — the box has
    /// the text matrix and CTM already applied, so a run scaled by a <c>Tm</c>/<c>cm</c> yields the
    /// size it is drawn at, which is the size the replacement has to be stamped at.
    /// </para>
    /// </summary>
    /// <param name="boxHeight">Transformed ascent-line to descent-line distance.</param>
    /// <param name="ascender">Font ascender, normalised to a 1000-unit em.</param>
    /// <param name="descender">Font descender (negative), normalised to a 1000-unit em.</param>
    internal static float EmSizeFromBoxHeight(float boxHeight, float ascender, float descender)
    {
        float span = (ascender - descender) / 1000f;
        // Fonts that report no usable vertical metrics — Type 3 faces, undecodable embedded
        // programs — leave the box height as the only estimate there is. Guarding on a plausible
        // range also keeps a zero or absurd span from producing an infinity or a NaN.
        if (!float.IsFinite(span) || span < 0.4f || span > 2f) return boxHeight;
        return boxHeight / span;
    }

    private static List<Chunk> CollectChunks(PdfDocument doc, int pageNumber)
    {
        var chunks = new List<Chunk>();
        var listener = new ChunkListener(chunks);
        var page = doc.GetPage(pageNumber);
        PdfIo.Guarded($"extracting text from page {pageNumber}", () =>
        {
            PdfStructureGuard.EnsureFormXObjectsTerminate(page);
            new ContentProcessor(listener).ProcessPage(page);
        });
        return chunks;
    }

    private sealed class ChunkListener : IContentListener
    {
        private readonly List<Chunk> _chunks;
        public ChunkListener(List<Chunk> chunks) => _chunks = chunks;

        public void OnText(TextRenderInfo info)
        {
            foreach (var single in info.Glyphs)
            {
                var asc = single.AscentLine;
                var desc = single.DescentLine;
                float minX = (float)Math.Min(asc.Start.X, desc.Start.X);
                float maxX = (float)Math.Max(asc.End.X, desc.End.X);
                float minY = (float)desc.Start.Y;
                float maxY = (float)asc.Start.Y;
                if (maxX <= minX) continue;
                float boxHeight = maxY - minY;
                // Rendering mode 3 draws nothing. It is how a searchable scan carries its OCR
                // layer: the words you see are pixels in the page image, and this text only exists
                // to be selected and searched.
                bool invisible = info.RenderMode == 3;
                _chunks.Add(new Chunk(single.Text,
                    new PdfRect(minX, minY, maxX - minX, boxHeight), boxHeight,
                    EmSizeFromBoxHeight(boxHeight, info.Font.Ascent, info.Font.Descent), info.FontName, invisible));
            }
        }
    }

    private static bool ContainsCenter(PdfRect region, PdfRect glyph)
    {
        float cx = glyph.Left + glyph.Width / 2;
        float cy = glyph.Bottom + glyph.Height / 2;
        return cx >= region.Left && cx <= region.Right &&
               cy >= region.Bottom && cy <= region.Top;
    }

    private static string AssembleText(List<Chunk> chunks)
    {
        if (chunks.Count == 0) return string.Empty;
        // Group into lines by baseline proximity, then order left-to-right.
        var lines = new List<List<Chunk>>();
        foreach (var chunk in chunks.OrderByDescending(c => c.BBox.Bottom))
        {
            var line = lines.FirstOrDefault(l =>
                Math.Abs(l[0].BBox.Bottom - chunk.BBox.Bottom) < l[0].FontHeight * 0.6f);
            if (line == null)
            {
                line = new List<Chunk>();
                lines.Add(line);
            }
            line.Add(chunk);
        }
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            if (sb.Length > 0) sb.Append('\n');
            Chunk? prev = null;
            foreach (var c in line.OrderBy(c => c.BBox.Left))
            {
                if (prev != null &&
                    c.BBox.Left - prev.BBox.Right > prev.FontHeight * 0.25f)
                    sb.Append(' ');
                sb.Append(c.Text);
                prev = c;
            }
        }
        return sb.ToString();
    }
}

/// <summary>Where a phrase must sit relative to a word for a text search to match it.</summary>
public enum TextMatchMode
{
    /// <summary>Anywhere, including within a word (the default).</summary>
    Contains,
    /// <summary>At the start of a word.</summary>
    StartsWith,
    /// <summary>At the end of a word.</summary>
    EndsWith,
    /// <summary>The whole word, bounded on both sides.</summary>
    WholeWord,
}

/// <summary>How a text search matches. Defaults to case-insensitive, anywhere within a word.</summary>
public sealed record SearchOptions(TextMatchMode Mode = TextMatchMode.Contains, bool CaseSensitive = false)
{
    /// <summary>Parses the wire values (any casing) to options; unknown mode falls back to Contains.</summary>
    public static SearchOptions Parse(string? mode, bool caseSensitive)
    {
        var m = (mode ?? "").Replace("-", "").Replace("_", "").Trim().ToLowerInvariant() switch
        {
            "startswith" or "starts" or "prefix" => TextMatchMode.StartsWith,
            "endswith" or "ends" or "suffix" => TextMatchMode.EndsWith,
            "wholeword" or "word" or "exact" => TextMatchMode.WholeWord,
            _ => TextMatchMode.Contains,
        };
        return new SearchOptions(m, caseSensitive);
    }
}
