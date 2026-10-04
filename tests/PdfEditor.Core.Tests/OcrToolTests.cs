using PdfEditor.Core.Pdf;
using PdfEditor.Core;
using SkiaSharp;

namespace PdfEditor.Tests;

/// <summary>
/// OCR depends on an external Tesseract install (like Word import depends on LibreOffice). These
/// tests adapt to the environment: where Tesseract is present the happy path is exercised; where
/// it is absent the caller must get a clear, actionable error — never a crash.
/// </summary>
public class OcrToolTests
{
    [Fact]
    public void CanOcr_ReturnsABoolean_WithoutThrowing()
    {
        bool available = OcrTool.CanOcr;
        Assert.IsType<bool>(available);
    }

    [Fact]
    public void MakeSearchable_BehavesPerTesseractAvailability()
    {
        byte[] pdf = TestPdfs.WithText(("Scanned looking text", 72, 700, 14));

        if (!OcrTool.CanOcr)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => OcrTool.MakeSearchable(pdf));
            Assert.Contains("Tesseract", ex.Message);
        }
        else
        {
            byte[] result = OcrTool.MakeSearchable(pdf);
            Assert.Equal(1, PdfInspector.GetInfo(result).PageCount);
        }
    }

    [Fact]
    public void ExtractText_BehavesPerTesseractAvailability()
    {
        byte[] pdf = TestPdfs.WithText(("HELLO OCR WORLD", 72, 700, 24));

        if (!OcrTool.CanOcr)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => OcrTool.ExtractText(pdf, 1));
            Assert.Contains("Tesseract", ex.Message);
        }
        else
        {
            string text = OcrTool.ExtractText(pdf, 1);
            Assert.Contains("OCR", text.ToUpperInvariant());
        }
    }

    /// <summary>
    /// Issue #21: the searchable copy must occupy the same page geometry as the original. The
    /// viewer lays every page out at (page points x zoom x 96/72), so a page that comes back
    /// larger than it went in is drawn proportionally larger — the "OCR results are zoomed in"
    /// symptom. Tesseract sizes its output page from the input image's resolution, so it has to
    /// be told the DPI the page was rendered at.
    /// </summary>
    [Fact]
    public void MakeSearchable_KeepsOriginalPageGeometry()
    {
        if (!OcrTool.CanOcr) return; // covered by MakeSearchable_BehavesPerTesseractAvailability

        byte[] pdf = TestPdfs.JpegScan();
        byte[] result = OcrTool.MakeSearchable(pdf);

        var box = PageBox(result, 1);
        // Within a pixel's worth of points at the OCR DPI (300): rounding the page to whole
        // pixels and back cannot drift further than that.
        Assert.Equal(TestPdfs.PageWidth, box.Width, 0.5);
        Assert.Equal(TestPdfs.PageHeight, box.Height, 0.5);
    }

    /// <summary>
    /// Issue #20: a JPEG-backed scan must still be viewable after OCR. When the searchable copy
    /// comes back with an inflated page box, the viewer's render request (up to 300 dpi) asks for
    /// a bitmap several times larger than the original page, which the renderer cannot allocate —
    /// the page silently falls back to a blank placeholder, i.e. "the image breaks and is not
    /// viewable anymore".
    /// </summary>
    [Fact]
    public void MakeSearchable_JpegScan_StillRendersAtTheViewersMaximumDpi()
    {
        if (!OcrTool.CanOcr) return;

        byte[] pdf = TestPdfs.JpegScan();
        const int viewerMaxDpi = 300; // viewer.js currentDpi() caps at 300
        var before = PngSize(PageRenderer.RenderPagePng(pdf, 1, viewerMaxDpi));

        byte[] result = OcrTool.MakeSearchable(pdf);
        var after = PngSize(PageRenderer.RenderPagePng(result, 1, viewerMaxDpi));

        Assert.InRange(after.Width, before.Width - 2, before.Width + 2);
        Assert.InRange(after.Height, before.Height - 2, before.Height + 2);
    }

    /// <summary>
    /// OCR-ing an already-OCR'd document must not compound: each pass that enlarged the page fed
    /// the next pass a bigger page, so a couple of rounds produced a document no renderer could
    /// open at all.
    /// </summary>
    [Fact]
    public void MakeSearchable_AppliedTwice_DoesNotCompoundThePageSize()
    {
        if (!OcrTool.CanOcr) return;

        byte[] twice = OcrTool.MakeSearchable(OcrTool.MakeSearchable(TestPdfs.JpegScan()));

        var box = PageBox(twice, 1);
        Assert.Equal(TestPdfs.PageWidth, box.Width, 1.0);
        Assert.Equal(TestPdfs.PageHeight, box.Height, 1.0);
    }

    private static (float Width, float Height) PageBox(byte[] pdf, int page)
    {
        var box = PdfDocument.Open(pdf).GetPage(page).MediaBox;
        return (box.Width, box.Height);
    }

    private static (int Width, int Height) PngSize(byte[] png)
    {
        using var bitmap = SKBitmap.Decode(png);
        return (bitmap.Width, bitmap.Height);
    }

    [Fact]
    public void ExtractText_InvalidPage_Throws()
    {
        byte[] pdf = TestPdfs.WithText(("one page", 72, 700, 12));
        if (!OcrTool.CanOcr) return; // page validation runs after the Tesseract check

        Assert.Throws<ArgumentOutOfRangeException>(() => OcrTool.ExtractText(pdf, 9));
    }
}
