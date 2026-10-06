using PdfEditor.Core;

namespace PdfEditor.Tests;

public class PageToolsTests
{
    [Fact]
    public void Rotate_AddsToExistingRotation_ForTheNamedPages()
    {
        byte[] pdf = TestPdfs.MultiPage(3);

        var result = PageTools.Rotate(pdf, new[] { 2 }, 90);

        var info = PdfInspector.GetInfo(result.Pdf);
        Assert.Equal(0, info.Pages[0].Rotation);
        Assert.Equal(90, info.Pages[1].Rotation);
        Assert.Equal(0, info.Pages[2].Rotation);
    }

    [Fact]
    public void Rotate_EmptyPageList_RotatesEveryPage()
    {
        byte[] pdf = TestPdfs.MultiPage(2);

        var result = PageTools.Rotate(pdf, Array.Empty<int>(), 180);

        var info = PdfInspector.GetInfo(result.Pdf);
        Assert.All(info.Pages, pg => Assert.Equal(180, pg.Rotation));
    }

    [Fact]
    public void Rotate_WrapsAround_ModuloThreeSixty()
    {
        byte[] pdf = TestPdfs.MultiPage(1);

        // 270 + 180 = 450 -> 90
        var once = PageTools.Rotate(pdf, new[] { 1 }, 270);
        var twice = PageTools.Rotate(once.Pdf, new[] { 1 }, 180);

        Assert.Equal(90, PdfInspector.GetInfo(twice.Pdf).Pages[0].Rotation);
    }

    [Fact]
    public void Rotate_NegativeDelta_TurnsCounterClockwise()
    {
        byte[] pdf = TestPdfs.MultiPage(1);

        var result = PageTools.Rotate(pdf, new[] { 1 }, -90);

        Assert.Equal(270, PdfInspector.GetInfo(result.Pdf).Pages[0].Rotation);
    }

    [Fact]
    public void Rotate_NonMultipleOf90_Throws()
    {
        byte[] pdf = TestPdfs.MultiPage(1);
        Assert.Throws<ArgumentException>(() => PageTools.Rotate(pdf, new[] { 1 }, 45));
    }

    [Fact]
    public void Rotate_InvalidPage_Throws()
    {
        byte[] pdf = TestPdfs.MultiPage(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => PageTools.Rotate(pdf, new[] { 5 }, 90));
    }

    [Fact]
    public void Rotate_PreservesPageContent()
    {
        byte[] pdf = TestPdfs.MultiPage(2, "Confidential");

        var result = PageTools.Rotate(pdf, new[] { 1 }, 90);

        Assert.Contains("Confidential 1", TestPdfAssert.ExtractText(result.Pdf, 1));
    }

    [Fact]
    public void Arrange_ReordersPages_IntoTheGivenSequence()
    {
        byte[] pdf = TestPdfs.MultiPage(3);

        var result = PageTools.Arrange(pdf, new[] { 3, 1, 2 });

        Assert.Equal(3, PdfInspector.GetInfo(result.Pdf).PageCount);
        Assert.Contains("Page 3", TestPdfAssert.ExtractText(result.Pdf, 1));
        Assert.Contains("Page 1", TestPdfAssert.ExtractText(result.Pdf, 2));
        Assert.Contains("Page 2", TestPdfAssert.ExtractText(result.Pdf, 3));
    }

    [Fact]
    public void Arrange_OmittingAPage_DeletesIt()
    {
        byte[] pdf = TestPdfs.MultiPage(3);

        var result = PageTools.Arrange(pdf, new[] { 1, 3 });

        Assert.Equal(2, PdfInspector.GetInfo(result.Pdf).PageCount);
        Assert.Contains("Page 1", TestPdfAssert.ExtractText(result.Pdf, 1));
        Assert.Contains("Page 3", TestPdfAssert.ExtractText(result.Pdf, 2));
    }

    [Fact]
    public void Arrange_EmptyOrder_Throws()
    {
        byte[] pdf = TestPdfs.MultiPage(2);
        Assert.Throws<ArgumentException>(() => PageTools.Arrange(pdf, Array.Empty<int>()));
    }

    [Fact]
    public void Arrange_InvalidPage_Throws()
    {
        byte[] pdf = TestPdfs.MultiPage(2);
        Assert.Throws<ArgumentOutOfRangeException>(() => PageTools.Arrange(pdf, new[] { 1, 5 }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Arrange_ZeroOrNegativePage_Throws(int badPage)
    {
        byte[] pdf = TestPdfs.MultiPage(2);
        Assert.Throws<ArgumentOutOfRangeException>(() => PageTools.Arrange(pdf, new[] { 1, badPage }));
    }

    [Fact]
    public void Arrange_RepeatedPageNumber_DuplicatesThatPage()
    {
        byte[] pdf = TestPdfs.MultiPage(2);

        // Page 1 appears twice: the output has three pages, the first two both being page 1.
        var result = PageTools.Arrange(pdf, new[] { 1, 1, 2 });

        Assert.Equal(3, PdfInspector.GetInfo(result.Pdf).PageCount);
        Assert.Contains("Page 1", TestPdfAssert.ExtractText(result.Pdf, 1));
        Assert.Contains("Page 1", TestPdfAssert.ExtractText(result.Pdf, 2));
        Assert.Contains("Page 2", TestPdfAssert.ExtractText(result.Pdf, 3));
    }

    [Fact]
    public void Arrange_SinglePageKept_DropsTheRest()
    {
        byte[] pdf = TestPdfs.MultiPage(4);

        var result = PageTools.Arrange(pdf, new[] { 2 });

        Assert.Equal(1, PdfInspector.GetInfo(result.Pdf).PageCount);
        Assert.Contains("Page 2", TestPdfAssert.ExtractText(result.Pdf, 1));
    }

    [Fact]
    public void Arrange_FullReverse_PreservesEveryPage()
    {
        byte[] pdf = TestPdfs.MultiPage(3);

        var result = PageTools.Arrange(pdf, new[] { 3, 2, 1 });

        Assert.Equal(3, PdfInspector.GetInfo(result.Pdf).PageCount);
        Assert.Contains("Page 3", TestPdfAssert.ExtractText(result.Pdf, 1));
        Assert.Contains("Page 1", TestPdfAssert.ExtractText(result.Pdf, 3));
    }

    [Fact]
    public void Arrange_EncryptedSource_WorksWithPassword_AndStaysEncrypted()
    {
        byte[] locked = Encryptor.Encrypt(TestPdfs.MultiPage(3, "Secret"), "pw");

        var result = PageTools.Arrange(locked, new[] { 3, 1 }, "pw");

        // Rebuilt from the source's pages, but still the same protected document.
        Assert.True(Encryptor.IsEncrypted(result.Pdf));
        Assert.Contains("Secret 3", TestPdfAssert.ExtractText(result.Pdf, 1, "pw"));
        Assert.Contains("Secret 1", TestPdfAssert.ExtractText(result.Pdf, 2, "pw"));
    }

    [Fact]
    public void Arrange_WrongPasswordForEncryptedSource_Throws()
    {
        byte[] locked = Encryptor.Encrypt(TestPdfs.MultiPage(2), "correct");
        Assert.ThrowsAny<Exception>(() => PageTools.Arrange(locked, new[] { 1 }, "wrong"));
    }
}
