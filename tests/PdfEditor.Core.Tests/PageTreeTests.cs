using System.Text;
using PdfEditor.Core.Pdf;
using Xunit;

namespace PdfEditor.Tests;

public class PageTreeTests
{
    [Fact]
    public void AddingAPage_GivesEachExistingPageItsOwnCopyOfInheritedBoxesAndResources()
    {
        // Two pages inheriting a direct /MediaBox and /Resources from their /Pages node.
        byte[] pdf = Fuzz.RawPdf.Build(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /MediaBox [0 0 400 600] /Resources << /Font << >> >> >>",
            "<< /Type /Page /Parent 2 0 R >>",
            "<< /Type /Page /Parent 2 0 R >>",
        }.Select(o => Encoding.ASCII.GetBytes(o)).ToList(), "/Root 1 0 R");
        var doc = PdfDocument.Open(pdf);

        doc.AddNewPage(595, 842);
        var first = doc.GetPage(1);
        var second = doc.GetPage(2);
        first.Dictionary.GetAsArray(PdfName.MediaBox)!.Set(2, new PdfNumber(500));
        first.GetOrCreateResources().GetAsDictionary(PdfName.Font)!.Put(PdfName.Of("F9"), new PdfDictionary());

        Assert.Equal(400, second.MediaBox.Width);
        Assert.False(second.Resources!.GetAsDictionary(PdfName.Font)!.ContainsKey(PdfName.Of("F9")));
        Assert.Equal(new PdfRect(0, 0, 595, 842), doc.GetPage(3).MediaBox);
    }
}
