using System.Text.RegularExpressions;
using PdfEditor.Core.Pdf;

namespace PdfEditor.Core;

/// <summary>
/// Compares the text of two document versions page by page and reports what changed. The diff is
/// word-level (a longest-common-subsequence alignment of the words on each page), which highlights
/// insertions and deletions while ignoring reflow/whitespace. Purely textual — it does not diff
/// images or exact positioning.
/// </summary>
public static class DocComparer
{
    // Above this many words on a page, the O(n*m) LCS is skipped for a cheaper multiset diff so a
    // huge page can't stall the host; the reported added/removed words are still accurate.
    private const int LcsWordCap = 4000;

    public static ComparisonReport Compare(byte[] oldPdf, byte[] newPdf,
        string? oldPassword = null, string? newPassword = null)
    {
        var docOld = PdfIo.OpenReadOnly(oldPdf, oldPassword);
        var docNew = PdfIo.OpenReadOnly(newPdf, newPassword);
        int pagesOld = docOld.PageCount;
        int pagesNew = docNew.PageCount;

        var pages = new List<PageDiff>();
        int changed = 0, added = 0, removed = 0;
        for (int i = 1; i <= Math.Max(pagesOld, pagesNew); i++)
        {
            string[] a = i <= pagesOld ? Words(docOld, i) : Array.Empty<string>();
            string[] b = i <= pagesNew ? Words(docNew, i) : Array.Empty<string>();
            var (addedWords, removedWords) = Diff(a, b);
            var diff = new PageDiff(i, addedWords, removedWords);
            if (diff.Changed) changed++;
            added += addedWords.Count;
            removed += removedWords.Count;
            pages.Add(diff);
        }
        return new ComparisonReport(pagesOld, pagesNew, changed, added, removed, pages);
    }

    private static string[] Words(PdfDocument doc, int page)
    {
        var pdfPage = doc.GetPage(page);
        string text = PdfIo.Guarded($"extracting text from page {page}", () =>
        {
            PdfStructureGuard.EnsureFormXObjectsTerminate(pdfPage);
            return LocationTextExtraction.ExtractPage(pdfPage);
        });
        return Regex.Split(text.Trim(), @"\s+", RegexOptions.None, TimeSpan.FromSeconds(2))
            .Where(w => w.Length > 0).ToArray();
    }

    /// <summary>Returns (added, removed) words aligning <paramref name="a"/> (old) to <paramref name="b"/> (new).</summary>
    private static (IReadOnlyList<string> Added, IReadOnlyList<string> Removed) Diff(string[] a, string[] b)
    {
        if (a.Length == 0) return (b, Array.Empty<string>());
        if (b.Length == 0) return (Array.Empty<string>(), a);

        // Guard against pathological page sizes: fall back to a multiset difference.
        if ((long)a.Length * b.Length > (long)LcsWordCap * LcsWordCap)
            return MultisetDiff(a, b);

        // LCS length table, then backtrack to classify each word as common / added / removed.
        int n = a.Length, m = b.Length;
        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var added = new List<string>();
        var removed = new List<string>();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y]) { x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) removed.Add(a[x++]);
            else added.Add(b[y++]);
        }
        while (x < n) removed.Add(a[x++]);
        while (y < m) added.Add(b[y++]);
        return (added, removed);
    }

    /// <summary>Order-insensitive difference of two word bags (used only for very large pages).</summary>
    private static (IReadOnlyList<string> Added, IReadOnlyList<string> Removed) MultisetDiff(string[] a, string[] b)
    {
        var counts = new Dictionary<string, int>();
        foreach (var w in a) counts[w] = counts.GetValueOrDefault(w) + 1;
        var added = new List<string>();
        foreach (var w in b)
        {
            if (counts.TryGetValue(w, out int c) && c > 0) counts[w] = c - 1;
            else added.Add(w);
        }
        var removed = counts.Where(kv => kv.Value > 0).SelectMany(kv => Enumerable.Repeat(kv.Key, kv.Value)).ToList();
        return (added, removed);
    }
}
