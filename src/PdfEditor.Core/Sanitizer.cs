using PdfEditor.Core.Pdf;

namespace PdfEditor.Core;

/// <summary>
/// Finds and removes "hidden information" a PDF carries that a user usually does not intend to
/// share: document metadata (author, software, timestamps, XMP), embedded file attachments,
/// JavaScript and outward-reaching actions, comment/markup annotations, bookmarks, and optional-
/// content layers. This is the "sanitise before sharing" counterpart to redaction (which removes
/// visible content) — here the target is data that never shows on the page.
/// </summary>
public static class Sanitizer
{
    private static readonly PdfName[] KeepAnnotations = { PdfName.Link, PdfName.Widget };

    // Info keys the PDF producer stamps automatically; they aren't user-authored "hidden data", so
    // they don't count toward the report (every rewritten PDF has a Producer/ModDate).
    private static readonly HashSet<string> AutoInfoKeys =
        new() { "Producer", "CreationDate", "ModDate", "Trapped" };

    // Standard Info fields cleared through the public API (which won't expose the raw dictionary
    // in read+write mode); any other key is a custom one removed by name.
    private static readonly HashSet<string> StandardInfoKeys =
        new() { "Author", "Title", "Subject", "Keywords", "Creator" };

    /// <summary>Counts each category of hidden information present, for a "what will be removed" preview.</summary>
    public static HiddenDataReport Inspect(byte[] pdf, string? password = null)
    {
        var doc = PdfIo.OpenReadOnly(pdf, password);
        var catalog = doc.Catalog!;

        var safety = PdfSafety.Scan(pdf, password);
        return new HiddenDataReport(
            MetadataFields: CountMetadata(doc, catalog),
            Attachments: CountAttachments(doc, catalog),
            ScriptsAndActions: safety.JavaScriptCount + safety.UrlCount,
            Annotations: CountAnnotations(doc),
            Bookmarks: CountOutlines(catalog.GetAsDictionary(PdfName.Outlines)?.GetAsDictionary(PdfName.First)),
            HiddenLayers: catalog.GetAsDictionary(PdfName.OCProperties)?.GetAsArray(PdfName.OCGs)?.Count ?? 0);
    }

    /// <summary>Removes the selected categories of hidden information and returns the cleaned PDF.</summary>
    public static EditResult Sanitize(byte[] pdf, SanitizeOptions options, string? password = null)
    {
        // Scripts/actions are stripped by the (already-tested) safety pass first. Its output keeps
        // the document's encryption, so the remaining passes open it with the same password.
        byte[] working = options.ScriptsAndActions
            ? PdfSafety.StripActive(pdf, javaScript: true, urls: true, password).Pdf
            : pdf;

        // The Info dictionary isn't reachable through the trailer once a writer is attached, so
        // discover any custom metadata keys read-only first and clear them by name below.
        var customKeys = options.Metadata ? CustomInfoKeys(working, password) : new List<string>();

        var doc = PdfIo.Open(working, password);
        var catalog = doc.Catalog!;
        if (options.Metadata) StripMetadata(doc, catalog, customKeys);
        if (options.Attachments) StripAttachments(doc, catalog);
        if (options.Annotations) StripAnnotations(doc);
        if (options.Bookmarks) catalog.Remove(PdfName.Outlines);
        if (options.HiddenLayers) catalog.Remove(PdfName.OCProperties);
        return EditResult.Of(PdfIo.Save(doc));
    }

    // ------------------------------------------------------------- counting

    private static int CountMetadata(PdfDocument doc, PdfDictionary catalog)
    {
        int count = 0;
        var info = doc.Trailer.GetAsDictionary(PdfName.Info); // reachable in read-only mode
        if (info != null)
            foreach (var key in info.Keys)
                if (!AutoInfoKeys.Contains(key.Value)
                    && info.Get(key) is PdfString s && !string.IsNullOrEmpty(s.ToUnicodeString()))
                    count++;
        if (catalog.Get(PdfName.Metadata) != null) count++; // XMP packet
        return count;
    }

    /// <summary>Custom (non-standard, non-auto) Info keys, read while no writer is attached.</summary>
    private static List<string> CustomInfoKeys(byte[] pdf, string? password)
    {
        var doc = PdfIo.OpenReadOnly(pdf, password);
        var info = doc.Trailer.GetAsDictionary(PdfName.Info);
        if (info == null) return new List<string>();
        return info.Keys.Select(k => k.Value)
            .Where(k => !StandardInfoKeys.Contains(k) && !AutoInfoKeys.Contains(k))
            .ToList();
    }

    private static int CountAttachments(PdfDocument doc, PdfDictionary catalog)
    {
        int count = NameTreeSize(catalog.GetAsDictionary(PdfName.Names)?.GetAsDictionary(PdfName.EmbeddedFiles));
        count += catalog.GetAsArray(PdfName.AF)?.Count ?? 0;
        for (int i = 1; i <= doc.PageCount; i++)
            foreach (var annot in doc.GetPage(i).GetAnnotations())
                if (PdfName.FileAttachment.Equals(annot.GetAsName(PdfName.Subtype))) count++;
        return count;
    }

    private static int CountAnnotations(PdfDocument doc)
    {
        int count = 0;
        for (int i = 1; i <= doc.PageCount; i++)
            foreach (var annot in doc.GetPage(i).GetAnnotations())
                if (!KeepAnnotations.Any(k => k.Equals(annot.GetAsName(PdfName.Subtype)))) count++;
        return count;
    }

    private static int NameTreeSize(PdfDictionary? tree)
    {
        if (tree == null) return 0;
        int count = (tree.GetAsArray(PdfName.Names)?.Count ?? 0) / 2;
        var kids = tree.GetAsArray(PdfName.Kids);
        if (kids != null)
            foreach (var kid in kids)
                if (kid is PdfDictionary kd) count += NameTreeSize(kd);
        return count;
    }

    private static int CountOutlines(PdfDictionary? node)
    {
        int count = 0;
        var seen = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
        while (node != null && seen.Add(node)) // guard against malformed cyclic /Next chains
        {
            count++;
            count += CountOutlines(node.GetAsDictionary(PdfName.First));
            node = node.GetAsDictionary(PdfName.Next);
        }
        return count;
    }

    // ------------------------------------------------------------- removal

    private static void StripMetadata(PdfDocument doc, PdfDictionary catalog, IEnumerable<string> customKeys)
    {
        // Clear the standard identifying fields, and drop any custom keys by name. Producer/dates
        // are left for the writer to re-stamp — they are tool data, not user-authored hidden info.
        if (doc.Info is { } di)
        {
            foreach (var key in new[] { PdfName.Author, PdfName.Title, PdfName.Subject, PdfName.Keywords, PdfName.Creator })
                di.Remove(key);
            foreach (var key in customKeys) di.Remove(PdfName.Of(key));
        }
        catalog.Remove(PdfName.Metadata); // XMP
    }

    private static void StripAttachments(PdfDocument doc, PdfDictionary catalog)
    {
        catalog.GetAsDictionary(PdfName.Names)?.Remove(PdfName.EmbeddedFiles);
        catalog.Remove(PdfName.AF);
        for (int i = 1; i <= doc.PageCount; i++)
        {
            var page = doc.GetPage(i);
            foreach (var annot in page.GetAnnotations().ToList())
                if (PdfName.FileAttachment.Equals(annot.GetAsName(PdfName.Subtype)))
                    page.RemoveAnnotation(annot);
        }
    }

    private static void StripAnnotations(PdfDocument doc)
    {
        for (int i = 1; i <= doc.PageCount; i++)
        {
            var page = doc.GetPage(i);
            foreach (var annot in page.GetAnnotations().ToList())
                if (!KeepAnnotations.Any(k => k.Equals(annot.GetAsName(PdfName.Subtype))))
                    page.RemoveAnnotation(annot);
        }
    }
}
