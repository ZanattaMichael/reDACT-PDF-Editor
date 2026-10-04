using PdfEditor.Core.Pdf;

namespace PdfEditor.Core;

/// <summary>Extracts the link annotations embedded in a document.</summary>
public static class UrlTools
{
    /// <summary>The web (URI) links only — used for URL safety scanning and the Links panel.</summary>
    public static IReadOnlyList<PdfLink> ExtractLinks(byte[] pdf, string? password = null)
    {
        var doc = PdfIo.OpenReadOnly(pdf, password);
        var links = new List<PdfLink>();
        for (int i = 1; i <= doc.PageCount; i++)
            foreach (var annot in doc.GetPage(i).Annotations)
            {
                var rect = PdfRect.FromArray(annot.GetAsArray(PdfName.Rect));
                CollectUri(annot.Get(PdfName.A), i, rect, links);
            }
        return links;
    }

    /// <summary>
    /// Every <c>/Link</c> annotation, whatever its action (URI, JavaScript, go-to, launch, submit,
    /// …), with its clickable rectangle — so the viewer can draw a hotspot over each one. URI links
    /// carry their URL and get a risk rating; the rest are highlighted by kind.
    /// </summary>
    public static IReadOnlyList<PdfLink> ExtractLinkAnnotations(byte[] pdf, string? password = null)
    {
        var doc = PdfIo.OpenReadOnly(pdf, password);
        var links = new List<PdfLink>();
        for (int i = 1; i <= doc.PageCount; i++)
            foreach (var annot in doc.GetPage(i).Annotations)
            {
                if (!PdfName.Link.Equals(annot.GetAsName(PdfName.Subtype))) continue;
                var ad = annot;
                var (kind, url) = ClassifyLink(ad);
                var rect = PdfRect.FromArray(annot.GetAsArray(PdfName.Rect));
                links.Add(rect == null
                    ? new PdfLink(i, url, kind)
                    : new PdfLink(i, url, kind, rect.Value.X, rect.Value.Y, rect.Value.Width, rect.Value.Height));
            }
        return links;
    }

    /// <summary>Classifies a link annotation's action into (kind, url).</summary>
    private static (string Kind, string Url) ClassifyLink(PdfDictionary annot)
    {
        var a = annot.GetAsDictionary(PdfName.A);
        var s = a?.GetAsName(PdfName.S);
        if (s != null)
        {
            if (s.Equals(PdfName.URI)) return ("uri", a!.GetAsString(PdfName.URI)?.ToUnicodeString() ?? "");
            if (s.Equals(PdfName.JavaScript)) return ("javascript", "");
            if (s.Equals(PdfName.GoTo)) return ("goto", "");
            if (s.Equals(PdfName.GoToR)) return ("remote-goto", "");
            if (s.Equals(PdfName.Launch)) return ("launch", "");
            if (s.Equals(PdfName.Named)) return ("named", a!.GetAsName(PdfName.N)?.Value ?? "");
            if (s.Equals(PdfName.SubmitForm)) return ("submit", "");
            return ("link", "");
        }
        // A destination-only link (internal page jump) has /Dest instead of an /A action.
        return annot.Get(PdfName.Dest) != null ? ("goto", "") : ("link", "");
    }

    private static void CollectUri(PdfObject? obj, int page, PdfRect? rect,
        List<PdfLink> links)
    {
        if (obj is PdfArray arr)
        {
            foreach (var e in arr) CollectUri(e, page, rect, links);
            return;
        }
        if (obj is not PdfDictionary a) return;
        if (a.GetAsName(PdfName.S)?.Equals(PdfName.URI) == true)
        {
            var uri = a.GetAsString(PdfName.URI)?.ToUnicodeString();
            if (!string.IsNullOrWhiteSpace(uri))
                links.Add(rect == null
                    ? new PdfLink(page, uri)
                    : new PdfLink(page, uri, "uri", rect.Value.X, rect.Value.Y, rect.Value.Width, rect.Value.Height));
        }
        CollectUri(a.Get(PdfName.Next), page, rect, links); // chained actions
    }
}
