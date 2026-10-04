using SkiaSharp;

namespace PdfEditor.Core;

/// <summary>
/// A rendered, pixel-level diff of one page across two document versions. Unlike the word-level
/// <see cref="DocComparer"/>, this catches changes text extraction misses — moved images, vector
/// art, font/spacing shifts, scanned content — by rendering both pages and comparing the pixels.
/// The output is an image that fades the unchanged content to grey and paints the differences:
/// content only in the new version in red (added), content only in the old version in blue
/// (removed). (#46)
/// </summary>
public static class VisualDiff
{
    // Sum-of-channel-differences above which a pixel counts as changed. ~60/765 tolerates
    // anti-aliasing and JPEG-ish noise while still catching a real glyph or line.
    private const int ChangedThreshold = 60;

    private static readonly SKColor Added = new(220, 30, 30);    // in new, not old
    private static readonly SKColor Removed = new(30, 90, 220);  // in old, not new
    private static readonly SKColor Paper = SKColors.White;

    /// <summary>
    /// Renders <paramref name="page"/> from both versions and returns a diff image plus the fraction
    /// of pixels that changed. A page missing from one side is treated as blank, so its entire
    /// content shows as added or removed.
    /// </summary>
    public static VisualPageDiff DiffPage(byte[] oldPdf, byte[] newPdf, int page,
        int dpi = 120, string? oldPassword = null, string? newPassword = null)
    {
        using var oldBmp = RenderOrNull(oldPdf, page, dpi, oldPassword);
        using var newBmp = RenderOrNull(newPdf, page, dpi, newPassword);

        int w = Math.Max(oldBmp?.Width ?? 0, newBmp?.Width ?? 0);
        int h = Math.Max(oldBmp?.Height ?? 0, newBmp?.Height ?? 0);
        if (w == 0 || h == 0)
            return new VisualPageDiff(page, 0d, BlankPng());

        // Draw each side onto a white canvas of the common size, so the two are the same dimensions
        // and colour format before the pixel walk (a page that grew/shrank still diffs cleanly).
        var oldPx = OnWhite(oldBmp, w, h);
        var newPx = OnWhite(newBmp, w, h);

        var outPx = new SKColor[w * h];
        long changed = 0;
        for (int i = 0; i < outPx.Length; i++)
        {
            SKColor o = oldPx[i], n = newPx[i];
            int dist = Math.Abs(o.Red - n.Red) + Math.Abs(o.Green - n.Green) + Math.Abs(o.Blue - n.Blue);
            if (dist > ChangedThreshold)
            {
                changed++;
                // Whichever side is darker holds the ink: darker-new = added, darker-old = removed.
                outPx[i] = Luma(n) < Luma(o) ? Added : Removed;
            }
            else
            {
                // Unchanged: keep context but fade it right back so the highlights dominate.
                outPx[i] = Fade(n, 0.22f);
            }
        }

        using var outBmp = new SKBitmap(w, h);
        outBmp.Pixels = outPx;
        using var image = SKImage.FromBitmap(outBmp);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 90);
        return new VisualPageDiff(page, (double)changed / outPx.Length, encoded.ToArray());
    }

    private static SKBitmap? RenderOrNull(byte[] pdf, int page, int dpi, string? password)
    {
        if (page < 1 || page > PageCount(pdf, password)) return null;
        return SKBitmap.Decode(PageRenderer.RenderPagePng(pdf, page, dpi, password));
    }

    private static int PageCount(byte[] pdf, string? password)
    {
        return PdfIo.OpenReadOnly(pdf, password).PageCount;
    }

    /// <summary>Composites a bitmap (or blank) onto a white canvas of the target size and reads its pixels.</summary>
    private static SKColor[] OnWhite(SKBitmap? src, int w, int h)
    {
        using var canvasBmp = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(canvasBmp))
        {
            canvas.Clear(Paper);
            if (src != null) canvas.DrawBitmap(src, 0, 0);
        }
        return canvasBmp.Pixels;
    }

    private static float Luma(SKColor c) => 0.299f * c.Red + 0.587f * c.Green + 0.114f * c.Blue;

    /// <summary>Mixes a colour toward white, keeping only <paramref name="ink"/> of its darkness.</summary>
    private static SKColor Fade(SKColor c, float ink) => new(
        (byte)(255 - (255 - c.Red) * ink),
        (byte)(255 - (255 - c.Green) * ink),
        (byte)(255 - (255 - c.Blue) * ink));

    private static byte[] BlankPng()
    {
        using var bmp = new SKBitmap(1, 1);
        bmp.SetPixel(0, 0, Paper);
        using var image = SKImage.FromBitmap(bmp);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 90);
        return encoded.ToArray();
    }
}

/// <summary>A single page's visual diff: the page number, the changed-pixel fraction, and the PNG.</summary>
public sealed record VisualPageDiff(int Page, double ChangedFraction, byte[] Png);
