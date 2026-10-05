using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using SkiaSharp;

namespace PdfEditor.Tests;

/// <summary>Inspection helpers used by assertions.</summary>
public static class TestPdfAssert
{
    /// <summary>Extracts all text from a page using standard extraction.</summary>
    public static string ExtractText(byte[] pdf, int page = 1, string? password = null)
    {
        var props = new ReaderProperties();
        if (password != null) props.SetPassword(System.Text.Encoding.UTF8.GetBytes(password));
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf), props));
        return PdfTextExtractor.GetTextFromPage(doc.GetPage(page), new LocationTextExtractionStrategy());
    }

    /// <summary>
    /// Whether <paramref name="text"/> occurs anywhere in the saved file: in its raw bytes, or in the
    /// decoded data of any stream it holds, whether or not anything draws that stream. Text extraction
    /// only sees what a page draws, so it can't tell redacted content that is gone from content that
    /// is merely no longer drawn.
    /// </summary>
    public static bool AppearsAnywhere(byte[] pdf, string text)
    {
        byte[] needle = System.Text.Encoding.Latin1.GetBytes(text);
        if (pdf.AsSpan().IndexOf(needle) >= 0) return true;
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        for (int i = 1; i < doc.GetNumberOfPdfObjects(); i++)
        {
            if (doc.GetPdfObject(i) is PdfStream stream && stream.GetBytes().AsSpan().IndexOf(needle) >= 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// The (base font name, type size) of every text-showing run on a page, read from the rendered
    /// graphics state.
    /// <para>
    /// This is deliberately independent of <c>TextTools</c>'s own measurement: the font-fidelity
    /// tests (#29) must not verify the detector against itself, or a detector that is wrong in a
    /// self-consistent way passes. The size reported here is the <c>Tf</c> operand scaled by the
    /// text/graphics matrices, i.e. the size the glyphs are actually drawn at.
    /// </para>
    /// </summary>
    public static IReadOnlyList<(string Font, float Size)> DrawnRuns(byte[] pdf, int page = 1)
    {
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        var listener = new RunListener();
        new PdfCanvasProcessor(listener).ProcessPageContent(doc.GetPage(page));
        return listener.Runs;
    }

    private sealed class RunListener : IEventListener
    {
        public List<(string Font, float Size)> Runs { get; } = new();

        public void EventOccurred(IEventData data, EventType type)
        {
            if (data is not TextRenderInfo t || string.IsNullOrWhiteSpace(t.GetText())) return;
            // GetFontSize() is the Tf operand; the vertical scale of the text matrix carries any
            // Tm/cm scaling on top of it.
            var matrix = t.GetTextMatrix();
            float scale = (float)Math.Sqrt(Math.Abs(
                matrix.Get(iText.Kernel.Geom.Matrix.I11) * matrix.Get(iText.Kernel.Geom.Matrix.I22)
                - matrix.Get(iText.Kernel.Geom.Matrix.I12) * matrix.Get(iText.Kernel.Geom.Matrix.I21)));
            string name = t.GetFont()?.GetFontProgram()?.GetFontNames()?.GetFontName() ?? "";
            Runs.Add((name, t.GetFontSize() * (scale == 0 ? 1 : scale)));
        }

        public ICollection<EventType>? GetSupportedEvents() => null;
    }

    /// <summary>Counts image draw events on a page (XObject and inline images).</summary>
    public static int CountImages(byte[] pdf, int page = 1)
    {
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        var listener = new ImageCounter();
        new PdfCanvasProcessor(listener).ProcessPageContent(doc.GetPage(page));
        return listener.Count;
    }

    /// <summary>Renders the page and returns the colour of the pixel at a user-space point.</summary>
    public static SKColor PixelAt(byte[] pdf, int page, float userX, float userY, int dpi = 72)
    {
        byte[] png = PdfEditor.Core.PageRenderer.RenderPagePng(pdf, page, dpi);
        using var bitmap = SKBitmap.Decode(png);
        float scale = dpi / 72f;
        using var docReader = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        float pageHeight = docReader.GetPage(page).GetPageSize().GetHeight();
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
        using var docReader = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        float pageHeight = docReader.GetPage(page).GetPageSize().GetHeight();

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

    private sealed class ImageCounter : IEventListener
    {
        public int Count { get; private set; }

        public void EventOccurred(IEventData data, EventType type)
        {
            if (data is ImageRenderInfo) Count++;
        }

        public ICollection<EventType>? GetSupportedEvents() => null;
    }
}
