using System.Globalization;
using System.Text;
using PdfEditor.Tests.Fuzz;

namespace PdfEditor.Tests.Golden;

/// <summary>
/// The golden-file corpus (issue #53): documents whose structure is awkward in the ways real
/// production PDFs are awkward, rather than the tidy ones <see cref="TestPdfs"/> builds through
/// the engine's high-level writer.
/// <para>
/// Every fixture is generated in-repo — nothing is downloaded, and nothing carries a licence.
/// They are written byte by byte through <see cref="RawPdf"/> because the features that matter
/// here (Type 3 glyph procedures, Identity-H composite fonts, <c>/ToUnicode</c> CMaps, luminosity
/// soft masks, isolated/knockout transparency groups) either cannot be produced through a
/// high-level API at all or come out normalised into something less interesting. Writing the bytes
/// also makes the corpus bit-for-bit reproducible, which
/// <see cref="GoldenRegressionTests.Corpus_IsBitForBitReproducible"/> pins.
/// </para>
/// <para>
/// The corpus deliberately does <em>not</em> re-cover ground the Tier 2 siblings already hold:
/// malformed streams and damaged cross-reference tables live in
/// <see cref="CorruptPdfs"/> (#52) and <see cref="FuzzCorpus"/> (#54), and this suite pulls a few
/// of those in by reference instead of rewriting them. The gaps it fills are complex fonts and
/// transparency.
/// </para>
/// </summary>
internal static class GoldenPdfs
{
    /// <summary>
    /// One corpus document. <paramref name="WellFormed"/> is false for the fixtures that are
    /// broken on purpose: an operation over those is allowed to fail or to produce a document that
    /// still carries findings, so the suite records what happens rather than demanding it be clean.
    /// </summary>
    /// <param name="WellFormed">
    /// False for fixtures that are broken on purpose: an operation over one of those is allowed to
    /// fail, or to produce a document the validator still complains about.
    /// </param>
    /// <param name="Reproducible">
    /// False for the fixtures the engine's writer produces, whose bytes carry a random <c>/ID</c>
    /// and a <c>/ModDate</c> and therefore differ between two identical runs.
    /// </param>
    internal sealed record GoldenDoc(
        string Name, byte[] Bytes, bool WellFormed, bool Reproducible, string Description);

    // ------------------------------------------------------------------ shared object fragments

    private const string GrayText = "0 g\nBT /F1 10 Tf 40 560 Td (golden corpus) Tj ET\n";

    private const string HelveticaFont = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>";

    private static byte[] Content(string text) => Encoding.Latin1.GetBytes(text);

    /// <summary>A <c>/ToUnicode</c> CMap mapping consecutive 2-byte codes from 1 upward to <paramref name="text"/>.</summary>
    private static string ToUnicodeCMap(string text)
    {
        var mappings = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
            mappings.Append(CultureInfo.InvariantCulture, $"<{i + 1:X4}> <{(int)text[i]:X4}>\n");

        return "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n"
            + "/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n"
            + "/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n"
            + "1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n"
            + text.Length.ToString(CultureInfo.InvariantCulture) + " beginbfchar\n"
            + mappings
            + "endbfchar\nendcmap\nCMapName currentdict /CMap defineresource pop\nend\nend";
    }

    // ------------------------------------------------------------------ complex fonts

    /// <summary>
    /// A Type 3 font: every glyph is its own content stream (<c>/CharProcs</c>), positioned by a
    /// non-default <c>/FontMatrix</c> and reached through an <c>/Encoding /Differences</c> array.
    /// There is no font program at all, so anything that assumes glyphs come from an embedded
    /// TrueType/Type1 program — width lookup, text extraction, redaction's glyph-box arithmetic —
    /// has to cope with a font whose "program" is PDF drawing operators.
    /// </summary>
    public static byte[] Type3GlyphProcedures() => RawPdf.Build(new[]
    {
        RawPdf.Obj("<< /Type /Catalog /Pages 2 0 R >>"),
        RawPdf.Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        RawPdf.Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 600] "
            + "/Resources << /Font << /T3 5 0 R /F1 9 0 R >> >> /Contents 4 0 R >>"),
        RawPdf.StreamObj("", Content(
            "BT /T3 36 Tf 40 480 Td (abab) Tj ET\n" + GrayText)),
        RawPdf.Obj("<< /Type /Font /Subtype /Type3 /FontBBox [0 0 750 750] "
            + "/FontMatrix [0.001 0 0 0.001 0 0] /CharProcs 6 0 R "
            + "/Encoding << /Type /Encoding /Differences [97 /square 98 /triangle] >> "
            + "/FirstChar 97 /LastChar 98 /Widths [750 750] /Resources << >> >>"),
        RawPdf.Obj("<< /square 7 0 R /triangle 8 0 R >>"),
        RawPdf.StreamObj("", Content("750 0 0 0 750 750 d1\n0 0 750 750 re f\n")),
        RawPdf.StreamObj("", Content("750 0 0 0 750 750 d1\n0 0 m 750 0 l 375 750 l f\n")),
        RawPdf.Obj(HelveticaFont),
    }, "/Root 1 0 R");

    /// <summary>
    /// A composite (Type 0) font with <c>Identity-H</c> encoding: the content stream shows
    /// two-byte CIDs, not characters, so the only route from bytes to text is the
    /// <c>/ToUnicode</c> CMap. This is what every modern subsetted font in a real document looks
    /// like, and it is the case where naive text extraction returns mojibake and naive redaction
    /// measures the wrong boxes.
    /// </summary>
    public static byte[] Type0IdentityHWithToUnicode()
    {
        byte[] cmap = Content(ToUnicodeCMap("GOLDEN"));
        return RawPdf.Build(new[]
        {
            RawPdf.Obj("<< /Type /Catalog /Pages 2 0 R >>"),
            RawPdf.Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            RawPdf.Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 600] "
                + "/Resources << /Font << /C0 5 0 R /F1 9 0 R >> >> /Contents 4 0 R >>"),
            RawPdf.StreamObj("", Content(
                "BT /C0 24 Tf 40 480 Td <000100020003000400050006> Tj ET\n" + GrayText)),
            RawPdf.Obj("<< /Type /Font /Subtype /Type0 /BaseFont /AAAAAA+DejaVuSans "
                + "/Encoding /Identity-H /DescendantFonts [6 0 R] /ToUnicode 8 0 R >>"),
            RawPdf.Obj("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /AAAAAA+DejaVuSans "
                + "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> "
                + "/FontDescriptor 7 0 R /DW 1000 /W [1 [684 787 615 748 651 774]] "
                + "/CIDToGIDMap /Identity >>"),
            RawPdf.Obj("<< /Type /FontDescriptor /FontName /AAAAAA+DejaVuSans /Flags 4 "
                + "/FontBBox [-1021 -463 1793 1232] /ItalicAngle 0 /Ascent 928 /Descent -236 "
                + "/CapHeight 700 /StemV 80 >>"),
            RawPdf.StreamObj("", cmap),
            RawPdf.Obj(HelveticaFont),
        }, "/Root 1 0 R");
    }

    /// <summary>
    /// A simple font whose <c>/Encoding /Differences</c> array remaps codes onto unrelated glyph
    /// names, so the literal bytes in the content stream (<c>ABC</c>) are not the text the page
    /// shows (<c>ZYX</c>). Anything that searches or redacts by matching the raw string operand
    /// instead of the decoded text gets this wrong.
    /// </summary>
    public static byte[] DifferencesEncoding() => RawPdf.Build(new[]
    {
        RawPdf.Obj("<< /Type /Catalog /Pages 2 0 R >>"),
        RawPdf.Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        RawPdf.Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 600] "
            + "/Resources << /Font << /D1 5 0 R /F1 6 0 R >> >> /Contents 4 0 R >>"),
        RawPdf.StreamObj("", Content("BT /D1 28 Tf 40 480 Td (ABC) Tj ET\n" + GrayText)),
        RawPdf.Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /FirstChar 65 /LastChar 67 "
            + "/Widths [722 667 611] "
            + "/Encoding << /Type /Encoding /BaseEncoding /WinAnsiEncoding "
            + "/Differences [65 /Z /Y /X] >> >>"),
        RawPdf.Obj(HelveticaFont),
    }, "/Root 1 0 R");

    /// <summary>
    /// A TrueType font whose embedded program (<c>/FontFile2</c>) is Flate-compressed rubbish: the
    /// dictionary, widths and encoding are all correct, so the document is perfectly openable, but
    /// any code that actually tries to parse the font program has to survive it. Subsetted fonts
    /// truncated by a broken exporter look exactly like this in the wild.
    /// </summary>
    public static byte[] BrokenEmbeddedFontProgram()
    {
        // Deterministic pseudo-random bytes: a plausible font-program length, no valid sfnt header.
        var junk = new byte[512];
        for (int i = 0; i < junk.Length; i++) junk[i] = (byte)(i * 37 + 11);

        return RawPdf.Build(new[]
        {
            RawPdf.Obj("<< /Type /Catalog /Pages 2 0 R >>"),
            RawPdf.Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            RawPdf.Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 600] "
                + "/Resources << /Font << /T1 5 0 R /F1 8 0 R >> >> /Contents 4 0 R >>"),
            RawPdf.StreamObj("", Content("BT /T1 20 Tf 40 480 Td (subset) Tj ET\n" + GrayText)),
            RawPdf.Obj("<< /Type /Font /Subtype /TrueType /BaseFont /BCDEEE+Calibri "
                + "/FirstChar 98 /LastChar 117 /Widths "
                + "[525 0 0 498 0 0 0 0 0 0 0 0 0 0 0 0 498 525 0 0 525 0 0] "
                + "/Encoding /WinAnsiEncoding /FontDescriptor 6 0 R >>"),
            RawPdf.Obj("<< /Type /FontDescriptor /FontName /BCDEEE+Calibri /Flags 32 "
                + "/FontBBox [-503 -313 1240 1026] /ItalicAngle 0 /Ascent 750 /Descent -250 "
                + "/CapHeight 632 /StemV 80 /FontFile2 7 0 R >>"),
            RawPdf.StreamObj("/Filter /FlateDecode /Length1 2048", RawPdf.Deflate(junk)),
            RawPdf.Obj(HelveticaFont),
        }, "/Root 1 0 R");
    }

    /// <summary>
    /// Text drawn with <c>Tf</c> naming a font that is not in the page's <c>/Resources</c> at all.
    /// Real exporters produce this when they drop a resource during a page split; the page still
    /// has to render, and the missing font must not become an unhandled crash.
    /// </summary>
    public static byte[] TextWithMissingFontResource() => RawPdf.Build(new[]
    {
        RawPdf.Obj("<< /Type /Catalog /Pages 2 0 R >>"),
        RawPdf.Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        RawPdf.Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 600] "
            + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>"),
        RawPdf.StreamObj("", Content(
            "BT /Missing 18 Tf 40 480 Td (orphaned font) Tj ET\n" + GrayText)),
        RawPdf.Obj(HelveticaFont),
    }, "/Root 1 0 R");

    // ------------------------------------------------------------------ transparency

    /// <summary>
    /// Constant alpha (<c>/ca</c>, <c>/CA</c>) and a non-Normal blend mode applied through
    /// <c>/ExtGState</c>, over two overlapping filled rectangles — the minimum shape of "this page
    /// has transparency". A rewrite that drops the <c>/ExtGState</c> resource or the <c>gs</c>
    /// operator changes what the page looks like without changing anything a structural check would
    /// notice, which is precisely what a golden projection is for.
    /// </summary>
    public static byte[] ConstantAlphaAndBlendMode() => RawPdf.Build(new[]
    {
        RawPdf.Obj("<< /Type /Catalog /Pages 2 0 R >>"),
        RawPdf.Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        RawPdf.Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 600] /Resources << "
            + "/Font << /F1 5 0 R >> /ExtGState << "
            + "/GSa << /Type /ExtGState /ca 0.4 /CA 0.6 /BM /Multiply >> "
            + "/GSb << /Type /ExtGState /ca 0.75 /BM /Screen /AIS false >> "
            + ">> >> /Contents 4 0 R >>"),
        RawPdf.StreamObj("", Content(
            "q /GSa gs 1 0 0 rg 40 300 200 160 re f Q\n"
            + "q /GSb gs 0 0 1 rg 140 360 200 160 re f Q\n"
            + GrayText)),
        RawPdf.Obj(HelveticaFont),
    }, "/Root 1 0 R");

    /// <summary>
    /// A luminosity soft mask: the <c>/ExtGState</c>'s <c>/SMask</c> names a form XObject whose own
    /// transparency group supplies the mask's luminosity. Two indirections deep, and the mask form
    /// is referenced from a graphics state rather than drawn with <c>Do</c> — so anything that
    /// walks only the <c>/XObject</c> resource dictionary will not see it, and anything that
    /// rewrites resources can orphan it.
    /// </summary>
    public static byte[] LuminositySoftMask() => RawPdf.Build(new[]
    {
        RawPdf.Obj("<< /Type /Catalog /Pages 2 0 R >>"),
        RawPdf.Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        RawPdf.Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 600] /Resources << "
            + "/Font << /F1 5 0 R >> /ExtGState << /GSm << /Type /ExtGState "
            + "/SMask << /Type /Mask /S /Luminosity /G 6 0 R /BC [0] >> >> >> "
            + ">> /Contents 4 0 R >>"),
        RawPdf.StreamObj("", Content(
            "q /GSm gs 0.1 0.5 0.9 rg 40 300 300 200 re f Q\n" + GrayText)),
        RawPdf.Obj(HelveticaFont),
        RawPdf.StreamObj(
            "/Type /XObject /Subtype /Form /BBox [40 300 340 500] "
            + "/Group << /Type /Group /S /Transparency /CS /DeviceGray /I true >>",
            Content("0.2 g 40 300 150 200 re f\n1 g 190 300 150 200 re f\n")),
    }, "/Root 1 0 R");

    /// <summary>
    /// An image XObject carrying a separate alpha channel as an <c>/SMask</c> image — the shape
    /// every PNG-with-transparency takes once it is in a PDF. The mask is an image in its own
    /// right, so a pixel-level operation (redaction's scrubber, downsampling) has to handle two
    /// images that must stay the same size as each other.
    /// </summary>
    public static byte[] ImageWithAlphaSoftMask()
    {
        const int w = 8, h = 8;
        var rgb = new byte[w * h * 3];
        var alpha = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                rgb[i * 3] = (byte)(x * 32);
                rgb[i * 3 + 1] = (byte)(y * 32);
                rgb[i * 3 + 2] = 128;
                alpha[i] = (byte)(x >= y ? 255 : 0);   // a hard diagonal cut-out
            }

        return RawPdf.Build(new[]
        {
            RawPdf.Obj("<< /Type /Catalog /Pages 2 0 R >>"),
            RawPdf.Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            RawPdf.Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 600] /Resources << "
                + "/Font << /F1 5 0 R >> /XObject << /Im1 6 0 R >> >> /Contents 4 0 R >>"),
            RawPdf.StreamObj("", Content(
                "q 240 0 0 240 40 280 cm /Im1 Do Q\n" + GrayText)),
            RawPdf.Obj(HelveticaFont),
            RawPdf.StreamObj(string.Create(CultureInfo.InvariantCulture,
                $"/Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /SMask 7 0 R"),
                RawPdf.Deflate(rgb)),
            RawPdf.StreamObj(string.Create(CultureInfo.InvariantCulture,
                $"/Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode"),
                RawPdf.Deflate(alpha)),
        }, "/Root 1 0 R");
    }

    /// <summary>
    /// Transparency groups nested three deep, each with its own alpha and blend mode and the
    /// innermost one isolated <em>and</em> knocked out (<c>/I true /K true</c>) — the combination
    /// that Illustrator and InDesign emit and that most rewriters flatten incorrectly. This is also
    /// the corpus's nested-XObject case: the same recursion the fuzz suite drives to 200 levels,
    /// but at a depth a real document actually uses and with state that has to survive the walk.
    /// </summary>
    public static byte[] NestedTransparencyGroups() => RawPdf.Build(new[]
    {
        RawPdf.Obj("<< /Type /Catalog /Pages 2 0 R >>"),
        RawPdf.Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        RawPdf.Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 600] /Resources << "
            + "/Font << /F1 5 0 R >> /XObject << /Fm1 6 0 R >> "
            + "/ExtGState << /GS1 << /Type /ExtGState /ca 0.8 /BM /Normal >> >> "
            + ">> /Contents 4 0 R >>"),
        RawPdf.StreamObj("", Content(
            "q /GS1 gs 1 0 0 1 40 280 cm /Fm1 Do Q\n" + GrayText)),
        RawPdf.Obj(HelveticaFont),
        RawPdf.StreamObj(
            "/Type /XObject /Subtype /Form /BBox [0 0 300 240] "
            + "/Group << /Type /Group /S /Transparency /CS /DeviceRGB /I false /K false >> "
            + "/Resources << /XObject << /Fm2 7 0 R >> "
            + "/ExtGState << /GS2 << /Type /ExtGState /ca 0.5 /BM /Multiply >> >> >>",
            Content("0 0.6 0.2 rg 0 0 300 240 re f\nq /GS2 gs 1 0 0 1 30 30 cm /Fm2 Do Q\n")),
        RawPdf.StreamObj(
            "/Type /XObject /Subtype /Form /BBox [0 0 200 160] "
            + "/Group << /Type /Group /S /Transparency /CS /DeviceRGB /I true /K true >> "
            + "/Resources << /XObject << /Fm3 8 0 R >> "
            + "/ExtGState << /GS3 << /Type /ExtGState /ca 0.35 /BM /Darken >> >> "
            + "/Font << /F1 5 0 R >> >>",
            Content("0.9 0.1 0.1 rg 0 0 200 160 re f\n"
                + "q /GS3 gs 1 0 0 1 20 20 cm /Fm3 Do Q\n"
                + "0 g BT /F1 9 Tf 6 6 Td (inner group) Tj ET\n")),
        RawPdf.StreamObj(
            "/Type /XObject /Subtype /Form /BBox [0 0 120 100] "
            + "/Group << /Type /Group /S /Transparency /CS /DeviceGray /I true /K false >>",
            Content("0.15 g 0 0 120 100 re f\n")),
    }, "/Root 1 0 R");
}
