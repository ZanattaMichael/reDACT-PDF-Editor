using System.Globalization;
using System.Text;
using PdfEditor.Core;
using PdfEditor.Core.Pdf;

namespace PdfEditor.Tests.Golden;

/// <summary>
/// Renders a PDF as a deterministic, human-readable summary — the thing the golden files actually
/// hold.
/// <para>
/// <b>Why not compare bytes?</b> Because the writer's output is not byte-reproducible. Every
/// document it writes gets an Info <c>/ModDate</c> stamped at save (and a new document a fresh
/// random <c>/ID</c>); two calls to the same helper with the same input already differ.
/// A byte-for-byte golden suite over this engine would be permanently flaky, and the usual
/// response to a permanently flaky suite is to delete it or to regenerate the goldens on every
/// red run — at which point it rubber-stamps regressions instead of catching them.
/// (<see cref="GoldenSuiteSelfTests.RawBytes_AreNotReproducible_ButTheProjectionIs"/> pins that
/// premise, so if the writer ever becomes deterministic this comment is proven wrong rather than just
/// going stale.)
/// </para>
/// <para>
/// So the golden is a <em>semantic projection</em>: page geometry, extracted text, the font /
/// XObject / graphics-state inventory of every page and of every nested form XObject, the operator
/// census of each content stream, the AcroForm field list, and the <see cref="ExportValidator"/>'s
/// findings. It records everything a rewrite could plausibly damage and nothing that changes
/// between two identical runs. Timestamps, <c>/ID</c>, and the producer string are deliberately
/// excluded; their <em>presence</em> is recorded (the sanitiser is supposed to remove them) but
/// never their value.
/// </para>
/// </summary>
internal static class GoldenProjection
{
    /// <summary>How deep to follow form XObjects into their own resources.</summary>
    private const int MaxResourceDepth = 4;

    /// <summary>Extracted text is collapsed to this many characters; goldens stay reviewable.</summary>
    private const int MaxTextLength = 200;

    /// <summary>Describes <paramref name="pdf"/>. Never throws: an unreadable document is described as such.</summary>
    public static string Describe(byte[] pdf, string? password = null)
    {
        var report = new StringBuilder();
        report.Append("bytes: ").Append(Bucket(pdf.Length)).Append('\n');

        try
        {
            var doc = PdfDocument.Open(pdf, string.IsNullOrEmpty(password) ? null : password);
            DescribeDocument(doc, report);
        }
        catch (Exception ex)
        {
            report.Append("document: unreadable (").Append(ex.GetType().Name).Append(")\n");
        }

        DescribeValidation(pdf, password, report);
        return report.ToString();
    }

    /// <summary>
    /// Describes the result of an operation, including the case where the operation refused the
    /// document. A thrown exception is part of the recorded behaviour — an operation that starts
    /// or stops rejecting a corpus document is exactly the kind of regression this suite exists to
    /// surface — so only its type and a normalised message are recorded, never a stack trace.
    /// </summary>
    public static string DescribeOperation(string name, Func<byte[]> operation)
    {
        var report = new StringBuilder("== ").Append(name).Append(" ==\n");
        byte[] output;
        try
        {
            output = operation();
        }
        catch (Exception ex)
        {
            return report.Append("threw: ").Append(ex.GetType().Name).Append(": ")
                .Append(Normalise(ex.Message)).Append('\n').ToString();
        }
        return report.Append(Describe(output)).ToString();
    }

    // ------------------------------------------------------------------ document

    private static void DescribeDocument(PdfDocument doc, StringBuilder report)
    {
        report.Append("pdf-version: PDF-").Append(doc.Version).Append('\n');
        report.Append("pages: ").Append(doc.PageCount).Append('\n');
        report.Append("encrypted: ").Append(Yn(doc.WasEncrypted)).Append('\n');

        DescribeInfo(doc, report);
        DescribeCatalog(doc, report);

        for (int i = 1; i <= doc.PageCount; i++)
            DescribePage(doc.GetPage(i), i, report);

        DescribeAcroForm(doc, report);
    }

    /// <summary>
    /// The Info dictionary, with the two nondeterministic entries reduced to a yes/no. The
    /// producer string is omitted entirely: it names whichever tool last wrote the file, so
    /// including it would churn every golden whenever that changes without telling anyone anything.
    /// </summary>
    private static void DescribeInfo(PdfDocument doc, StringBuilder report)
    {
        var info = doc.Info;
        var parts = new List<string>();
        foreach (var key in new[] { PdfName.Title, PdfName.Author, PdfName.Subject, PdfName.Keywords })
        {
            string? value = info?.GetAsString(key)?.ToUnicodeString();
            if (!string.IsNullOrEmpty(value)) parts.Add($"{key.Value}={Normalise(value)}");
        }
        parts.Add("has-CreationDate=" + Yn(info?.Get(PdfName.CreationDate) != null));
        parts.Add("has-ModDate=" + Yn(info?.Get(PdfName.ModDate) != null));
        parts.Add("has-ID=" + Yn(doc.Trailer.Get(PdfName.ID) != null));
        report.Append("info: ").Append(string.Join(' ', parts)).Append('\n');
    }

    private static void DescribeCatalog(PdfDocument doc, StringBuilder report)
    {
        var catalog = doc.Catalog ?? new PdfDictionary();
        var parts = new List<string>
        {
            "has-OpenAction=" + Yn(catalog.Get(PdfName.OpenAction) != null),
            "has-Names=" + Yn(catalog.Get(PdfName.Names) != null),
            "has-Outlines=" + Yn(catalog.Get(PdfName.Outlines) != null),
            "has-OCProperties=" + Yn(catalog.Get(PdfName.OCProperties) != null),
            "has-Metadata=" + Yn(catalog.Get(PdfName.Metadata) != null),
        };
        report.Append("catalog: ").Append(string.Join(' ', parts)).Append('\n');
    }

    // ------------------------------------------------------------------ page

    private static void DescribePage(PdfPage page, int number, StringBuilder report)
    {
        var dict = page.Dictionary;
        report.Append("page ").Append(number.ToString(CultureInfo.InvariantCulture)).Append(": ")
            .Append("media=").Append(Box(page.GetInherited(PdfName.MediaBox) as PdfArray))
            .Append(" crop=").Append(Box(dict.GetAsArray(PdfName.CropBox)))
            .Append(" rotate=").Append(page.Rotation.ToString(CultureInfo.InvariantCulture))
            .Append(" annots=").Append((dict.GetAsArray(PdfName.Annots)?.Count ?? 0)
                .ToString(CultureInfo.InvariantCulture))
            .Append(" group=").Append(Group(dict.GetAsDictionary(PdfName.Group)))
            .Append('\n');

        report.Append("  text: ").Append(Safely(() => Normalise(LocationTextExtraction.ExtractPage(page)))).Append('\n');
        report.Append("  typeset: ").Append(Safely(() => Typeset(page))).Append('\n');
        report.Append("  ops: ").Append(Safely(() => Operators(page.GetContentBytes()))).Append('\n');
        DescribeResources(page.Resources, "  ", 0, report);
    }

    // ------------------------------------------------------------------ resources

    private static void DescribeResources(PdfDictionary? resources, string indent, int depth, StringBuilder report)
    {
        if (resources == null) return;

        DescribeEntries(resources, PdfName.Font, indent + "font", Font, report);
        DescribeEntries(resources, PdfName.ExtGState, indent + "gs", GraphicsState, report);
        DescribeEntries(resources, PdfName.Shading, indent + "shading", Shading, report);

        var xobjects = resources.GetAsDictionary(PdfName.XObject);
        if (xobjects == null) return;
        foreach (var key in Sorted(xobjects))
        {
            var stream = xobjects.GetAsStream(key);
            report.Append(indent).Append("xobject ").Append(key.Value).Append(": ")
                .Append(stream == null ? "<not a stream>" : XObject(stream)).Append('\n');

            if (stream != null && PdfName.Form.Equals(stream.GetAsName(PdfName.Subtype))
                && depth + 1 < MaxResourceDepth)
            {
                report.Append(indent).Append("  ops: ")
                    .Append(Safely(() => Operators(stream.GetDecodedBytes()))).Append('\n');
                DescribeResources(stream.GetAsDictionary(PdfName.Resources), indent + "  ", depth + 1, report);
            }
        }
    }

    private static void DescribeEntries(PdfDictionary resources, PdfName category, string label,
        Func<PdfDictionary, string> describe, StringBuilder report)
    {
        var dict = resources.GetAsDictionary(category);
        if (dict == null) return;
        foreach (var key in Sorted(dict))
        {
            var entry = dict.GetAsDictionary(key);
            report.Append(label).Append(' ').Append(key.Value).Append(": ")
                .Append(entry == null ? "<not a dictionary>" : describe(entry)).Append('\n');
        }
    }

    private static string Font(PdfDictionary font)
    {
        var parts = new List<string>
        {
            "subtype=" + Name(font.GetAsName(PdfName.Subtype)),
            "base=" + Name(font.GetAsName(PdfName.BaseFont)),
        };

        var encoding = font.Get(PdfName.Encoding);
        if (encoding is PdfName encodingName) parts.Add("encoding=" + encodingName.Value);
        else if (encoding is PdfDictionary encodingDict)
            parts.Add("encoding=" + Name(encodingDict.GetAsName(PdfName.BaseEncoding))
                + "+Differences[" + (encodingDict.GetAsArray(PdfName.Differences)?.Count ?? 0)
                    .ToString(CultureInfo.InvariantCulture) + "]");

        parts.Add("toUnicode=" + Yn(font.Get(PdfName.ToUnicode) != null));
        parts.Add("widths=" + (font.GetAsArray(PdfName.Widths)?.Count ?? 0).ToString(CultureInfo.InvariantCulture));

        if (font.GetAsDictionary(PdfName.CharProcs) is { } procs)
            parts.Add("charProcs=" + string.Join('/', Sorted(procs).Select(k => k.Value)));
        if (font.GetAsArray(PdfName.FontMatrix) is { } matrix)
            parts.Add("fontMatrix=" + Box(matrix));
        if (font.GetAsArray(PdfName.DescendantFonts)?.GetAsDictionary(0) is { } descendant)
            parts.Add("descendant=[" + Font(descendant) + "]");
        if (font.GetAsDictionary(PdfName.FontDescriptor) is { } descriptor)
        {
            PdfObject? program = descriptor.Get(PdfName.FontFile) ?? descriptor.Get(PdfName.FontFile2)
                ?? descriptor.Get(PdfName.FontFile3);
            parts.Add("embedded=" + Yn(program != null));
        }

        return string.Join(' ', parts);
    }

    private static string GraphicsState(PdfDictionary gs)
    {
        var parts = new List<string>();
        foreach (var key in Sorted(gs))
        {
            if (PdfName.Type.Equals(key)) continue;
            parts.Add(key.Value + "=" +
                (PdfName.SMask.Equals(key) ? SoftMask(gs.Get(key)) : Value(gs.Get(key))));
        }
        return parts.Count == 0 ? "<empty>" : string.Join(' ', parts);
    }

    /// <summary>
    /// A graphics-state soft mask, spelled out rather than reduced to a list of keys. The mask's
    /// group form (<c>/G</c>) is reachable only from here — it is not in any <c>/XObject</c>
    /// resource dictionary — so a rewrite that fails to carry it over produces a document that is
    /// structurally valid and visually wrong. Recording whether <c>/G</c> still resolves to a
    /// transparency group is the only way this suite would see that.
    /// </summary>
    private static string SoftMask(PdfObject? smask) => smask switch
    {
        null => "none",
        PdfName name => "/" + name.Value,
        PdfDictionary mask => Name(mask.GetAsName(PdfName.S))
            + "(G=" + (mask.GetAsStream(PdfName.G) is { } g
                ? "form " + Group(g.GetAsDictionary(PdfName.Group)) : "missing")
            + " BC=" + Box(mask.GetAsArray(PdfName.BC))
            + " TR=" + Yn(mask.Get(PdfName.TR) != null) + ")",
        _ => Value(smask),
    };

    private static string Shading(PdfDictionary shading) =>
        "type=" + Value(shading.Get(PdfName.ShadingType)) + " cs=" + Value(shading.Get(PdfName.ColorSpace));

    private static string XObject(PdfStream stream)
    {
        var subtype = stream.GetAsName(PdfName.Subtype);
        var parts = new List<string> { "subtype=" + Name(subtype) };

        if (PdfName.Image.Equals(subtype))
        {
            parts.Add("size=" + Value(stream.Get(PdfName.Width)) + "x" + Value(stream.Get(PdfName.Height)));
            parts.Add("cs=" + Value(stream.Get(PdfName.ColorSpace)));
            parts.Add("bpc=" + Value(stream.Get(PdfName.BitsPerComponent)));
            parts.Add("filter=" + Value(stream.Get(PdfName.Filter)));
            parts.Add("smask=" + Yn(stream.Get(PdfName.SMask) != null));
            parts.Add("mask=" + Yn(stream.Get(PdfName.Mask) != null));
        }
        else
        {
            parts.Add("bbox=" + Box(stream.GetAsArray(PdfName.BBox)));
            parts.Add("matrix=" + Box(stream.GetAsArray(PdfName.Matrix)));
            parts.Add("group=" + Group(stream.GetAsDictionary(PdfName.Group)));
        }
        return string.Join(' ', parts);
    }

    private static string Group(PdfDictionary? group) => group == null
        ? "none"
        : Name(group.GetAsName(PdfName.S)) + "(cs=" + Value(group.Get(PdfName.CS))
            + " I=" + Value(group.Get(PdfName.I)) + " K=" + Value(group.Get(PdfName.K)) + ")";

    // ------------------------------------------------------------------ forms

    private static void DescribeAcroForm(PdfDocument doc, StringBuilder report)
    {
        var acro = doc.Catalog?.GetAsDictionary(PdfName.AcroForm);
        if (acro == null) { report.Append("acroform: none\n"); return; }

        var fields = acro.GetAsArray(PdfName.Fields);
        report.Append("acroform: needAppearances=").Append(Value(acro.Get(PdfName.NeedAppearances)))
            .Append(" fields=").Append((fields?.Count ?? 0).ToString(CultureInfo.InvariantCulture))
            .Append('\n');

        for (int i = 0; fields != null && i < fields.Count; i++)
        {
            var field = fields.GetAsDictionary(i);
            if (field == null) { report.Append("  field: <not a dictionary>\n"); continue; }
            report.Append("  field: name=").Append(Value(field.Get(PdfName.T)))
                .Append(" type=").Append(Name(field.GetAsName(PdfName.FT)))
                .Append(" value=").Append(Value(field.Get(PdfName.V)))
                // A widget with no /AP renders blank in viewers that do not regenerate
                // appearances — the invariant ExportValidator's PDF040 also watches.
                .Append(" hasAppearance=").Append(Yn(field.Get(PdfName.AP) != null))
                .Append('\n');
        }
    }

    // ------------------------------------------------------------------ validation

    private static void DescribeValidation(byte[] pdf, string? password, StringBuilder report)
    {
        ValidationReport validation;
        try
        {
            validation = ExportValidator.Validate(pdf, password);
        }
        catch (Exception ex)
        {
            report.Append("validation: threw ").Append(ex.GetType().Name).Append('\n');
            return;
        }

        if (validation.Findings.Count == 0) { report.Append("validation: clean\n"); return; }

        // Codes and severities only: the messages carry byte offsets and object numbers that move
        // whenever the writer's output shifts by a byte, which would make the golden brittle for
        // no diagnostic gain — the code already names the defect class.
        var grouped = validation.Findings
            .GroupBy(f => (f.Severity, f.Code))
            .OrderBy(g => g.Key.Code, StringComparer.Ordinal)
            .Select(g => $"{g.Key.Severity}/{g.Key.Code}x{g.Count().ToString(CultureInfo.InvariantCulture)}");
        report.Append("validation: ").Append(string.Join(' ', grouped)).Append('\n');
    }

    // ------------------------------------------------------------------ primitives

    /// <summary>
    /// The operator census of a content stream: every operator that appears, with how often.
    /// Tokenised properly rather than pattern-matched, so an operator name occurring inside a
    /// string literal is not counted. This is what catches a rewrite that silently drops
    /// <c>gs</c>, <c>Do</c> or <c>Tj</c> while leaving the resource dictionaries intact.
    /// </summary>
    private static string Operators(byte[] content)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var op in ContentParser.Parse(content))
            counts[op.Operator] = counts.TryGetValue(op.Operator, out int n) ? n + 1 : 1;
        return counts.Count == 0 ? "<none>"
            : string.Join(' ', counts.Select(kv => kv.Key + "x" + kv.Value.ToString(CultureInfo.InvariantCulture)));
    }

    private static string Box(PdfArray? array) => array == null
        ? "none"
        : "[" + string.Join(' ', Enumerable.Range(0, array.Count).Select(i => Value(array.Get(i)))) + "]";

    private static string Name(PdfName? name) => name?.Value ?? "none";

    private static string Value(PdfObject? obj) => obj switch
    {
        null => "none",
        PdfNumber n => n.Value.ToString("0.####", CultureInfo.InvariantCulture),
        PdfName n => "/" + n.Value,
        PdfBoolean b => b.Value ? "true" : "false",
        PdfString s => "(" + Normalise(s.ToUnicodeString()) + ")",
        PdfArray a => Box(a),
        PdfDictionary d => "<<" + string.Join(' ', Sorted(d).Select(k => "/" + k.Value)) + ">>",
        _ => obj.GetType().Name,
    };

    private static IEnumerable<PdfName> Sorted(PdfDictionary dict) =>
        dict.Keys.Where(k => dict.Get(k) != null).OrderBy(k => k.Value, StringComparer.Ordinal);

    private static string Yn(bool value) => value ? "yes" : "no";

    /// <summary>
    /// Collapses runs of whitespace and truncates, so a golden line stays a golden line. Bucketing
    /// nothing else: the text must still differ when the document's text differs.
    /// </summary>
    private static string Normalise(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "<empty>";
        var collapsed = new StringBuilder(text.Length);
        bool space = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c)) { space = collapsed.Length > 0; continue; }
            if (space) { collapsed.Append(' '); space = false; }
            collapsed.Append(char.IsControl(c) ? '?' : c);
        }
        string result = collapsed.ToString();
        if (result.Length == 0) return "<empty>";
        return result.Length <= MaxTextLength ? result : result[..MaxTextLength] + "…";
    }

    /// <summary>
    /// File size, rounded down to the nearest 256 bytes. The exact length moves by a byte or two
    /// between runs (the <c>/ID</c> is fixed-width but xref offsets are not), so recording it
    /// exactly would be flaky — but recording nothing would hide a rewrite that doubled the file.
    /// </summary>
    private static string Bucket(int length) =>
        "~" + (length / 256 * 256).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The distinct <c>face@size</c> pairs the page's text is actually drawn in, sorted.
    /// <para>
    /// The resource inventory above records which fonts a page <em>declares</em>; this records what
    /// the glyphs are really set in, which is the thing an editing regression damages. #29 shipped
    /// because nothing in the suite looked at type size: replaced text came back 7–21% smaller than
    /// the original and every projection stayed identical. Sizes are the <c>Tf</c> operand with the
    /// text and graphics matrices applied, rounded to 0.1pt so a harmless last-bit difference in a
    /// matrix cannot make the recordings flap.
    /// </para>
    /// </summary>
    private static string Typeset(PdfPage page)
    {
        var listener = new TypesetListener();
        new ContentProcessor(listener).ProcessPage(page);
        return listener.Runs.Count == 0 ? "<no text>" : string.Join(' ', listener.Runs);
    }

    private sealed class TypesetListener : IContentListener
    {
        public SortedSet<string> Runs { get; } = new(StringComparer.Ordinal);

        public void OnText(TextRenderInfo t)
        {
            if (string.IsNullOrWhiteSpace(t.Text)) return;

            var m = t.TextToUser;
            double scale = Math.Sqrt(Math.Abs(m.A * m.D - m.B * m.C));
            double size = t.FontSize * (scale == 0 ? 1 : scale);
            string face = string.IsNullOrEmpty(t.FontName) ? "<unnamed>" : t.FontName;

            Runs.Add(string.Create(CultureInfo.InvariantCulture, $"{face}@{size:F1}"));
        }
    }

    private static string Safely(Func<string> describe)
    {
        try { return describe(); }
        catch (Exception ex) { return "<failed: " + ex.GetType().Name + ">"; }
    }
}
