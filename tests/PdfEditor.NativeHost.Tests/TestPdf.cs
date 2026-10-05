using System.Text;
using PdfEditor.Core.Pdf;
using PdfEditor.Core.Pdf.Fonts;

namespace PdfEditor.NativeHost.Tests;

/// <summary>Tiny deterministic PDF fixtures for exercising the message dispatcher in-process.</summary>
internal static class TestPdf
{
    private const double Width = 595, Height = 842;

    public static byte[] OnePage(string text = "hello") => ManyPages(1, _ => text);

    public static byte[] ManyPages(int count) => ManyPages(count, i => $"Page {i}");

    private static byte[] ManyPages(int count, Func<int, string> text)
    {
        var doc = PdfDocument.CreateNew();
        var font = PdfFont.Standard(StandardFonts.Helvetica);
        var fontRef = doc.MakeIndirect(font.Dictionary!);
        for (int i = 0; i < count; i++)
        {
            var page = doc.AddNewPage(Width, Height);
            var name = PdfResources.Add(page.GetOrCreateResources(), PdfName.Font, "F", fontRef);
            page.AppendContent(new ContentBuilder().BeginText().Font(name, 14)
                .MoveText(72, 700).ShowText(font.Encode(text(i))).EndText().ToArray());
        }
        return doc.Save();
    }

    public static byte[] WithField(string name = "field1", string value = "")
    {
        var doc = PdfDocument.CreateNew();
        var page = doc.AddNewPage(Width, Height);
        var field = AcroForm.NewWidget(new PdfRect(100, 600, 200, 24), styled: false);
        field.Put(PdfName.FT, PdfName.Tx);
        field.Put(PdfName.DA, PdfString.FromText("/Helv 12 Tf 0 g"));
        var node = AcroForm.AddMergedField(doc, page, field, name);
        AcroForm.SetValue(doc, node, value);
        return doc.Save();
    }

    public static byte[] WithJavaScript()
    {
        var doc = PdfDocument.CreateNew();
        doc.AddNewPage(Width, Height);
        var action = new PdfDictionary();
        action.Put(PdfName.S, PdfName.JavaScript);
        action.Put(PdfName.JS, PdfString.FromText("app.alert('x');"));
        doc.Catalog!.Put(PdfName.OpenAction, action);
        return doc.Save();
    }

    public static byte[] WithLink(string url = "https://github.com/example/repo")
    {
        var doc = PdfDocument.CreateNew();
        var page = doc.AddNewPage(Width, Height);
        var action = new PdfDictionary();
        action.Put(PdfName.S, PdfName.URI);
        action.Put(PdfName.URI, new PdfString(Encoding.ASCII.GetBytes(url)));
        var link = new PdfDictionary();
        link.Put(PdfName.Type, PdfName.Annot);
        link.Put(PdfName.Subtype, PdfName.Link);
        link.Put(PdfName.Rect, new PdfRect(72, 700, 200, 20).ToArray());
        link.Put(PdfName.A, action);
        page.AddAnnotation(link);
        return doc.Save();
    }

    public static string Base64(byte[] pdf) => Convert.ToBase64String(pdf);
}
