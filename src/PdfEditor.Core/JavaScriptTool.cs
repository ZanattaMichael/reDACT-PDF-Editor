using PdfEditor.Core.Pdf;

namespace PdfEditor.Core;

/// <summary>
/// Authors document-level JavaScript (the /Names /JavaScript tree an Acrobat-style PDF runs when
/// it is opened). This is the deliberate, user-initiated counterpart to <see cref="PdfSafety"/>,
/// which detects and (by default) strips such scripts: a form author adds calculation/validation
/// logic here on purpose. The viewer only ever rasterises pages, so nothing added here executes
/// inside the editor — it runs in Acrobat/Chrome once the saved file is opened there.
/// </summary>
public static class JavaScriptTool
{
    /// <summary>
    /// Adds (or replaces, by name) a named document-level JavaScript. The name identifies the
    /// script in the document's JavaScript name tree; re-using a name overwrites that entry.
    /// </summary>
    public static EditResult AddDocumentScript(byte[] pdf, string name, string script,
        string? password = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A script name is required.", nameof(name));
        if (string.IsNullOrEmpty(script))
            throw new ArgumentException("The script is empty.", nameof(script));

        var doc = PdfIo.Open(pdf, password);
        var catalog = doc.Catalog!;
        string key = name.Trim();
        var entries = PdfNameTree.Read(JavaScriptTree(catalog))
            .Where(e => e.Key.ToUnicodeString() != key).ToList();
        entries.Add((PdfString.FromText(key), JavaScriptAction(script)));
        PdfNameTree.Write(catalog, PdfName.JavaScript, entries);
        return EditResult.Of(PdfIo.Save(doc));
    }

    /// <summary>Lists every named document-level JavaScript with its source text.</summary>
    public static IReadOnlyList<PdfScript> ListScripts(byte[] pdf, string? password = null)
    {
        var doc = PdfIo.OpenReadOnly(pdf, password);
        return PdfNameTree.Read(JavaScriptTree(doc.Catalog!))
            .Select(e => new PdfScript(e.Key.ToUnicodeString(), ExtractSource(e.Value)))
            .ToList();
    }

    /// <summary>
    /// Removes the named document-level JavaScript, keeping any others. Removing an unknown name is
    /// a no-op (the returned document is unchanged apart from a normal rewrite).
    /// </summary>
    public static EditResult RemoveScript(byte[] pdf, string name, string? password = null)
    {
        var doc = PdfIo.Open(pdf, password);
        var catalog = doc.Catalog!;
        var survivors = PdfNameTree.Read(JavaScriptTree(catalog))
            .Where(e => e.Key.ToUnicodeString() != name).ToList();
        // Drop the whole JavaScript tree, then re-add the scripts we are keeping.
        catalog.GetAsDictionary(PdfName.Names)?.Remove(PdfName.JavaScript);
        if (survivors.Count > 0) PdfNameTree.Write(catalog, PdfName.JavaScript, survivors);
        return EditResult.Of(PdfIo.Save(doc));
    }

    private static PdfDictionary? JavaScriptTree(PdfDictionary catalog) =>
        catalog.GetAsDictionary(PdfName.Names)?.GetAsDictionary(PdfName.JavaScript);

    /// <summary>A JavaScript action dictionary (§12.6.4.16).</summary>
    internal static PdfDictionary JavaScriptAction(string script)
    {
        var action = new PdfDictionary();
        action.Put(PdfName.Type, PdfName.Of("Action"));
        action.Put(PdfName.S, PdfName.JavaScript);
        action.Put(PdfName.JS, PdfString.FromText(script));
        return action;
    }

    /// <summary>Reads the script source out of a JavaScript action (the /JS string or stream).</summary>
    private static string ExtractSource(PdfObject value) => value switch
    {
        PdfDictionary action => action.Get(PdfName.JS) switch
        {
            PdfString s => s.ToUnicodeString(),
            PdfStream st => System.Text.Encoding.UTF8.GetString(st.GetDecodedBytes()),
            _ => ""
        },
        _ => ""
    };
}
