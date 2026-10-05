using PdfEditor.Core.Pdf;
using PdfEditor.Core.Pdf.Fonts;

namespace PdfEditor.Core;

/// <summary>
/// Stamps Bates numbers — the sequential, zero-padded identifiers legal documents carry (e.g.
/// <c>ACME000001</c>) — into a corner of each page. Like the watermark, the number is written into
/// the page content stream rather than added as an annotation, so it becomes part of the page and
/// cannot be toggled off or deleted from an annotations panel.
/// </summary>
public static class BatesTool
{
    private static readonly PdfColor DefaultColour = PdfColor.Black;

    /// <summary>Where on the page the number is stamped.</summary>
    public enum Corner
    {
        BottomRight, BottomCenter, BottomLeft,
        TopRight, TopCenter, TopLeft,
    }

    /// <summary>Distance, in points, the number is inset from the page edges.</summary>
    private const float Margin = 24f;

    /// <summary>
    /// Adds a Bates number to each target page: prefix + the page's index (starting at
    /// <c>Start</c>, zero-padded to <c>Digits</c>) + suffix, per <paramref name="options"/>.
    /// </summary>
    /// <returns>The edited document, plus the first and last labels applied.</returns>
    public static BatesResult AddBatesNumbers(byte[] pdf, BatesOptions? options = null, string? password = null)
    {
        var o = options ?? new BatesOptions();
        if (o.Start < 0) throw new ArgumentOutOfRangeException(nameof(options), "Start must be zero or greater.");
        int digits = Math.Clamp(o.Digits, 1, 12);
        float fontSize = Math.Clamp(o.FontSize, 4f, 72f);
        string prefix = o.Prefix ?? "";
        string suffix = o.Suffix ?? "";
        var colour = TextTools.ParseColor(o.ColorHex) ?? DefaultColour;
        var font = PdfFont.Standard(StandardFonts.Helvetica);

        string? firstLabel = null, lastLabel = null;
        var doc = PdfIo.Open(pdf, password);
        var fontDict = doc.MakeIndirect(font.Dictionary!);
        var target = NormalizePages(o.Pages, doc.PageCount);
        for (int i = 0; i < target.Count; i++)
        {
            int pageNum = target[i];
            // Invariant culture: a Bates number is an identifier, not locale-formatted text.
            string number = (o.Start + i).ToString(System.Globalization.CultureInfo.InvariantCulture);
            string label = prefix + number.PadLeft(digits, '0') + suffix;
            firstLabel ??= label;
            lastLabel = label;

            var page = doc.GetPage(pageNum);
            var box = page.MediaBox;
            float textWidth = (float)font.MeasureText(label, fontSize);
            var (x, y) = Anchor(o.Position, box, textWidth, fontSize);

            var fName = PdfResources.Add(page.GetOrCreateResources(), PdfName.Font, "F", fontDict);
            var canvas = new ContentBuilder()
                .BeginText().Font(fName, fontSize).FillColor(colour)
                .MoveText(x, y).ShowText(font.Encode(label)).EndText();
            PdfContentGuard.DrawInDefaultUserSpace(page, canvas.ToArray());
        }
        return new BatesResult(PdfIo.Save(doc), firstLabel ?? "", lastLabel ?? "");
    }

    /// <summary>Baseline start point for the label in the requested corner, inset by the margin.</summary>
    private static (float X, float Y) Anchor(Corner position, PdfRect box, float textWidth, float fontSize)
    {
        float left = box.Left, right = box.Right;
        float bottom = box.Bottom, top = box.Top;
        float x = position switch
        {
            Corner.BottomLeft or Corner.TopLeft => left + Margin,
            Corner.BottomCenter or Corner.TopCenter => (left + right) / 2f - textWidth / 2f,
            _ => right - Margin - textWidth, // right-aligned corners
        };
        // Bottom corners sit the baseline a margin above the edge; top corners a margin below the
        // top, leaving room for the glyphs' height.
        bool isTop = position is Corner.TopLeft or Corner.TopCenter or Corner.TopRight;
        float y = isTop ? top - Margin - fontSize : bottom + Margin;
        return (x, y);
    }

    /// <summary>Parses a position string (any casing, hyphen optional) to a <see cref="Corner"/>.</summary>
    public static Corner ParseCorner(string? position)
    {
        string p = (position ?? "").Replace("-", "").Replace("_", "").Trim().ToLowerInvariant();
        return p switch
        {
            "bottomleft" => Corner.BottomLeft,
            "bottomcenter" or "bottomcentre" => Corner.BottomCenter,
            "topright" => Corner.TopRight,
            "topcenter" or "topcentre" => Corner.TopCenter,
            "topleft" => Corner.TopLeft,
            _ => Corner.BottomRight,
        };
    }

    private static List<int> NormalizePages(IReadOnlyList<int>? pages, int total)
    {
        if (pages == null || pages.Count == 0)
            return Enumerable.Range(1, total).ToList();
        return pages.Where(p => p >= 1 && p <= total).Distinct().OrderBy(p => p).ToList();
    }
}

/// <summary>Options for <see cref="BatesTool.AddBatesNumbers"/>.</summary>
/// <param name="Start">Number given to the first stamped page.</param>
/// <param name="Digits">Minimum digit count; the number is left-padded with zeros to reach it.</param>
/// <param name="Pages">1-based pages to stamp, or null/empty for every page.</param>
public sealed record BatesOptions(
    string? Prefix = null, string? Suffix = null, int Start = 1, int Digits = 6,
    BatesTool.Corner Position = BatesTool.Corner.BottomRight, float FontSize = 10f,
    string? ColorHex = null, IReadOnlyList<int>? Pages = null);

/// <summary>Result of a Bates run: the edited PDF and the first/last labels stamped.</summary>
public sealed record BatesResult(byte[] Pdf, string FirstLabel, string LastLabel);
