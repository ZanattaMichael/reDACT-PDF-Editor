using System.Runtime.InteropServices;
using SkiaSharp;

namespace PdfEditor.Core.Pdf;

/// <summary>
/// Reads the first image of a baseline TIFF — the format document scanners write — for importing
/// a scan as a PDF page. Uncompressed, PackBits, LZW and Deflate strips are decoded to a bitmap;
/// CCITT fax data (the usual compression for black-and-white scans) is not decoded at all but
/// carried into the PDF as-is, since <c>CCITTFaxDecode</c> is a PDF filter every viewer applies.
/// JPEG-in-TIFF and tiled layouts are declined with a clear error.
/// </summary>
internal static class TiffDecoder
{
    private sealed class Ifd
    {
        public int Width, Height, Compression = 1, Photometric = 1, SamplesPerPixel = 1;
        public int Predictor = 1, PlanarConfig = 1, FillOrder = 1, T4Options;
        public int[] BitsPerSample = { 1 };
        public long[] StripOffsets = Array.Empty<long>();
        public long[] StripByteCounts = Array.Empty<long>();
        public int[]? ColorMap;
        public bool Tiled;
        public int ExtraSamples;
    }

    public static bool IsTiff(byte[] data) => data.Length >= 8
        && ((data[0] == 'I' && data[1] == 'I' && data[2] == 42 && data[3] == 0)
            || (data[0] == 'M' && data[1] == 'M' && data[2] == 0 && data[3] == 42));

    /// <summary>Reads integers in the file's byte order; anything past the end of the data reads as 0.</summary>
    private sealed class TiffReader
    {
        private readonly byte[] _d;
        private readonly bool _littleEndian;

        public TiffReader(byte[] d)
        {
            _d = d;
            _littleEndian = d[0] == 'I';
        }

        public uint U16(long at)
        {
            if (at + 2 > _d.Length) return 0u;
            return _littleEndian ? (uint)(_d[at] | _d[at + 1] << 8) : (uint)(_d[at] << 8 | _d[at + 1]);
        }

        public uint U32(long at)
        {
            if (at + 4 > _d.Length) return 0u;
            return _littleEndian
                ? (uint)(_d[at] | _d[at + 1] << 8 | _d[at + 2] << 16 | _d[at + 3] << 24)
                : (uint)(_d[at] << 24 | _d[at + 1] << 16 | _d[at + 2] << 8 | _d[at + 3]);
        }

        /// <summary>
        /// A directory entry's values: <paramref name="n"/> of them (at most 2^20), each
        /// <paramref name="size"/> bytes wide, starting at <paramref name="at"/>.
        /// </summary>
        public long[] Values(long n, int size, long at)
        {
            var v = new long[Math.Min(n, 1 << 20)];
            for (int k = 0; k < v.Length; k++) v[k] = Value(size, at, k);
            return v;
        }

        private long Value(int size, long at, int k) => size switch
        {
            2 => U16(at + k * 2L),
            1 => at + k < _d.Length ? _d[at + k] : 0,
            _ => U32(at + k * 4L),
        };
    }

    private static Ifd ReadFirstIfd(byte[] d)
    {
        var reader = new TiffReader(d);
        long ifd = reader.U32(4);
        if (ifd <= 0 || ifd + 2 > d.Length) throw new ArgumentException("The TIFF has no image directory.");
        int count = (int)reader.U16(ifd);
        var result = new Ifd();
        for (int i = 0; i < count; i++)
        {
            long e = ifd + 2 + i * 12L;
            if (e + 12 > d.Length) break;
            ReadEntry(reader, e, result);
        }
        if (result.Width <= 0 || result.Height <= 0 || (long)result.Width * result.Height > 100_000_000)
            throw new ArgumentException("The TIFF declares no usable image size.");
        return result;
    }

    /// <summary>Reads the 12-byte directory entry at <paramref name="e"/> into <paramref name="result"/>.</summary>
    private static void ReadEntry(TiffReader reader, long e, Ifd result)
    {
        int tag = (int)reader.U16(e), type = (int)reader.U16(e + 2);
        long n = reader.U32(e + 4);
        int size = type switch { 3 => 2, 4 => 4, 1 or 2 or 7 => 1, _ => 4 };
        long valueAt = n * size <= 4 ? e + 8 : reader.U32(e + 8);
        long[] Values() => reader.Values(n, size, valueAt);
        switch (tag)
        {
            case 256: result.Width = (int)Values()[0]; break;
            case 257: result.Height = (int)Values()[0]; break;
            case 258: result.BitsPerSample = Values().Select(v => (int)v).ToArray(); break;
            case 259: result.Compression = (int)Values()[0]; break;
            case 262: result.Photometric = (int)Values()[0]; break;
            case 266: result.FillOrder = (int)Values()[0]; break;
            case 273: result.StripOffsets = Values(); break;
            case 277: result.SamplesPerPixel = (int)Values()[0]; break;
            case 279: result.StripByteCounts = Values(); break;
            case 284: result.PlanarConfig = (int)Values()[0]; break;
            case 292: result.T4Options = (int)Values()[0]; break;
            case 317: result.Predictor = (int)Values()[0]; break;
            case 320: result.ColorMap = Values().Select(v => (int)v).ToArray(); break;
            case 322: case 323: case 324: case 325: result.Tiled = true; break;
            case 338: result.ExtraSamples = (int)n; break;
        }
    }

    /// <summary>A CCITT-compressed single-strip TIFF as a CCITTFaxDecode image XObject; null when not CCITT.</summary>
    public static (PdfStream Image, int Width, int Height)? TryCreateCcittXObject(byte[] data)
    {
        var ifd = ReadFirstIfd(data);
        if (ifd.Compression is not (2 or 3 or 4)) return null;
        if (ifd.StripOffsets.Length != 1)
            throw new ArgumentException("This TIFF stores its fax data in several strips, which cannot be embedded; save it as a single-strip TIFF, PNG or PDF first.");
        long offset = ifd.StripOffsets[0];
        long length = ifd.StripByteCounts.Length > 0 ? ifd.StripByteCounts[0] : data.Length - offset;
        if (offset < 0 || offset + length > data.Length) throw new ArgumentException("The TIFF's image data lies outside the file.");
        byte[] fax = data.AsSpan((int)offset, (int)length).ToArray();
        if (ifd.FillOrder == 2)
            for (int i = 0; i < fax.Length; i++) fax[i] = ReverseBits(fax[i]);

        var image = new PdfStream();
        image.Put(PdfName.Type, PdfName.XObject);
        image.Put(PdfName.Subtype, PdfName.Image);
        image.Put(PdfName.Width, new PdfNumber(ifd.Width));
        image.Put(PdfName.Height, new PdfNumber(ifd.Height));
        image.Put(PdfName.ColorSpace, PdfName.DeviceGray);
        image.Put(PdfName.BitsPerComponent, new PdfNumber(1));
        image.Put(PdfName.Filter, PdfName.Of("CCITTFaxDecode"));
        image.Put(PdfName.DecodeParms, CcittParameters(ifd));
        image.SetRawData(fax);
        return (image, ifd.Width, ifd.Height);
    }

    /// <summary>The CCITTFaxDecode parameters that describe the TIFF's fax data.</summary>
    private static PdfDictionary CcittParameters(Ifd ifd)
    {
        var parms = new PdfDictionary();
        int k = ifd.Compression == 4 ? -1 : ifd.Compression == 3 && (ifd.T4Options & 1) != 0 ? 1 : 0;
        parms.Put(PdfName.K, new PdfNumber(k));
        parms.Put(PdfName.Columns, new PdfNumber(ifd.Width));
        parms.Put(PdfName.Of("Rows"), new PdfNumber(ifd.Height));
        // Photometric 0 is WhiteIsZero: set bits are black.
        if (ifd.Photometric == 0) parms.Put(PdfName.Of("BlackIs1"), PdfBoolean.True);
        if (ifd.Compression == 2 || (ifd.T4Options & 4) != 0) parms.Put(PdfName.Of("EncodedByteAlign"), PdfBoolean.True);
        return parms;
    }

    private static byte ReverseBits(byte b)
    {
        int r = 0;
        for (int i = 0; i < 8; i++) r |= ((b >> i) & 1) << (7 - i);
        return (byte)r;
    }

    /// <summary>Decodes the first image to a bitmap; throws <see cref="ArgumentException"/> for what it cannot read.</summary>
    public static SKBitmap Decode(byte[] data)
    {
        var ifd = ReadFirstIfd(data);
        EnsureDecodable(ifd);
        int bps = ifd.BitsPerSample[0];
        int spp = Math.Max(1, ifd.SamplesPerPixel);
        int rowBytes = (ifd.Width * spp * bps + 7) / 8;
        byte[] pixels = ReadStrips(data, ifd, rowBytes, spp, bps);
        if (pixels.Length < (long)rowBytes * ifd.Height) throw new ArgumentException("The TIFF's image data is truncated.");

        var output = ToRgba(new SampleLayout(pixels, rowBytes, spp, bps), ifd);
        var bitmap = new SKBitmap(new SKImageInfo(ifd.Width, ifd.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        Marshal.Copy(output, 0, bitmap.GetPixels(), output.Length);
        return bitmap;
    }

    private static void EnsureDecodable(Ifd ifd)
    {
        if (ifd.Tiled) throw new ArgumentException("Tiled TIFF images are not supported; save the scan as a striped TIFF, PNG or PDF.");
        if (ifd.Compression is 6 or 7) throw new ArgumentException("JPEG-compressed TIFF images are not supported; save the scan as JPEG, PNG or PDF.");
        if (ifd.Compression is not (1 or 5 or 8 or 32946 or 32773))
            throw new ArgumentException($"TIFF compression {ifd.Compression} is not supported.");
        if (ifd.PlanarConfig != 1 && ifd.SamplesPerPixel > 1) throw new ArgumentException("Planar TIFF images are not supported.");
        int bps = ifd.BitsPerSample[0];
        if (bps is not (1 or 4 or 8 or 16)) throw new ArgumentException($"TIFF images with {bps} bits per sample are not supported.");
    }

    /// <summary>Every strip, decompressed and with any horizontal predictor undone, end to end.</summary>
    private static byte[] ReadStrips(byte[] data, Ifd ifd, int rowBytes, int spp, int bps)
    {
        var raw = new MemoryStream();
        for (int s = 0; s < ifd.StripOffsets.Length; s++)
        {
            long offset = ifd.StripOffsets[s];
            long length = s < ifd.StripByteCounts.Length ? ifd.StripByteCounts[s] : data.Length - offset;
            if (offset < 0 || offset >= data.Length) break;
            length = Math.Min(length, data.Length - offset);
            byte[] strip = data.AsSpan((int)offset, (int)length).ToArray();
            byte[] decoded = Decompress(strip, ifd.Compression);
            if (ifd.Predictor == 2 && bps == 8) UndoHorizontalPredictor(decoded, rowBytes, spp);
            raw.Write(decoded);
        }
        return raw.ToArray();
    }

    private static byte[] Decompress(byte[] strip, int compression) => compression switch
    {
        1 => strip,
        32773 => PackBits(strip),
        5 => TiffLzw(strip),
        _ => PdfFilters.Inflate(strip),
    };

    private static void UndoHorizontalPredictor(byte[] decoded, int rowBytes, int spp)
    {
        for (int row = 0; row * rowBytes < decoded.Length; row++)
            for (int i = row * rowBytes + spp; i < Math.Min(decoded.Length, (row + 1) * rowBytes); i++)
                decoded[i] = (byte)(decoded[i] + decoded[i - spp]);
    }

    /// <summary>Where each pixel's samples sit in the decoded strips.</summary>
    private readonly record struct SampleLayout(byte[] Pixels, int RowBytes, int SamplesPerPixel, int BitsPerSample)
    {
        public int Sample(int x, int y, int c)
        {
            int bit = (x * SamplesPerPixel + c) * BitsPerSample;
            int at = y * RowBytes + bit / 8;
            if (BitsPerSample == 8) return Pixels[at];
            if (BitsPerSample == 16) return Pixels[at]; // the high byte, in either byte order closely enough
            return (Pixels[at] >> (8 - BitsPerSample - bit % 8)) & ((1 << BitsPerSample) - 1);
        }
    }

    private static byte[] ToRgba(SampleLayout layout, Ifd ifd)
    {
        var output = new byte[ifd.Width * ifd.Height * 4];
        int max = (1 << Math.Min(layout.BitsPerSample, 8)) - 1;
        for (int y = 0; y < ifd.Height; y++)
        {
            for (int x = 0; x < ifd.Width; x++)
            {
                var (r, g, b, a) = PixelAt(layout, ifd, x, y, max);
                int o = (y * ifd.Width + x) * 4;
                output[o] = r;
                output[o + 1] = g;
                output[o + 2] = b;
                output[o + 3] = a;
            }
        }
        return output;
    }

    private static (byte R, byte G, byte B, byte A) PixelAt(SampleLayout layout, Ifd ifd, int x, int y, int max)
    {
        int Sample(int c) => layout.Sample(x, y, c);
        switch (ifd.Photometric)
        {
            case 0: // WhiteIsZero
            {
                byte v = (byte)(255 - Sample(0) * 255 / max);
                return (v, v, v, 255);
            }
            case 1: // BlackIsZero
            {
                byte v = (byte)(Sample(0) * 255 / max);
                return (v, v, v, 255);
            }
            case 2: // RGB
                return ((byte)(Sample(0) * 255 / max), (byte)(Sample(1) * 255 / max), (byte)(Sample(2) * 255 / max),
                    ifd.ExtraSamples > 0 && layout.SamplesPerPixel >= 4 ? (byte)(Sample(3) * 255 / max) : (byte)255);
            case 3 when ifd.ColorMap != null: // palette: 16-bit R, G, B tables
            {
                int index = Sample(0), n = ifd.ColorMap.Length / 3;
                int i = Math.Min(index, n - 1);
                return ((byte)(ifd.ColorMap[i] >> 8), (byte)(ifd.ColorMap[n + i] >> 8), (byte)(ifd.ColorMap[2 * n + i] >> 8), 255);
            }
            default:
                throw new ArgumentException($"TIFF photometric interpretation {ifd.Photometric} is not supported.");
        }
    }

    private static byte[] PackBits(byte[] data)
    {
        var output = new MemoryStream(data.Length * 2);
        int i = 0;
        while (i < data.Length)
        {
            sbyte n = (sbyte)data[i++];
            if (n >= 0)
            {
                int count = Math.Min(n + 1, data.Length - i);
                output.Write(data, i, count);
                i += count;
            }
            else if (n != -128 && i < data.Length)
            {
                byte b = data[i++];
                for (int k = 0; k < 1 - n; k++) output.WriteByte(b);
            }
        }
        return output.ToArray();
    }

    /// <summary>TIFF's LZW: MSB-first codes with early change — the PDF decoder with its defaults.</summary>
    private static byte[] TiffLzw(byte[] data)
    {
        try
        {
            return PdfFilters.LzwDecode(data, earlyChange: 1);
        }
        catch (PdfFormatException e)
        {
            throw new ArgumentException("The TIFF's LZW data is corrupt: " + e.Message, e);
        }
    }
}
