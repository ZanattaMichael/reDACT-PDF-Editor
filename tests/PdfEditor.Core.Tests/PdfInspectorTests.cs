using PdfEditor.Core.Pdf;
using PdfEditor.Core;

namespace PdfEditor.Tests;

public class PdfInspectorTests
{
    [Fact]
    public void OriginZeroPage_ReportsZeroOffset()
    {
        byte[] pdf = TestPdfs.WithText(("hello", 72, 700, 14));

        var info = PdfInspector.GetInfo(pdf);

        Assert.Equal(0, info.Pages[0].X);
        Assert.Equal(0, info.Pages[0].Y);
        Assert.Equal(TestPdfs.PageWidth, info.Pages[0].Width);
        Assert.Equal(TestPdfs.PageHeight, info.Pages[0].Height);
    }

    [Fact]
    public void NonZeroOriginPage_ExposesTheBoxOrigin()
    {
        // A page whose MediaBox is [100 200 500 700] — origin (100,200), size 400x500.
        // The viewer needs this origin: the rendered image's bottom-left corresponds to
        // (100,200) in user space, not (0,0), so without it screen->document mapping (and
        // therefore redaction placement) is offset by the origin.
        var info = PdfInspector.GetInfo(OnePage(new PdfRect(100, 200, 400, 500), null, 0));

        Assert.Equal(100, info.Pages[0].X);
        Assert.Equal(200, info.Pages[0].Y);
        Assert.Equal(400, info.Pages[0].Width);
        Assert.Equal(500, info.Pages[0].Height);
        Assert.Equal(0, info.Pages[0].Rotation);
    }

    [Fact]
    public void ReportsTheCropBox_NotTheMediaBox()
    {
        // PDFium renders the crop box; the geometry the viewer gets must match it, otherwise
        // every mapped coordinate is wrong for any document that sets a crop box.
        var info = PdfInspector.GetInfo(OnePage(new PdfRect(0, 0, 595, 842), new PdfRect(50, 60, 400, 500), 0));

        Assert.Equal(50, info.Pages[0].X);
        Assert.Equal(60, info.Pages[0].Y);
        Assert.Equal(400, info.Pages[0].Width);
        Assert.Equal(500, info.Pages[0].Height);
    }

    [Fact]
    public void ClampsACropBoxThatExceedsTheMediaBox_ToWhatIsRendered()
    {
        // A crop box larger than / offset beyond the media box: the renderer only shows the
        // intersection, so that is what must be reported (not the unclamped crop box as authored).
        // The crop box extends past the media box on every side.
        var info = PdfInspector.GetInfo(OnePage(new PdfRect(0, 0, 612, 792), new PdfRect(-50, -50, 712, 892), 0));

        Assert.Equal(0, info.Pages[0].X);
        Assert.Equal(0, info.Pages[0].Y);
        Assert.Equal(612, info.Pages[0].Width);
        Assert.Equal(792, info.Pages[0].Height);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void ExposesThePageRotation(int rotation)
    {
        var info = PdfInspector.GetInfo(OnePage(new PdfRect(0, 0, 595, 842), null, rotation));

        Assert.Equal(rotation, info.Pages[0].Rotation);
    }

    /// <summary>A one-page document with the given media box, optional crop box and rotation.</summary>
    private static byte[] OnePage(PdfRect media, PdfRect? crop, int rotation)
    {
        var doc = PdfDocument.CreateNew();
        var page = doc.AddNewPage(media.Width, media.Height);
        page.Dictionary.Put(PdfName.MediaBox, media.ToArray());
        if (crop is { } c) page.Dictionary.Put(PdfName.CropBox, c.ToArray());
        if (rotation != 0) page.Rotation = rotation;
        return doc.Save();
    }
}
