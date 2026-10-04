using PdfEditor.Core;
using SkiaSharp;
using Xunit;

namespace PdfEditor.Tests;

public class RedactorTests
{
    [Fact]
    public void RemovesTextInsideRegion_AndKeepsTextOutside()
    {
        byte[] pdf = TestPdfs.WithText(
            ("TOP SECRET PAYLOAD", 72, 700, 14),
            ("public information", 72, 600, 14));

        var result = Redactor.Redact(pdf, new[] { new RectRegion(1, 60, 690, 300, 30) });

        string text = TestPdfAssert.ExtractText(result.Pdf);
        Assert.DoesNotContain("SECRET", text);
        Assert.DoesNotContain("PAYLOAD", text);
        Assert.Contains("public information", text);
    }

    [Fact]
    public void PaintsOpaqueBlackBoxOverRegion()
    {
        byte[] pdf = TestPdfs.WithText(("TOP SECRET", 72, 700, 14));

        var result = Redactor.Redact(pdf, new[] { new RectRegion(1, 60, 690, 200, 30) });

        var pixel = TestPdfAssert.PixelAt(result.Pdf, 1, 160, 705);
        Assert.Equal(new SKColor(0, 0, 0, 255), pixel);
    }

    [Fact]
    public void HatchFill_StillRedacts_AndCoversTheRegion()
    {
        byte[] pdf = TestPdfs.WithText(("TOP SECRET", 72, 700, 14));

        var result = Redactor.Redact(pdf, new[] { new RectRegion(1, 60, 690, 200, 30) },
            fill: Redactor.Fill.Hatch);

        // The text is still truly removed...
        Assert.DoesNotContain("SECRET", TestPdfAssert.ExtractText(result.Pdf));
        // ...and the region is heavily inked (the solid base under the hatch keeps it opaque).
        Assert.True(TestPdfAssert.InkFraction(result.Pdf, 1, 62, 692, 258, 718, 150) > 0.8,
            "the hatched box should still fully cover its region");
    }

    [Fact]
    public void RemovesOnlyTheCoveredWord_WhenRegionCoversPartOfALine()
    {
        byte[] pdf = TestPdfs.WithText(("Alpha Bravo Charlie", 72, 700, 14));
        var match = Assert.Single(TextTools.FindText(pdf, "Bravo"));

        var result = Redactor.Redact(pdf, new[]
        {
            new RectRegion(match.Page, match.X, match.Y, match.Width, match.Height)
        });

        string text = TestPdfAssert.ExtractText(result.Pdf);
        Assert.DoesNotContain("Bravo", text);
        Assert.Contains("Alpha", text);
        Assert.Contains("Charlie", text);
    }

    [Fact]
    public void PreservesPositionOfTextAfterARedactedWord()
    {
        byte[] pdf = TestPdfs.WithText(("Alpha Bravo Charlie", 72, 700, 14));
        var before = Assert.Single(TextTools.FindText(pdf, "Charlie"));
        var bravo = Assert.Single(TextTools.FindText(pdf, "Bravo"));

        var result = Redactor.Redact(pdf, new[]
        {
            new RectRegion(bravo.Page, bravo.X, bravo.Y, bravo.Width, bravo.Height)
        });

        var after = Assert.Single(TextTools.FindText(result.Pdf, "Charlie"));
        Assert.Equal(before.X, after.X, 1.0);
        Assert.Equal(before.Y, after.Y, 1.0);
    }

    [Fact]
    public void RemovesImageFullyInsideRegion()
    {
        byte[] pdf = TestPdfs.WithImage(100, 500, 120, 80);
        Assert.Equal(1, TestPdfAssert.CountImages(pdf));

        var result = Redactor.Redact(pdf, new[] { new RectRegion(1, 90, 490, 140, 100) });

        Assert.Equal(0, TestPdfAssert.CountImages(result.Pdf));
        Assert.Contains("Document with image", TestPdfAssert.ExtractText(result.Pdf));
    }

    [Fact]
    public void ScrubsPixels_WhenRegionPartiallyOverlapsImage()
    {
        byte[] pdf = TestPdfs.WithImage(100, 500, 200, 100);

        // Cover only the left half of the image.
        var result = Redactor.Redact(pdf, new[] { new RectRegion(1, 100, 500, 100, 100) });

        // The image must still exist, but its covered pixels must be black even
        // ignoring the overlay box (which also covers them). The uncovered half stays red.
        Assert.Equal(1, TestPdfAssert.CountImages(result.Pdf));
        var uncovered = TestPdfAssert.PixelAt(result.Pdf, 1, 250, 550);
        Assert.True(uncovered.Red > 200 && uncovered.Green < 60,
            $"uncovered image half should stay red but was {uncovered}");
    }

    [Fact]
    public void RemovesAnnotationsIntersectingRegion()
    {
        byte[] pdf = TestPdfs.WithLinkAnnotation(72, 650, 120, 20);

        var result = Redactor.Redact(pdf, new[] { new RectRegion(1, 60, 640, 200, 40) });

        Assert.Empty(PdfEditor.Core.Pdf.PdfDocument.Open(result.Pdf).GetPage(1).Annotations);
    }

    [Fact]
    public void RedactingMultipleRegionsAcrossPages_Works()
    {
        byte[] pdf = TestPdfs.MultiPage(3, "Confidential");

        var result = Redactor.Redact(pdf, new[]
        {
            new RectRegion(1, 60, 760, 300, 30),
            new RectRegion(3, 60, 760, 300, 30)
        });

        Assert.DoesNotContain("Confidential", TestPdfAssert.ExtractText(result.Pdf, 1));
        Assert.Contains("Confidential", TestPdfAssert.ExtractText(result.Pdf, 2));
        Assert.DoesNotContain("Confidential", TestPdfAssert.ExtractText(result.Pdf, 3));
    }

    [Fact]
    public void BlackBox_LandsOnTheContent_WhenPageLeavesATransformActive()
    {
        // Regression: Chrome / Google-Docs-exported PDFs apply a top-level scale+flip matrix that
        // is never wrapped in q/Q, so it is still active at the end of the content stream. A box
        // appended with a plain PdfCanvas inherited that matrix and landed scaled/flipped away,
        // even though the content removal (CTM-aware) was correct. The box must land on the word.
        byte[] pdf = TestPdfs.ChromeStyleLeftoverCtm("SECRET");
        var match = Assert.Single(TextTools.FindText(pdf, "SECRET"));
        float cy = match.Y + match.Height / 2;

        // The word really renders where the engine reports it: some pixel along that band is dark
        // (before redaction), proving the engine's coordinates agree with what PDFium renders.
        bool textRendersHere = false;
        for (float dx = 1; dx < match.Width && !textRendersHere; dx += 1)
            if (TestPdfAssert.PixelAt(pdf, 1, match.X + dx, cy, 150).Red < 128) textRendersHere = true;
        Assert.True(textRendersHere, $"expected the word to render along y={cy:F0} but that band was blank");

        var result = Redactor.Redact(pdf, new[]
        {
            new RectRegion(match.Page, match.X, match.Y, match.Width, match.Height)
        });

        // After redaction the box is opaque black across the whole word (not shifted elsewhere).
        for (float dx = 2; dx < match.Width; dx += 4)
        {
            var boxPixel = TestPdfAssert.PixelAt(result.Pdf, 1, match.X + dx, cy, 150);
            Assert.Equal(new SKColor(0, 0, 0, 255), boxPixel);
        }
    }

    [Fact]
    public void NoRegions_ReturnsDocumentUnchanged()
    {
        byte[] pdf = TestPdfs.WithText(("hello", 72, 700, 12));
        var result = Redactor.Redact(pdf, Array.Empty<RectRegion>());
        Assert.Equal(pdf, result.Pdf);
    }

    [Fact]
    public void InvalidPage_Throws()
    {
        byte[] pdf = TestPdfs.WithText(("hello", 72, 700, 12));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Redactor.Redact(pdf, new[] { new RectRegion(9, 0, 0, 10, 10) }));
    }

    [Fact]
    public void RedactsEncryptedDocument_WithPassword()
    {
        byte[] pdf = TestPdfs.WithText(("TOP SECRET", 72, 700, 14));
        byte[] encrypted = Encryptor.Encrypt(pdf, "pw123");

        var result = Redactor.Redact(encrypted, new[] { new RectRegion(1, 60, 690, 200, 30) }, "pw123");

        Assert.DoesNotContain("SECRET", TestPdfAssert.ExtractText(result.Pdf, 1, "pw123"));
    }
}
