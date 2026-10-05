using System.Globalization;
using System.Text;
using PdfEditor.Core.Pdf;

namespace PdfEditor.Tests;

/// <summary>
/// Builds deliberately malformed PDFs, byte for byte, so the export validator can be proven to
/// actually flag each class of defect (a validator that says "valid" for everything is worse
/// than none). Every fixture is generated in-repo — nothing is downloaded.
/// <para>
/// The documents are written by hand rather than through the engine because a writer refuses to emit a
/// broken cross-reference table, a wrong <c>/Length</c>, or undecodable stream data: the whole
/// point of these fixtures is the corruption a writer would never produce.
/// </para>
/// </summary>
public static class CorruptPdfs
{
    /// <summary>How the raw writer should deviate from a well-formed file.</summary>
    /// <param name="Header">First line of the file (a valid file starts with <c>%PDF-</c>).</param>
    /// <param name="IncludeEof">When false the trailing <c>%%EOF</c> marker is omitted.</param>
    /// <param name="StartXrefOverride">Replaces the byte offset written after <c>startxref</c>.</param>
    /// <param name="ShiftXrefEntryForObject">
    /// Object number whose cross-reference entry is written pointing off target, by
    /// <paramref name="ShiftBy"/> bytes.
    /// </param>
    /// <param name="ShiftBy">How far off target that entry points.</param>
    /// <param name="IncludeStartXref">When false the <c>startxref</c> keyword is omitted.</param>
    /// <param name="SubsectionHeader">Overrides the first cross-reference subsection header.</param>
    /// <param name="Trailer">Overrides the whole trailer dictionary.</param>
    public sealed record Options(
        string Header = "%PDF-1.7",
        bool IncludeEof = true,
        long? StartXrefOverride = null,
        int? ShiftXrefEntryForObject = null,
        long ShiftBy = 7,
        bool IncludeStartXref = true,
        string? SubsectionHeader = null,
        string? Trailer = null);

    /// <summary>An object body written verbatim between <c>N 0 obj</c> and <c>endobj</c>.</summary>
    public static byte[] Obj(string body) => Encoding.Latin1.GetBytes(body);

    /// <summary>
    /// A stream object whose dictionary is written verbatim — so a test can declare a
    /// <c>/Length</c> or <c>/Filter</c> that does not match <paramref name="data"/>.
    /// </summary>
    public static byte[] StreamObj(string dictionary, byte[] data)
    {
        using var buffer = new MemoryStream();
        buffer.Write(Encoding.Latin1.GetBytes(dictionary + "\nstream\n"));
        buffer.Write(data);
        buffer.Write(Encoding.Latin1.GetBytes("\nendstream"));
        return buffer.ToArray();
    }

    /// <summary>
    /// Assembles numbered objects into a classic (table-based) PDF, applying the requested
    /// corruption. Object <c>i + 1</c> is <c>objects[i]</c>.
    /// </summary>
    public static byte[] Build(IReadOnlyList<byte[]> objects, Options? options = null)
    {
        var o = options ?? new Options();
        using var buffer = new MemoryStream();
        void Write(string s) => buffer.Write(Encoding.Latin1.GetBytes(s));

        Write(o.Header + "\n%âãÏÓ\n");
        var offsets = new long[objects.Count + 1];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i + 1] = buffer.Length;
            Write($"{i + 1} 0 obj\n");
            buffer.Write(objects[i]);
            Write("\nendobj\n");
        }

        long xref = buffer.Length;
        Write("xref\n" + (o.SubsectionHeader ?? $"0 {objects.Count + 1}") + "\n0000000000 65535 f \n");
        for (int i = 1; i <= objects.Count; i++)
        {
            long offset = offsets[i];
            // Shifted entries land somewhere other than the object's "N 0 obj" header.
            if (o.ShiftXrefEntryForObject == i) offset += o.ShiftBy;
            Write(offset.ToString(CultureInfo.InvariantCulture).PadLeft(10, '0') + " 00000 n \n");
        }
        Write("trailer\n" + (o.Trailer ?? $"<< /Size {objects.Count + 1} /Root 1 0 R >>") + "\n");
        if (o.IncludeStartXref)
            Write($"startxref\n{(o.StartXrefOverride ?? xref).ToString(CultureInfo.InvariantCulture)}\n");
        if (o.IncludeEof) Write("%%EOF\n");
        return buffer.ToArray();
    }

    private static readonly byte[] Content =
        Encoding.Latin1.GetBytes("BT /F1 12 Tf 20 100 Td (Hello) Tj ET");

    /// <summary>
    /// The baseline objects of a small, entirely well-formed one-page document. Callers replace
    /// individual entries to introduce exactly one defect.
    /// </summary>
    public static List<byte[]> BaselineObjects() =>
    [
        Obj("<< /Type /Catalog /Pages 2 0 R >>"),
        Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] "
            + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>"),
        StreamObj($"<< /Length {Content.Length} >>", Content),
        Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
    ];

    /// <summary>A hand-written but entirely well-formed document — the control fixture.</summary>
    public static byte[] WellFormed() => Build(BaselineObjects());

    /// <summary>The file does not start with the <c>%PDF-</c> signature.</summary>
    public static byte[] MissingHeader() =>
        Build(BaselineObjects(), new Options(Header: "%XDF-1.7"));

    /// <summary>The trailing <c>%%EOF</c> marker is missing (a truncated write).</summary>
    public static byte[] MissingEof() =>
        Build(BaselineObjects(), new Options(IncludeEof: false));

    /// <summary><c>startxref</c> points past the end of the file.</summary>
    public static byte[] StartXrefOutOfRange() =>
        Build(BaselineObjects(), new Options(StartXrefOverride: 9_000_000));

    /// <summary><c>startxref</c> points at bytes that are not a cross-reference section.</summary>
    public static byte[] StartXrefNotAnXref() =>
        Build(BaselineObjects(), new Options(StartXrefOverride: 12));

    /// <summary>There is no <c>startxref</c> keyword at all.</summary>
    public static byte[] MissingStartXref() =>
        Build(BaselineObjects(), new Options(IncludeStartXref: false));

    /// <summary>The <c>startxref</c> keyword is not followed by a number.</summary>
    public static byte[] StartXrefWithoutOffset()
    {
        byte[] pdf = Build(BaselineObjects(), new Options(IncludeStartXref: false));
        return [.. pdf, .. Encoding.Latin1.GetBytes("startxref\n%%EOF\n")];
    }

    /// <summary>The cross-reference table's subsection header is not two numbers.</summary>
    public static byte[] MalformedXrefSubsection() =>
        Build(BaselineObjects(), new Options(SubsectionHeader: "zero five"));

    /// <summary>One cross-reference entry points a few bytes away from its object header.</summary>
    public static byte[] ShiftedXrefEntry() =>
        Build(BaselineObjects(), new Options(ShiftXrefEntryForObject: 3));

    /// <summary>One cross-reference entry points past the end of the file.</summary>
    public static byte[] XrefEntryPastEndOfFile() =>
        Build(BaselineObjects(), new Options(ShiftXrefEntryForObject: 3, ShiftBy: 5_000_000));

    /// <summary>Junk bytes precede the <c>%PDF-</c> signature.</summary>
    public static byte[] HeaderNotAtStart() =>
        Build(BaselineObjects(), new Options(Header: "junk before the signature\n%PDF-1.7"));

    /// <summary>The content stream declares a <c>/Length</c> shorter than the data it holds.</summary>
    public static byte[] WrongStreamLength()
    {
        var objects = BaselineObjects();
        objects[3] = StreamObj("<< /Length 5 >>", Content);
        return Build(objects);
    }

    /// <summary>The content stream is never closed by an <c>endstream</c> keyword.</summary>
    public static byte[] UnterminatedStream()
    {
        var objects = BaselineObjects();
        objects[3] = Obj($"<< /Length {Content.Length} >>\nstream\n" + Encoding.Latin1.GetString(Content));
        return Build(objects);
    }

    /// <summary>
    /// A well-formed document whose stream length is an indirect reference — legal, and the case
    /// the byte-level length check has to skip rather than mis-report.
    /// </summary>
    public static byte[] IndirectStreamLength()
    {
        var objects = BaselineObjects();
        objects[3] = StreamObj("<< /Length 6 0 R >>", Content);
        objects.Add(Obj(Content.Length.ToString(CultureInfo.InvariantCulture)));
        return Build(objects);
    }

    /// <summary>
    /// A well-formed document whose content stream is hex-encoded through a filter <em>array</em>
    /// — the array form of /Filter must validate as cleanly as the single-name form.
    /// </summary>
    public static byte[] HexEncodedContentStream()
    {
        var objects = BaselineObjects();
        byte[] hex = Encoding.Latin1.GetBytes(Convert.ToHexString(Content) + ">");
        objects[3] = StreamObj($"<< /Length {hex.Length} /Filter [/ASCIIHexDecode] >>", hex);
        return Build(objects);
    }

    /// <summary>The content stream claims to be Flate-compressed but holds plain text.</summary>
    public static byte[] UndecodableStream()
    {
        var objects = BaselineObjects();
        objects[3] = StreamObj($"<< /Length {Content.Length} /Filter /FlateDecode >>", Content);
        return Build(objects);
    }

    /// <summary>The content stream names a filter that does not exist in the PDF specification.</summary>
    public static byte[] UnknownStreamFilter()
    {
        var objects = BaselineObjects();
        objects[3] = StreamObj($"<< /Length {Content.Length} /Filter /SuperDecode >>", Content);
        return Build(objects);
    }

    /// <summary>The page's <c>/MediaBox</c> has zero area, so the page has no renderable surface.</summary>
    public static byte[] DegenerateMediaBox()
    {
        var objects = BaselineObjects();
        objects[2] = Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 0] "
            + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>");
        return Build(objects);
    }

    /// <summary>The page's <c>/MediaBox</c> does not have four entries.</summary>
    public static byte[] MediaBoxWithThreeNumbers()
    {
        var objects = BaselineObjects();
        objects[2] = Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200] "
            + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>");
        return Build(objects);
    }

    /// <summary>
    /// A well-formed PDF 1.5+ document that stores its objects in object streams behind a
    /// cross-reference <em>stream</em> instead of a classic table — the compact layout many
    /// producers use, which the byte-level checks must not mistake for corruption.
    /// </summary>
    public static byte[] FullyCompressed(byte[] source) =>
        PdfDocument.Open(source).Save(new PdfSaveOptions { ObjectStreams = true });

    /// <summary>Neither the page nor any ancestor carries a <c>/MediaBox</c>.</summary>
    public static byte[] MissingMediaBox()
    {
        var objects = BaselineObjects();
        objects[2] = Obj("<< /Type /Page /Parent 2 0 R "
            + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>");
        return Build(objects);
    }

    /// <summary>The page dictionary omits its required <c>/Type /Page</c> entry.</summary>
    public static byte[] PageWithoutType()
    {
        var objects = BaselineObjects();
        objects[2] = Obj("<< /Parent 2 0 R /MediaBox [0 0 200 200] "
            + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>");
        return Build(objects);
    }

    /// <summary>The page tree's <c>/Count</c> disagrees with the number of pages it actually holds.</summary>
    public static byte[] PageCountMismatch()
    {
        var objects = BaselineObjects();
        objects[1] = Obj("<< /Type /Pages /Kids [3 0 R] /Count 4 >>");
        return Build(objects);
    }

    /// <summary>The page tree is empty: the export dropped every page.</summary>
    public static byte[] NoPages()
    {
        var objects = BaselineObjects();
        objects[1] = Obj("<< /Type /Pages /Kids [] /Count 0 >>");
        return Build(objects);
    }

    /// <summary>The document catalog has no <c>/Pages</c> entry at all.</summary>
    public static byte[] CatalogWithoutPageTree()
    {
        var objects = BaselineObjects();
        objects[0] = Obj("<< /Type /Catalog >>");
        return Build(objects);
    }

    /// <summary>The trailer's <c>/Root</c> resolves to a font, not a document catalog.</summary>
    public static byte[] RootIsNotACatalog() =>
        Build(BaselineObjects(), new Options(Trailer: "<< /Size 6 /Root 5 0 R >>"));

    /// <summary>Bytes that are not a PDF at all — the parser cannot open them.</summary>
    public static byte[] NotAPdf() => Encoding.Latin1.GetBytes("this is not a PDF file at all\n");

    /// <summary>
    /// A one-page document with a text field whose widget has had its normal appearance stream
    /// (<c>/AP /N</c>) removed, without the document opting into <c>/NeedAppearances</c> — the
    /// field renders blank in viewers that do not regenerate appearances themselves.
    /// </summary>
    public static byte[] FormFieldWithoutAppearance(byte[] source)
    {
        var doc = PdfDocument.Open(source);
        foreach (var field in AcroForm.TerminalFields(doc))
            foreach (var widget in field.Widgets)
                widget.Remove(PdfName.AP);
        return doc.Save();
    }

    /// <summary>
    /// A one-page document whose form-field widget is no longer listed in any page's
    /// <c>/Annots</c> array, so the field exists but can never be clicked.
    /// </summary>
    public static byte[] FormFieldOrphanedFromPage(byte[] source)
    {
        var doc = PdfDocument.Open(source);
        foreach (var page in doc.Pages) page.Dictionary.Remove(PdfName.Annots);
        return doc.Save();
    }
}
