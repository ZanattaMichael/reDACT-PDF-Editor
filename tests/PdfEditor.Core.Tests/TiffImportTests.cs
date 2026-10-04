using PdfEditor.Core;
using PdfEditor.Core.Pdf;
using SkiaSharp;
using Xunit;

namespace PdfEditor.Tests;

/// <summary>
/// Importing a scanned TIFF as a PDF. Scanners write TIFF in many shapes, so each encoding the
/// decoder claims to read is exercised from bytes built here, and each one it refuses is refused
/// with a message the user can act on rather than an internal error.
/// </summary>
public class TiffImportTests
{
    [Fact]
    public void Gray8_Uncompressed_LittleEndian()
    {
        byte[] tiff = Tiff.Build(littleEndian: true, width: 4, height: 2, bitsPerSample: new[] { 8 },
            photometric: 1, strips: new[] { new byte[] { 0, 85, 170, 255, 255, 170, 85, 0 } });

        using var bitmap = PdfImages.DecodeBitmap(tiff)!;
        Assert.Equal(new SKColor(0, 0, 0), Rgb(bitmap, 0, 0));
        Assert.Equal(new SKColor(170, 170, 170), Rgb(bitmap, 2, 0));
        Assert.Equal(new SKColor(255, 255, 255), Rgb(bitmap, 0, 1));
    }

    [Fact]
    public void Bilevel_WhiteIsZero_BigEndian()
    {
        // 1 = black under WhiteIsZero: the first pixel of each row is black, the rest white.
        byte[] tiff = Tiff.Build(littleEndian: false, width: 3, height: 2, bitsPerSample: new[] { 1 },
            photometric: 0, strips: new[] { new byte[] { 0b1000_0000, 0b1000_0000 } });

        using var bitmap = PdfImages.DecodeBitmap(tiff)!;
        Assert.Equal(new SKColor(0, 0, 0), Rgb(bitmap, 0, 1));
        Assert.Equal(new SKColor(255, 255, 255), Rgb(bitmap, 2, 1));
    }

    [Fact]
    public void Rgb_DeflateWithHorizontalPredictor_AndAlpha()
    {
        // RGBA rows, differenced as TIFF predictor 2 stores them.
        byte[] row = { 10, 20, 30, 255, 15, 25, 35, 128 };
        byte[] differenced = { 10, 20, 30, 255, 5, 5, 5, unchecked((byte)(128 - 255)) };
        byte[] tiff = Tiff.Build(littleEndian: true, width: 2, height: 1, bitsPerSample: new[] { 8, 8, 8, 8 },
            photometric: 2, samplesPerPixel: 4, compression: 8, predictor: 2, extraSamples: 1,
            strips: new[] { PdfFilters.FlateEncode(differenced) });

        using var bitmap = PdfImages.DecodeBitmap(tiff)!;
        var second = bitmap.GetPixel(1, 0);
        Assert.Equal((row[4], row[5], row[6], row[7]), (second.Red, second.Green, second.Blue, second.Alpha));
    }

    [Fact]
    public void Palette4_PackBits()
    {
        // Palette: index 0 red, index 1 blue (16-bit TIFF colour map entries).
        var map = new int[3 * 16];
        map[0] = 0xFFFF;            // red[0]
        map[16 + 0] = 0;            // green[0]
        map[32 + 1] = 0xFFFF;       // blue[1]
        byte[] pixels = { 0x01, 0x10, 0x00, 0x00 };   // 4x2 at 4 bits: 0 1 1 0 / 0 0 0 0
        byte[] tiff = Tiff.Build(littleEndian: true, width: 4, height: 2, bitsPerSample: new[] { 4 },
            photometric: 3, compression: 32773, colorMap: map, strips: new[] { Tiff.PackBits(pixels) });

        using var bitmap = PdfImages.DecodeBitmap(tiff)!;
        Assert.Equal(new SKColor(255, 0, 0), Rgb(bitmap, 0, 0));
        Assert.Equal(new SKColor(0, 0, 255), Rgb(bitmap, 1, 0));
        Assert.Equal(new SKColor(255, 0, 0), Rgb(bitmap, 3, 1));
    }

    [Fact]
    public void Rgb_Lzw_InSeveralStrips()
    {
        byte[] top = Enumerable.Repeat(new byte[] { 200, 0, 0 }, 6).SelectMany(p => p).ToArray();
        byte[] bottom = Enumerable.Repeat(new byte[] { 0, 0, 200 }, 6).SelectMany(p => p).ToArray();
        byte[] tiff = Tiff.Build(littleEndian: true, width: 3, height: 4, bitsPerSample: new[] { 8, 8, 8 },
            photometric: 2, samplesPerPixel: 3, compression: 5, rowsPerStrip: 2,
            strips: new[] { Tiff.Lzw(top), Tiff.Lzw(bottom) });

        using var bitmap = PdfImages.DecodeBitmap(tiff)!;
        Assert.Equal(new SKColor(200, 0, 0), Rgb(bitmap, 2, 1));
        Assert.Equal(new SKColor(0, 0, 200), Rgb(bitmap, 0, 3));
    }

    [Fact]
    public void Gray16_UsesTheHighByte()
    {
        byte[] tiff = Tiff.Build(littleEndian: false, width: 2, height: 1, bitsPerSample: new[] { 16 },
            photometric: 1, strips: new[] { new byte[] { 0x80, 0x00, 0xFF, 0xFF } });

        using var bitmap = PdfImages.DecodeBitmap(tiff)!;
        Assert.Equal(new SKColor(255, 255, 255), Rgb(bitmap, 1, 0));
        Assert.InRange(Rgb(bitmap, 0, 0).Red, 120, 136);
    }

    [Fact]
    public void ImportedTiff_RendersInPdfium()
    {
        byte[] pixels = Enumerable.Repeat(new byte[] { 0, 160, 0 }, 16 * 16).SelectMany(p => p).ToArray();
        byte[] tiff = Tiff.Build(littleEndian: true, width: 16, height: 16, bitsPerSample: new[] { 8, 8, 8 },
            photometric: 2, samplesPerPixel: 3, strips: new[] { pixels });

        byte[] pdf = DocumentImport.ImageToPdf(tiff);

        var info = PdfInspector.GetInfo(pdf);
        Assert.Equal(1, info.PageCount);
        var page = info.Pages[0];
        var pixel = TestPdfAssert.PixelAt(pdf, 1, page.Width / 2, page.Height / 2, 72);
        Assert.Equal(0, pixel.Red);
        Assert.InRange(pixel.Green, 150, 170);
    }

    [Theory]
    [InlineData(4, 0, -1, false)]   // Group 4, BlackIsZero
    [InlineData(3, 1, 1, true)]     // Group 3 2-D, WhiteIsZero
    [InlineData(2, 1, 0, false)]    // Modified Huffman (byte aligned)
    public void FaxTiff_IsEmbeddedAsCcitt_WithoutDecoding(int compression, int t4Options, int k, bool whiteIsZero)
    {
        byte[] fax = { 0x26, 0xA0, 0x5F, 0x00 };
        byte[] tiff = Tiff.Build(littleEndian: true, width: 1728, height: 3, bitsPerSample: new[] { 1 },
            photometric: whiteIsZero ? 0 : 1, compression: compression, t4Options: t4Options, fillOrder: 2,
            strips: new[] { fax });

        var (image, width, height) = PdfImages.CreateXObject(tiff);

        Assert.Equal((1728, 3), (width, height));
        Assert.Equal("CCITTFaxDecode", image.GetAsName(PdfName.Filter)!.Value);
        var parms = image.GetAsDictionary(PdfName.DecodeParms)!;
        Assert.Equal(k, parms.GetAsInt(PdfName.K));
        Assert.Equal(whiteIsZero, parms.GetAsBool(PdfName.Of("BlackIs1")) ?? false);
        Assert.Equal(compression == 2, parms.GetAsBool(PdfName.Of("EncodedByteAlign")) ?? false);
        // FillOrder 2 stores each byte's bits reversed; PDF wants them most significant first.
        Assert.Equal(new byte[] { 0x64, 0x05, 0xFA, 0x00 }, image.RawData);
    }

    [Fact]
    public void FaxTiff_InSeveralStrips_IsRefusedWithAWayForward()
    {
        byte[] tiff = Tiff.Build(littleEndian: true, width: 8, height: 2, bitsPerSample: new[] { 1 },
            photometric: 0, compression: 4, rowsPerStrip: 1, strips: new[] { new byte[] { 0 }, new byte[] { 0 } });

        var ex = Assert.Throws<ArgumentException>(() => PdfImages.CreateXObject(tiff));
        Assert.Contains("single-strip", ex.Message);
    }

    public static TheoryData<string, byte[]> Unsupported => new()
    {
        { "Tiled", Tiff.Build(true, 2, 2, new[] { 8 }, 1, new[] { new byte[4] }, tiled: true) },
        { "JPEG-compressed", Tiff.Build(true, 2, 2, new[] { 8 }, 1, new[] { new byte[4] }, compression: 7) },
        { "compression 9", Tiff.Build(true, 2, 2, new[] { 8 }, 1, new[] { new byte[4] }, compression: 9) },
        { "Planar", Tiff.Build(true, 2, 2, new[] { 8, 8, 8 }, 2, new[] { new byte[12] }, samplesPerPixel: 3, planar: 2) },
        { "2 bits per sample", Tiff.Build(true, 2, 2, new[] { 2 }, 1, new[] { new byte[2] }) },
        { "truncated", Tiff.Build(true, 4, 4, new[] { 8 }, 1, new[] { new byte[3] }) },
        { "photometric interpretation 5", Tiff.Build(true, 1, 1, new[] { 8, 8, 8, 8 }, 5, new[] { new byte[4] }, samplesPerPixel: 4) },
        { "no usable image size", Tiff.Build(true, 0, 2, new[] { 8 }, 1, new[] { new byte[4] }) },
        { "no image directory", new byte[] { (byte)'I', (byte)'I', 42, 0, 0, 0, 0, 0 } },
        { "LZW data is corrupt", Tiff.Build(true, 2, 2, new[] { 8 }, 1, new[] { new byte[] { 0xFF, 0xFF, 0xFF } }, compression: 5) },
    };

    [Theory]
    [MemberData(nameof(Unsupported))]
    public void UnsupportedTiff_IsRefusedWithAnExplanation(string expected, byte[] tiff)
    {
        var ex = Assert.Throws<ArgumentException>(() => PdfImages.DecodeBitmap(tiff));
        Assert.Contains(expected, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static SKColor Rgb(SKBitmap bitmap, int x, int y)
    {
        var c = bitmap.GetPixel(x, y);
        return new SKColor(c.Red, c.Green, c.Blue);
    }

    /// <summary>A minimal TIFF writer: one image directory, strip-organised.</summary>
    private static class Tiff
    {
        public static byte[] Build(bool littleEndian, int width, int height, int[] bitsPerSample, int photometric,
            byte[][] strips, int samplesPerPixel = 1, int compression = 1, int predictor = 1, int rowsPerStrip = 0,
            int extraSamples = 0, int[]? colorMap = null, int t4Options = 0, int fillOrder = 1, bool tiled = false,
            int planar = 1)
        {
            var body = new List<byte>();
            void U16(List<byte> to, int v) { if (littleEndian) { to.Add((byte)v); to.Add((byte)(v >> 8)); } else { to.Add((byte)(v >> 8)); to.Add((byte)v); } }
            void U32(List<byte> to, long v)
            {
                var b = new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24) };
                to.AddRange(littleEndian ? b : b.Reverse());
            }

            // Header, then the strips and arrays, then the directory.
            var header = new List<byte> { (byte)(littleEndian ? 'I' : 'M'), (byte)(littleEndian ? 'I' : 'M') };
            U16(header, 42);
            const int dataStart = 8;
            var offsets = new List<long>();
            foreach (var strip in strips) { offsets.Add(dataStart + body.Count); body.AddRange(strip); }

            var entries = new List<(int Tag, int Type, long[] Values)>
            {
                (256, 4, new long[] { width }),
                (257, 4, new long[] { height }),
                (258, 3, bitsPerSample.Select(b => (long)b).ToArray()),
                (259, 3, new long[] { compression }),
                (262, 3, new long[] { photometric }),
                (266, 3, new long[] { fillOrder }),
                (273, 4, offsets.ToArray()),
                (277, 3, new long[] { samplesPerPixel }),
                (278, 4, new long[] { rowsPerStrip > 0 ? rowsPerStrip : height }),
                (279, 4, strips.Select(s => (long)s.Length).ToArray()),
                (284, 3, new long[] { planar }),
                (292, 4, new long[] { t4Options }),
                (317, 3, new long[] { predictor }),
            };
            if (colorMap != null) entries.Add((320, 3, colorMap.Select(c => (long)c).ToArray()));
            if (tiled) entries.Add((322, 4, new long[] { 16 }));
            if (extraSamples > 0) entries.Add((338, 3, Enumerable.Repeat(2L, extraSamples).ToArray()));
            entries.Sort((a, b) => a.Tag.CompareTo(b.Tag));

            // Values that do not fit in the entry go after the strips.
            var valueOffsets = new Dictionary<int, long>();
            foreach (var (tag, type, values) in entries)
            {
                int size = type == 3 ? 2 : 4;
                if (values.Length * size <= 4) continue;
                if (body.Count % 2 == 1) body.Add(0);
                valueOffsets[tag] = dataStart + body.Count;
                foreach (var v in values) { if (size == 2) U16(body, (int)v); else U32(body, v); }
            }
            if (body.Count % 2 == 1) body.Add(0);
            long ifdOffset = dataStart + body.Count;
            U32(header, ifdOffset);

            var ifd = new List<byte>();
            U16(ifd, entries.Count);
            foreach (var (tag, type, values) in entries)
            {
                U16(ifd, tag);
                U16(ifd, type);
                U32(ifd, values.Length);
                if (valueOffsets.TryGetValue(tag, out long at)) { U32(ifd, at); continue; }
                var inline = new List<byte>();
                foreach (var v in values) { if (type == 3) U16(inline, (int)v); else U32(inline, v); }
                while (inline.Count < 4) inline.Add(0);
                ifd.AddRange(inline);
            }
            U32(ifd, 0);
            return header.Concat(body).Concat(ifd).ToArray();
        }

        /// <summary>PackBits: replicate runs for repeated bytes, literal runs otherwise, then a no-op.</summary>
        public static byte[] PackBits(byte[] data)
        {
            var output = new List<byte>();
            int i = 0;
            while (i < data.Length)
            {
                int run = 1;
                while (i + run < data.Length && run < 128 && data[i + run] == data[i]) run++;
                if (run >= 2)
                {
                    output.Add(unchecked((byte)(1 - run)));
                    output.Add(data[i]);
                    i += run;
                    continue;
                }
                int start = i;
                while (i < data.Length && i - start < 128 && !(i + 1 < data.Length && data[i] == data[i + 1])) i++;
                output.Add((byte)(i - start - 1));
                output.AddRange(data[start..i]);
            }
            output.Add(0x80); // the no-op header a decoder must skip
            return output.ToArray();
        }

        /// <summary>TIFF LZW (MSB-first, early change), for inputs small enough to stay at 9-bit codes.</summary>
        public static byte[] Lzw(byte[] data)
        {
            var table = new Dictionary<string, int>();
            for (int i = 0; i < 256; i++) table[((char)i).ToString()] = i;
            int next = 258;
            var codes = new List<int> { 256 };
            string w = "";
            foreach (byte b in data)
            {
                string wc = w + (char)b;
                if (table.ContainsKey(wc)) { w = wc; continue; }
                codes.Add(table[w]);
                table[wc] = next++;
                w = ((char)b).ToString();
            }
            if (w.Length > 0) codes.Add(table[w]);
            codes.Add(257);
            if (next >= 510) throw new InvalidOperationException("Test LZW encoder only emits 9-bit codes.");

            var output = new List<byte>();
            int buffer = 0, bits = 0;
            foreach (int code in codes)
            {
                buffer = (buffer << 9) | code;
                bits += 9;
                while (bits >= 8) { output.Add((byte)(buffer >> (bits - 8))); bits -= 8; }
            }
            if (bits > 0) output.Add((byte)(buffer << (8 - bits)));
            return output.ToArray();
        }
    }
}
