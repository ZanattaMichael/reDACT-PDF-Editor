using PdfEditor.Core;
using SkiaSharp;
using Xunit;

namespace PdfEditor.Tests;

public class TextToolsTests
{
    /// <summary>
    /// Editing a word that sits on top of artwork must not disturb the artwork. Text editing removes
    /// the original content through the same machinery redaction uses, and that machinery blacks out
    /// the pixels of any image the region touches — so editing a line over a letterhead, watermark or
    /// scanned page punched a black rectangle through it. The text is what is being replaced; the
    /// picture behind it is not.
    /// </summary>
    [Fact]
    public void ReplaceTextInRegion_OverAnImage_LeavesTheImageAlone()
    {
        byte[] pdf = TestPdfs.WithTextOverImage("HELLO", 120, 600, 14);
        var match = Assert.Single(TextTools.FindText(pdf, "HELLO"));
        // A box dragged slightly proud of the text, which is what the Edit tool actually produces —
        // and it leaves a margin of plain image inside the region to sample, away from the
        // antialiased glyph edges.
        const float pad = 6;
        var region = new RectRegion(1, match.X - pad, match.Y - pad,
            match.Width + 2 * pad, match.Height + 2 * pad);

        float marginX = match.X - pad / 2, marginY = match.Y + match.Height / 2;
        Assert.Equal(SKColors.Red, TestPdfAssert.PixelAt(pdf, 1, marginX, marginY));

        var result = TextTools.ReplaceTextInRegion(pdf, region, "WORLD");

        // Inside the edited region, and elsewhere in the image: both still the original artwork.
        Assert.Equal(SKColors.Red, TestPdfAssert.PixelAt(result.Pdf, 1, marginX, marginY));
        Assert.Equal(SKColors.Red, TestPdfAssert.PixelAt(result.Pdf, 1, 350, 620));
    }

    /// <summary>
    /// The other half of the rule: on a searchable scan the words on screen <em>are</em> the image,
    /// and the only real text is the invisible OCR layer over them. Removing just that layer leaves
    /// the old words visibly in place with the replacement stamped across them — two texts on top of
    /// each other. So here the pixels must go too, painted out in the surrounding paper colour
    /// rather than the black redaction uses.
    /// </summary>
    [Fact]
    public void ReplaceTextInRegion_OnASearchableScan_ErasesTheScannedWordsInPaperNotBlack()
    {
        byte[] pdf = TestPdfs.SearchableScan("SALARY 100", 77, 663, 24);
        var match = Assert.Single(TextTools.FindText(pdf, "SALARY 100"));
        var region = new RectRegion(1, match.X, match.Y, match.Width, match.Height);

        // The band holding "ALARY 100" — past the first glyph, which the replacement will occupy.
        const float x0 = 95, x1 = 210, y0 = 664, y1 = 678;
        Assert.True(TestPdfAssert.InkFraction(pdf, 1, x0, y0, x1, y1) > 0.1,
            "the scanned words should be on the page to begin with");

        // Replaced with something short, so most of the old word's area is left bare and what the
        // band measures is the erase, not the replacement's own glyphs.
        var result = TextTools.ReplaceTextInRegion(pdf, region, "X");

        Assert.Equal("X", TestPdfAssert.ExtractText(result.Pdf).Trim());
        Assert.True(TestPdfAssert.InkFraction(result.Pdf, 1, x0, y0, x1, y1) < 0.01,
            "the scanned words should have been erased with the replacement");
        // Erased in paper, not in redaction black — a black bar here is the bug from the other side.
        Assert.Equal(SKColors.White, TestPdfAssert.PixelAt(result.Pdf, 1, 150, 670));
    }

    /// <summary>
    /// Replacement text longer than what it replaces has to survive intact. The region handed to the
    /// stamper is the measured bounding box of the words being replaced, and laying the paragraph out
    /// inside it meant the layout dropped whatever did not fit: "HELLO" replaced by "WORLD" came back as
    /// "WORL", with no warning and no error.
    /// </summary>
    [Fact]
    public void ReplaceTextInRegion_KeepsReplacementTextThatIsLongerThanTheOriginal()
    {
        byte[] pdf = TestPdfs.WithText(("HELLO", 120, 600, 14));
        var match = Assert.Single(TextTools.FindText(pdf, "HELLO"));
        var region = new RectRegion(1, match.X, match.Y, match.Width, match.Height);

        var result = TextTools.ReplaceTextInRegion(pdf, region, "WORLDWIDE");

        Assert.Contains("WORLDWIDE", TestPdfAssert.ExtractText(result.Pdf), StringComparison.Ordinal);
    }

    [Fact]
    public void GetTextInRegion_ReturnsTextAndFontSize()
    {
        byte[] pdf = TestPdfs.WithText(
            ("Invoice Number 12345", 72, 700, 14),
            ("unrelated footer", 72, 100, 9));

        var region = TextTools.GetTextInRegion(pdf, new RectRegion(1, 60, 690, 300, 30));

        Assert.Equal("Invoice Number 12345", region.Text);
        Assert.InRange(region.FontSize, 8, 20);
    }

    [Fact]
    public void GetTextInRegion_InsertsASpace_BetweenSeparateTextRunsOnTheSameLine()
    {
        // Two independent ShowText calls on the same baseline, positioned with a gap but
        // with no space glyph of their own: AssembleText must recognise the visual gap
        // and stitch them back together with an inferred space.
        byte[] pdf = TestPdfs.WithText(("Hello", 72, 700, 14), ("World", 160, 700, 14));

        var region = TextTools.GetTextInRegion(pdf, new RectRegion(1, 60, 690, 200, 30));

        Assert.Equal("Hello World", region.Text);
    }

    [Fact]
    public void GetTextInRegion_EmptyRegion_ReturnsEmpty()
    {
        byte[] pdf = TestPdfs.WithText(("hello", 72, 700, 12));
        var region = TextTools.GetTextInRegion(pdf, new RectRegion(1, 400, 100, 50, 50));
        Assert.Equal(string.Empty, region.Text);
    }

    [Fact]
    public void ReplaceTextInRegion_RemovesOldText_AndStampsNewText()
    {
        byte[] pdf = TestPdfs.WithText(("Old Company Name", 72, 700, 14));

        var result = TextTools.ReplaceTextInRegion(pdf,
            new RectRegion(1, 60, 690, 300, 30), "New Corp Ltd");

        string text = TestPdfAssert.ExtractText(result.Pdf);
        Assert.DoesNotContain("Old Company", text);
        Assert.Contains("New Corp Ltd", text);
    }

    [Fact]
    public void ReplaceTextInRegion_StampsNewText_WhenPageLeavesATransformActive()
    {
        // Regression (same root cause as the redaction box): on a Chrome / Google-Docs PDF whose
        // content leaves a scale+flip matrix active, stamped replacement text used to be scaled and
        // flipped away instead of landing at the region. The new glyphs must render at the region.
        byte[] pdf = TestPdfs.ChromeStyleLeftoverCtm("SECRET");
        var match = Assert.Single(TextTools.FindText(pdf, "SECRET"));
        var region = new RectRegion(match.Page, match.X - 2, match.Y - 2, match.Width + 80, match.Height + 4);

        var result = TextTools.ReplaceTextInRegion(pdf, region, "PUBLIC", fontSize: match.Height);

        // Some glyph of the stamped word renders dark somewhere along the region's baseline.
        bool anyDark = false;
        for (float dx = 0; dx < region.Width && !anyDark; dx += 2)
        {
            var px = TestPdfAssert.PixelAt(result.Pdf, 1, region.X + dx, match.Y + match.Height * 0.45f, 150);
            if (px.Red < 128 && px.Green < 128 && px.Blue < 128) anyDark = true;
        }
        Assert.True(anyDark, "stamped replacement text did not render at the region — a leftover transform likely displaced it");
    }

    [Fact]
    public void GetTextInRegion_ReportsHelveticaSansForPlainText()
    {
        byte[] pdf = TestPdfs.WithText(("basic helvetica", 72, 700, 14));

        var region = TextTools.GetTextInRegion(pdf, new RectRegion(1, 60, 690, 300, 30));

        Assert.Equal("helvetica", region.FontFamily);
        Assert.False(region.Bold);
        Assert.False(region.Italic);
    }

    [Fact]
    public void ReplaceTextInRegion_AppliesTheChosenFontFamilyAndStyle()
    {
        byte[] pdf = TestPdfs.WithText(("plain text here", 72, 700, 14));
        var region = new RectRegion(1, 60, 690, 300, 30);

        var result = TextTools.ReplaceTextInRegion(pdf, region, "styled words",
            fontSize: 14, fontFamily: "times", bold: true, italic: true);

        // Re-reading the region detects the family/style that was stamped.
        var reread = TextTools.GetTextInRegion(result.Pdf, region);
        Assert.Contains("styled words", reread.Text);
        Assert.Equal("times", reread.FontFamily);
        Assert.True(reread.Bold);
        Assert.True(reread.Italic);
    }

    [Fact]
    public void ReplaceTextInRegion_WithColour_ProducesReadableText()
    {
        byte[] pdf = TestPdfs.WithText(("colour me", 72, 700, 14));

        var result = TextTools.ReplaceTextInRegion(pdf, new RectRegion(1, 60, 690, 300, 30),
            "red text", fontSize: 14, colorHex: "#ff0000");

        Assert.Contains("red text", TestPdfAssert.ExtractText(result.Pdf));
    }

    [Theory]
    [InlineData("times", true, true)]
    [InlineData("courier", false, false)]
    [InlineData("helvetica", true, false)]
    public void ReplaceTextInRegion_EveryFamilyStyleCombination_StaysReadable(string family, bool bold, bool italic)
    {
        byte[] pdf = TestPdfs.WithText(("before", 72, 700, 14));

        var result = TextTools.ReplaceTextInRegion(pdf, new RectRegion(1, 60, 690, 300, 30),
            "after words", fontSize: 14, fontFamily: family, bold: bold, italic: italic);

        Assert.Contains("after words", TestPdfAssert.ExtractText(result.Pdf));
    }

    [Fact]
    public void MoveText_RelocatesTheRun_ByTheGivenDelta()
    {
        byte[] pdf = TestPdfs.WithText(("MOVEME", 100, 500, 14));
        var before = Assert.Single(TextTools.FindText(pdf, "MOVEME"));
        var region = new RectRegion(1, before.X, before.Y, before.Width, before.Height);

        var result = TextTools.MoveText(pdf, region, dx: 130, dy: -90);

        var after = Assert.Single(TextTools.FindText(result.Pdf, "MOVEME"));
        Assert.True(after.X > before.X + 100, $"expected x to move right (was {before.X}, now {after.X})");
        Assert.True(after.Y < before.Y - 60, $"expected y to move down (was {before.Y}, now {after.Y})");
    }

    [Fact]
    public void MoveText_EmptyRegion_IsANoOp()
    {
        byte[] pdf = TestPdfs.WithText(("hello", 72, 700, 12));
        var result = TextTools.MoveText(pdf, new RectRegion(1, 300, 300, 40, 20), 10, 10);
        Assert.Equal(pdf, result.Pdf);
    }

    [Fact]
    public void GetTextSpans_ReturnsRuns_WithPositions()
    {
        byte[] pdf = TestPdfs.WithText(("Selectable Text Here", 72, 700, 14));

        var spans = TextTools.GetTextSpans(pdf, 1);

        var span = Assert.Single(spans);
        Assert.Contains("Selectable", span.Text);
        Assert.True(span.Width > 0 && span.Height > 0);
        // Roughly where it was drawn (baseline near y=700).
        Assert.InRange(span.X, 60, 90);
        Assert.InRange(span.Y, 690, 705);
    }

    [Fact]
    public void GetTextSpans_EmptyPage_ReturnsEmpty()
    {
        var doc = PdfEditor.Core.Pdf.PdfDocument.CreateNew();
        doc.AddNewPage(595, 842);
        Assert.Empty(TextTools.GetTextSpans(doc.Save(), 1));
    }

    [Fact]
    public void FindText_LocatesPhraseOnCorrectPage()
    {
        byte[] pdf = TestPdfs.MultiPage(3, "Chapter");

        var matches = TextTools.FindText(pdf, "Chapter 2");

        var match = Assert.Single(matches);
        Assert.Equal(2, match.Page);
        Assert.True(match.Width > 0 && match.Height > 0);
    }

    [Fact]
    public void FindText_NoMatch_ReturnsEmpty()
    {
        byte[] pdf = TestPdfs.WithText(("hello world", 72, 700, 12));
        Assert.Empty(TextTools.FindText(pdf, "absent"));
    }

    [Fact]
    public void FindText_DefaultsToCaseInsensitiveContains()
    {
        byte[] pdf = TestPdfs.WithText(("The Assignment is Confidential", 72, 700, 12));

        // Different case, and a substring inside a word ("sign" within "Assignment").
        Assert.Single(TextTools.FindText(pdf, "confidential"));
        Assert.Single(TextTools.FindText(pdf, "sign"));
    }

    [Fact]
    public void FindText_CaseSensitive_OnlyMatchesExactCase()
    {
        byte[] pdf = TestPdfs.WithText(("Confidential", 72, 700, 12));
        var opts = new SearchOptions(TextMatchMode.Contains, CaseSensitive: true);

        Assert.Empty(TextTools.FindText(pdf, "confidential", null, opts));
        Assert.Single(TextTools.FindText(pdf, "Confidential", null, opts));
    }

    [Fact]
    public void FindText_WholeWord_DoesNotMatchAsubstringInsideAWord()
    {
        byte[] pdf = TestPdfs.WithText(("Assignment sign", 72, 700, 12));
        var opts = new SearchOptions(TextMatchMode.WholeWord);

        // "sign" as a whole word matches the standalone word, not the "sign" inside "Assignment".
        var match = Assert.Single(TextTools.FindText(pdf, "sign", null, opts));
        Assert.True(match.Width > 0);
    }

    [Fact]
    public void FindText_StartsWith_AnchorsToTheWordStart()
    {
        byte[] pdf = TestPdfs.WithText(("apply application misapply", 72, 700, 12));
        var opts = new SearchOptions(TextMatchMode.StartsWith);

        // "appl" starts "apply" and "application" but not "misapply".
        Assert.Equal(2, TextTools.FindText(pdf, "appl", null, opts).Count);
    }

    [Theory]
    [InlineData("starts-with", TextMatchMode.StartsWith)]
    [InlineData("WHOLEWORD", TextMatchMode.WholeWord)]
    [InlineData("ends_with", TextMatchMode.EndsWith)]
    [InlineData("anything", TextMatchMode.Contains)]
    public void SearchOptions_Parse_MapsWireValues(string input, TextMatchMode expected)
    {
        Assert.Equal(expected, SearchOptions.Parse(input, false).Mode);
    }

    [Fact]
    public void ReplaceAll_ReplacesEveryOccurrence()
    {
        byte[] pdf = TestPdfs.WithText(
            ("ACME did the work for ACME", 72, 700, 12),
            ("Signed by ACME", 72, 650, 12));

        var (result, count) = TextTools.ReplaceAll(pdf, "ACME", "Globex");

        Assert.Equal(3, count);
        string text = TestPdfAssert.ExtractText(result.Pdf);
        Assert.DoesNotContain("ACME", text);
        Assert.Contains("Globex", text);
    }

    [Fact]
    public void ReplaceAll_StampsTheReplacementAtTheOriginalTypeSize()
    {
        // #86: ReplaceAll passed the match's ascender-to-descender box height (m.Height) as the font
        // size. That box is only ~0.93 of the em for Helvetica, so every replacement came out ~7%
        // too small. Re-measuring the replaced run must give the original 24pt, not the ~22.2pt box.
        byte[] pdf = TestPdfs.WithText(("SECRET", 72, 700, 24));

        var (result, count) = TextTools.ReplaceAll(pdf, "SECRET", "PUBLIC");
        Assert.Equal(1, count);

        float measured = TextTools.GetTextInRegion(result.Pdf, new RectRegion(1, 60, 688, 220, 44)).FontSize;
        Assert.InRange(measured, 23f, 25f);
    }

    [Fact]
    public void ReplaceAll_KeepsSurroundingWordsIntact()
    {
        byte[] pdf = TestPdfs.WithText(("Payable to ACME within 30 days", 72, 700, 12));

        var (result, count) = TextTools.ReplaceAll(pdf, "ACME", "Globex");

        Assert.Equal(1, count);
        string text = TestPdfAssert.ExtractText(result.Pdf);
        Assert.Contains("Payable to", text);
        Assert.Contains("within 30 days", text);
        Assert.Contains("Globex", text);
    }
}
