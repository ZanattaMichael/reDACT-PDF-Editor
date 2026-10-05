using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PdfEditor.Core.Pdf;

namespace PdfEditor.Core;

/// <summary>
/// Post-export sanity checks on the bytes this application is about to hand back to the user.
/// <para>
/// Every export path (redaction, flattening, signing, OCR, sanitising, merging) rewrites the
/// document, and a writer bug shows up as a file that opens in one viewer and not another. This
/// validator re-reads the finished bytes the way a strict consumer would and reports what is
/// wrong <em>and where</em>: cross-reference/trailer integrity, stream length and filter/decode
/// round-trip, page-tree consistency, and the cheap AcroForm appearance invariants.
/// </para>
/// <para>
/// It is deliberately read-only and never throws for a malformed document — a broken file is a
/// report full of findings, not an exception — so callers can run it after a save without any
/// risk of failing an export that would otherwise have succeeded.
/// </para>
/// </summary>
public static class ExportValidator
{
    /// <summary>Filters defined by the PDF specification; anything else no viewer can decode.</summary>
    private static readonly HashSet<string> KnownFilters = new(StringComparer.Ordinal)
    {
        "FlateDecode", "LZWDecode", "ASCII85Decode", "ASCIIHexDecode", "RunLengthDecode",
        "CCITTFaxDecode", "JBIG2Decode", "DCTDecode", "JPXDecode", "Crypt",
        "Fl", "LZW", "A85", "AHx", "RL", "CCF"  // abbreviations legal in inline images
    };

    private static readonly Regex LengthPattern =
        new(@"/Length\s+(\d+)(\s+\d+\s+R)?", RegexOptions.Compiled, TimeSpan.FromSeconds(2));

    private static readonly Regex ObjectHeaderPattern =
        new(@"(\d+)\s+(\d+)\s+obj", RegexOptions.Compiled, TimeSpan.FromSeconds(2));

    /// <summary>The most cross-reference problems worth listing; beyond this they all share a cause.</summary>
    private const int MaxXrefFindings = 10;

    /// <summary>
    /// Re-reads an exported document and reports everything that would make a viewer struggle
    /// with it. Never throws: an unreadable document comes back as a report whose
    /// <see cref="ValidationReport.IsValid"/> is false.
    /// </summary>
    public static ValidationReport Validate(byte[] pdf, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        var findings = new List<ValidationFinding>();

        // Latin-1 maps every byte to exactly one char, so string offsets are byte offsets.
        string raw = Encoding.Latin1.GetString(pdf);
        CheckHeader(raw, findings);
        CheckEofMarker(raw, findings);
        CheckCrossReferences(raw, findings);
        CheckStreamLengths(raw, findings);
        CheckWithParser(pdf, password, findings);

        return new ValidationReport(findings);
    }

    // --------------------------------------------------------------- file markers

    private static void CheckHeader(string raw, List<ValidationFinding> findings)
    {
        // The signature must appear within the first 1024 bytes (Acrobat's own tolerance).
        int index = raw.Length == 0 ? -1
            : raw.IndexOf("%PDF-", 0, Math.Min(raw.Length, 1024), StringComparison.Ordinal);
        if (index == 0) return;

        string start = raw.Length == 0 ? "<empty file>" : Printable(raw[..Math.Min(16, raw.Length)]);
        findings.Add(new ValidationFinding("PDF001", ValidationSeverity.Error, "byte offset 0",
            index < 0
                ? $"The output does not contain the '%PDF-' signature in its first 1024 bytes "
                  + $"(it starts with \"{start}\"). Viewers will refuse to open it — the export "
                  + "most likely wrote something other than a PDF, or wrote nothing at all."
                : $"The '%PDF-' signature is at byte offset {index} instead of 0 (the file starts "
                  + $"with \"{start}\"). Strict viewers only look at offset 0; strip whatever "
                  + "precedes the signature."));
    }

    private static void CheckEofMarker(string raw, List<ValidationFinding> findings)
    {
        const int tailSize = 2048;
        int from = Math.Max(0, raw.Length - tailSize);
        if (raw.IndexOf("%%EOF", from, StringComparison.Ordinal) >= 0) return;
        findings.Add(new ValidationFinding("PDF002", ValidationSeverity.Error,
            $"byte offset {raw.Length} (end of file)",
            $"No '%%EOF' marker in the last {tailSize} bytes of the {raw.Length}-byte output. "
            + "The file is truncated: the writer was interrupted or the stream was closed before "
            + "the trailer was flushed."));
    }

    // --------------------------------------------------------------- cross-reference table

    private static void CheckCrossReferences(string raw, List<ValidationFinding> findings)
    {
        int marker = raw.LastIndexOf("startxref", StringComparison.Ordinal);
        if (marker < 0)
        {
            if (raw.Length > 0)
                findings.Add(new ValidationFinding("PDF003", ValidationSeverity.Error, "trailer",
                    "The output has no 'startxref' keyword, so no viewer can find the "
                    + "cross-reference table. The trailer was never written."));
            return;
        }

        int cursor = marker + "startxref".Length;
        long? offset = ReadInteger(raw, ref cursor);
        if (offset is null)
        {
            findings.Add(new ValidationFinding("PDF003", ValidationSeverity.Error,
                $"byte offset {marker}",
                "The 'startxref' keyword is not followed by a byte offset. The trailer is "
                + "malformed and the cross-reference table cannot be located."));
            return;
        }

        if (offset <= 0 || offset >= raw.Length)
        {
            findings.Add(new ValidationFinding("PDF003", ValidationSeverity.Error,
                $"byte offset {marker}",
                $"'startxref' names byte offset {offset}, but the file is {raw.Length} bytes long. "
                + "The offset was computed against a different (longer or earlier) buffer than the "
                + "one that was written."));
            return;
        }

        int at = (int)offset.Value;
        if (raw.AsSpan(at).StartsWith("xref"))
        {
            CheckXrefTable(raw, at, findings);
            return;
        }
        if (ObjectHeaderPattern.Match(raw, at, Math.Min(32, raw.Length - at)) is { Success: true, Index: var i }
            && i == at)
        {
            return; // cross-reference stream; its own contents are validated by the parser pass
        }

        findings.Add(new ValidationFinding("PDF003", ValidationSeverity.Error,
            $"byte offset {at}",
            $"'startxref' names byte offset {at}, but the bytes there are "
            + $"\"{Printable(raw.Substring(at, Math.Min(24, raw.Length - at)))}\" — neither an "
            + "'xref' table nor a cross-reference stream object header."));
    }

    /// <summary>
    /// Walks a classic cross-reference table and verifies each in-use entry actually points at
    /// the object header it claims. This is the check that catches an export writing correct
    /// objects behind a stale offset table — parsers silently repair it, so nothing downstream
    /// would ever notice.
    /// </summary>
    private static void CheckXrefTable(string raw, int start, List<ValidationFinding> findings)
    {
        int cursor = start + "xref".Length;
        int reported = 0;
        while (reported < MaxXrefFindings)
        {
            SkipWhitespace(raw, ref cursor);
            if (raw.AsSpan(Math.Min(cursor, raw.Length)).StartsWith("trailer")) return;

            long? first = ReadInteger(raw, ref cursor);
            long? count = ReadInteger(raw, ref cursor);
            if (first is null || count is null || count < 0 || count > 10_000_000)
            {
                findings.Add(new ValidationFinding("PDF003", ValidationSeverity.Error,
                    $"byte offset {cursor}",
                    "The cross-reference table has a malformed subsection header (expected "
                    + "'<first-object-number> <count>'). The table cannot be read past this point."));
                return;
            }

            for (long i = 0; i < count && reported < MaxXrefFindings; i++)
            {
                long objectNumber = first.Value + i;
                long? entryOffset = ReadInteger(raw, ref cursor);
                long? generation = ReadInteger(raw, ref cursor);
                SkipWhitespace(raw, ref cursor);
                char kind = cursor < raw.Length ? raw[cursor++] : '?';
                if (entryOffset is null || generation is null || kind == '?') return;
                if (kind != 'n') continue; // free entry: nothing to point at

                if (CheckXrefEntry(raw, objectNumber, generation.Value, entryOffset.Value) is { } finding)
                {
                    findings.Add(finding);
                    reported++;
                }
            }
        }
    }

    private static ValidationFinding? CheckXrefEntry(string raw, long objectNumber, long generation, long offset)
    {
        string location = $"xref entry for object {objectNumber} {generation} R";
        if (offset <= 0 || offset >= raw.Length)
            return new ValidationFinding("PDF004", ValidationSeverity.Error, location,
                $"The cross-reference entry points at byte offset {offset}, which is outside the "
                + $"{raw.Length}-byte file. Object {objectNumber} is unreachable; viewers will have "
                + "to scan the whole file to repair the document.");

        int at = (int)offset;
        var match = ObjectHeaderPattern.Match(raw, at, Math.Min(48, raw.Length - at));
        bool pointsAtHeader = match.Success && match.Index == at
            && long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) == objectNumber;
        if (pointsAtHeader) return null;

        return new ValidationFinding("PDF004", ValidationSeverity.Error, location,
            $"The cross-reference entry points at byte offset {offset}, but the bytes there are "
            + $"\"{Printable(raw.Substring(at, Math.Min(24, raw.Length - at)))}\" instead of "
            + $"\"{objectNumber} {generation} obj\". The offset table does not match the objects "
            + "that were written.");
    }

    // --------------------------------------------------------------- stream lengths

    /// <summary>
    /// Verifies that every stream's declared <c>/Length</c> lands exactly on its
    /// <c>endstream</c> keyword. Parsers repair a wrong length by scanning, so this defect is
    /// invisible to any check that goes through a parser — but strict consumers trust /Length
    /// and read garbage.
    /// </summary>
    private static void CheckStreamLengths(string raw, List<ValidationFinding> findings)
    {
        int pos = 0;
        int reported = 0;
        while (reported < MaxXrefFindings)
        {
            int keyword = raw.IndexOf("stream", pos, StringComparison.Ordinal);
            if (keyword < 0) return;
            if (!IsStreamKeyword(raw, keyword))
            {
                // "endstream", or the word inside a name such as /application#2foctet-stream.
                pos = keyword + "stream".Length;
                continue;
            }

            int dataStart = keyword + "stream".Length;
            if (dataStart < raw.Length && raw[dataStart] == '\r') dataStart++;
            if (dataStart < raw.Length && raw[dataStart] == '\n') dataStart++;

            var (objectId, declared) = LookBackForLength(raw, keyword);
            if (declared is null)
            {
                // Indirect /Length: nothing to compare against, resynchronise past the data.
                pos = NextEndstream(raw, dataStart);
                continue;
            }

            int expected = dataStart + declared.Value;
            if (EndstreamStartsAt(raw, expected, out int after))
            {
                pos = after;
                continue;
            }

            int actual = raw.IndexOf("endstream", dataStart, StringComparison.Ordinal);
            string effect = actual - dataStart > declared.Value ? "a truncated" : "an over-long";
            findings.Add(new ValidationFinding("PDF031", ValidationSeverity.Error, objectId,
                actual < 0
                    ? $"The stream declares /Length {declared} but no 'endstream' keyword follows "
                      + $"its data (which begins at byte offset {dataStart}). The stream is unterminated."
                    : $"The stream declares /Length {declared}, but its 'endstream' keyword is at "
                      + $"byte offset {actual}, i.e. {actual - dataStart} bytes after the data begins "
                      + $"(expected {declared}). A viewer that trusts /Length will read "
                      + effect + " stream."));
            reported++;
            pos = actual < 0 ? raw.Length : actual + "endstream".Length;
        }
    }

    /// <summary>
    /// True when the <c>stream</c> at this offset really opens stream data: the specification
    /// requires it to close a dictionary and be followed by a line break, which rules out
    /// <c>endstream</c> and the word appearing inside a name or inside stream data.
    /// </summary>
    private static bool IsStreamKeyword(string raw, int at)
    {
        int after = at + "stream".Length;
        if (after >= raw.Length || (raw[after] != '\r' && raw[after] != '\n')) return false;
        int before = at;
        while (before > 0 && char.IsWhiteSpace(raw[before - 1])) before--;
        return before >= 2 && raw[before - 1] == '>' && raw[before - 2] == '>';
    }

    private static bool EndstreamStartsAt(string raw, int expected, out int after)
    {
        after = 0;
        if (expected < 0 || expected > raw.Length) return false;
        int p = expected;
        if (p < raw.Length && raw[p] == '\r') p++;
        if (p < raw.Length && raw[p] == '\n') p++;
        if (!raw.AsSpan(Math.Min(p, raw.Length)).StartsWith("endstream")) return false;
        after = p + "endstream".Length;
        return true;
    }

    private static int NextEndstream(string raw, int from)
    {
        int at = raw.IndexOf("endstream", from, StringComparison.Ordinal);
        return at < 0 ? raw.Length : at + "endstream".Length;
    }

    /// <summary>
    /// Finds the <c>/Length</c> and the owning object header immediately preceding a
    /// <c>stream</c> keyword. Returns a null length when it is an indirect reference.
    /// </summary>
    private static (string ObjectId, int? Length) LookBackForLength(string raw, int keyword)
    {
        const int window = 3000;
        int from = Math.Max(0, keyword - window);
        string dictionary = raw[from..keyword];

        string objectId = "stream at byte offset " + keyword;
        var headers = ObjectHeaderPattern.Matches(dictionary);
        if (headers.Count > 0)
        {
            var last = headers[^1];
            objectId = $"object {last.Groups[1].Value} {last.Groups[2].Value} R";
        }

        var lengths = LengthPattern.Matches(dictionary);
        if (lengths.Count == 0) return (objectId, null);
        var length = lengths[^1];
        if (length.Groups[2].Success) return (objectId, null); // /Length is an indirect reference
        return int.TryParse(length.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? (objectId, value)
            : (objectId, null);
    }

    // --------------------------------------------------------------- parsed-document checks

    private static void CheckWithParser(byte[] pdf, string? password, List<ValidationFinding> findings)
    {
        PdfDocument document;
        try
        {
            document = PdfIo.OpenReadOnly(pdf, password);
        }
        catch (Exception ex)
        {
            findings.Add(new ValidationFinding("PDF010", ValidationSeverity.Error, "whole document",
                $"The exported bytes cannot be parsed as a PDF: {ex.Message}. Nothing downstream "
                + "of this point could be checked — fix this before looking at any other finding."));
            return;
        }

        try
        {
            CheckRepairedXref(document, findings);
            var catalog = CheckCatalog(document, findings);
            // The page tree is walked directly, trusting nothing about /Count, so a tree whose
            // count lies is reported rather than fatal — reporting is this class's whole job.
            var pages = catalog is null ? new List<PdfDictionary>() : CollectPages(catalog, findings);
            CheckStreamsDecode(document, findings);
            CheckAcroForm(document, pages, findings);
        }
        catch (Exception ex)
        {
            findings.Add(new ValidationFinding("PDF010", ValidationSeverity.Error, "whole document",
                $"Validation stopped: the document threw while being inspected ({ex.GetType().Name}: "
                + $"{ex.Message}). The remaining checks could not run."));
        }
    }

    private static void CheckRepairedXref(PdfDocument document, List<ValidationFinding> findings)
    {
        if (!document.XrefRebuilt) return;
        findings.Add(new ValidationFinding("PDF005", ValidationSeverity.Warning, "cross-reference table",
            "The parser could not use the cross-reference table at all and rebuilt it by "
            + "scanning the whole file for object headers. "
            + "The document still opens here, but the table as written is wrong and stricter "
            + "consumers (and anything appending an incremental update) will break."));
    }

    /// <summary>
    /// Returns the document catalog, or null when it is unusable. A /Root pointing at the wrong
    /// object surfaces here as a catalog with no page tree rather than as a type mismatch — hence
    /// the wording.
    /// </summary>
    private static PdfDictionary? CheckCatalog(PdfDocument document, List<ValidationFinding> findings)
    {
        var root = document.Catalog;
        if (root?.Get(PdfName.Pages) is not null) return root;

        findings.Add(new ValidationFinding("PDF011", ValidationSeverity.Error, "trailer /Root",
            "The document catalog has no /Pages entry, so there is no page tree to render. Either "
            + "the catalog was written without one, or the trailer's /Root names the wrong object "
            + "— an object number that shifted during the rewrite is the usual cause."));
        return null;
    }

    /// <summary>Walks the page tree, checking every page it reaches, and returns those pages.</summary>
    private static List<PdfDictionary> CollectPages(PdfDictionary catalog, List<ValidationFinding> findings)
    {
        var pages = new List<PdfDictionary>();
        Descend(catalog.GetAsDictionary(PdfName.Pages), pages,
            new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance), 0);

        if (pages.Count == 0)
        {
            findings.Add(new ValidationFinding("PDF020", ValidationSeverity.Error, "page tree",
                "No pages are reachable from the document catalog. An export that drops every "
                + "page produces a file that opens to a blank error in most viewers."));
            return pages;
        }

        int? declared = catalog.GetAsDictionary(PdfName.Pages)?.GetAsInt(PdfName.Count);
        if (declared is not null && declared != pages.Count)
            findings.Add(new ValidationFinding("PDF012", ValidationSeverity.Warning, "page tree /Count",
                $"The page tree declares /Count {declared} but only {pages.Count} page(s) are "
                + "reachable through its /Kids. Viewers that trust /Count show the wrong page "
                + "count, and some refuse the missing pages outright."));

        for (int i = 0; i < pages.Count; i++) CheckPage(pages[i], i + 1, findings);
        return pages;
    }

    private static void Descend(PdfDictionary? node, List<PdfDictionary> pages,
        HashSet<PdfDictionary> seen, int depth)
    {
        // The depth and cycle guards keep a corrupt (self-referencing) tree from hanging validation.
        if (node is null || depth > 64 || !seen.Add(node)) return;
        var kids = node.GetAsArray(PdfName.Kids);
        if (kids is null)
        {
            pages.Add(node);
            return;
        }
        for (int i = 0; i < kids.Count; i++) Descend(kids.GetAsDictionary(i), pages, seen, depth + 1);
    }

    private static void CheckPage(PdfDictionary page, int number, List<ValidationFinding> findings)
    {
        string location = $"page {number}";
        if (!PdfName.Page.Equals(page.GetAsName(PdfName.Type)))
            findings.Add(new ValidationFinding("PDF022", ValidationSeverity.Warning, location,
                "The page dictionary has no /Type /Page entry. It is required by the "
                + "specification, and viewers that validate the page tree may skip the page."));

        var box = InheritedMediaBox(page, 0);
        if (box is null)
        {
            findings.Add(new ValidationFinding("PDF021", ValidationSeverity.Error, location,
                "Neither the page nor any of its ancestors declares a /MediaBox, so the page has "
                + "no defined size. Viewers fall back to their own default and the page renders at "
                + "the wrong scale."));
            return;
        }

        string spelled = "[" + string.Join(" ", Enumerable.Range(0, box.Count).Select(i => box.Get(i)?.ToString() ?? "null")) + "]";
        if (box.Count != 4 || Enumerable.Range(0, 4).Any(i => box.GetAsNumber(i) is null))
        {
            findings.Add(new ValidationFinding("PDF021", ValidationSeverity.Error, location,
                $"The /MediaBox is not four finite numbers (it is {spelled}). The page rectangle "
                + "cannot be computed."));
            return;
        }

        var values = box.ToDoubleArray();
        double width = Math.Abs(values[2] - values[0]);
        double height = Math.Abs(values[3] - values[1]);
        if (width <= 0 || height <= 0)
            findings.Add(new ValidationFinding("PDF021", ValidationSeverity.Error, location,
                $"The /MediaBox {spelled} has zero area ({width} x {height} points), so the page has no "
                + "renderable surface. A rectangle was probably written with its corners in the "
                + "wrong order or collapsed by a transform."));
    }

    private static PdfArray? InheritedMediaBox(PdfDictionary? page, int depth)
    {
        if (page is null || depth > 32) return null;
        return page.GetAsArray(PdfName.MediaBox)
            ?? InheritedMediaBox(page.GetAsDictionary(PdfName.Parent), depth + 1);
    }

    private static void CheckStreamsDecode(PdfDocument document, List<ValidationFinding> findings)
    {
        foreach (int number in document.ObjectNumbers.ToList())
        {
            PdfObject? candidate;
            try
            {
                candidate = document.GetObject(number);
            }
            catch (Exception ex)
            {
                findings.Add(new ValidationFinding("PDF030", ValidationSeverity.Error,
                    $"object {number} 0 R",
                    $"The object could not be loaded: {ex.Message}. The cross-reference entry or "
                    + "the object body written for it is corrupt."));
                continue;
            }
            if (candidate is PdfStream stream) CheckStream(stream, number, findings);
        }
    }

    private static void CheckStream(PdfStream stream, int number, List<ValidationFinding> findings)
    {
        string location = $"object {number} 0 R";
        var filters = stream.FilterNames().ToList();
        var unknown = filters.Where(f => !KnownFilters.Contains(f)).ToList();
        if (unknown.Count > 0)
        {
            findings.Add(new ValidationFinding("PDF032", ValidationSeverity.Error, location,
                $"The stream declares the filter(s) /{string.Join(", /", unknown)}, which are not "
                + "defined by the PDF specification. No viewer can decode this stream — the filter "
                + "name was written incorrectly."));
            return; // decoding would only restate the same problem
        }

        string chain = filters.Count == 0 ? "no filter" : "/" + string.Join(" then /", filters);
        string reason;
        try
        {
            // The engine decodes strictly: truncated or corrupt data throws rather than coming
            // back short, so a successful decode is proof the stream round-trips.
            stream.GetDecodedBytes();
            reason = "";
        }
        catch (Exception ex)
        {
            reason = ex.Message;
        }
        if (reason.Length == 0) return;

        findings.Add(new ValidationFinding("PDF030", ValidationSeverity.Error, location,
            $"The stream ({chain}, {stream.RawData.Length} bytes as written) does not decode: "
            + $"{reason} The data and the declared filter disagree — the bytes were written raw "
            + "under a compressed filter, or the encoded data was truncated."));
    }

    // --------------------------------------------------------------- AcroForm invariants

    private static void CheckAcroForm(PdfDocument document, List<PdfDictionary> pages,
        List<ValidationFinding> findings)
    {
        var form = document.Catalog?.GetAsDictionary(PdfName.AcroForm);
        if (form is null) return;

        bool needAppearances = form.GetAsBool(PdfName.NeedAppearances) == true;
        var onPages = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
        foreach (var page in pages)
        {
            var annots = page.GetAsArray(PdfName.Annots);
            if (annots is null) continue;
            foreach (var annot in annots) onPages.Add(annot);
        }

        foreach (var field in AcroForm.TerminalFields(document))
        {
            foreach (var widget in field.Widgets)
            {
                if (!needAppearances && widget.GetAsDictionary(PdfName.AP)?.Get(PdfName.N) is null)
                    findings.Add(new ValidationFinding("PDF040", ValidationSeverity.Warning,
                        $"form field '{field.Name}'",
                        "The field's widget has no normal appearance stream (/AP /N) and the form "
                        + "does not set /NeedAppearances, so viewers that do not regenerate "
                        + "appearances (Chrome's built-in viewer among them) draw the field blank. "
                        + "Regenerate the appearance when writing the field value."));

                if (widget.IsIndirect && !onPages.Contains(widget))
                    findings.Add(new ValidationFinding("PDF041", ValidationSeverity.Warning,
                        $"form field '{field.Name}'",
                        "The field's widget annotation is not listed in any page's /Annots array, "
                        + "so the field exists in the AcroForm but is invisible and cannot be "
                        + "clicked. Add the widget to the page it belongs to."));
            }
        }
    }

    // --------------------------------------------------------------- small parsing helpers

    private static void SkipWhitespace(string raw, ref int cursor)
    {
        while (cursor < raw.Length && char.IsWhiteSpace(raw[cursor])) cursor++;
    }

    private static long? ReadInteger(string raw, ref int cursor)
    {
        SkipWhitespace(raw, ref cursor);
        int start = cursor;
        while (cursor < raw.Length && char.IsAsciiDigit(raw[cursor])) cursor++;
        if (cursor == start) return null;
        return long.TryParse(raw.AsSpan(start, cursor - start), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out long value) ? value : null;
    }

    /// <summary>Renders a byte snippet for a log message, escaping anything unprintable.</summary>
    private static string Printable(string snippet)
    {
        var builder = new StringBuilder(snippet.Length);
        foreach (char c in snippet)
            builder.Append(c is >= ' ' and <= '~' ? c : '.');
        return builder.ToString();
    }
}
