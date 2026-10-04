using System.Globalization;
using PdfEditor.Core;
using Xunit;

namespace PdfEditor.Tests;

/// <summary>
/// Font fidelity when editing existing text (issue #29): an edited run must come back out of the
/// document in the same family, weight, slant and — the part that was actually broken — the same
/// type size it went in at.
/// <para>
/// Sizes are asserted against <see cref="TestPdfAssert.DrawnRuns"/>, which reads the <c>Tf</c>
/// operand out of the rewritten content stream, rather than against
/// <see cref="TextTools.GetTextInRegion"/>. Checking the detector with the detector would let a
/// self-consistently wrong measurement pass.
/// </para>
/// </summary>
public class TextFontFidelityTests
{
    /// <summary>The band the fixtures' single line of text sits in.</summary>
    private static RectRegion Band(float size) => new(1, 60, 700 - size * 0.5f, 400, size * 2f);

    public static TheoryData<string, float> StandardFonts() => new()
    {
        { "Helvetica", 10f },
        { "Helvetica", 24f },
        { "Helvetica-Bold", 18f },
        { "Times-Roman", 11f },
        { "Times-BoldItalic", 24f },
        { "Courier", 12f },
        { "Courier-Oblique", 24f },
    };

    /// <summary>
    /// The detector must report the size the text was <em>set</em> in. It used to report the height
    /// of the ascender-to-descender box instead, which is only (ascender-descender)/1000 of the em:
    /// 92.5% for Helvetica, 90% for Times, 78.6% for Courier.
    /// </summary>
    [Theory]
    [MemberData(nameof(StandardFonts))]
    public void GetTextInRegion_ReportsTheTypeSizeTheTextWasSetIn(string fontName, float size)
    {
        byte[] pdf = TestPdfs.WithTextInFont(fontName, "Measure me", size);

        var found = TextTools.GetTextInRegion(pdf, Band(size));

        Assert.Equal("Measure me", found.Text);
        Assert.True(Math.Abs(found.FontSize - size) <= 0.25f, string.Create(CultureInfo.InvariantCulture,
            $"{fontName} was set at {size}pt but was detected as {found.FontSize:F2}pt."));
    }

    /// <summary>
    /// The end-to-end complaint: replace a run without naming a size and the stamped text must be
    /// drawn at the size the original was drawn at.
    /// </summary>
    [Theory]
    [MemberData(nameof(StandardFonts))]
    public void ReplaceTextInRegion_StampsTheReplacementAtTheOriginalTypeSize(string fontName, float size)
    {
        byte[] pdf = TestPdfs.WithTextInFont(fontName, "Original words", size);
        var region = Band(size);
        var found = TextTools.GetTextInRegion(pdf, region);

        var result = TextTools.ReplaceTextInRegion(pdf, region, "Replacement words",
            fontSize: null, fontFamily: found.FontFamily, bold: found.Bold, italic: found.Italic);

        var run = Assert.Single(TestPdfAssert.DrawnRuns(result.Pdf));
        Assert.True(Math.Abs(run.Size - size) <= 0.25f, string.Create(CultureInfo.InvariantCulture,
            $"{fontName} text set at {size}pt was re-stamped at {run.Size:F2}pt."));
    }

    /// <summary>
    /// The compounding case, and the one a user actually notices: the size error was applied afresh
    /// on every edit, so text shrank a little each time it was touched. Courier lost 21% per pass —
    /// three edits took 24pt down to under 12pt.
    /// </summary>
    [Fact]
    public void ReplaceTextInRegion_RepeatedEdits_DoNotShrinkTheText()
    {
        const float size = 24f;
        byte[] pdf = TestPdfs.WithTextInFont("Courier", "Edit me", size);
        var region = Band(size);

        for (int pass = 1; pass <= 3; pass++)
        {
            var found = TextTools.GetTextInRegion(pdf, region);
            pdf = TextTools.ReplaceTextInRegion(pdf, region, "Edit me",
                fontSize: null, fontFamily: found.FontFamily, bold: found.Bold, italic: found.Italic).Pdf;

            var run = Assert.Single(TestPdfAssert.DrawnRuns(pdf));
            Assert.True(Math.Abs(run.Size - size) <= 0.25f, string.Create(CultureInfo.InvariantCulture,
                $"After {pass} edit(s), 24pt Courier is being drawn at {run.Size:F2}pt."));
        }
    }

    /// <summary>
    /// #96: an edited run must land on the <em>same baseline</em> as the text it replaced, in-line
    /// with the words around it. The edit path used to lay the replacement out top-down in a box, so
    /// the layout engine's leading pushed the first line below the original line. The region here is
    /// the tight ascent-to-descent box the viewer actually selects, not a loose band.
    /// </summary>
    [Fact]
    public void ReplaceTextInRegion_KeepsTheReplacementOnTheOriginalBaseline()
    {
        const float size = 24f;
        // WithTextInFont draws its baseline at y=700.
        byte[] pdf = TestPdfs.WithTextInFont("Helvetica", "ORIGINAL", size);
        var span = Assert.Single(TextTools.GetTextSpans(pdf, 1));
        var region = new RectRegion(1, span.X, span.Y, span.Width, span.Height);

        var result = TextTools.ReplaceTextInRegion(pdf, region, "REPLACED");

        var after = Assert.Single(TextTools.GetTextSpans(result.Pdf, 1));
        Assert.Equal("REPLACED", after.Text);
        // Same font and size, so TextSpan.Y (the descent line) is the baseline shifted by a fixed
        // descent; equal descent lines therefore mean equal baselines. Before the fix the run landed
        // roughly 0.3*em (~7pt at 24pt) below the line.
        Assert.True(Math.Abs(after.Y - span.Y) <= 1.5f, string.Create(CultureInfo.InvariantCulture,
            $"Edited text landed at descent-line {after.Y:F2}, {Math.Abs(after.Y - span.Y):F2}pt off "
            + $"the original {span.Y:F2}; it should sit in-line with the surrounding text."));
    }

    /// <summary>Moving a run must not resize it either — it re-stamps at the detected size.</summary>
    [Fact]
    public void MoveText_PreservesTheTypeSize()
    {
        const float size = 20f;
        byte[] pdf = TestPdfs.WithTextInFont(
            "Times-Roman", "Move me", size);

        var moved = TextTools.MoveText(pdf, Band(size), 0, -120);

        var run = Assert.Single(TestPdfAssert.DrawnRuns(moved.Pdf));
        Assert.True(Math.Abs(run.Size - size) <= 0.25f, string.Create(CultureInfo.InvariantCulture,
            $"Moved 20pt text is being drawn at {run.Size:F2}pt."));
    }

    /// <summary>Family, weight and slant survive the round trip along with the size.</summary>
    [Theory]
    [MemberData(nameof(StandardFonts))]
    public void ReplaceTextInRegion_ReproducesTheOriginalFontFace(string fontName, float size)
    {
        byte[] pdf = TestPdfs.WithTextInFont(fontName, "Face check", size);
        var region = Band(size);
        var found = TextTools.GetTextInRegion(pdf, region);

        var result = TextTools.ReplaceTextInRegion(pdf, region, "Face check",
            fontSize: null, fontFamily: found.FontFamily, bold: found.Bold, italic: found.Italic);

        var run = Assert.Single(TestPdfAssert.DrawnRuns(result.Pdf));
        Assert.Equal(fontName, run.Font);
    }

    // ------------------------------------------------------- deliberate fallback

    /// <summary>
    /// When the run's font is one the stamper can actually reproduce, the edit is silent — a
    /// warning on every edit would train users to ignore the one that matters.
    /// </summary>
    [Fact]
    public void ReplaceTextInRegion_WhenTheFontIsReproduced_DoesNotWarnAboutSubstitution()
    {
        byte[] pdf = TestPdfs.WithTextInFont(
            "Times-Bold", "Reproducible", 14);

        var result = TextTools.ReplaceTextInRegion(pdf, Band(14), "Reproducible");

        Assert.DoesNotContain(result.Warnings, w => w.Contains("substitut", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A font outside the three families the stamper can produce gets silently swapped for the
    /// nearest one it can. Symbol is the case that needs no embedded-font machinery to demonstrate:
    /// replacing Symbol text turns Greek letterforms into Helvetica, which is a visible change to
    /// the document and must be reported rather than assumed to be fine.
    /// </summary>
    [Fact]
    public void ReplaceTextInRegion_WhenTheOriginalFontCannotBeReproduced_ReportsTheSubstitution()
    {
        byte[] pdf = TestPdfs.WithTextInFont(
            "Symbol", "abgd", 18);

        var result = TextTools.ReplaceTextInRegion(pdf, Band(18), "alpha beta");

        string warning = Assert.Single(result.Warnings,
            w => w.Contains("substitut", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Symbol", warning, StringComparison.Ordinal);       // names the original
        Assert.Contains("Helvetica", warning, StringComparison.Ordinal);    // and the replacement
    }

    [Theory]
    [InlineData("ABCDEF+Calibri", "Calibri")]         // the six-letter subset tag PDF writers add
    [InlineData("Calibri", "Calibri")]                 // no tag
    [InlineData("ABCDE+X", "ABCDE+X")]                 // too short to be a tag; left alone
    [InlineData("", "")]
    [InlineData(null, "")]
    public void StripSubsetPrefix_RemovesOnlyARealSubsetTag(string? input, string expected)
        => Assert.Equal(expected, TextTools.StripSubsetPrefix(input));

    /// <summary>
    /// The substitution notice keys off the original PostScript name, so a subset-tagged copy of a
    /// face the editor can stamp (<c>ABCDEF+Helvetica</c>) must not be reported as a substitution —
    /// nothing about it changes — while a genuinely different face must be.
    /// </summary>
    [Theory]
    [InlineData("Helvetica", "Helvetica", false)]
    [InlineData("ABCDEF+Helvetica", "Helvetica", false)]
    [InlineData("Times-Bold", "Times-Bold", false)]
    [InlineData("", "Helvetica", false)]               // nothing detected: nothing to claim
    [InlineData("ABCDEF+Calibri", "Helvetica", true)]
    [InlineData("Symbol", "Helvetica", true)]
    public void DescribeSubstitution_ReportsOnlyARealFaceChange(string source, string stamp, bool expected)
        => Assert.Equal(expected, TextTools.DescribeSubstitution(source, stamp) is not null);

    /// <summary>
    /// The metric guard: fonts reporting an implausible or unusable ascender/descender span fall
    /// back to the box height rather than producing a divide-by-nothing.
    /// </summary>
    [Theory]
    [InlineData(0f, 0f, 18.5f)]          // no metrics at all
    [InlineData(100f, -50f, 18.5f)]      // span 0.15 — too small to be an em
    [InlineData(3000f, -500f, 18.5f)]    // span 3.5 — too large
    [InlineData(718f, -207f, 20f)]       // Helvetica: 18.5 / 0.925 = 20
    public void EmSizeFromBoxHeight_FallsBackWhenMetricsAreUnusable(
        float ascender, float descender, float expected)
        => Assert.Equal(expected, TextTools.EmSizeFromBoxHeight(18.5f, ascender, descender), 0.01f);

    /// <summary>
    /// A run whose font exposes no usable vertical metrics at all still has to yield a plausible
    /// size rather than a zero, an infinity, or a NaN that would propagate into the stamp.
    /// </summary>
    [Fact]
    public void GetTextInRegion_FontWithoutUsableMetrics_StillReportsAPlausibleSize()
    {
        byte[] pdf = Golden.GoldenPdfs.Type3GlyphProcedures();

        var found = TextTools.GetTextInRegion(pdf, new RectRegion(1, 0, 0, 600, 800));

        Assert.True(float.IsFinite(found.FontSize) && found.FontSize > 0,
            string.Create(CultureInfo.InvariantCulture, $"Type 3 run reported size {found.FontSize}."));
    }
}
