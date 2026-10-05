using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace PdfEditor.Tests.Fuzz;

/// <summary>How the cross-reference machinery of a generated file should be damaged.</summary>
internal enum XrefDamage
{
    /// <summary>A correct xref table and trailer.</summary>
    None,
    /// <summary>Every entry points at byte 0.</summary>
    ZeroOffsets,
    /// <summary>Every entry is off by a few bytes, so objects start mid-token.</summary>
    ShiftedOffsets,
    /// <summary>Every entry points past the end of the file.</summary>
    OffsetsPastEof,
    /// <summary><c>startxref</c> names an offset far beyond the file.</summary>
    GarbageStartxref,
    /// <summary>The xref table and trailer are omitted entirely.</summary>
    NoXref,
    /// <summary>The trailer dictionary is cut off mid-token.</summary>
    TruncatedTrailer,
    /// <summary>The trailer's <c>/Size</c> disagrees wildly with the object count.</summary>
    WrongSize,
}

/// <summary>
/// A byte-level PDF writer. Unlike <see cref="TestPdfs"/> (which drives the engine's writer and therefore
/// can only ever produce *valid* documents) this emits exactly the bytes it is told to, so a test can
/// declare a wrong <c>/Length</c>, a cyclic page tree, or a filter that does not match its payload.
/// Everything it produces is deterministic — no timestamps, no random object ids.
/// </summary>
internal static class RawPdf
{
    private const string Header = "%PDF-1.7\n%âãÏÓ\n";

    /// <summary>Encodes an object body written as text (Latin-1, i.e. one byte per char).</summary>
    public static byte[] Obj(string body) => Encoding.Latin1.GetBytes(body);

    /// <summary>
    /// Builds a stream object body: <c>&lt;&lt; /Length n <paramref name="dictEntries"/> &gt;&gt;</c>
    /// followed by the raw <paramref name="data"/>. <paramref name="declaredLength"/> overrides the
    /// <c>/Length</c> actually written, which is how the wrong-length cases are produced.
    /// </summary>
    public static byte[] StreamObj(string dictEntries, byte[] data, int? declaredLength = null,
        bool omitEndstream = false)
    {
        var buffer = new MemoryStream();
        Append(buffer, string.Create(CultureInfo.InvariantCulture,
            $"<< /Length {declaredLength ?? data.Length} {dictEntries} >>\nstream\n"));
        buffer.Write(data);
        Append(buffer, omitEndstream ? "\n" : "\nendstream");
        return buffer.ToArray();
    }

    /// <summary>
    /// Assembles <paramref name="objects"/> (object 1 first) into a complete file, appending the
    /// given <paramref name="trailerEntries"/> to the trailer dictionary and damaging the xref
    /// section as requested.
    /// </summary>
    public static byte[] Build(IReadOnlyList<byte[]> objects, string trailerEntries,
        XrefDamage damage = XrefDamage.None)
    {
        var file = new MemoryStream();
        Append(file, Header);

        var offsets = new List<long>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(file.Length);
            Append(file, string.Create(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n"));
            file.Write(objects[i]);
            Append(file, "\nendobj\n");
        }

        if (damage == XrefDamage.NoXref)
        {
            Append(file, "%%EOF\n");
            return file.ToArray();
        }

        long xrefOffset = file.Length;
        int size = objects.Count + 1;
        Append(file, string.Create(CultureInfo.InvariantCulture, $"xref\n0 {size}\n0000000000 65535 f \n"));
        foreach (long offset in offsets)
        {
            long written = damage switch
            {
                XrefDamage.ZeroOffsets => 0,
                XrefDamage.ShiftedOffsets => offset + 3,
                XrefDamage.OffsetsPastEof => offset + 900_000,
                _ => offset,
            };
            Append(file, written.ToString(CultureInfo.InvariantCulture).PadLeft(10, '0') + " 00000 n \n");
        }

        int declaredSize = damage == XrefDamage.WrongSize ? 999_999 : size;
        string trailer = string.Create(CultureInfo.InvariantCulture,
            $"trailer\n<< /Size {declaredSize} {trailerEntries} >>\n");
        if (damage == XrefDamage.TruncatedTrailer)
        {
            Append(file, "trailer\n<< /Size ");
            return file.ToArray();
        }

        Append(file, trailer);
        long startxref = damage == XrefDamage.GarbageStartxref ? xrefOffset + 500_000 : xrefOffset;
        Append(file, string.Create(CultureInfo.InvariantCulture, $"startxref\n{startxref}\n%%EOF\n"));
        return file.ToArray();
    }

    /// <summary>
    /// A one-page document whose content stream carries <paramref name="data"/> under
    /// <paramref name="filterEntries"/> (e.g. <c>"/Filter /FlateDecode"</c>). The rest of the
    /// document — catalog, page tree, font resource — is structurally valid, so anything that goes
    /// wrong is attributable to the stream.
    /// </summary>
    public static byte[] ContentStreamDoc(string filterEntries, byte[] data, int? declaredLength = null,
        bool omitEndstream = false, XrefDamage damage = XrefDamage.None)
        => Build(new[]
        {
            Obj("<< /Type /Catalog /Pages 2 0 R >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 600] " +
                "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>"),
            StreamObj(filterEntries, data, declaredLength, omitEndstream),
            Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
        }, "/Root 1 0 R", damage);

    /// <summary>
    /// A one-page document that draws a <paramref name="width"/>×<paramref name="height"/> image
    /// XObject whose bytes are <paramref name="data"/> under <paramref name="filterEntries"/> —
    /// the vehicle for the DCTDecode (JPEG) cases.
    /// </summary>
    public static byte[] ImageDoc(string filterEntries, byte[] data, int width, int height)
    {
        byte[] content = Encoding.ASCII.GetBytes("q 200 0 0 200 40 40 cm /Im1 Do Q\n");
        return Build(new[]
        {
            Obj("<< /Type /Catalog /Pages 2 0 R >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 600] " +
                "/Resources << /XObject << /Im1 5 0 R >> >> /Contents 4 0 R >>"),
            StreamObj("", content),
            StreamObj(string.Create(CultureInfo.InvariantCulture,
                $"/Type /XObject /Subtype /Image /Width {width} /Height {height} " +
                $"/ColorSpace /DeviceRGB /BitsPerComponent 8 {filterEntries}"), data),
        }, "/Root 1 0 R");
    }

    /// <summary>
    /// A valid <paramref name="pages"/>-page document, each page carrying its own uncompressed
    /// content stream. Byte-for-byte reproducible — unlike anything the engine's writer produces, which
    /// stamps a <c>/ModDate</c> (and, for a new document, a random <c>/ID</c>) into every file.
    /// </summary>
    public static byte[] MultiPageDoc(int pages)
    {
        var kids = new StringBuilder();
        for (int p = 0; p < pages; p++)
            kids.Append(CultureInfo.InvariantCulture, $"{3 + 2 * p} 0 R ");

        var objects = new List<byte[]>
        {
            Obj("<< /Type /Catalog /Pages 2 0 R >>"),
            Obj(string.Create(CultureInfo.InvariantCulture,
                $"<< /Type /Pages /Kids [{kids.ToString().TrimEnd()}] /Count {pages} >>")),
        };
        for (int p = 0; p < pages; p++)
        {
            objects.Add(Obj(string.Create(CultureInfo.InvariantCulture,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 600] " +
                $"/Resources << /Font << /F1 {3 + 2 * pages} 0 R >> >> /Contents {4 + 2 * p} 0 R >>")));
            objects.Add(StreamObj("", Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
                $"BT /F1 18 Tf 40 500 Td (Page {p + 1} payload) Tj ET\n0.2 0.4 0.9 rg 40 40 300 120 re f\n"))));
        }
        objects.Add(Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));
        return Build(objects, "/Root 1 0 R");
    }

    /// <summary>
    /// A page whose form XObjects are nested <paramref name="depth"/> levels deep, each drawing the
    /// next with <c>Do</c>. Acyclic, but arbitrarily deep — the other way to drive a recursive
    /// content processor off the end of the stack.
    /// </summary>
    public static byte[] DeeplyNestedForms(int depth)
    {
        byte[] draw = Encoding.ASCII.GetBytes("q /Nested Do Q\n");
        var objects = new List<byte[]>
        {
            Obj("<< /Type /Catalog /Pages 2 0 R >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 600] " +
                "/Resources << /XObject << /Nested 5 0 R >> >> /Contents 4 0 R >>"),
            StreamObj("", draw),
        };
        for (int level = 0; level < depth; level++)
        {
            bool last = level == depth - 1;
            string resources = last
                ? ""
                : string.Create(CultureInfo.InvariantCulture,
                    $"/Resources << /XObject << /Nested {6 + level} 0 R >> >>");
            objects.Add(StreamObj(
                $"/Type /XObject /Subtype /Form /BBox [0 0 100 100] {resources}",
                last ? Encoding.ASCII.GetBytes("0 0 1 rg 0 0 50 50 re f\n") : draw));
        }
        return Build(objects, "/Root 1 0 R");
    }

    /// <summary>zlib-compresses <paramref name="data"/> — the encoding FlateDecode expects.</summary>
    public static byte[] Deflate(byte[] data)
    {
        var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(data);
        return output.ToArray();
    }

    /// <summary>Encodes <paramref name="data"/> the way a RunLengthDecode stream expects.</summary>
    public static byte[] RunLength(byte[] data)
    {
        var output = new MemoryStream();
        int i = 0;
        while (i < data.Length)
        {
            int run = Math.Min(data.Length - i, 127);
            output.WriteByte((byte)(run - 1));
            output.Write(data, i, run);
            i += run;
        }
        output.WriteByte(128); // EOD
        return output.ToArray();
    }

    /// <summary>Encodes <paramref name="data"/> as ASCIIHexDecode input, EOD marker included.</summary>
    public static byte[] AsciiHex(byte[] data)
        => Encoding.ASCII.GetBytes(Convert.ToHexString(data) + ">");

    /// <summary>Encodes <paramref name="data"/> as ASCII85Decode input, EOD marker included.</summary>
    public static byte[] Ascii85(byte[] data)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < data.Length; i += 4)
        {
            int remaining = Math.Min(4, data.Length - i);
            uint block = 0;
            for (int j = 0; j < 4; j++)
                block = (block << 8) | (j < remaining ? data[i + j] : 0u);
            if (block == 0 && remaining == 4) { sb.Append('z'); continue; }
            var group = new char[5];
            for (int j = 4; j >= 0; j--) { group[j] = (char)('!' + (int)(block % 85)); block /= 85; }
            sb.Append(group, 0, remaining + 1);
        }
        return Encoding.ASCII.GetBytes(sb.Append("~>").ToString());
    }

    private static void Append(MemoryStream stream, string text)
        => stream.Write(Encoding.Latin1.GetBytes(text));
}
