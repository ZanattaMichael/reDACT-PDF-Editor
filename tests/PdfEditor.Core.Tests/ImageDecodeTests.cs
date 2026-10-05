using PdfEditor.Core.Pdf;
using SkiaSharp;
using Xunit;

namespace PdfEditor.Tests;

/// <summary>
/// Decoding image XObjects to pixels, which pixel-scrubbing (redacting part of a scanned image)
/// and visual comparison depend on. Each colour-space family a scanned PDF commonly uses is
/// covered, and the decode of an ordinary image is checked against PDFium's rendering of it.
/// </summary>
public class ImageDecodeTests
{
    private static PdfStream Image(int width, int height, PdfObject? colorSpace, int bpc, byte[] data,
        PdfArray? decode = null, bool imageMask = false)
    {
        var image = new PdfStream(data, compress: false);
        image.Put(PdfName.Type, PdfName.XObject);
        image.Put(PdfName.Subtype, PdfName.Image);
        image.Put(PdfName.Width, new PdfNumber(width));
        image.Put(PdfName.Height, new PdfNumber(height));
        if (imageMask) image.Put(PdfName.ImageMask, PdfBoolean.True);
        else image.Put(PdfName.BitsPerComponent, new PdfNumber(bpc));
        if (colorSpace != null) image.Put(PdfName.ColorSpace, colorSpace);
        if (decode != null) image.Put(PdfName.Decode, decode);
        return image;
    }

    private static SKBitmap Decode(PdfStream image, PdfDictionary? resources = null)
    {
        var bitmap = PdfImages.TryDecode(image, resources, out var failure);
        Assert.True(bitmap != null, failure);
        return bitmap!;
    }

    private static (byte, byte, byte) At(SKBitmap b, int x, int y)
    {
        var c = b.GetPixel(x, y);
        return (c.Red, c.Green, c.Blue);
    }

    [Fact]
    public void Gray_AtEveryBitDepth()
    {
        using var g8 = Decode(Image(2, 1, PdfName.DeviceGray, 8, new byte[] { 0, 200 }));
        Assert.Equal(((byte)200, (byte)200, (byte)200), At(g8, 1, 0));

        using var g4 = Decode(Image(2, 1, PdfName.DeviceGray, 4, new byte[] { 0x0F }));
        Assert.Equal(((byte)255, (byte)255, (byte)255), At(g4, 1, 0));

        using var g2 = Decode(Image(4, 1, PdfName.DeviceGray, 2, new byte[] { 0b00_01_10_11 }));
        Assert.Equal(((byte)85, (byte)85, (byte)85), At(g2, 1, 0));

        using var g16 = Decode(Image(1, 1, PdfName.Of("CalGray"), 16, new byte[] { 0x80, 0x00 }));
        Assert.Equal((byte)127, At(g16, 0, 0).Item1);
    }

    [Fact]
    public void Bilevel_WithAnInvertingDecodeArray()
    {
        using var bitmap = Decode(Image(2, 1, PdfName.DeviceGray, 1, new byte[] { 0b1000_0000 }, new PdfArray(1, 0)));
        Assert.Equal(((byte)0, (byte)0, (byte)0), At(bitmap, 0, 0));
        Assert.Equal(((byte)255, (byte)255, (byte)255), At(bitmap, 1, 0));
    }

    [Fact]
    public void StencilMask_PaintsWhereTheSampleIsZero()
    {
        using var bitmap = Decode(Image(2, 1, null, 1, new byte[] { 0b0100_0000 }, imageMask: true));
        Assert.Equal(((byte)0, (byte)0, (byte)0), At(bitmap, 0, 0));
        Assert.Equal(((byte)255, (byte)255, (byte)255), At(bitmap, 1, 0));
    }

    [Fact]
    public void Rgb_AndCmyk()
    {
        using var rgb = Decode(Image(1, 1, PdfName.DeviceRGB, 8, new byte[] { 10, 20, 30 }));
        Assert.Equal(((byte)10, (byte)20, (byte)30), At(rgb, 0, 0));

        using var cmyk = Decode(Image(2, 1, PdfName.DeviceCMYK, 8, new byte[] { 255, 0, 0, 0, 0, 0, 0, 255 }));
        Assert.Equal(((byte)0, (byte)255, (byte)255), At(cmyk, 0, 0)); // pure cyan
        Assert.Equal(((byte)0, (byte)0, (byte)0), At(cmyk, 1, 0));     // pure black
    }

    [Fact]
    public void IccBased_FollowsItsComponentCount()
    {
        foreach (var (n, data, expected) in new[]
        {
            (1, new byte[] { 99 }, ((byte)99, (byte)99, (byte)99)),
            (3, new byte[] { 1, 2, 3 }, ((byte)1, (byte)2, (byte)3)),
            (4, new byte[] { 0, 0, 0, 0 }, ((byte)255, (byte)255, (byte)255)),
        })
        {
            var profile = new PdfStream(new byte[] { 0 });
            profile.Put(PdfName.N, new PdfNumber(n));
            var cs = new PdfArray(new PdfObject[] { PdfName.Of("ICCBased"), profile });
            using var bitmap = Decode(Image(1, 1, cs, 8, data));
            Assert.Equal(expected, At(bitmap, 0, 0));
        }
    }

    [Fact]
    public void Indexed_WithStringAndStreamLookups_AndANamedResource()
    {
        byte[] palette = { 255, 0, 0, 0, 0, 255 };
        var withString = new PdfArray(new PdfObject[] { PdfName.Of("Indexed"), PdfName.DeviceRGB, new PdfNumber(1), new PdfString(palette) });
        using var a = Decode(Image(2, 1, withString, 4, new byte[] { 0x01 << 4 | 0x00 }));
        Assert.Equal(((byte)0, (byte)0, (byte)255), At(a, 0, 0));
        Assert.Equal(((byte)255, (byte)0, (byte)0), At(a, 1, 0));

        var withStream = new PdfArray(new PdfObject[] { PdfName.Of("I"), PdfName.DeviceRGB, new PdfNumber(1), new PdfStream(palette) });
        var resources = new PdfDictionary();
        var spaces = new PdfDictionary();
        spaces.Put(PdfName.Of("CS0"), withStream);
        resources.Put(PdfName.ColorSpace, spaces);
        // Index 7 is past hival: clamped to the last entry.
        using var b = Decode(Image(2, 1, PdfName.Of("CS0"), 8, new byte[] { 0, 7 }), resources);
        Assert.Equal(((byte)255, (byte)0, (byte)0), At(b, 0, 0));
        Assert.Equal(((byte)0, (byte)0, (byte)255), At(b, 1, 0));
    }

    [Fact]
    public void InlineImage_WithAbbreviations()
    {
        var dict = new PdfDictionary();
        dict.Put(PdfName.Of("W"), new PdfNumber(2));
        dict.Put(PdfName.Of("H"), new PdfNumber(1));
        dict.Put(PdfName.Of("BPC"), new PdfNumber(8));
        dict.Put(PdfName.Of("CS"), PdfName.Of("RGB"));
        dict.Put(PdfName.Of("F"), PdfName.Of("AHx"));
        byte[] hex = System.Text.Encoding.ASCII.GetBytes("FF0000 00FF00>");

        var bitmap = PdfImages.TryDecodeInline(dict, hex, null, out var failure);

        Assert.True(bitmap != null, failure);
        using (bitmap)
        {
            Assert.Equal(((byte)255, (byte)0, (byte)0), At(bitmap!, 0, 0));
            Assert.Equal(((byte)0, (byte)255, (byte)0), At(bitmap!, 1, 0));
        }
        var expanded = PdfImages.ExpandInline(dict, hex, null);
        Assert.Equal("DeviceRGB", expanded.GetAsName(PdfName.ColorSpace)!.Value);
        Assert.Equal("ASCIIHexDecode", expanded.GetAsName(PdfName.Filter)!.Value);
    }

    private static readonly Dictionary<string, Func<PdfStream>> UndecodableImages = new()
    {
        ["no usable dimensions"] = () => Image(0, 1, PdfName.DeviceGray, 8, new byte[1]),
        ["beyond the decode limit"] = () => Image(100_000, 100_000, PdfName.DeviceGray, 8, new byte[1]),
        ["3 bits per component"] = () => Image(1, 1, PdfName.DeviceGray, 3, new byte[1]),
        ["shorter than its dimensions"] = () => Image(4, 4, PdfName.DeviceRGB, 8, new byte[5]),
        ["colour space is not one"] = () => Image(1, 1, new PdfArray(new PdfObject[] { PdfName.Of("Separation"), PdfName.Of("Spot"), PdfName.DeviceGray, new PdfDictionary() }), 8, new byte[1]),
        ["/JPXDecode"] = () => WithFilter(Image(1, 1, PdfName.DeviceRGB, 8, new byte[3]), "JPXDecode"),
        ["JPEG data could not be decoded"] = () => WithFilter(Image(1, 1, PdfName.DeviceRGB, 8, new byte[] { 1, 2, 3 }), "DCTDecode"),
        ["threw"] = () => WithFilter(Image(1, 1, PdfName.DeviceRGB, 8, new byte[] { 1, 2, 3 }), "FlateDecode"),
    };

    private static PdfStream WithFilter(PdfStream stream, string filter)
    {
        stream.SetRawData(stream.RawData); // keep the bytes as they are, under the new filter
        stream.Put(PdfName.Filter, PdfName.Of(filter));
        return stream;
    }

    public static IEnumerable<object[]> Undecodable => UndecodableImages.Keys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(Undecodable))]
    public void Undecodable_ReportsWhy(string reason)
    {
        Assert.Null(PdfImages.TryDecode(UndecodableImages[reason](), null, out var failure));
        Assert.Contains(reason, failure);
    }

    [Fact]
    public void Jpeg_IsDecodedThroughItsCodec()
    {
        using var source = new SKBitmap(8, 8);
        using (var canvas = new SKCanvas(source)) canvas.Clear(new SKColor(0, 0, 200));
        using var encoded = SKImage.FromBitmap(source).Encode(SKEncodedImageFormat.Jpeg, 95);
        var (image, _, _) = PdfImages.CreateXObject(encoded.ToArray());

        using var bitmap = Decode(image);
        var (r, g, b) = At(bitmap, 4, 4);
        Assert.True(r < 20 && g < 20 && b > 180, $"expected blue, got {r},{g},{b}");
    }

    [Fact]
    public void GrayJpeg_IsEmbeddedAsDeviceGray_WithoutReencoding()
    {
        using var source = new SKBitmap(new SKImageInfo(6, 4, SKColorType.Gray8));
        using (var canvas = new SKCanvas(source)) canvas.Clear(new SKColor(90, 90, 90));
        byte[] jpeg = SKImage.FromBitmap(source).Encode(SKEncodedImageFormat.Jpeg, 90).ToArray();

        var (image, width, height) = PdfImages.CreateXObject(jpeg);

        Assert.Equal((6, 4), (width, height));
        Assert.Equal("DeviceGray", image.GetAsName(PdfName.ColorSpace)!.Value);
        Assert.Equal(jpeg, image.RawData);
    }

    [Fact]
    public void UnreadableImageFile_IsRefusedWithAPlainReason()
    {
        var ex = Assert.Throws<ArgumentException>(() => PdfImages.CreateXObject(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
        Assert.Contains("could not be decoded", ex.Message);
    }

    [Fact]
    public void Decoding_AgreesWithPdfium()
    {
        // A 4x4 indexed image drawn at 100x100pt: each sample covers a 25pt square, so PDFium's
        // pixel at its centre is that sample's colour without any smoothing.
        byte[] palette = { 230, 20, 20, 20, 160, 20, 20, 20, 230, 240, 240, 40 };
        var cs = new PdfArray(new PdfObject[] { PdfName.Of("Indexed"), PdfName.DeviceRGB, new PdfNumber(3), new PdfString(palette) });
        byte[] samples = Enumerable.Range(0, 16).Select(i => (byte)((i * 3 + i / 4) % 4)).ToArray();
        var image = Image(4, 4, cs, 8, samples);
        image.Put(PdfName.Of("Interpolate"), PdfBoolean.False);

        byte[] pdf = TestPdfs.Build(p =>
        {
            var name = PdfResources.Add(p.Page.GetOrCreateResources(), PdfName.XObject, "Im", image);
            p.Content.SaveState().Transform(100, 0, 0, 100, 100, 500).DrawXObject(name).RestoreState();
        });

        var xobjects = PdfDocument.Open(pdf).GetPage(1).Resources!.GetAsDictionary(PdfName.XObject)!;
        using var decoded = Decode(xobjects.GetAsStream(xobjects.Keys[0])!);
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                // Image row 0 is the top of the drawn square.
                var rendered = TestPdfAssert.PixelAt(pdf, 1, 100 + x * 25 + 12.5f, 600 - y * 25 - 12.5f, 72);
                var (r, g, b) = At(decoded, x, y);
                Assert.True(Math.Abs(r - rendered.Red) <= 2 && Math.Abs(g - rendered.Green) <= 2 && Math.Abs(b - rendered.Blue) <= 2,
                    $"sample ({x},{y}): decoded {r},{g},{b} but PDFium drew {rendered.Red},{rendered.Green},{rendered.Blue}");
            }
    }
}
