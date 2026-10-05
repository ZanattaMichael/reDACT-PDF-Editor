using PdfEditor.Core;
using SkiaSharp;
using Xunit;

namespace PdfEditor.Tests;

/// <summary>
/// Targets the harder-to-reach paths in <c>ContentStreamEditor</c> that the black-box
/// <see cref="RedactorTests"/> don't naturally exercise: inline images, form XObjects
/// (including the recursion depth guard), and the low-level <c>'</c>/<c>"</c> text
/// operators. Driven entirely through the public <see cref="Redactor"/> API.
/// </summary>
public class ContentStreamEditorCoverageTests
{
    [Fact]
    public void RemovesInlineImage_FullyInsideRegion()
    {
        byte[] pdf = TestPdfs.WithInlineImage(100, 500, 60, 60);
        Assert.Equal(1, TestPdfAssert.CountImages(pdf));

        var result = Redactor.Redact(pdf, new[] { new RectRegion(1, 90, 490, 90, 90) });

        Assert.Equal(0, TestPdfAssert.CountImages(result.Pdf));
        Assert.Contains("Document with inline image", TestPdfAssert.ExtractText(result.Pdf));
    }

    [Fact]
    public void KeepsInlineImage_OutsideRegion()
    {
        byte[] pdf = TestPdfs.WithInlineImage(100, 500, 60, 60);

        // A region nowhere near the inline image.
        var result = Redactor.Redact(pdf, new[] { new RectRegion(1, 400, 100, 50, 50) });

        Assert.Equal(1, TestPdfAssert.CountImages(result.Pdf));
        Assert.Contains("Document with inline image", TestPdfAssert.ExtractText(result.Pdf));
    }

    [Fact]
    public void FormXObjectOutsideRegion_IsPassedThroughUnchanged()
    {
        byte[] pdf = TestPdfs.WithForm("FORM TEXT", 100, 500, 200, 60);

        var result = Redactor.Redact(pdf, new[] { new RectRegion(1, 0, 0, 10, 10) });

        Assert.Contains("FORM TEXT", TestPdfAssert.ExtractText(result.Pdf));
    }

    [Fact]
    public void FormXObjectInsideRegion_IsRecursivelyEdited()
    {
        byte[] pdf = TestPdfs.WithForm("FORM SECRET", 100, 500, 200, 60);
        Assert.Contains("FORM SECRET", TestPdfAssert.ExtractText(pdf));

        var result = Redactor.Redact(pdf, new[] { new RectRegion(1, 90, 490, 220, 80) });

        Assert.DoesNotContain("FORM SECRET", TestPdfAssert.ExtractText(result.Pdf));
        Assert.Contains("Document with a form", TestPdfAssert.ExtractText(result.Pdf));
    }

    [Fact]
    public void DeeplyNestedForms_TriggerDepthGuard_WithoutCrashing()
    {
        // Six nested forms plus one more beyond the recursion limit: the editor must
        // give up gracefully (dropping the innermost content and recording a warning)
        // instead of recursing without bound.
        byte[] pdf = TestPdfs.WithNestedForms(7, 100, 500, 200, 60);

        var result = Redactor.Redact(pdf, new[] { new RectRegion(1, 90, 490, 220, 80) });

        Assert.Contains(result.Warnings, w => w.Contains("nesting too deep", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Document with nested forms", TestPdfAssert.ExtractText(result.Pdf));
    }

    [Fact]
    public void ModeratelyNestedForms_StillFullyRedactWithinTheLimit()
    {
        // Three levels is comfortably under the depth guard: the innermost text must
        // still be genuinely removed, proving recursion itself (not just the guard) works.
        byte[] pdf = TestPdfs.WithNestedForms(3, 100, 500, 200, 60);
        Assert.Contains("innermost", TestPdfAssert.ExtractText(pdf));

        var result = Redactor.Redact(pdf, new[] { new RectRegion(1, 90, 490, 220, 80) });

        Assert.DoesNotContain("innermost", TestPdfAssert.ExtractText(result.Pdf));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("nesting too deep"));
    }

    [Fact]
    public void QuoteOperator_RemovesOnlyTheTargetedLine()
    {
        byte[] pdf = TestPdfs.WithQuoteOperators("First line", "Second line", "Third line");
        var match = Assert.Single(TextTools.FindText(pdf, "Second line"));

        var result = Redactor.Redact(pdf, new[]
        {
            new RectRegion(match.Page, match.X, match.Y, match.Width, match.Height)
        });

        string text = TestPdfAssert.ExtractText(result.Pdf);
        Assert.Contains("First line", text);
        Assert.DoesNotContain("Second line", text);
        Assert.Contains("Third line", text);
    }

    [Fact]
    public void DoubleQuoteOperator_RemovesOnlyTheTargetedLine()
    {
        byte[] pdf = TestPdfs.WithQuoteOperators("First line", "Second line", "Third line");
        var match = Assert.Single(TextTools.FindText(pdf, "Third line"));

        var result = Redactor.Redact(pdf, new[]
        {
            new RectRegion(match.Page, match.X, match.Y, match.Width, match.Height)
        });

        string text = TestPdfAssert.ExtractText(result.Pdf);
        Assert.Contains("First line", text);
        Assert.Contains("Second line", text);
        Assert.DoesNotContain("Third line", text);
    }

    [Fact]
    public void KeepsImage_OutsideRegion()
    {
        byte[] pdf = TestPdfs.WithImage(100, 500, 200, 100);

        var result = Redactor.Redact(pdf, new[] { new RectRegion(1, 0, 0, 20, 20) });

        Assert.Equal(1, TestPdfAssert.CountImages(result.Pdf));
    }

    /// <summary>
    /// A partially-covered image is pixel-scrubbed rather than dropped, and the surviving part must
    /// come through unchanged. Nothing asserted the scrubbed <em>pixels</em> before — the other image
    /// tests only count draw calls, so a scrub that wrote the channels in the wrong order, misread
    /// the row stride, or mangled alpha would leave a visibly wrong image and still pass.
    /// <para>
    /// Solid red is the probe on purpose: it is the one primary that survives a correct RGB write and
    /// comes back blue if the buffer is ever read as BGR.
    /// </para>
    /// </summary>
    [Fact]
    public void PartiallyRedactedImage_BlacksOutTheOverlapAndLeavesTheRestIntact()
    {
        // A solid-red image occupying x 100..300, y 500..600.
        byte[] pdf = TestPdfs.WithImage(100, 500, 200, 100);
        Assert.Equal(SKColors.Red, TestPdfAssert.PixelAt(pdf, 1, 250, 550));

        // Covers the image's left half only, so the scrub path runs instead of the whole-image drop.
        var result = Redactor.Redact(pdf, new[] { new RectRegion(1, 90, 490, 100, 120) });

        Assert.Equal(1, TestPdfAssert.CountImages(result.Pdf));
        Assert.Empty(result.Warnings);
        Assert.Equal(SKColors.Black, TestPdfAssert.PixelAt(result.Pdf, 1, 150, 550));
        Assert.Equal(SKColors.Red, TestPdfAssert.PixelAt(result.Pdf, 1, 250, 550));
    }

    [Fact]
    public void TjArray_RemovesOnlyTheHitWord_KeepsTheOther()
    {
        byte[] pdf = TestPdfs.WithTjArray("Alpha", "Bravo", 72, 700, 14);
        var match = Assert.Single(TextTools.FindText(pdf, "Alpha"));

        var result = Redactor.Redact(pdf, new[]
        {
            new RectRegion(match.Page, match.X, match.Y, match.Width, match.Height)
        });

        string text = TestPdfAssert.ExtractText(result.Pdf);
        Assert.DoesNotContain("Alpha", text);
        Assert.Contains("Bravo", text);
    }

    [Fact]
    public void TjArrayWithEmptyString_StillSurgicallyRemovesOnlyTheHitWord()
    {
        // A zero-length string in the array still produces its own (empty) text run, so the
        // per-glyph path (not the encoding-mismatch fallback) handles it — and does so
        // correctly, leaving the untouched word intact.
        byte[] pdf = TestPdfs.WithTjArrayContainingEmptyString("Alpha", "Bravo", 72, 700, 14);
        var match = Assert.Single(TextTools.FindText(pdf, "Alpha"));

        var result = Redactor.Redact(pdf, new[]
        {
            new RectRegion(match.Page, match.X, match.Y, match.Width, match.Height)
        });

        string text = TestPdfAssert.ExtractText(result.Pdf);
        Assert.DoesNotContain("Alpha", text);
        Assert.Contains("Bravo", text);
    }

    [Fact]
    public void DanglingXObjectReference_IsPassedThroughWithoutCrashing()
    {
        byte[] pdf = TestPdfs.WithDanglingXObjectReference("visible text", 72, 700, 14);

        // A dangling "/Ghost Do" is invalid PDF regardless of our redactor, so verification
        // reads the page's raw content bytes directly rather than going through extraction.
        var result = Redactor.Redact(pdf, new[] { new RectRegion(1, 0, 0, 10, 10) });

        byte[] content = PdfEditor.Core.Pdf.PdfDocument.Open(result.Pdf).GetPage(1).GetContentBytes();
        Assert.Contains("visible text", System.Text.Encoding.ASCII.GetString(content));
    }

    [Fact]
    public void UnknownXObjectSubtype_IsPassedThroughWithoutCrashing()
    {
        byte[] pdf = TestPdfs.WithUnknownXObjectSubtype("visible text", 72, 700, 14);

        var result = Redactor.Redact(pdf, new[] { new RectRegion(1, 0, 0, 10, 10) });

        Assert.Contains("visible text", TestPdfAssert.ExtractText(result.Pdf));
    }

    [Fact]
    public void CorruptImage_ThatCannotBeScrubbed_IsRemovedEntirely_WithWarning()
    {
        byte[] pdf = TestPdfs.WithCorruptImage(100, 500, 200, 100);

        // Only partially overlap so the editor attempts a scrub (rather than the
        // fully-contained fast path, which drops the image without ever calling the
        // scrubber).
        var result = Redactor.Redact(pdf, new[] { new RectRegion(1, 100, 500, 100, 100) });

        Assert.Equal(0, TestPdfAssert.CountImages(result.Pdf));
        // The drop must be a *named* failure, not a bare one: a scrub failure can be transient
        // (memory pressure on decode/read-back), which reads very differently from an unsupported
        // format, and #88 is about being able to tell them apart from the log. The warning must
        // carry a concrete reason clause, not merely say the image was removed.
        Assert.Contains(result.Warnings, w =>
            w.Contains("could not be pixel-scrubbed (")
            && (w.Contains("could not be decoded")
                || w.Contains("could not be read")
                || w.Contains("scrubbing threw")));
    }
}
