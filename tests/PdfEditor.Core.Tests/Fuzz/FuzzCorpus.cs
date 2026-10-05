using System.Globalization;
using System.Text;
using SkiaSharp;

namespace PdfEditor.Tests.Fuzz;

/// <summary>One named input in the fuzz corpus.</summary>
internal sealed record FuzzCase(string Name, byte[] Bytes);

/// <summary>
/// The seed corpus and the mutator. Everything here is generated in-repo and is fully
/// deterministic: the same commit produces byte-identical inputs on every machine, and the
/// mutation stream is driven by <see cref="FuzzHarness.CorpusSeed"/> alone, so a failing case
/// named in a CI log can be reproduced locally by re-running the same test.
/// </summary>
internal static class FuzzCorpus
{
    private static readonly byte[] Payload = Encoding.ASCII.GetBytes(
        "BT /F1 24 Tf 40 500 Td (Fuzz corpus payload text) Tj ET\n" +
        "0 0 1 rg 40 40 320 200 re f\n");

    /// <summary>
    /// 8 MiB of highly compressible zeros: a classic decompression bomb once deflated down to a
    /// few hundred bytes. Large enough that an unbounded expansion is unmistakable, small enough
    /// that a *bounded* expansion still finishes inside the CI budget.
    /// </summary>
    private const int BombSize = 8 * 1024 * 1024;

    /// <summary>
    /// Streams whose declared filter and actual bytes disagree in every way the issue calls out:
    /// truncated, corrupt, wrong <c>/Length</c>, mismatched filter chains, and a bomb.
    /// </summary>
    public static IReadOnlyList<FuzzCase> FilterCases { get; } = BuildFilterCases();

    /// <summary>Documents whose *structure* is broken: xref, offsets, page tree, required keys.</summary>
    public static IReadOnlyList<FuzzCase> StructureCases { get; } = BuildStructureCases();

    /// <summary>
    /// The base documents the mutator chews on. Real, valid PDFs — so a mutation lands in a
    /// plausible place rather than producing obvious garbage that the reader rejects at byte 1.
    /// <para>
    /// They are all written by <see cref="RawPdf"/> (or by <see cref="TestPdfs.ChromeStyleLeftoverCtm"/>,
    /// which is likewise hand-written) rather than by the engine's writer, because <em>its output is not
    /// reproducible</em>: it stamps a <c>/ModDate</c> (and, for a new document, a random <c>/ID</c>) into every
    /// file, so two calls in the same process already differ. Seeding a fuzzer with those would
    /// make every failure a one-off that cannot be reproduced from the recorded seed.
    /// </para>
    /// </summary>
    public static IReadOnlyList<FuzzCase> MutationSeeds { get; } = new[]
    {
        new FuzzCase("seed-plain", RawPdf.ContentStreamDoc("", Payload)),
        new FuzzCase("seed-flate", RawPdf.ContentStreamDoc("/Filter /FlateDecode", RawPdf.Deflate(Payload))),
        new FuzzCase("seed-multipage", RawPdf.MultiPageDoc(3)),
        new FuzzCase("seed-chrome", TestPdfs.ChromeStyleLeftoverCtm()),
    };

    private static List<FuzzCase> BuildFilterCases()
    {
        byte[] flate = RawPdf.Deflate(Payload);
        byte[] corruptFlate = (byte[])flate.Clone();
        for (int i = 4; i < corruptFlate.Length; i += 3) corruptFlate[i] ^= 0x5A;

        byte[] bomb = RawPdf.Deflate(new byte[BombSize]);
        byte[] jpeg = Jpeg();
        byte[] corruptJpeg = (byte[])jpeg.Clone();
        for (int i = 20; i < corruptJpeg.Length; i += 7) corruptJpeg[i] ^= 0xFF;

        var cases = new List<FuzzCase>
        {
            // --- FlateDecode ---------------------------------------------------------------
            new("flate-valid", RawPdf.ContentStreamDoc("/Filter /FlateDecode", flate)),
            new("flate-truncated-half",
                RawPdf.ContentStreamDoc("/Filter /FlateDecode", flate[..(flate.Length / 2)])),
            new("flate-truncated-header", RawPdf.ContentStreamDoc("/Filter /FlateDecode", flate[..2])),
            new("flate-corrupt-body", RawPdf.ContentStreamDoc("/Filter /FlateDecode", corruptFlate)),
            new("flate-empty", RawPdf.ContentStreamDoc("/Filter /FlateDecode", Array.Empty<byte>())),
            new("flate-length-too-short",
                RawPdf.ContentStreamDoc("/Filter /FlateDecode", flate, declaredLength: 5)),
            new("flate-length-too-long",
                RawPdf.ContentStreamDoc("/Filter /FlateDecode", flate, declaredLength: flate.Length + 4096)),
            new("flate-length-negative",
                RawPdf.ContentStreamDoc("/Filter /FlateDecode", flate, declaredLength: -17)),
            new("flate-length-indirect-missing",
                RawPdf.Build(new[]
                {
                    RawPdf.Obj("<< /Type /Catalog /Pages 2 0 R >>"),
                    RawPdf.Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
                    RawPdf.Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 600] /Contents 4 0 R >>"),
                    Concat(Encoding.ASCII.GetBytes("<< /Length 99 0 R /Filter /FlateDecode >>\nstream\n"),
                        flate, Encoding.ASCII.GetBytes("\nendstream")),
                }, "/Root 1 0 R")),
            new("flate-no-endstream",
                RawPdf.ContentStreamDoc("/Filter /FlateDecode", flate, omitEndstream: true)),
            new("flate-bomb-8mib", RawPdf.ContentStreamDoc("/Filter /FlateDecode", bomb)),
            new("flate-bomb-as-image",
                RawPdf.ImageDoc("/Filter /FlateDecode", bomb, 4096, 4096)),

            // --- ASCIIHexDecode ------------------------------------------------------------
            new("asciihex-valid", RawPdf.ContentStreamDoc("/Filter /ASCIIHexDecode", RawPdf.AsciiHex(Payload))),
            new("asciihex-odd-digits",
                RawPdf.ContentStreamDoc("/Filter /ASCIIHexDecode", Encoding.ASCII.GetBytes("48656C6C6F2>"))),
            new("asciihex-invalid-chars",
                RawPdf.ContentStreamDoc("/Filter /ASCIIHexDecode", Encoding.ASCII.GetBytes("zzQQ!!##%%^^>"))),
            new("asciihex-no-eod",
                RawPdf.ContentStreamDoc("/Filter /ASCIIHexDecode", Encoding.ASCII.GetBytes("4142434445"))),

            // --- ASCII85Decode -------------------------------------------------------------
            new("ascii85-valid", RawPdf.ContentStreamDoc("/Filter /ASCII85Decode", RawPdf.Ascii85(Payload))),
            new("ascii85-invalid-chars",
                RawPdf.ContentStreamDoc("/Filter /ASCII85Decode", Encoding.ASCII.GetBytes("~~~vw~>"))),
            new("ascii85-truncated-group",
                RawPdf.ContentStreamDoc("/Filter /ASCII85Decode",
                    RawPdf.Ascii85(Payload)[..17])),
            new("ascii85-no-eod",
                RawPdf.ContentStreamDoc("/Filter /ASCII85Decode", Encoding.ASCII.GetBytes("87cURD]i,\"Ebo80"))),
            new("ascii85-z-inside-group",
                RawPdf.ContentStreamDoc("/Filter /ASCII85Decode", Encoding.ASCII.GetBytes("87zcURD~>"))),
            new("ascii85-overflow-group",
                RawPdf.ContentStreamDoc("/Filter /ASCII85Decode", Encoding.ASCII.GetBytes("uuuuu~>"))),

            // --- LZWDecode -----------------------------------------------------------------
            // No valid LZW encoder is needed here: every LZW case in the issue is a *broken* one.
            new("lzw-garbage",
                RawPdf.ContentStreamDoc("/Filter /LZWDecode", Payload)),
            new("lzw-truncated",
                RawPdf.ContentStreamDoc("/Filter /LZWDecode", new byte[] { 0x80, 0x0B, 0x60 })),
            new("lzw-all-ones",
                RawPdf.ContentStreamDoc("/Filter /LZWDecode", Enumerable.Repeat((byte)0xFF, 512).ToArray())),
            new("lzw-early-change-zero",
                RawPdf.ContentStreamDoc("/Filter /LZWDecode /DecodeParms << /EarlyChange 0 >>", Payload)),

            // --- RunLengthDecode -----------------------------------------------------------
            new("runlength-valid",
                RawPdf.ContentStreamDoc("/Filter /RunLengthDecode", RawPdf.RunLength(Payload))),
            new("runlength-truncated-literal",
                RawPdf.ContentStreamDoc("/Filter /RunLengthDecode", new byte[] { 100, 65, 66, 67 })),
            new("runlength-no-eod",
                RawPdf.ContentStreamDoc("/Filter /RunLengthDecode",
                    RawPdf.RunLength(Payload)[..^1])),
            new("runlength-trailing-run-byte",
                RawPdf.ContentStreamDoc("/Filter /RunLengthDecode", new byte[] { 250 })),

            // --- DCTDecode -----------------------------------------------------------------
            new("dct-valid", RawPdf.ImageDoc("/Filter /DCTDecode", jpeg, 32, 32)),
            new("dct-truncated", RawPdf.ImageDoc("/Filter /DCTDecode", jpeg[..(jpeg.Length / 3)], 32, 32)),
            new("dct-corrupt-scan", RawPdf.ImageDoc("/Filter /DCTDecode", corruptJpeg, 32, 32)),
            new("dct-not-a-jpeg", RawPdf.ImageDoc("/Filter /DCTDecode", Payload, 32, 32)),
            new("dct-dimensions-lie", RawPdf.ImageDoc("/Filter /DCTDecode", jpeg, 30000, 30000)),

            // --- Filter chains and nonsense filters ----------------------------------------
            new("chain-a85-flate-mismatch",
                RawPdf.ContentStreamDoc("/Filter [/ASCII85Decode /FlateDecode]", flate)),
            new("chain-flate-declared-twice",
                RawPdf.ContentStreamDoc("/Filter [/FlateDecode /FlateDecode]", flate)),
            new("chain-a85-then-flate-valid",
                RawPdf.ContentStreamDoc("/Filter [/ASCII85Decode /FlateDecode]", RawPdf.Ascii85(flate))),
            new("filter-unknown-name",
                RawPdf.ContentStreamDoc("/Filter /NoSuchDecode", Payload)),
            new("filter-is-a-number",
                RawPdf.ContentStreamDoc("/Filter 42", Payload)),
            new("filter-self-recursive-chain",
                RawPdf.ContentStreamDoc(
                    "/Filter [/FlateDecode /FlateDecode /FlateDecode /FlateDecode /FlateDecode]", flate)),
            new("crypt-filter-without-encryption",
                RawPdf.ContentStreamDoc("/Filter /Crypt", Payload)),
        };
        return cases;
    }

    private static List<FuzzCase> BuildStructureCases()
    {
        byte[][] good =
        {
            RawPdf.Obj("<< /Type /Catalog /Pages 2 0 R >>"),
            RawPdf.Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            RawPdf.Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 600] /Contents 4 0 R >>"),
            RawPdf.StreamObj("", Payload),
        };

        var cases = new List<FuzzCase>();
        foreach (var damage in Enum.GetValues<XrefDamage>())
            cases.Add(new FuzzCase("xref-" + damage, RawPdf.Build(good, "/Root 1 0 R", damage)));

        cases.AddRange(new FuzzCase[]
        {
            new("trailer-no-root", RawPdf.Build(good, "")),
            new("trailer-root-missing-object", RawPdf.Build(good, "/Root 77 0 R")),
            new("trailer-root-is-a-string", RawPdf.Build(good, "/Root (not a dictionary)")),

            new("pagetree-cycle-self", RawPdf.Build(new[]
            {
                RawPdf.Obj("<< /Type /Catalog /Pages 2 0 R >>"),
                RawPdf.Obj("<< /Type /Pages /Kids [2 0 R] /Count 1 >>"),
            }, "/Root 1 0 R")),
            new("pagetree-cycle-two-node", RawPdf.Build(new[]
            {
                RawPdf.Obj("<< /Type /Catalog /Pages 2 0 R >>"),
                RawPdf.Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
                RawPdf.Obj("<< /Type /Pages /Parent 2 0 R /Kids [2 0 R] /Count 1 >>"),
            }, "/Root 1 0 R")),
            new("pagetree-parent-cycle", RawPdf.Build(new[]
            {
                RawPdf.Obj("<< /Type /Catalog /Pages 2 0 R >>"),
                RawPdf.Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 /Parent 3 0 R >>"),
                RawPdf.Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 600] >>"),
            }, "/Root 1 0 R")),
            new("pagetree-count-lies", RawPdf.Build(new[]
            {
                RawPdf.Obj("<< /Type /Catalog /Pages 2 0 R >>"),
                RawPdf.Obj("<< /Type /Pages /Kids [3 0 R] /Count 100000 >>"),
                RawPdf.Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 600] >>"),
            }, "/Root 1 0 R")),
            new("pagetree-empty-kids", RawPdf.Build(new[]
            {
                RawPdf.Obj("<< /Type /Catalog /Pages 2 0 R >>"),
                RawPdf.Obj("<< /Type /Pages /Kids [] /Count 0 >>"),
            }, "/Root 1 0 R")),
            new("pagetree-kid-is-missing-object", RawPdf.Build(new[]
            {
                RawPdf.Obj("<< /Type /Catalog /Pages 2 0 R >>"),
                RawPdf.Obj("<< /Type /Pages /Kids [64 0 R] /Count 1 >>"),
            }, "/Root 1 0 R")),

            new("page-no-mediabox", RawPdf.Build(new[]
            {
                good[0], good[1],
                RawPdf.Obj("<< /Type /Page /Parent 2 0 R /Contents 4 0 R >>"),
                good[3],
            }, "/Root 1 0 R")),
            new("page-mediabox-degenerate", RawPdf.Build(new[]
            {
                good[0], good[1],
                RawPdf.Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 0 0] /Contents 4 0 R >>"),
                good[3],
            }, "/Root 1 0 R")),
            new("page-mediabox-nonsense", RawPdf.Build(new[]
            {
                good[0], good[1],
                RawPdf.Obj("<< /Type /Page /Parent 2 0 R /MediaBox [(a) (b) /c << >>] /Contents 4 0 R >>"),
                good[3],
            }, "/Root 1 0 R")),
            new("page-mediabox-astronomical", RawPdf.Build(new[]
            {
                good[0], good[1],
                RawPdf.Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 1e30 1e30] /Contents 4 0 R >>"),
                good[3],
            }, "/Root 1 0 R")),
            new("page-contents-missing-object", RawPdf.Build(new[]
            {
                good[0], good[1],
                RawPdf.Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 600] /Contents 88 0 R >>"),
            }, "/Root 1 0 R")),
            new("page-contents-is-a-dictionary", RawPdf.Build(new[]
            {
                good[0], good[1],
                RawPdf.Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 600] /Contents << /a 1 >> >>"),
            }, "/Root 1 0 R")),
            new("page-contents-points-at-itself", RawPdf.Build(new[]
            {
                good[0], good[1],
                RawPdf.Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 600] /Contents 3 0 R >>"),
            }, "/Root 1 0 R")),
            new("page-resources-cycle", RawPdf.Build(new[]
            {
                good[0], good[1],
                RawPdf.Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 600] " +
                           "/Resources << /XObject << /Im1 5 0 R >> >> /Contents 4 0 R >>"),
                RawPdf.StreamObj("", Encoding.ASCII.GetBytes("q /Im1 Do Q\n")),
                RawPdf.StreamObj("/Type /XObject /Subtype /Form /BBox [0 0 10 10] " +
                                 "/Resources << /XObject << /Im1 5 0 R >> >>",
                    Encoding.ASCII.GetBytes("q /Im1 Do Q\n")),
            }, "/Root 1 0 R")),
            new("form-xobject-nesting-200", RawPdf.DeeplyNestedForms(200)),
            new("form-xobject-nesting-4", RawPdf.DeeplyNestedForms(4)),
            new("page-type-is-wrong", RawPdf.Build(new[]
            {
                good[0], good[1],
                RawPdf.Obj("<< /Type /Bogus /Parent 2 0 R /MediaBox [0 0 400 600] /Contents 4 0 R >>"),
                good[3],
            }, "/Root 1 0 R")),

            new("object-numbering-mismatch", RawPdf.Build(new[]
            {
                RawPdf.Obj("<< /Type /Catalog /Pages 9 0 R >>"),
                RawPdf.Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
                RawPdf.Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 600] >>"),
            }, "/Root 1 0 R")),
            new("deeply-nested-arrays", RawPdf.Build(new[]
            {
                good[0], good[1],
                RawPdf.Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 600] /Contents 4 0 R /Junk " +
                           new string('[', 400) + new string(']', 400) + " >>"),
                good[3],
            }, "/Root 1 0 R")),
            new("dictionary-unterminated", RawPdf.Build(new[]
            {
                good[0], good[1],
                RawPdf.Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 600"),
            }, "/Root 1 0 R")),
            new("no-eof-marker",
                RawPdf.Build(good, "/Root 1 0 R")[..^6]),
            new("header-only", Encoding.ASCII.GetBytes("%PDF-1.7\n")),
            new("empty", Array.Empty<byte>()),
        });
        return cases;
    }

    /// <summary>
    /// Produces <paramref name="count"/> deterministic mutations of <paramref name="seed"/>. The
    /// PRNG is seeded from <see cref="FuzzHarness.CorpusSeed"/> combined with the seed document's
    /// name, so each document's mutation stream is independent yet perfectly reproducible, and
    /// adding a new seed document never renumbers the existing ones' cases.
    /// </summary>
    public static IEnumerable<FuzzCase> Mutate(FuzzCase seed, int count)
    {
        var rng = new Random(FuzzHarness.CorpusSeed ^ StableHash(seed.Name));
        for (int i = 0; i < count; i++)
        {
            var bytes = (byte[])seed.Bytes.Clone();
            string kind = ApplyMutation(rng, ref bytes);
            yield return new FuzzCase(
                string.Create(CultureInfo.InvariantCulture, $"{seed.Name}-{i:D3}-{kind}"), bytes);
        }
    }

    /// <summary>Applies one random mutation in place (or replaces the array) and names it.</summary>
    private static string ApplyMutation(Random rng, ref byte[] bytes)
    {
        if (bytes.Length < 32) return "noop";
        switch (rng.Next(7))
        {
            case 0: // single bit flip
                {
                    int at = rng.Next(bytes.Length);
                    bytes[at] ^= (byte)(1 << rng.Next(8));
                    return "bitflip";
                }
            case 1: // splat a random byte over a short run
                {
                    int at = rng.Next(bytes.Length);
                    int len = Math.Min(bytes.Length - at, 1 + rng.Next(16));
                    byte value = (byte)rng.Next(256);
                    for (int i = 0; i < len; i++) bytes[at + i] = value;
                    return "splat";
                }
            case 2: // truncate
                bytes = bytes[..(1 + rng.Next(bytes.Length - 1))];
                return "truncate";
            case 3: // delete an interior chunk (shifts every later offset — murder on the xref)
                {
                    int at = rng.Next(bytes.Length - 1);
                    int len = Math.Min(bytes.Length - at, 1 + rng.Next(64));
                    bytes = bytes[..at].Concat(bytes[(at + len)..]).ToArray();
                    return "delete";
                }
            case 4: // insert random bytes
                {
                    int at = rng.Next(bytes.Length);
                    var inserted = new byte[1 + rng.Next(32)];
                    rng.NextBytes(inserted);
                    bytes = bytes[..at].Concat(inserted).Concat(bytes[at..]).ToArray();
                    return "insert";
                }
            case 5: // scramble an ASCII digit run: turns xref offsets and /Length values into lies
                {
                    int at = FindDigit(rng, bytes);
                    if (at < 0) goto case 0;
                    for (int i = at; i < bytes.Length && bytes[i] is >= (byte)'0' and <= (byte)'9'; i++)
                        bytes[i] = (byte)('0' + rng.Next(10));
                    return "digits";
                }
            default: // swap two equal-sized chunks
                {
                    int len = 1 + rng.Next(32);
                    if (bytes.Length < 2 * len) goto case 0;
                    int a = rng.Next(bytes.Length - 2 * len);
                    int b = a + len + rng.Next(bytes.Length - a - 2 * len);
                    for (int i = 0; i < len; i++)
                        (bytes[a + i], bytes[b + i]) = (bytes[b + i], bytes[a + i]);
                    return "swap";
                }
        }
    }

    /// <summary>Finds an ASCII digit at or after a random position; -1 when there is none.</summary>
    private static int FindDigit(Random rng, byte[] bytes)
    {
        int start = rng.Next(bytes.Length);
        for (int i = start; i < bytes.Length; i++)
            if (bytes[i] is >= (byte)'0' and <= (byte)'9') return i;
        return -1;
    }

    /// <summary>
    /// A hash that does not vary between runs or frameworks — <see cref="string.GetHashCode()"/>
    /// is randomised per process and would make the corpus irreproducible.
    /// </summary>
    private static int StableHash(string text)
    {
        int hash = 17;
        foreach (char c in text) hash = unchecked(hash * 31 + c);
        return hash;
    }

    /// <summary>A tiny, deterministic baseline JPEG for the DCTDecode cases.</summary>
    private static byte[] Jpeg()
    {
        using var bitmap = new SKBitmap(32, 32);
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
                bitmap.SetPixel(x, y, new SKColor((byte)(x * 8), (byte)(y * 8), 128));
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Jpeg, 80).ToArray();
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var output = new MemoryStream();
        foreach (var part in parts) output.Write(part);
        return output.ToArray();
    }
}
