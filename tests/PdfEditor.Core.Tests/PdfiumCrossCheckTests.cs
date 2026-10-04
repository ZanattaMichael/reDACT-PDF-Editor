using PdfEditor.Core;
using PdfEditor.Core.Pdf;
using PdfEditor.Core.Pdf.Fonts;
using SkiaSharp;
using Xunit;

namespace PdfEditor.Tests;

/// <summary>
/// Checks the in-house PDF engine against an independent one. PDFium (Chrome's renderer, which the
/// app already uses for page previews) shares no code with the engine, so where they agree the
/// agreement means something: when the engine says a glyph occupies a box, PDFium's ink for that
/// glyph must be inside it — otherwise a redaction drawn over a search hit would miss the pixels
/// a reader actually sees. And every kind of file the writer produces must open and render in it.
/// </summary>
public class PdfiumCrossCheckTests
{
    private const int Dpi = 144;
    private const float PageHeight = TestPdfs.PageHeight;

    /// <summary>Tolerance, in points, for anti-aliasing and the renderer's hinting.</summary>
    private const float Slack = 1.5f;

    public static TheoryData<string, string> TextCases => new()
    {
        { "plain", "BT /{F} 24 Tf 72 700 Td (HAMBURGEFONTS) Tj ET" },
        { "spacing-and-scaling", "BT /{F} 20 Tf 150 Tz 2 Tc 4 Tw 72 640 Td (HAM BURG) Tj ET" },
        { "rotated-90", "BT /{F} 18 Tf 0 1 -1 0 300 300 Tm (HAMBURG) Tj ET" },
        { "chrome-scale-flip", "0.75 0 0 -0.75 0 842 cm BT /{F} 32 Tf 1 0 0 -1 96 300 Tm (HAMBURG) Tj ET" },
        { "tj-kerning", "BT /{F} 22 Tf 72 560 Td [(HAM) -400 (BURG) 250 (ER)] TJ ET" },
        { "rise-and-ctm-scale", "q 1.5 0 0 1.5 0 0 cm BT /{F} 14 Tf 3 Ts 50 300 Td (HAMBURG) Tj ET Q" },
    };

    [Theory]
    [MemberData(nameof(TextCases))]
    public void GlyphBoxes_ContainTheInkPdfiumRenders(string name, string content)
    {
        foreach (var font in new[] { StandardFonts.Helvetica, StandardFonts.TimesRoman, StandardFonts.Courier })
        {
            byte[] pdf = TestPdfs.Build(p => p.Content.Raw(content.Replace("{F}", p.Font(font).Value) + "\n"));

            var box = EngineTextBox(pdf);
            var ink = InkBox(pdf);
            Assert.True(ink.HasValue, $"{name}/{font}: PDFium rendered nothing");
            var i = ink!.Value;

            Assert.True(i.Left >= box.Left - Slack && i.Right <= box.Right + Slack
                        && i.Bottom >= box.Bottom - Slack && i.Top <= box.Top + Slack,
                $"{name}/{font}: ink {Describe(i)} falls outside the engine's glyph box {Describe(box)}");

            // And the box is not trivially large: the ink spans most of it along the line. (Across
            // the line the box spans ascent to descent, which capitals do not fill.)
            bool vertical = box.Height > box.Width;
            double inkSpan = vertical ? i.Height : i.Width, boxSpan = vertical ? box.Height : box.Width;
            Assert.True(inkSpan >= boxSpan * 0.85,
                $"{name}/{font}: ink {Describe(i)} covers too little of the glyph box {Describe(box)}");
        }
    }

    public static TheoryData<string> WriterOutputs => new()
    {
        "full-rewrite", "object-streams", "aes256-encrypted", "incremental-signature", "merged", "arranged",
    };

    [Theory]
    [MemberData(nameof(WriterOutputs))]
    public void EveryKindOfWriterOutput_RendersInPdfium(string kind)
    {
        byte[] source = TestPdfs.MultiPage(2, "Rendered");
        string? password = null;
        byte[] output = kind switch
        {
            "full-rewrite" => PdfDocument.Open(source).Save(),
            "object-streams" => PdfDocument.Open(source).Save(new PdfSaveOptions { ObjectStreams = true }),
            "aes256-encrypted" => Encryptor.Encrypt(source, password = "open sesame"),
            "incremental-signature" => Signer.SignDigitally(source,
                CertificateFactory.CreateSelfSignedPkcs12("Cross Check", "pw"), "pw"),
            "merged" => Merger.Merge(new[] { source, TestPdfs.MultiPage(1, "Second") }),
            "arranged" => PageTools.Arrange(source, new[] { 2, 1 }).Pdf,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var ink = InkBox(output, password);
        Assert.True(ink.HasValue, $"{kind}: PDFium rendered a blank first page");
        Assert.Contains("Rendered", TestPdfAssert.ExtractText(output, 1, password));
    }

    // ------------------------------------------------------------------ helpers

    private static PdfRect EngineTextBox(byte[] pdf)
    {
        var listener = new Boxes();
        new ContentProcessor(listener).ProcessPage(PdfDocument.Open(pdf).GetPage(1));
        Assert.NotEmpty(listener.Found);
        return listener.Found.Aggregate((a, b) => a.Union(b));
    }

    private sealed class Boxes : IContentListener
    {
        public List<PdfRect> Found { get; } = new();

        public void OnText(TextRenderInfo info)
        {
            foreach (var glyph in info.Glyphs)
                if (!string.IsNullOrWhiteSpace(glyph.Text)) Found.Add(glyph.BoundingBox);
        }
    }

    /// <summary>The bounding box, in PDF user space, of every non-white pixel PDFium renders on page 1.</summary>
    private static PdfRect? InkBox(byte[] pdf, string? password = null)
    {
        using var bitmap = SKBitmap.Decode(PageRenderer.RenderPagePng(pdf, 1, Dpi, password));
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                var c = bitmap.GetPixel(x, y);
                if (c.Alpha < 128 || (c.Red > 200 && c.Green > 200 && c.Blue > 200)) continue;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
        if (maxX < 0) return null;
        float scale = 72f / Dpi;
        return PdfRect.FromCorners(minX * scale, PageHeight - (maxY + 1) * scale,
            (maxX + 1) * scale, PageHeight - minY * scale);
    }

    private static string Describe(PdfRect r) =>
        FormattableString.Invariant($"[{r.Left:F1} {r.Bottom:F1} {r.Right:F1} {r.Top:F1}]");
}
