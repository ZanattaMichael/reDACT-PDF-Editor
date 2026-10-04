using PdfEditor.Core;
using PdfEditor.Tests.Fuzz;
using SkiaSharp;

namespace PdfEditor.Tests.Golden;

using GoldenDoc = GoldenPdfs.GoldenDoc;

/// <summary>
/// The corpus and the operations the golden suite runs over it.
/// <para>
/// Complex fonts and transparency come from <see cref="GoldenPdfs"/>, which exists because nothing
/// else in the repository covered them. Nested XObjects and malformed streams are pulled in by
/// reference from the Tier 2 siblings (<see cref="TestPdfs"/>, <see cref="CorruptPdfs"/> from #52,
/// <see cref="RawPdf"/> from #54) rather than rewritten, so there is exactly one definition of
/// each fixture.
/// </para>
/// </summary>
internal static class GoldenCorpus
{
    /// <summary>
    /// A band across the middle of the page. Chosen to overlap the drawn content of every corpus
    /// document (which is why the raw fixtures all draw around y=280..520) so redaction actually
    /// has something to remove rather than quietly no-opping.
    /// </summary>
    public static readonly RectRegion Region = new(1, 20, 260, 320, 260);

    /// <summary>
    /// Every corpus document, in a fixed order.
    /// <para>
    /// <c>WellFormed: false</c> marks the fixtures that are broken on purpose — an operation over
    /// one of those may legitimately refuse it or produce something the validator still complains
    /// about, so <see cref="GoldenRegressionTests.Operations_OverWellFormedCorpus_ProduceValidOutput"/>
    /// skips them and only the recorded projection holds their behaviour in place.
    /// </para>
    /// <para>
    /// <c>Reproducible: false</c> marks the ones the engine's writer produces, whose bytes
    /// carry a <c>/ModDate</c> and a random <c>/ID</c>; they are excluded from the pinned corpus
    /// hash for that reason, and are the direct evidence for why the goldens are projections
    /// rather than bytes.
    /// </para>
    /// </summary>
    public static IReadOnlyList<GoldenDoc> Documents { get; } = new List<GoldenDoc>
    {
        // ---- complex fonts (the gap this issue exists to fill) ----
        new("font-type3-glyph-procedures", GoldenPdfs.Type3GlyphProcedures(), true, true,
            "Type 3 font whose glyphs are content streams, reached through /Differences."),
        new("font-type0-identity-h", GoldenPdfs.Type0IdentityHWithToUnicode(), true, true,
            "Composite Identity-H font: 2-byte CIDs, text only recoverable via /ToUnicode."),
        new("font-differences-encoding", GoldenPdfs.DifferencesEncoding(), true, true,
            "Simple font whose /Differences make the shown text differ from the literal bytes."),
        new("font-broken-embedded-program", GoldenPdfs.BrokenEmbeddedFontProgram(), false, true,
            "TrueType font whose /FontFile2 is undecodable rubbish."),
        new("font-missing-resource", GoldenPdfs.TextWithMissingFontResource(), false, true,
            "Tf names a font that is not in the page's /Resources."),

        // ---- transparency (the other gap) ----
        new("alpha-constant-and-blend", GoldenPdfs.ConstantAlphaAndBlendMode(), true, true,
            "/ca, /CA and non-Normal blend modes via /ExtGState."),
        new("alpha-luminosity-softmask", GoldenPdfs.LuminositySoftMask(), true, true,
            "/ExtGState /SMask naming a luminosity group form XObject."),
        new("alpha-image-softmask", GoldenPdfs.ImageWithAlphaSoftMask(), true, true,
            "Image XObject with a separate /SMask alpha image."),
        new("alpha-nested-groups", GoldenPdfs.NestedTransparencyGroups(), true, true,
            "Three levels of transparency group, innermost isolated and knocked out."),

        // ---- nested XObjects: already covered elsewhere, pulled in rather than rewritten ----
        new("nested-forms-raw-4", RawPdf.DeeplyNestedForms(4), true, true,
            "Four hand-written form XObjects, each drawing the next (#54's fixture)."),
        new("nested-forms-engine-3", TestPdfs.WithNestedForms(3, 60, 400, 200, 120), true, false,
            "Engine-built nested form XObjects (TestPdfs)."),

        // ---- malformed streams: likewise ----
        new("stream-wrong-length", CorruptPdfs.WrongStreamLength(), false, true,
            "Content stream whose /Length is shorter than its data (#52's fixture)."),
        new("stream-undecodable-flate", CorruptPdfs.UndecodableStream(), false, true,
            "Content stream claiming /FlateDecode over plain text (#52's fixture)."),

        // ---- ordinary documents, so a regression in the common path is visible too ----
        new("plain-multipage", RawPdf.MultiPageDoc(3), true, true,
            "Three plain pages, hand-written and byte-reproducible."),
        new("hidden-data", TestPdfs.WithHiddenData(), true, false,
            "Metadata, attachment, JavaScript, annotation, bookmark and an OCG (TestPdfs)."),
        new("acroform-text-field", TestPdfs.WithTextField("golden.existing", "recorded"), true, false,
            "A single AcroForm text field (TestPdfs)."),
    };

    /// <summary>A tiny deterministic PNG for the signature operation; built once, never downloaded.</summary>
    private static readonly Lazy<byte[]> SignatureImage = new(() =>
    {
        using var bitmap = new SKBitmap(24, 12);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Navy };
            canvas.DrawRect(2, 2, 20, 8, paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    });

    /// <summary>
    /// The operations every corpus document is put through. Between them they cover the rewrite
    /// paths that matter: content-stream editing (redact), page-dictionary editing (rotate),
    /// document assembly (merge), object removal (sanitise), AcroForm creation (add field) and
    /// annotation/appearance writing (image signature).
    /// <para>
    /// OCR is deliberately absent: it needs the Tesseract binary, and an operation that silently
    /// does nothing when a binary is missing is exactly the failure mode this repository has
    /// already shipped once.
    /// </para>
    /// </summary>
    public static IReadOnlyList<(string Name, Func<byte[], byte[]> Run)> Operations { get; } =
        new List<(string, Func<byte[], byte[]>)>
        {
            ("redact", pdf => Redactor.Redact(pdf, new[] { Region }).Pdf),
            // In-place text editing: the path #29 was about. Neither a size nor a face is named,
            // so the result records what the editor infers from the run it is replacing — which is
            // what the `typeset:` line in the projection exists to pin down.
            ("replace-region-text", pdf => TextTools.ReplaceTextInRegion(pdf, Region, "replaced copy").Pdf),
            ("rotate-90", pdf => PageTools.Rotate(pdf, new[] { 1 }, 90).Pdf),
            ("merge-with-self", pdf => Merger.Merge(new[] { pdf, pdf })),
            ("sanitize", pdf => Sanitizer.Sanitize(pdf, new SanitizeOptions()).Pdf),
            ("add-text-field", pdf => FormTools.AddTextField(
                pdf, 1, new RectRegion(1, 40, 60, 180, 24), "golden.added", "filled").Pdf),
            ("sign-image", pdf => Signer.AddImageSignature(
                pdf, new RectRegion(1, 200, 60, 120, 40), SignatureImage.Value)),
        };
}
