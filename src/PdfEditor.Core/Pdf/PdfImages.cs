using System.Runtime.InteropServices;
using SkiaSharp;

namespace PdfEditor.Core.Pdf;

/// <summary>
/// Moves raster images between their PDF form (image XObjects, inline images) and bitmaps:
/// encoding an uploaded PNG/JPEG/… as an XObject, and decoding an XObject's pixels for
/// scrubbing, thumbnails and re-drawing.
/// </summary>
internal static class PdfImages
{
    /// <summary>The largest image, in pixels, the engine will decode. Bigger is a memory attack, not a scan.</summary>
    private const long MaxPixels = 100_000_000;

    // ------------------------------------------------------------------ encoding

    /// <summary>
    /// Builds an image XObject from an encoded image file (the first page of a multi-page TIFF).
    /// JPEG is embedded as-is (DCTDecode — no generation loss); everything else is decoded and
    /// stored as Flate-compressed RGB, with a soft mask when the image has any transparency.
    /// </summary>
    public static (PdfStream Image, int Width, int Height) CreateXObject(byte[] encoded)
    {
        if (TiffDecoder.IsTiff(encoded)) return TiffPageXObject(TiffDecoder.Open(encoded), 0);
        if (TryReadJpegHeader(encoded, out int jw, out int jh, out int components, out bool adobeInverted))
        {
            var jpeg = new PdfStream();
            jpeg.Put(PdfName.Type, PdfName.XObject);
            jpeg.Put(PdfName.Subtype, PdfName.Image);
            jpeg.Put(PdfName.Width, new PdfNumber(jw));
            jpeg.Put(PdfName.Height, new PdfNumber(jh));
            jpeg.Put(PdfName.ColorSpace, components switch { 1 => PdfName.DeviceGray, 4 => PdfName.DeviceCMYK, _ => PdfName.DeviceRGB });
            jpeg.Put(PdfName.BitsPerComponent, new PdfNumber(8));
            if (components == 4 && adobeInverted) jpeg.Put(PdfName.Decode, new PdfArray(1, 0, 1, 0, 1, 0, 1, 0));
            jpeg.Put(PdfName.Filter, PdfName.Of("DCTDecode"));
            jpeg.SetRawData(encoded);
            return (jpeg, jw, jh);
        }

        using var bitmap = DecodeBitmap(encoded)
            ?? throw new ArgumentException("The image could not be decoded; it is corrupt or in an unsupported format.");
        return (FromBitmap(bitmap), bitmap.Width, bitmap.Height);
    }

    /// <summary>
    /// One image XObject per image an encoded file holds: every page of a multi-page TIFF, in
    /// order, and the single image of anything else (as <see cref="CreateXObject"/> builds it).
    /// </summary>
    public static IEnumerable<(PdfStream Image, int Width, int Height)> CreateXObjects(byte[] encoded)
    {
        if (!TiffDecoder.IsTiff(encoded)) return new[] { CreateXObject(encoded) };
        var pages = TiffDecoder.Open(encoded);
        return Enumerable.Range(0, pages.Count).Select(index => TiffPageXObject(pages, index));
    }

    /// <summary>A TIFF page as an image XObject: fax data embedded as it is, anything else decoded.</summary>
    private static (PdfStream Image, int Width, int Height) TiffPageXObject(TiffDecoder.Pages pages, int index)
    {
        if (pages.TryCreateCcittXObject(index) is { } fax) return fax;
        using var bitmap = pages.Decode(index);
        return (FromBitmap(bitmap), bitmap.Width, bitmap.Height);
    }

    /// <summary>Decodes any supported image file (SkiaSharp's formats, plus the first page of a baseline TIFF).</summary>
    public static SKBitmap? DecodeBitmap(byte[] encoded)
    {
        if (TiffDecoder.IsTiff(encoded)) return TiffDecoder.Decode(encoded);
        return SkiaDecode(encoded);
    }

    /// <summary>
    /// Decodes with SkiaSharp, or returns null for data it has no codec for. (SKBitmap.Decode
    /// throws ArgumentNullException in that case, which reached users as a meaningless error.)
    /// </summary>
    private static SKBitmap? SkiaDecode(byte[] encoded)
    {
        using var data = SKData.CreateCopy(encoded);
        using var codec = SKCodec.Create(data);
        return codec == null ? null : SKBitmap.Decode(codec);
    }

    /// <summary>An image XObject holding <paramref name="bitmap"/> as RGB, plus an /SMask when it is translucent.</summary>
    public static PdfStream FromBitmap(SKBitmap bitmap)
    {
        var (rgb, alpha) = ReadRgbAndAlpha(bitmap);
        var image = new PdfStream(rgb);
        image.Put(PdfName.Type, PdfName.XObject);
        image.Put(PdfName.Subtype, PdfName.Image);
        image.Put(PdfName.Width, new PdfNumber(bitmap.Width));
        image.Put(PdfName.Height, new PdfNumber(bitmap.Height));
        image.Put(PdfName.ColorSpace, PdfName.DeviceRGB);
        image.Put(PdfName.BitsPerComponent, new PdfNumber(8));
        if (alpha != null)
        {
            var mask = new PdfStream(alpha);
            mask.Put(PdfName.Type, PdfName.XObject);
            mask.Put(PdfName.Subtype, PdfName.Image);
            mask.Put(PdfName.Width, new PdfNumber(bitmap.Width));
            mask.Put(PdfName.Height, new PdfNumber(bitmap.Height));
            mask.Put(PdfName.ColorSpace, PdfName.DeviceGray);
            mask.Put(PdfName.BitsPerComponent, new PdfNumber(8));
            image.Put(PdfName.SMask, mask);
        }
        return image;
    }

    /// <summary>Packed 8-bit RGB, and packed alpha when any pixel is not fully opaque (else null).</summary>
    public static (byte[] Rgb, byte[]? Alpha) ReadRgbAndAlpha(SKBitmap bitmap)
    {
        int width = bitmap.Width, height = bitmap.Height;
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var rgba = new byte[checked(info.RowBytes * height)];
        using (var pixels = bitmap.PeekPixels())
        {
            var pin = GCHandle.Alloc(rgba, GCHandleType.Pinned);
            try
            {
                if (pixels == null || !pixels.ReadPixels(info, pin.AddrOfPinnedObject(), info.RowBytes, 0, 0))
                    throw new InvalidOperationException("The image's pixels could not be read.");
            }
            finally
            {
                pin.Free();
            }
        }
        var rgb = new byte[checked(width * height * 3)];
        var alpha = new byte[width * height];
        bool translucent = false;
        for (int src = 0, dst = 0, a = 0; src < rgba.Length; src += 4, a++)
        {
            rgb[dst++] = rgba[src];
            rgb[dst++] = rgba[src + 1];
            rgb[dst++] = rgba[src + 2];
            alpha[a] = rgba[src + 3];
            translucent |= rgba[src + 3] != 255;
        }
        return (rgb, translucent ? alpha : null);
    }

    /// <summary>Reads a JPEG's frame header; false when the bytes are not a JPEG this can embed as-is.</summary>
    internal static bool TryReadJpegHeader(byte[] data, out int width, out int height, out int components, out bool adobeInverted)
    {
        width = height = components = 0;
        adobeInverted = false;
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return false;
        int p = 2;
        while (p + 4 <= data.Length)
        {
            if (data[p] != 0xFF) return false;
            byte marker = data[p + 1];
            if (marker == 0xFF) { p++; continue; }
            if (IsStandaloneMarker(marker)) { p += 2; continue; }
            int length = data[p + 2] << 8 | data[p + 3];
            if (length < 2 || p + 2 + length > data.Length) return false;
            adobeInverted |= IsAdobeSegment(data, p, marker, length);
            if (IsStartOfFrame(marker)) return TryReadFrame(data, p, length, out width, out height, out components);
            p += 2 + length;
        }
        return false;
    }

    /// <summary>Markers that stand alone, with no length or segment after them.</summary>
    private static bool IsStandaloneMarker(byte marker) => marker is 0xD8 or 0x01 || (marker >= 0xD0 && marker <= 0xD7);

    /// <summary>Adobe-written CMYK JPEGs store inverted values, and say so in an APP14 "Adobe" segment.</summary>
    private static bool IsAdobeSegment(byte[] data, int p, byte marker, int length) =>
        marker == 0xEE && length >= 12 && data[p + 4] == 'A' && data[p + 5] == 'd' && data[p + 6] == 'o';

    private static bool IsStartOfFrame(byte marker) => marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);

    private static bool TryReadFrame(byte[] data, int p, int length, out int width, out int height, out int components)
    {
        width = height = components = 0;
        if (length < 8) return false;
        height = data[p + 5] << 8 | data[p + 6];
        width = data[p + 7] << 8 | data[p + 8];
        components = data[p + 9];
        return width > 0 && height > 0 && components is 1 or 3 or 4;
    }

    // ------------------------------------------------------------------ decoding

    /// <summary>
    /// Decodes an image XObject to a bitmap. Returns null with <paramref name="failure"/> set when
    /// the image cannot be decoded (an unsupported codec such as JPX/JBIG2/CCITT, an exotic
    /// colour space, or corrupt data) — callers that must not leave pixels behind fail closed.
    /// </summary>
    public static SKBitmap? TryDecode(PdfStream image, PdfDictionary? resources, out string? failure)
        => TryDecode(image, image.GetDecodedBytes, image.FilterNames(), resources, out failure);

    /// <summary>Decodes an inline image (abbreviated keys allowed).</summary>
    public static SKBitmap? TryDecodeInline(PdfDictionary dict, byte[] data, PdfDictionary? resources, out string? failure)
    {
        var expanded = ExpandInline(dict, data, resources);
        return TryDecode(expanded, expanded.GetDecodedBytes, expanded.FilterNames(), resources, out failure);
    }

    private static SKBitmap? TryDecode(PdfDictionary image, Func<byte[]> decode, IReadOnlyList<string> filters,
        PdfDictionary? resources, out string? failure)
    {
        failure = null;
        try
        {
            int width = image.GetAsInt(PdfName.Width) ?? 0;
            int height = image.GetAsInt(PdfName.Height) ?? 0;
            if (width <= 0 || height <= 0) { failure = "the image declares no usable dimensions"; return null; }
            if ((long)width * height > MaxPixels) { failure = $"the image is {width}x{height} pixels, beyond the decode limit"; return null; }

            string? codec = filters.LastOrDefault(PdfFilters.IsImageFilter);
            byte[] data = decode();
            if (codec is "DCTDecode" or "DCT") return DecodeJpeg(data, out failure);
            if (codec != null) { failure = $"images compressed with /{codec} cannot be decoded by this editor"; return null; }

            bool mask = image.GetAsBool(PdfName.ImageMask) == true;
            int bpc = mask ? 1 : image.GetAsInt(PdfName.BitsPerComponent) ?? 8;
            if (bpc is not (1 or 2 or 4 or 8 or 16)) { failure = $"the image declares {bpc} bits per component, which PDF does not allow"; return null; }

            var colorSpace = mask ? null : ResolveColorSpace(image.Get(PdfName.ColorSpace), resources);
            var converter = mask ? Converter.Mask() : Converter.For(colorSpace, image.GetAsArray(PdfName.Decode), bpc);
            if (converter == null) { failure = "the image's colour space is not one this editor can decode"; return null; }

            int rowBytes = (width * converter.Components * bpc + 7) / 8;
            if ((long)rowBytes * height > data.Length) { failure = "the image data is shorter than its dimensions require"; return null; }

            var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            var pixels = DecodePixels(data, width, height, rowBytes, converter, bpc);
            Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
            return bitmap;
        }
        catch (Exception e) when (e is PdfFormatException or ArgumentException or InvalidOperationException
                                       or OverflowException or OutOfMemoryException)
        {
            failure = $"decoding threw {e.GetType().Name}: {e.Message}";
            return null;
        }
    }

    private static SKBitmap? DecodeJpeg(byte[] data, out string? failure)
    {
        var jpeg = SkiaDecode(data);
        failure = jpeg == null ? "the JPEG data could not be decoded" : null;
        return jpeg;
    }

    /// <summary>Unpacks the samples row by row into opaque RGBA pixels.</summary>
    private static byte[] DecodePixels(byte[] data, int width, int height, int rowBytes, Converter converter, int bpc)
    {
        var pixels = new byte[width * height * 4];
        var samples = new int[converter.Components];
        int max = (1 << bpc) - 1;
        for (int y = 0; y < height; y++)
        {
            int rowStart = y * rowBytes;
            for (int x = 0; x < width; x++)
            {
                for (int c = 0; c < converter.Components; c++)
                    samples[c] = Sample(data, rowStart, (x * converter.Components + c) * bpc, bpc);
                var (r, g, b) = converter.ToRgb(samples, max);
                int o = (y * width + x) * 4;
                pixels[o] = r;
                pixels[o + 1] = g;
                pixels[o + 2] = b;
                pixels[o + 3] = 255;
            }
        }
        return pixels;
    }

    private static int Sample(byte[] data, int rowStart, int bitOffset, int bpc)
    {
        if (bpc == 8) return data[rowStart + bitOffset / 8];
        if (bpc == 16) return data[rowStart + bitOffset / 8] << 8 | data[rowStart + bitOffset / 8 + 1];
        int b = data[rowStart + bitOffset / 8];
        int shift = 8 - bpc - bitOffset % 8;
        return (b >> shift) & ((1 << bpc) - 1);
    }

    private static PdfObject? ResolveColorSpace(PdfObject? cs, PdfDictionary? resources)
    {
        if (cs is PdfName name && name.Value is not ("DeviceGray" or "DeviceRGB" or "DeviceCMYK" or "G" or "RGB" or "CMYK"
                or "CalGray" or "CalRGB" or "Indexed" or "I"))
            return resources?.GetAsDictionary(PdfName.ColorSpace)?.Get(name) ?? cs;
        return cs;
    }

    /// <summary>Maps image samples to RGB for the colour spaces scrubbing has to handle.</summary>
    private sealed class Converter
    {
        public int Components { get; private init; }
        private Func<int[], int, (byte, byte, byte)> _map = (_, _) => (0, 0, 0);

        public (byte R, byte G, byte B) ToRgb(int[] samples, int max) => _map(samples, max);

        public static Converter Mask() => new()
        {
            Components = 1,
            // A stencil mask paints where the sample is 0 (with the default /Decode).
            _map = (s, _) => s[0] == 0 ? ((byte)0, (byte)0, (byte)0) : ((byte)255, (byte)255, (byte)255),
        };

        public static Converter? For(PdfObject? colorSpace, PdfArray? decode, int bpc)
        {
            string? family = colorSpace switch
            {
                PdfName n => n.Value,
                PdfArray a => a.GetAsName(0)?.Value,
                null => "DeviceGray",
                _ => null,
            };
            bool invert = decode is { Count: >= 2 } && decode.GetNumber(0) > decode.GetNumber(1);
            switch (family)
            {
                case "DeviceGray" or "G" or "CalGray" or "DeviceRGB" or "RGB" or "CalRGB" or "DeviceCMYK" or "CMYK":
                    return ForDevice(family, invert);
                case "ICCBased" when colorSpace is PdfArray icc:
                    int n = icc.GetAsStream(1)?.GetAsInt(PdfName.N) ?? 3;
                    return For(n switch { 1 => PdfName.DeviceGray, 4 => PdfName.DeviceCMYK, _ => PdfName.DeviceRGB }, decode, bpc);
                case "Indexed" or "I" when colorSpace is PdfArray indexed:
                    return ForIndexed(indexed);
                default:
                    return null;
            }
        }

        private static byte Scale(int v, int max, bool invert) => (byte)(invert ? 255 - v * 255 / max : v * 255 / max);

        private static Converter? ForDevice(string family, bool invert) => family switch
        {
            "DeviceGray" or "G" or "CalGray" => new Converter
            {
                Components = 1,
                _map = (s, max) => { byte g = Scale(s[0], max, invert); return (g, g, g); },
            },
            "DeviceRGB" or "RGB" or "CalRGB" => new Converter
            {
                Components = 3,
                _map = (s, max) => (Scale(s[0], max, invert), Scale(s[1], max, invert), Scale(s[2], max, invert)),
            },
            "DeviceCMYK" or "CMYK" => new Converter
            {
                Components = 4,
                _map = (s, max) =>
                {
                    double c = Scale(s[0], max, invert) / 255.0, m = Scale(s[1], max, invert) / 255.0;
                    double y = Scale(s[2], max, invert) / 255.0, k = Scale(s[3], max, invert) / 255.0;
                    return ((byte)(255 * (1 - c) * (1 - k)), (byte)(255 * (1 - m) * (1 - k)), (byte)(255 * (1 - y) * (1 - k)));
                },
            },
            _ => null,
        };

        private static Converter? ForIndexed(PdfArray indexed)
        {
            var baseConverter = For(indexed.Get(1), null, 8);
            int hival = indexed.GetAsNumber(2)?.IntValue() ?? 0;
            byte[] lookup = indexed.Get(3) switch
            {
                PdfString s => s.Bytes,
                PdfStream st => st.GetDecodedBytes(),
                _ => Array.Empty<byte>(),
            };
            if (baseConverter == null) return null;
            int comps = baseConverter.Components;
            return new Converter
            {
                Components = 1,
                _map = (s, _) =>
                {
                    int index = Math.Clamp(s[0], 0, hival) * comps;
                    if (index + comps > lookup.Length) return (0, 0, 0);
                    var entry = new int[comps];
                    for (int k = 0; k < comps; k++) entry[k] = lookup[index + k];
                    return baseConverter._map(entry, 255);
                },
            };
        }
    }

    // ------------------------------------------------------------------ inline images

    private static readonly Dictionary<string, string> InlineKeys = new(StringComparer.Ordinal)
    {
        ["W"] = "Width", ["H"] = "Height", ["BPC"] = "BitsPerComponent", ["CS"] = "ColorSpace",
        ["F"] = "Filter", ["DP"] = "DecodeParms", ["IM"] = "ImageMask", ["D"] = "Decode", ["I"] = "Interpolate",
    };

    private static readonly Dictionary<string, string> InlineValues = new(StringComparer.Ordinal)
    {
        ["G"] = "DeviceGray", ["RGB"] = "DeviceRGB", ["CMYK"] = "DeviceCMYK", ["I"] = "Indexed",
        ["AHx"] = "ASCIIHexDecode", ["A85"] = "ASCII85Decode", ["LZW"] = "LZWDecode", ["Fl"] = "FlateDecode",
        ["RL"] = "RunLengthDecode", ["CCF"] = "CCITTFaxDecode", ["DCT"] = "DCTDecode",
    };

    /// <summary>
    /// Turns an inline image into an equivalent image XObject: abbreviations expanded, a named
    /// colour space resolved from <paramref name="resources"/> (an XObject cannot see the page's
    /// colour-space resources by name).
    /// </summary>
    public static PdfStream ExpandInline(PdfDictionary dict, byte[] data, PdfDictionary? resources)
    {
        var stream = new PdfStream();
        stream.Put(PdfName.Type, PdfName.XObject);
        stream.Put(PdfName.Subtype, PdfName.Image);
        foreach (var key in dict.Keys)
        {
            string k = InlineKeys.TryGetValue(key.Value, out var full) ? full : key.Value;
            var value = Expand(dict.Get(key));
            if (k == "ColorSpace" && value is PdfName csName && csName.Value is not ("DeviceGray" or "DeviceRGB" or "DeviceCMYK" or "Indexed"))
                value = resources?.GetAsDictionary(PdfName.ColorSpace)?.Get(csName) ?? value;
            if (value != null) stream.Put(PdfName.Of(k), value);
        }
        stream.SetRawData(data);
        return stream;
    }

    private static PdfObject? Expand(PdfObject? value) => value switch
    {
        PdfName n when InlineValues.TryGetValue(n.Value, out var full) => PdfName.Of(full),
        PdfArray a => new PdfArray(a.Select(v => Expand(v) ?? PdfNull.Instance)),
        _ => value,
    };
}
