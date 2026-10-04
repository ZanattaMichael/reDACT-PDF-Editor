using PdfEditor.Core.Pdf;

namespace PdfEditor.Core;

/// <summary>Applies a highlighter mark over text.</summary>
public static class HighlightTool
{
    // A pleasant highlighter yellow when no colour is given.
    private static readonly PdfColor DefaultColour = PdfColor.Rgb(255, 235, 59);

    /// <summary>
    /// Paints highlight rectangles over the given regions on a page. The rectangles are drawn with
    /// a Multiply blend so the text underneath shows straight through — the paper turns the
    /// highlight colour while the (usually dark) glyphs stay legible. Drawn in the page's default
    /// user space (via <see cref="PdfContentGuard"/>) so a leftover page transform can't shift them.
    /// </summary>
    public static EditResult AddHighlight(byte[] pdf, int page, IReadOnlyList<RectRegion> rects,
        string? colorHex = null, string? password = null)
    {
        if (rects.Count == 0) return EditResult.Of(pdf);
        var colour = TextTools.ParseColor(colorHex) ?? DefaultColour;

        var doc = PdfIo.Open(pdf, password);
        if (page < 1 || page > doc.PageCount)
            throw new ArgumentOutOfRangeException(nameof(page), $"Page {page} does not exist.");

        var pdfPage = doc.GetPage(page);
        var gs = new PdfDictionary();
        gs.Put(PdfName.Type, PdfName.ExtGState);
        gs.Put(PdfName.BM, PdfName.Multiply);
        var gsName = PdfResources.Add(pdfPage.GetOrCreateResources(), PdfName.ExtGState, "Gs", doc.MakeIndirect(gs));
        var canvas = new ContentBuilder().GraphicsState(gsName).FillColor(colour);
        foreach (var r in rects)
            canvas.Rectangle(r.X, r.Y, r.Width, r.Height);
        canvas.Fill();
        PdfContentGuard.DrawInDefaultUserSpace(pdfPage, canvas.ToArray());
        return EditResult.Of(PdfIo.Save(doc));
    }
}
