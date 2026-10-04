using PdfEditor.Core.Pdf;

namespace PdfEditor.Core;

/// <summary>
/// Applies true redaction: the content beneath each region (text, images, vector marks
/// inside form XObjects) is removed from the file, then an opaque black box is painted
/// over the region.
/// </summary>
public static class Redactor
{
    /// <summary>How the redaction box is painted (purely cosmetic; the content is removed either way).</summary>
    public enum Fill
    {
        /// <summary>A flat opaque black rectangle.</summary>
        Solid,
        /// <summary>Solid black overlaid with a diagonal hatch, for a heavier "redacted" look.</summary>
        Hatch,
    }

    public static EditResult Redact(byte[] pdf, IEnumerable<RectRegion> regions,
        string? password = null, Fill fill = Fill.Solid)
        => Apply(pdf, regions, drawBoxes: true, password, fill: fill);

    /// <summary>
    /// Removes the content in the regions without painting black boxes (used by text and image
    /// editing). <paramref name="kinds"/> selects what may be taken out: text editing passes
    /// <see cref="ContentKinds.TextOnly"/> so artwork behind the text is left alone, while moving an
    /// image needs the default and must take the image with it.
    /// </summary>
    internal static EditResult RemoveContent(byte[] pdf, IEnumerable<RectRegion> regions,
        string? password = null, ContentKinds kinds = ContentKinds.All)
        => Apply(pdf, regions, drawBoxes: false, password, kinds);

    private static EditResult Apply(byte[] pdf, IEnumerable<RectRegion> regions, bool drawBoxes,
        string? password, ContentKinds kinds = ContentKinds.All, Fill fill = Fill.Solid)
    {
        var byPage = regions.GroupBy(r => r.Page).ToDictionary(g => g.Key, g => g.ToList());
        if (byPage.Count == 0) return EditResult.Of(pdf);

        var warnings = new List<string>();
        var doc = PdfIo.Open(pdf, password);
        foreach (var (pageNumber, pageRegions) in byPage)
        {
            if (pageNumber < 1 || pageNumber > doc.PageCount)
                throw new ArgumentOutOfRangeException(nameof(regions), $"Page {pageNumber} does not exist.");
            var page = doc.GetPage(pageNumber);
            var rects = pageRegions.Select(r => new PdfRect(r.X, r.Y, r.Width, r.Height)).ToList();

            var editor = ContentStreamEditor.Create(rects, doc, warnings, kinds: kinds);
            editor.EditPage(page);

            RemoveAnnotationsIn(page, rects);

            if (drawBoxes)
                DrawBoxesInDefaultUserSpace(page, rects, fill);
        }
        return new EditResult(PdfIo.Save(doc), warnings);
    }

    /// <summary>
    /// Paints the opaque black boxes over the regions. Drawing in the page's default user space
    /// (see <see cref="PdfContentGuard.DrawInDefaultUserSpace"/>) keeps the boxes aligned with the
    /// content even when the page leaves a scale/flip transform active — which is why the box used
    /// to land in the wrong place on Chrome / Google-Docs-exported PDFs while the removal was fine.
    /// </summary>
    private static void DrawBoxesInDefaultUserSpace(PdfPage page, IList<PdfRect> rects, Fill fill)
    {
        var canvas = new ContentBuilder();
        canvas.FillRgb(0, 0, 0);
        foreach (var r in rects)
            canvas.Rectangle(r.Left, r.Bottom, r.Width, r.Height);
        canvas.Fill();

        // The hatch is purely visual — the content is already gone, and the solid black beneath keeps
        // the box fully opaque. It just gives a heavier, textured "redacted" look.
        if (fill == Fill.Hatch)
            foreach (var r in rects)
                DrawHatch(canvas, r);
        PdfContentGuard.DrawInDefaultUserSpace(page, canvas.ToArray());
    }

    /// <summary>Overlays a diagonal hatch, clipped to the box, in a slightly lighter grey.</summary>
    private static void DrawHatch(ContentBuilder canvas, PdfRect r)
    {
        canvas.SaveState();
        canvas.Rectangle(r.Left, r.Bottom, r.Width, r.Height).Clip().EndPath();
        canvas.StrokeGray(0.30).LineWidth(0.8);
        const float step = 4f;
        float h = r.Height;
        // 45° lines sweeping across the box; starting h to the left of the box so the whole face fills.
        for (float x = r.Left - h; x <= r.Right; x += step)
            canvas.MoveTo(x, r.Bottom).LineTo(x + h, r.Top);
        canvas.Stroke();
        canvas.RestoreState();
    }

    private static void RemoveAnnotationsIn(PdfPage page, IList<PdfRect> regions)
    {
        foreach (var annotation in page.Annotations.ToArray())
        {
            var rect = PdfRect.FromArray(annotation.GetAsArray(PdfName.Rect));
            if (rect is { } r && regions.Any(region => region.Intersects(r)))
                page.RemoveAnnotation(annotation);
        }
    }
}
