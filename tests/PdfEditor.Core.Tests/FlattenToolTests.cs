using PdfEditor.Core.Pdf;
using PdfEditor.Core;
using Xunit;

namespace PdfEditor.Tests;

public class FlattenToolTests
{
    /// <summary>A page with a red square markup annotation that carries a normal appearance stream.</summary>
    private static byte[] WithSquareAnnotation(PdfRect rect)
    {
        var doc = PdfDocument.CreateNew();
        doc.AddNewPage(595, 842);
        AddSquare(doc, rect);
        return doc.Save();
    }

    private static void AddSquare(PdfDocument doc, PdfRect rect)
    {
        var ap = new PdfStream(new ContentBuilder().FillRgb(1, 0, 0).Rectangle(0, 0, rect.Width, rect.Height).Fill().ToArray());
        ap.Put(PdfName.Type, PdfName.XObject);
        ap.Put(PdfName.Subtype, PdfName.Form);
        ap.Put(PdfName.BBox, new PdfArray(0, 0, rect.Width, rect.Height));
        var apDict = new PdfDictionary();
        apDict.Put(PdfName.N, doc.MakeIndirect(ap));
        var annot = new PdfDictionary();
        annot.Put(PdfName.Type, PdfName.Annot);
        annot.Put(PdfName.Subtype, PdfName.Of("Square"));
        annot.Put(PdfName.Rect, rect.ToArray());
        annot.Put(PdfName.AP, apDict);
        annot.Put(PdfName.F, new PdfNumber(4)); // print
        doc.GetPage(1).AddAnnotation(annot);
    }

    private static int FormFieldCount(byte[] pdf) => AcroForm.AllNodes(PdfDocument.Open(pdf)).Count;

    private static int AnnotationCount(byte[] pdf) => PdfDocument.Open(pdf).GetPage(1).Annotations.Count;

    [Fact]
    public void Flatten_Forms_MakesFieldsStatic()
    {
        byte[] pdf = TestPdfs.WithTextField("name", "Jane Doe");
        Assert.Equal(1, FormFieldCount(pdf));

        var result = FlattenTool.Flatten(pdf, FlattenTool.Mode.Forms);

        Assert.Equal(1, result.FormFieldsFlattened);
        Assert.Equal(0, FormFieldCount(result.Pdf));
    }

    [Fact]
    public void Flatten_Forms_BuildsTheAppearanceOfAFieldThatHasNone()
    {
        // A field with a value but no /AP, relying on /NeedAppearances — as many form generators
        // write them. Flattening must still put the value on the page.
        byte[] pdf = Fuzz.RawPdf.Build(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] /DA (/Helv 0 Tf 0 g) /NeedAppearances true >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Annots [4 0 R] /Resources << /Font << /Helv 5 0 R >> >> >>",
            "<< /FT /Tx /T (fullName) /V (Ada Lovelace) /Type /Annot /Subtype /Widget /Rect [100 700 300 724] /P 3 0 R /DA (/Helv 12 Tf 0 g) >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        }.Select(o => System.Text.Encoding.ASCII.GetBytes(o)).ToList(), "/Root 1 0 R");

        var result = FlattenTool.Flatten(pdf, FlattenTool.Mode.Forms);

        Assert.Equal(1, result.FormFieldsFlattened);
        Assert.Equal(0, AnnotationCount(result.Pdf));
        Assert.Contains("Ada Lovelace", TestPdfAssert.ExtractText(result.Pdf));
        bool inked = Enumerable.Range(0, 40).Any(i => TestPdfAssert.PixelAt(result.Pdf, 1, 104 + i * 2, 711, 144).Red < 128);
        Assert.True(inked, "the flattened value is not drawn in the field's rectangle");
    }

    [Fact]
    public void Flatten_AnnotationsOnly_BakesTheAppearance_AndRemovesTheAnnotation()
    {
        byte[] pdf = WithSquareAnnotation(new PdfRect(100, 600, 80, 50));
        Assert.Equal(1, AnnotationCount(pdf));

        var result = FlattenTool.Flatten(pdf, FlattenTool.Mode.AnnotationsOnly);

        Assert.Equal(1, result.AnnotationsFlattened);
        Assert.Equal(0, AnnotationCount(result.Pdf));
        // The red appearance is now part of the page content.
        var px = TestPdfAssert.PixelAt(result.Pdf, 1, 140, 625, 150);
        Assert.True(px.Red > 180 && px.Green < 100 && px.Blue < 100, $"expected baked-in red, got {px}");
    }

    [Fact]
    public void Flatten_AnnotationsOnly_LeavesFormFieldsInteractive()
    {
        byte[] pdf = TestPdfs.WithTextField("name", "Jane");

        var result = FlattenTool.Flatten(pdf, FlattenTool.Mode.AnnotationsOnly);

        Assert.Equal(0, result.AnnotationsFlattened); // a widget is a form field, not a markup annotation
        Assert.Equal(1, FormFieldCount(result.Pdf));  // still fillable
    }

    [Fact]
    public void Flatten_Everything_FlattensFormsAndAnnotations()
    {
        // A document with both a form field and a markup annotation.
        byte[] withField = TestPdfs.WithTextField("name", "Jane");
        byte[] pdf = AddSquareAnnotationTo(withField, new PdfRect(300, 600, 80, 50));

        var result = FlattenTool.Flatten(pdf, FlattenTool.Mode.Everything);

        Assert.Equal(1, result.FormFieldsFlattened);
        Assert.Equal(1, result.AnnotationsFlattened);
        Assert.Equal(0, FormFieldCount(result.Pdf));
        Assert.Equal(0, AnnotationCount(result.Pdf));
    }

    [Fact]
    public void Flatten_LinkAnnotation_IsLeftAlone()
    {
        byte[] pdf = TestPdfs.WithLinkAnnotation(80, 500, 160, 20);

        var result = FlattenTool.Flatten(pdf, FlattenTool.Mode.AnnotationsOnly);

        Assert.Equal(0, result.AnnotationsFlattened); // links carry no bakeable appearance
        Assert.Equal(1, AnnotationCount(result.Pdf));
    }

    private static byte[] AddSquareAnnotationTo(byte[] pdf, PdfRect rect)
    {
        var doc = PdfDocument.Open(pdf);
        AddSquare(doc, rect);
        return doc.Save();
    }
}
