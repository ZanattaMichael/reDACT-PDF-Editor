using System.IO.Compression;
using System.Text;
using PdfEditor.Core.Pdf;
using Xunit;

namespace PdfEditor.Tests;

/// <summary>
/// The stream filters of ISO 32000-1 §7.4. Each one is fed data encoded here by an independent
/// encoder, so a round trip proves the decoder reads the format and does not merely undo a
/// mirror-image bug. Corrupt input has to be refused with a reason, never decoded to garbage
/// that a redaction would then write back.
/// </summary>
public class FilterTests
{
    private static readonly byte[] Sample = Encoding.ASCII.GetBytes(
        "BT /F1 12 Tf 72 700 Td (The quick brown fox jumps over the lazy dog) Tj ET\n" +
        string.Concat(Enumerable.Repeat("0 0 m 10 10 l S\n", 20)));

    private static byte[] Decode(byte[] raw, PdfObject filter, PdfObject? parms = null)
    {
        var stream = PdfStream.FromFile(new PdfDictionary(), raw);
        stream.Put(PdfName.Filter, filter);
        if (parms != null) stream.Put(PdfName.DecodeParms, parms);
        return PdfFilters.Decode(stream);
    }

    private static PdfArray Names(params string[] names) => new(names.Select(n => (PdfObject)PdfName.Of(n)));

    [Theory]
    [InlineData(CompressionLevel.Optimal)]       // dynamic Huffman blocks
    [InlineData(CompressionLevel.NoCompression)] // stored blocks
    [InlineData(CompressionLevel.Fastest)]
    public void Flate_ZlibWrapped(CompressionLevel level)
    {
        var output = new MemoryStream();
        using (var z = new ZLibStream(output, level, leaveOpen: true)) z.Write(Sample);
        Assert.Equal(Sample, Decode(output.ToArray(), PdfName.FlateDecode));
    }

    [Fact]
    public void Flate_BareDeflate_AsSomeProducersWriteIt()
    {
        var output = new MemoryStream();
        using (var z = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true)) z.Write(Sample);
        Assert.Equal(Sample, Decode(output.ToArray(), PdfName.Of("Fl")));
    }

    [Fact]
    public void Flate_Truncated_IsRefused_ButLenientInflateKeepsWhatItCould()
    {
        byte[] full = PdfFilters.FlateEncode(Sample);
        byte[] cut = full[..(full.Length / 2)];

        Assert.ThrowsAny<FormatException>(() => Decode(cut, PdfName.FlateDecode));
        byte[] partial = PdfFilters.InflateLenient(cut);
        Assert.True(partial.Length > 0 && Sample.AsSpan().StartsWith(partial));
    }

    [Fact]
    public void Flate_Garbage_IsRefused()
    {
        Assert.ThrowsAny<FormatException>(() => Decode(Encoding.ASCII.GetBytes("this is not deflate data at all"), PdfName.FlateDecode));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Lzw_WithEitherEarlyChange(int earlyChange)
    {
        byte[] data = Encoding.ASCII.GetBytes("TOBEORNOTTOBEORTOBEORNOT TOBEORNOTTOBEORTOBEORNOT");
        var parms = new PdfDictionary();
        parms.Put(PdfName.EarlyChange, new PdfNumber(earlyChange));
        Assert.Equal(data, Decode(TestEncoders.Lzw(data), PdfName.Of("LZWDecode"), parms));
    }

    [Fact]
    public void Lzw_LongInput_GrowsItsCodeWidth()
    {
        // Enough distinct sequences to push the table past 511, 1023 and 2047 entries.
        var random = new Random(170);
        byte[] data = Enumerable.Range(0, 6000).Select(_ => (byte)random.Next(0, 40)).ToArray();
        Assert.Equal(data, Decode(TestEncoders.Lzw(data), PdfName.Of("LZW")));
    }

    /// <summary>
    /// LZW from an independent encoder: libtiff 4 (via Pillow), which writes PDF-compatible LZW
    /// with early change. 700 samples push the table past 511 entries, so the codes widen to 10
    /// bits partway through — the step a decoder that only ever met short streams gets wrong.
    /// </summary>
    [Fact]
    public void Lzw_FromLibtiff_DecodesAcrossTheCodeWidthChange()
    {
        byte[] expected = Enumerable.Range(0, 700).Select(i => (byte)((i * 37) ^ (i >> 3))).ToArray();
        Assert.Equal(expected, Decode(Convert.FromBase64String(LibtiffLzw), PdfName.Of("LZWDecode")));
        Assert.Equal(Convert.FromBase64String(LibtiffLzw), TestEncoders.Lzw(expected));
    }

    private const string LibtiffLzw =
        "gAAEpKN6UXLeAYpJhzSy9cAHFRSO6YXrmBYsKJ7TzBcgPGRVPCkYLuBYwLp6U7NdAXGRZQijZz2CY8LKCV7IeofHRlQCrazx" +
        "DhINqCU7ceIWJZhQi7bz1FBPMSaX7QeouIZkTKzawJFhHPSdWDsBImIZ4Sqyd4FEBfPSRZjrAw+LZ0SLKc4AHpXOStZDjDw5" +
        "KiQWraf4EFJOSa5bjjAwtOCXXTCc4IFp2Ta8YLrBxROifUTFdA0KJ+TagZrqG5dPCHUTJCgzLxmQasZISH5YMqPVT1CA7Ixh" +
        "RjYfoSG5MMKGbbxCgrJxlXDfeQKF5AMq+ab0BIjIyZXjXdQNEB8TK2aboAoiP6VWDPdQBLh7SyuZbkAJaO6QVrHcQ9LBzRxU" +
        "mIEACjUJ5aEyfwRgSNgplsTpgBOBQ5CuXBOmMFYGDiLZfHCYgXgkOosEIcJnBWCA+i0Q52mQG4JDyUhBncawZg4PJREedBqh" +
        "+DQilARZ7GiHg0CaURDn4aIbDWIJSE2fxqgQN4hF0T54GqBwxiIXJJnsFIGDOLRekgZgUgMMYsFqSRnhKAA/i0WJ2GWEwPD2" +
        "KhYnUY4QA6O4pEadBhh8DI6FgSp9G+EgEjcWZMn4YYTAaKBbk0cRjhQBorF2ThwmWFw4ioX5BHKZAKDiLxdkAdplAuPosFOQ" +
        "R0hoCY/CMUZGHSGQPjwIpXkUaoYA2MwglYfBuhkC42CCUx9miGgFjcIpMH+aQVAeMAik8eZqBSAYzFyTh7mUFoAC4XJLHmZA" +
        "SgEL5akgd5lBCPgtlsRx1mIEI9CuWBGnOYQOjwKZXEScjqOkah8k8IY0gYGZrH6UAjjU4jhFMJY2AiHZvECUgnjkCocHIQJX" +
        "CWOAOh0c5GlQvQMmIcZHFsKY+AyYR3kQWq2BKYB1ksWIuMoyROFiKwFhCYh9k8Wo0AeETCMENwBhIbJ5ksJI2AOHRungVgkj" +
        "MAYcGqeRXiKMCdGiRhViMPwNhoaJFFOIA+pYdpEFGLw8goqipCINIHGmfJOFGIw2hga59KIoQbG2fhAlWJwIhob5xEKVA6Ai" +
        "HxtnARpVDuDocP0RIqDmDwTGGdhEikP4MBK9haiggIA=";

    [Fact]
    public void Lzw_CorruptOrTruncated_IsRefused()
    {
        byte[] good = TestEncoders.Lzw(Sample);
        Assert.ThrowsAny<FormatException>(() => Decode(good[..(good.Length / 2)], PdfName.Of("LZWDecode")));
        Assert.ThrowsAny<FormatException>(() => Decode(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, PdfName.Of("LZWDecode")));
    }

    [Fact]
    public void AsciiHex_IgnoresWhitespace_AndPadsAnOddFinalDigit()
    {
        byte[] encoded = Encoding.ASCII.GetBytes("48 65\n6C6C 6F2>");
        Assert.Equal(Encoding.ASCII.GetBytes("Hello "), Decode(encoded, PdfName.Of("AHx")));
        Assert.ThrowsAny<FormatException>(() => Decode(Encoding.ASCII.GetBytes("48G5>"), PdfName.Of("ASCIIHexDecode")));
    }

    [Fact]
    public void Ascii85_RoundTrips_WithZGroupsAndPartialFinalGroup()
    {
        byte[] data = Sample.Concat(new byte[8]).Concat(new byte[] { 1, 2, 3 }).ToArray();
        string encoded = TestEncoders.Ascii85(data);
        Assert.Contains("z", encoded);
        Assert.Equal(data, Decode(Encoding.ASCII.GetBytes("<~" + encoded + "~>"), PdfName.Of("A85")));
    }

    [Fact]
    public void Ascii85_MillionsOfZeroGroups_DecodeWithoutExhaustingTheStack()
    {
        // Each 'z' is four zero bytes. Allocating them on the stack per character would overflow
        // it, which .NET cannot catch: the whole host would die on one crafted stream.
        byte[] encoded = Enumerable.Repeat((byte)'z', 2_000_000).Concat("~>"u8.ToArray()).ToArray();
        byte[] decoded = Decode(encoded, PdfName.Of("ASCII85Decode"));
        Assert.Equal(8_000_000, decoded.Length);
        Assert.All(decoded.Take(16), b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData("ab{de~>", "alphabet")]
    [InlineData("abzde~>", "'z' inside a group")]
    [InlineData("abcdea~>", "single-character group")]
    [InlineData("uuuuu~>", "over 2^32")]
    public void Ascii85_Corrupt_IsRefused(string encoded, string reason)
    {
        var ex = Assert.ThrowsAny<FormatException>(() => Decode(Encoding.ASCII.GetBytes(encoded), PdfName.Of("ASCII85Decode")));
        Assert.Contains(reason, ex.Message);
    }

    [Fact]
    public void RunLength_RoundTrips_AndRefusesTruncation()
    {
        byte[] data = Encoding.ASCII.GetBytes("aaaaaaaaaabcdefgggggggggggggggggh");
        byte[] encoded = TestEncoders.RunLength(data);
        Assert.Equal(data, Decode(encoded, PdfName.Of("RL")));

        Assert.ThrowsAny<FormatException>(() => Decode(new byte[] { 10, (byte)'a' }, PdfName.Of("RunLengthDecode")));
        Assert.ThrowsAny<FormatException>(() => Decode(new byte[] { 250 }, PdfName.Of("RunLengthDecode")));
    }

    [Theory]
    [InlineData(0)] // None
    [InlineData(1)] // Sub
    [InlineData(2)] // Up
    [InlineData(3)] // Average
    [InlineData(4)] // Paeth
    [InlineData(-1)] // a different type on every row, as PNG optimum does
    public void PngPredictors_AreUndone(int type)
    {
        const int colors = 3, columns = 7, rows = 5;
        var random = new Random(type + 10);
        byte[] image = Enumerable.Range(0, colors * columns * rows).Select(_ => (byte)random.Next(256)).ToArray();
        byte[] predicted = TestEncoders.PngPredict(image, colors, columns, type);
        var parms = new PdfDictionary();
        parms.Put(PdfName.Predictor, new PdfNumber(type == -1 ? 15 : 10 + type));
        parms.Put(PdfName.Colors, new PdfNumber(colors));
        parms.Put(PdfName.Columns, new PdfNumber(columns));

        Assert.Equal(image, Decode(PdfFilters.FlateEncode(predicted), PdfName.FlateDecode, parms));
    }

    [Fact]
    public void TiffPredictor_IsUndone()
    {
        byte[] image = { 10, 20, 30, 40, 50, 60, 5, 5, 5, 6, 6, 6 };
        byte[] differenced = { 10, 20, 30, 30, 30, 30, 5, 5, 5, 1, 1, 1 };
        var parms = new PdfDictionary();
        parms.Put(PdfName.Predictor, new PdfNumber(2));
        parms.Put(PdfName.Colors, new PdfNumber(3));
        parms.Put(PdfName.Columns, new PdfNumber(2));

        Assert.Equal(image, Decode(PdfFilters.FlateEncode(differenced), PdfName.FlateDecode, parms));
    }

    [Fact]
    public void Chain_WithPerFilterParameters()
    {
        var parms = new PdfDictionary();
        parms.Put(PdfName.Predictor, new PdfNumber(12));
        parms.Put(PdfName.Columns, new PdfNumber(4));
        byte[] image = Enumerable.Range(0, 32).Select(i => (byte)(i * 7)).ToArray();
        byte[] encoded = Encoding.ASCII.GetBytes(
            TestEncoders.Ascii85(PdfFilters.FlateEncode(TestEncoders.PngPredict(image, 1, 4, 2))) + "~>");

        byte[] decoded = Decode(encoded, Names("ASCII85Decode", "FlateDecode"),
            new PdfArray(new PdfObject[] { PdfNull.Instance, parms }));

        Assert.Equal(image, decoded);
    }

    [Fact]
    public void Chain_StopsAtAnImageCodec()
    {
        byte[] jpegish = { 0xFF, 0xD8, 0xFF, 0xD9 };
        byte[] encoded = Encoding.ASCII.GetBytes(Convert.ToHexString(jpegish) + ">");
        Assert.Equal(jpegish, Decode(encoded, Names("ASCIIHexDecode", "DCTDecode")));
    }

    [Fact]
    public void UnknownFilter_AndOverlongChain_AreRefused()
    {
        var unknown = Assert.ThrowsAny<FormatException>(() => Decode(Sample, PdfName.Of("RotDecode")));
        Assert.Contains("/RotDecode", unknown.Message);

        var chain = Assert.ThrowsAny<FormatException>(() => Decode(Sample, Names(Enumerable.Repeat("Crypt", 9).ToArray())));
        Assert.Contains("9 filters", chain.Message);
    }
}

/// <summary>Reference encoders for the filter tests, written from the specifications.</summary>
internal static class TestEncoders
{
    /// <summary>LZW as PDF and TIFF use it: MSB-first, with a clear code at the start and when the table fills.</summary>
    public static byte[] Lzw(byte[] data, int earlyChange = 1)
    {
        var output = new List<byte>();
        int buffer = 0, bits = 0, width = 9;
        void Emit(int code)
        {
            buffer = (buffer << width) | code;
            bits += width;
            while (bits >= 8) { output.Add((byte)(buffer >> (bits - 8))); bits -= 8; }
            buffer &= (1 << bits) - 1;
        }

        var table = new Dictionary<string, int>();
        void Reset()
        {
            table.Clear();
            for (int i = 0; i < 256; i++) table[((char)i).ToString()] = i;
            width = 9;
        }

        Reset();
        Emit(256);
        int next = 258;
        string w = "";
        foreach (byte b in data)
        {
            string wc = w + (char)b;
            if (table.ContainsKey(wc)) { w = wc; continue; }
            Emit(table[w]);
            table[wc] = next++;
            if (next + earlyChange > (1 << width) && width < 12) width++;
            if (next >= 4094)
            {
                Emit(256);
                Reset();
                next = 258;
            }
            w = ((char)b).ToString();
        }
        if (w.Length > 0)
        {
            Emit(table[w]);
            next++;
            if (next + earlyChange > (1 << width) && width < 12) width++;
        }
        Emit(257);
        if (bits > 0) output.Add((byte)(buffer << (8 - bits)));
        return output.ToArray();
    }

    public static string Ascii85(byte[] data)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < data.Length; i += 4)
        {
            int n = Math.Min(4, data.Length - i);
            uint value = 0;
            for (int k = 0; k < 4; k++) value = (value << 8) | (k < n ? data[i + k] : 0u);
            if (n == 4 && value == 0) { sb.Append('z'); continue; }
            var group = new char[5];
            for (int k = 4; k >= 0; k--) { group[k] = (char)('!' + value % 85); value /= 85; }
            sb.Append(group, 0, n + 1);
        }
        return sb.ToString();
    }

    public static byte[] RunLength(byte[] data)
    {
        var output = new List<byte>();
        int i = 0;
        while (i < data.Length)
        {
            int run = 1;
            while (i + run < data.Length && run < 128 && data[i + run] == data[i]) run++;
            if (run >= 2)
            {
                output.Add((byte)(257 - run));
                output.Add(data[i]);
                i += run;
                continue;
            }
            int start = i;
            while (i < data.Length && i - start < 128 && !(i + 1 < data.Length && data[i] == data[i + 1])) i++;
            output.Add((byte)(i - start - 1));
            output.AddRange(data[start..i]);
        }
        output.Add(128); // EOD
        return output.ToArray();
    }

    /// <summary>PNG row filtering (RFC 2083 §6); <paramref name="type"/> -1 cycles through all five.</summary>
    public static byte[] PngPredict(byte[] image, int bytesPerPixel, int columns, int type)
    {
        int rowBytes = bytesPerPixel * columns;
        var output = new List<byte>();
        var prior = new byte[rowBytes];
        for (int r = 0; r * rowBytes < image.Length; r++)
        {
            var row = image.AsSpan(r * rowBytes, rowBytes).ToArray();
            int t = type == -1 ? r % 5 : type;
            output.Add((byte)t);
            for (int i = 0; i < rowBytes; i++)
            {
                int left = i >= bytesPerPixel ? row[i - bytesPerPixel] : 0;
                int up = prior[i];
                int upLeft = i >= bytesPerPixel ? prior[i - bytesPerPixel] : 0;
                int predictor = t switch
                {
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    4 => Paeth(left, up, upLeft),
                    _ => 0,
                };
                output.Add((byte)(row[i] - predictor));
            }
            prior = row;
        }
        return output.ToArray();
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
