using PdfEditor.Core.Pdf;

namespace PdfEditor.Core;

/// <summary>
/// Repositions raster images on a page. Because a placed image is baked into the page content
/// stream, moving it means removing the original draw (like the text-move tool does) and drawing
/// the same image again at the shifted rectangle — in the page's default user space so it lands
/// correctly even on Chrome/Google-Docs PDFs that leave a transform active. The image object
/// itself is reused, not re-encoded, so its colour space, masks and compression come through
/// unchanged.
/// </summary>
public static class ImageTools
{
    /// <summary>
    /// Moves every image overlapping <paramref name="source"/> by (<paramref name="dx"/>,
    /// <paramref name="dy"/>) in PDF user space. A no-op when the region holds no image.
    /// </summary>
    public static EditResult MoveImage(byte[] pdf, int page, RectRegion source, float dx, float dy,
        string? password = null)
    {
        var images = FindImages(pdf, page, source, password);
        if (images.Count == 0) return EditResult.Of(pdf);

        // Remove the original image content over each image's own bounds, then redraw shifted.
        var removeRegions = images
            .Select(i => new RectRegion(page, i.Rect.X, i.Rect.Y, i.Rect.Width, i.Rect.Height))
            .ToList();
        var removed = Redactor.RemoveContent(pdf, removeRegions, password);

        var doc = PdfIo.Open(removed.Pdf, password);
        var target = doc.GetPage(page);
        var resources = target.GetOrCreateResources();
        var canvas = new ContentBuilder();
        foreach (var (image, rect) in images)
        {
            var name = PdfResources.Add(resources, PdfName.XObject, "Im", image);
            canvas.SaveState()
                .Transform(rect.Width, 0, 0, rect.Height, rect.X + dx, rect.Y + dy)
                .DrawXObject(name)
                .RestoreState();
        }
        PdfContentGuard.DrawInDefaultUserSpace(target, canvas.ToArray());
        return new EditResult(PdfIo.Save(doc), removed.Warnings);
    }

    /// <summary>
    /// The images drawn over <paramref name="region"/>, each as an XObject (an inline image is
    /// converted to one) carried over from the source document, with the rectangle it covers.
    /// </summary>
    private static List<(PdfStream Image, PdfRect Rect)> FindImages(byte[] pdf, int page,
        RectRegion region, string? password)
    {
        var doc = PdfIo.OpenReadOnly(pdf, password);
        if (page < 1 || page > doc.PageCount)
            throw new ArgumentOutOfRangeException(nameof(page), $"Page {page} does not exist.");
        var pdfPage = doc.GetPage(page);
        var finder = new ImageFinder(new PdfRect(region.X, region.Y, region.Width, region.Height), pdfPage.Resources);
        PdfIo.Guarded($"scanning images on page {page}", () => new ContentProcessor(finder).ProcessPage(pdfPage));
        return finder.Found;
    }

    private sealed class ImageFinder : IContentListener
    {
        private readonly PdfRect _region;
        private readonly PdfDictionary? _resources;
        public List<(PdfStream Image, PdfRect Rect)> Found { get; } = new();

        public ImageFinder(PdfRect region, PdfDictionary? resources)
        {
            _region = region;
            _resources = resources;
        }

        public void OnImage(ImageRenderInfo info)
        {
            var rect = info.BoundingBox;
            if (!rect.Intersects(_region)) return;
            var image = info.Stream
                ?? PdfImages.ExpandInline(info.InlineDictionary!, info.InlineData ?? Array.Empty<byte>(), _resources);
            Found.Add((image, rect));
        }
    }
}
