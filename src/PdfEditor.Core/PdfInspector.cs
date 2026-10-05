using PdfEditor.Core.Pdf;

namespace PdfEditor.Core;

/// <summary>Reads basic document facts (page geometry, encryption state).</summary>
public static class PdfInspector
{
    public static DocumentInfo GetInfo(byte[] pdf, string? password = null)
    {
        bool encrypted = Encryptor.IsEncrypted(pdf);
        var doc = PdfIo.OpenReadOnly(pdf, password);
        var pages = new List<PageInfo>();
        for (int p = 1; p <= doc.PageCount; p++)
        {
            var page = doc.GetPage(p);
            // The renderer (PDFium) shows the *effective* crop box — the crop box intersected
            // with the media box — and applies the page rotation. The crop box as authored can be
            // larger than or offset beyond the media box; reporting that unclamped would scale/shift
            // every coordinate the viewer maps, so redactions would land in the wrong place.
            // Report exactly what gets rendered.
            var box = EffectiveBox(page.CropBox, page.MediaBox);
            pages.Add(new PageInfo(p, box.X, box.Y, box.Width, box.Height, page.Rotation));
        }
        return new DocumentInfo(pages.Count, pages, encrypted);
    }

    /// <summary>The crop box clamped to the media box (what actually gets rendered).</summary>
    private static PdfRect EffectiveBox(PdfRect crop, PdfRect media) =>
        crop.Intersect(media) ?? media; // degenerate intersection — fall back
}
