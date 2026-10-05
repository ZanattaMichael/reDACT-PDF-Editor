using System.Runtime.InteropServices;
using PdfEditor.Core.Pdf;
using SkiaSharp;

namespace PdfEditor.Core;

/// <summary>What colour a scrubbed area of an image is painted.</summary>
internal enum ScrubFill
{
    /// <summary>Opaque black — redaction, where the point is that something was removed.</summary>
    Black,

    /// <summary>
    /// The dominant colour immediately around the scrubbed area, so erasing words from a scanned
    /// page leaves paper rather than a black bar. Falls back to white when there is nothing to
    /// sample.
    /// </summary>
    SurroundingPaper,
}

/// <summary>
/// Blacks out the pixels of an image XObject that fall inside redaction regions,
/// re-encoding the image so the original pixel data is truly gone.
/// </summary>
internal static class ImageScrubber
{
    /// <summary>
    /// Attempts to scrub the region overlap out of the image's pixel data.
    /// <paramref name="drawnBBox"/> is the user-space rectangle the image is drawn into.
    /// Returns false when the image could not be scrubbed, in which case the caller must fall back
    /// to dropping the image; <paramref name="failureReason"/> then says why, so the drop is a named
    /// failure rather than a silent one (#88) — a decode failure in particular can be transient
    /// (memory pressure), which reads very differently from an unsupported format.
    /// </summary>
    public static bool TryScrubPixels(PdfStream imageStream, PdfRect drawnBBox,
        IList<PdfRect> regions, out string? failureReason, ScrubFill fill = ScrubFill.Black,
        PdfDictionary? resources = null)
    {
        failureReason = null;
        try
        {
            using var bitmap = PdfImages.TryDecode(imageStream, resources, out string? decodeFailure);
            if (bitmap == null)
            {
                failureReason = "the image could not be decoded — " + (decodeFailure ?? "an unsupported/corrupt format");
                return false;
            }

            float sx = bitmap.Width / drawnBBox.Width;
            float sy = bitmap.Height / drawnBBox.Height;
            var targets = new List<SKRect>();
            foreach (var region in regions)
            {
                float left = Math.Max(region.Left, drawnBBox.Left);
                float right = Math.Min(region.Right, drawnBBox.Right);
                float bottom = Math.Max(region.Bottom, drawnBBox.Bottom);
                float top = Math.Min(region.Top, drawnBBox.Top);
                if (left >= right || bottom >= top) continue;
                // Image rows run top-down while PDF user space runs bottom-up.
                float px = (left - drawnBBox.Left) * sx;
                float pyTop = (drawnBBox.Top - top) * sy;
                targets.Add(SKRect.Create(px, pyTop, (right - left) * sx, (top - bottom) * sy));
            }
            if (targets.Count == 0) return true;

            using var canvas = new SKCanvas(bitmap);
            using var paint = new SKPaint { Style = SKPaintStyle.Fill };
            foreach (var target in targets)
            {
                // Sampled per rect, before anything is painted, so one rect's fill can never be
                // read back as another's surroundings.
                paint.Color = fill == ScrubFill.SurroundingPaper ? PaperAround(bitmap, target) : SKColors.Black;
                canvas.DrawRect(target, paint);
            }
            canvas.Flush();

            byte[]? rgb = ReadRgb(bitmap);
            // No readable pixels means we cannot prove the scrubbed image is what gets written, so
            // report failure and let the caller drop the image outright. Failing closed is the only
            // safe direction here: this is redaction.
            if (rgb == null)
            {
                failureReason = "the scrubbed pixels could not be read back "
                    + "(possibly transient memory pressure)";
                return false;
            }
            ReplaceWithRgb(imageStream, bitmap.Width, bitmap.Height, rgb);
            return true;
        }
        catch (Exception e)
        {
            failureReason = $"scrubbing threw {e.GetType().Name}: {e.Message}";
            return false;
        }
    }

    /// <summary>
    /// The dominant colour in a band just outside <paramref name="target"/> — the paper the words
    /// being erased were printed on. The mode rather than the mean, because a band that clips a
    /// neighbouring glyph or a rule would drag an average toward grey, while the most common colour
    /// is still the background. Colours are bucketed to 16 levels per channel first so a scan's
    /// sensor noise doesn't split one paper colour across thousands of near-identical values.
    /// </summary>
    private static SKColor PaperAround(SKBitmap bitmap, SKRect target)
    {
        const int band = 4;
        var outer = SKRect.Create(target.Left - band, target.Top - band,
            target.Width + 2 * band, target.Height + 2 * band);
        var counts = new Dictionary<int, (int Count, SKColor Colour)>();
        int x0 = Math.Max(0, (int)outer.Left), x1 = Math.Min(bitmap.Width - 1, (int)outer.Right);
        int y0 = Math.Max(0, (int)outer.Top), y1 = Math.Min(bitmap.Height - 1, (int)outer.Bottom);

        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                if (target.Contains(x, y)) continue; // inside the area being erased: not paper
                var c = bitmap.GetPixel(x, y);
                int key = (c.Red >> 4) << 8 | (c.Green >> 4) << 4 | c.Blue >> 4;
                var seen = counts.TryGetValue(key, out var v) ? v : (0, c);
                counts[key] = (seen.Item1 + 1, seen.Item2);
            }
        }
        if (counts.Count == 0) return SKColors.White; // the rect covers the whole image
        return counts.Values.OrderByDescending(v => v.Count).First().Colour;
    }

    /// <summary>
    /// Reads the scrubbed bitmap out as packed 8-bit RGB, the form the PDF stream wants.
    /// <para>
    /// One bulk <see cref="SKPixmap.ReadPixels(SKImageInfo, IntPtr, int, int, int)"/> into a pinned
    /// buffer, then a walk to drop the alpha byte. This used to encode the bitmap to PNG, decode that
    /// PNG straight back into a second bitmap, and read the copy a pixel at a time through
    /// <c>GetPixel</c> — one managed-to-native call per pixel, 16.7 million of them on a 4096-square
    /// image, on top of a PNG compress/decompress round trip whose output was never otherwise used
    /// (the already-scrubbed bitmap was passed in and ignored). Redacting over a 4096-square image
    /// took 8.5s because of it, against a 20s fuzz budget it intermittently blew on CI.
    /// </para>
    /// <para>
    /// <see cref="SKAlphaType.Unpremul"/> is requested deliberately: <c>GetPixel</c> returned
    /// unpremultiplied components, and since the stream written below has no alpha channel, taking
    /// premultiplied bytes instead would darken every partially transparent pixel toward black.
    /// </para>
    /// </summary>
    /// <returns>Packed RGB, three bytes per pixel; null if the pixels could not be read.</returns>
    private static byte[]? ReadRgb(SKBitmap bitmap)
    {
        int width = bitmap.Width, height = bitmap.Height;
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        int rowBytes = info.RowBytes;
        var rgba = new byte[checked(rowBytes * height)];

        using var pixels = bitmap.PeekPixels();
        if (pixels == null) return null;

        var pin = GCHandle.Alloc(rgba, GCHandleType.Pinned);
        try
        {
            if (!pixels.ReadPixels(info, pin.AddrOfPinnedObject(), rowBytes, 0, 0)) return null;
        }
        finally { pin.Free(); }

        var rgb = new byte[checked(width * height * 3)];
        for (int src = 0, dst = 0; src < rgba.Length; src += 4)
        {
            rgb[dst++] = rgba[src];
            rgb[dst++] = rgba[src + 1];
            rgb[dst++] = rgba[src + 2];
        }
        return rgb;
    }

    private static void ReplaceWithRgb(PdfStream imageStream, int width, int height, byte[] rgb)
    {
        // Store as FlateDecoded raw RGB — universally supported and avoids
        // format-specific entries left over from the original image.
        foreach (var key in imageStream.Keys.ToArray())
        {
            if (!PdfName.Subtype.Equals(key) && !PdfName.Type.Equals(key))
                imageStream.Remove(key);
        }
        imageStream.SetData(rgb);
        imageStream.Put(PdfName.Width, new PdfNumber(width));
        imageStream.Put(PdfName.Height, new PdfNumber(height));
        imageStream.Put(PdfName.ColorSpace, PdfName.DeviceRGB);
        imageStream.Put(PdfName.BitsPerComponent, new PdfNumber(8));
    }
}
