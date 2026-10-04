using PdfEditor.Core.Pdf;
using SkiaSharp;

namespace PdfEditor.Tests;

/// <summary>Inspection helpers used by assertions.</summary>
public static class TestPdfAssert
{
    /// <summary>Extracts all text from a page in reading order.</summary>
    public static string ExtractText(byte[] pdf, int page = 1, string? password = null) =>
        LocationTextExtraction.ExtractPage(PdfDocument.Open(pdf, password).GetPage(page));

    /// <summary>
    /// The (base font name, type size) of every text-showing run on a page, read from the
    /// interpreted graphics state.
    /// <para>
    /// This deliberately does not go through <c>TextTools</c>'s own measurement — the font-fidelity
    /// tests (#29) must not verify the detector against itself. The size reported here is the
    /// <c>Tf</c> operand scaled by the text/graphics matrices, i.e. the size the glyphs are
    /// actually drawn at. (The cross-engine check that our interpreter agrees with PDFium lives in
    /// <c>PdfiumCrossCheckTests</c>.)
    /// </para>
    /// </summary>
    public static IReadOnlyList<(string Font, float Size)> DrawnRuns(byte[] pdf, int page = 1)
    {
        var listener = new RunListener();
        new ContentProcessor(listener).ProcessPage(PdfDocument.Open(pdf).GetPage(page));
        return listener.Runs;
    }

    private sealed class RunListener : IContentListener
    {
        public List<(string Font, float Size)> Runs { get; } = new();

        public void OnText(TextRenderInfo t)
        {
            if (string.IsNullOrWhiteSpace(t.Text)) return;
            // FontSize is the Tf operand; the text-to-user matrix carries any Tm/cm scaling on top.
            var m = t.TextToUser;
            float scale = (float)Math.Sqrt(Math.Abs(m.A * m.D - m.B * m.C));
            Runs.Add((t.FontName, (float)t.FontSize * (scale == 0 ? 1 : scale)));
        }
    }

    /// <summary>Counts image draw events on a page (XObject and inline images).</summary>
    public static int CountImages(byte[] pdf, int page = 1)
    {
        var listener = new ImageCounter();
        new ContentProcessor(listener).ProcessPage(PdfDocument.Open(pdf).GetPage(page));
        return listener.Count;
    }

    private static float PageHeight(byte[] pdf, int page) => PdfDocument.Open(pdf).GetPage(page).MediaBox.Height;

    /// <summary>Renders the page and returns the colour of the pixel at a user-space point.</summary>
    public static SKColor PixelAt(byte[] pdf, int page, float userX, float userY, int dpi = 72)
    {
        byte[] png = PdfEditor.Core.PageRenderer.RenderPagePng(pdf, page, dpi);
        using var bitmap = SKBitmap.Decode(png);
        float scale = dpi / 72f;
        float pageHeight = PageHeight(pdf, page);
        int px = (int)(userX * scale);
        int py = (int)((pageHeight - userY) * scale);
        return bitmap.GetPixel(Math.Clamp(px, 0, bitmap.Width - 1), Math.Clamp(py, 0, bitmap.Height - 1));
    }

    /// <summary>
    /// Fraction of pixels in a user-space band that are dark — i.e. how much ink is in it. Sampling
    /// a band rather than a point is what you want for "are these words still on the page": a single
    /// coordinate can land between the legs of an A and report white on a page full of text.
    /// </summary>
    public static double InkFraction(byte[] pdf, int page, float x0, float y0, float x1, float y1,
        int dpi = 72)
    {
        byte[] png = PdfEditor.Core.PageRenderer.RenderPagePng(pdf, page, dpi);
        using var bitmap = SKBitmap.Decode(png);
        float scale = dpi / 72f;
        float pageHeight = PageHeight(pdf, page);

        int dark = 0, total = 0;
        for (float uy = y0; uy <= y1; uy += 0.5f)
        {
            for (float ux = x0; ux <= x1; ux += 0.5f)
            {
                int px = Math.Clamp((int)(ux * scale), 0, bitmap.Width - 1);
                int py = Math.Clamp((int)((pageHeight - uy) * scale), 0, bitmap.Height - 1);
                var c = bitmap.GetPixel(px, py);
                total++;
                if (c.Red < 128 && c.Green < 128 && c.Blue < 128) dark++;
            }
        }
        return total == 0 ? 0 : (double)dark / total;
    }

    private sealed class ImageCounter : IContentListener
    {
        public int Count { get; private set; }
        public void OnImage(ImageRenderInfo info) => Count++;
    }
}
