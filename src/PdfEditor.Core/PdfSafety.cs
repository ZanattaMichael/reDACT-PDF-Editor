using PdfEditor.Core.Pdf;

namespace PdfEditor.Core;

/// <summary>
/// Detects and removes "active content" in a PDF: embedded JavaScript and actions that reach
/// outside the document (open a URL, launch a file, submit a form, jump to a remote file).
/// The viewer renders pages to images so nothing here ever executes — but the moment a document
/// is saved back out and opened in Acrobat/Chrome, these can run. They are surfaced and, until
/// the user explicitly keeps them, stripped on save.
/// </summary>
public static class PdfSafety
{
    // Action subtypes (/S) that reach outside the document.
    private static readonly PdfName[] UrlActions =
        { PdfName.URI, PdfName.Launch, PdfName.SubmitForm, PdfName.GoToR, PdfName.ImportData };

    public static SafetyReport Scan(byte[] pdf, string? password = null)
    {
        var doc = PdfIo.OpenReadOnly(pdf, password);
        int js = 0, url = 0;
        var samples = new List<string>();
        var catalog = doc.Catalog!;

        // Document-level JavaScript name tree (/Names /JavaScript).
        js += CountNameTreeJs(catalog.GetAsDictionary(PdfName.Names)?.GetAsDictionary(PdfName.JavaScript), samples);

        // Document open action + additional actions.
        Classify(catalog.Get(PdfName.OpenAction), ref js, ref url, samples);
        ClassifyAA(catalog.GetAsDictionary(PdfName.AA), ref js, ref url, samples);

        for (int i = 1; i <= doc.PageCount; i++)
        {
            var page = doc.GetPage(i);
            ClassifyAA(page.Dictionary.GetAsDictionary(PdfName.AA), ref js, ref url, samples);
            foreach (var annot in page.Annotations)
            {
                var ad = annot;
                Classify(ad.Get(PdfName.A), ref js, ref url, samples);
                ClassifyAA(ad.GetAsDictionary(PdfName.AA), ref js, ref url, samples);
            }
        }
        return new SafetyReport(js, url, samples.Take(12).ToList());
    }

    /// <summary>
    /// Returns the full source of every embedded JavaScript in the document (document-level name
    /// tree, open action, additional actions, and annotation actions) — so a warning can point the
    /// user at the actual code. Never executed; returned purely as text.
    /// </summary>
    public static IReadOnlyList<string> JavaScriptSources(byte[] pdf, string? password = null)
    {
        var doc = PdfIo.OpenReadOnly(pdf, password);
        var sources = new List<string>();
        var catalog = doc.Catalog!;

        CollectNameTreeJs(catalog.GetAsDictionary(PdfName.Names)?.GetAsDictionary(PdfName.JavaScript), sources);
        CollectActionJs(catalog.Get(PdfName.OpenAction), sources);
        CollectAaJs(catalog.GetAsDictionary(PdfName.AA), sources);
        for (int i = 1; i <= doc.PageCount; i++)
        {
            var page = doc.GetPage(i);
            CollectAaJs(page.Dictionary.GetAsDictionary(PdfName.AA), sources);
            foreach (var annot in page.Annotations)
            {
                var ad = annot;
                CollectActionJs(ad.Get(PdfName.A), sources);
                CollectAaJs(ad.GetAsDictionary(PdfName.AA), sources);
            }
        }
        return sources;
    }

    private static void CollectActionJs(PdfObject? obj, List<string> sources)
    {
        if (obj is PdfArray arr) { foreach (var e in arr) CollectActionJs(e, sources); return; }
        if (obj is not PdfDictionary a) return;
        if (a.GetAsName(PdfName.S)?.Equals(PdfName.JavaScript) == true)
        {
            string src = JsText(a);
            if (!string.IsNullOrWhiteSpace(src)) sources.Add(src);
        }
        CollectActionJs(a.Get(PdfName.Next), sources); // chained actions
    }

    private static void CollectAaJs(PdfDictionary? aa, List<string> sources)
    {
        if (aa == null) return;
        foreach (var key in aa.Keys.ToList()) CollectActionJs(aa.Get(key), sources);
    }

    private static void CollectNameTreeJs(PdfDictionary? tree, List<string> sources)
    {
        if (tree == null) return;
        var names = tree.GetAsArray(PdfName.Names);
        if (names != null)
            for (int i = 1; i < names.Count; i += 2)
                if (names.Get(i) is PdfDictionary d)
                {
                    string src = JsText(d);
                    if (!string.IsNullOrWhiteSpace(src)) sources.Add(src);
                }
        var kids = tree.GetAsArray(PdfName.Kids);
        if (kids != null)
            foreach (var kid in kids)
                if (kid is PdfDictionary kd) CollectNameTreeJs(kd, sources);
    }

    private static string JsText(PdfDictionary action) => action.Get(PdfName.JS) switch
    {
        PdfString s => s.ToUnicodeString(),
        PdfStream st => System.Text.Encoding.UTF8.GetString(st.GetDecodedBytes()),
        _ => ""
    };

    /// <summary>Removes all embedded JavaScript and outward-reaching actions (internal links kept).</summary>
    public static EditResult StripActive(byte[] pdf, string? password = null)
        => StripActive(pdf, javaScript: true, urls: true, password);

    /// <summary>
    /// Selectively removes active content: embedded JavaScript when <paramref name="javaScript"/>
    /// is set, and outward-reaching URL/launch/submit actions when <paramref name="urls"/> is set.
    /// Internal (same-document) links are always preserved.
    /// </summary>
    public static EditResult StripActive(byte[] pdf, bool javaScript, bool urls, string? password = null)
    {
        var doc = PdfIo.Open(pdf, password);
        var catalog = doc.Catalog!;
        if (javaScript)
        {
            catalog.GetAsDictionary(PdfName.Names)?.Remove(PdfName.JavaScript);
            catalog.Remove(PdfName.AA); // document additional actions are JavaScript triggers
        }
        RemoveActionIf(catalog, PdfName.OpenAction, javaScript, urls);

        for (int i = 1; i <= doc.PageCount; i++)
        {
            var page = doc.GetPage(i);
            if (javaScript) page.Dictionary.Remove(PdfName.AA);
            foreach (var annot in page.Annotations)
            {
                var ad = annot;
                RemoveActionIf(ad, PdfName.A, javaScript, urls);
                if (javaScript) ad.Remove(PdfName.AA);
            }
        }
        return EditResult.Of(PdfIo.Save(doc));
    }

    private static void RemoveActionIf(PdfDictionary owner, PdfName key, bool js, bool urls)
    {
        if (owner.Get(key) is not PdfDictionary a) return;
        var s = a.GetAsName(PdfName.S);
        if (s == null) return;
        bool isJs = s.Equals(PdfName.JavaScript);
        bool isUrl = UrlActions.Any(s.Equals);
        if ((isJs && js) || (isUrl && urls)) owner.Remove(key);
    }

    private static void Classify(PdfObject? obj, ref int js, ref int url, List<string> samples)
    {
        if (obj is PdfArray arr)
        {
            foreach (var e in arr) Classify(e, ref js, ref url, samples);
            return;
        }
        if (obj is not PdfDictionary a) return;

        var s = a.GetAsName(PdfName.S);
        if (s != null)
        {
            if (s.Equals(PdfName.JavaScript)) { js++; AddSample(samples, JsSample(a)); }
            else if (s.Equals(PdfName.URI))
            {
                url++;
                AddSample(samples, a.GetAsString(PdfName.URI)?.ToUnicodeString() ?? "URI");
            }
            else if (UrlActions.Any(s.Equals)) { url++; AddSample(samples, s.Value); }
        }
        Classify(a.Get(PdfName.Next), ref js, ref url, samples); // chained actions
    }

    private static void ClassifyAA(PdfDictionary? aa, ref int js, ref int url, List<string> samples)
    {
        if (aa == null) return;
        foreach (var key in aa.Keys.ToList()) Classify(aa.Get(key), ref js, ref url, samples);
    }

    private static int CountNameTreeJs(PdfDictionary? tree, List<string> samples)
    {
        if (tree == null) return 0;
        int count = 0;
        var names = tree.GetAsArray(PdfName.Names);
        if (names != null)
            for (int i = 1; i < names.Count; i += 2)
            {
                count++;
                if (names.Get(i) is PdfDictionary d) AddSample(samples, JsSample(d));
            }
        var kids = tree.GetAsArray(PdfName.Kids);
        if (kids != null)
            foreach (var kid in kids)
                if (kid is PdfDictionary kd) count += CountNameTreeJs(kd, samples);
        return count;
    }

    private static string JsSample(PdfDictionary action)
    {
        var js = action.Get(PdfName.JS);
        string text = js switch
        {
            PdfString s => s.ToUnicodeString(),
            PdfStream st => System.Text.Encoding.UTF8.GetString(st.GetDecodedBytes()),
            _ => "JavaScript"
        };
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return "JS: " + (text.Length > 80 ? text[..80] + "…" : text);
    }

    private static void AddSample(List<string> samples, string sample)
    {
        if (samples.Count < 12 && !samples.Contains(sample)) samples.Add(sample);
    }
}
