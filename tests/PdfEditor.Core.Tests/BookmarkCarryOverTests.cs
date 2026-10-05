using System.Text;
using PdfEditor.Core;
using PdfEditor.Core.Pdf;
using Xunit;

namespace PdfEditor.Tests;

/// <summary>
/// Merging and arranging rebuild the document from copied pages, so bookmarks have to be carried
/// across deliberately — and only those that still lead somewhere: a bookmark to a deleted page is
/// a dead entry that names exactly what was removed.
/// </summary>
public class BookmarkCarryOverTests
{
    [Fact]
    public void Merge_KeepsEachDocumentsBookmarks_PointingAtTheirMergedPages()
    {
        byte[] merged = Merger.Merge(new[] { WithBookmarks("A"), WithBookmarks("B") });

        Assert.Equal(new[]
        {
            "A one -> 1", "A two -> 2", "  A two point one -> 2", "A three -> 3",
            "B one -> 4", "B two -> 5", "  B two point one -> 5", "B three -> 6",
        }, Outline(merged));
    }

    [Fact]
    public void Arrange_DropsBookmarksToDeletedPages_AndFollowsTheNewOrder()
    {
        byte[] arranged = PageTools.Arrange(WithBookmarks("A"), new[] { 3, 1 }).Pdf;

        Assert.Equal(new[] { "A one -> 2", "A three -> 1" }, Outline(arranged));
    }

    [Fact]
    public void Arrange_KeepsAParentWhoseOwnPageWentButWhoseChildStayed()
    {
        var doc = PdfDocument.Open(WithBookmarks("A"));
        // Repoint the child at page 3, then delete page 2: the parent loses its destination but
        // still has to be there to hold the child.
        var two = Item(doc, "A two")!;
        var child = two.GetAsDictionary(PdfName.First)!;
        child.Remove(PdfName.A);
        child.Put(PdfName.Dest, new PdfArray { doc.GetPage(3).Dictionary, PdfName.Of("Fit") });

        byte[] arranged = PageTools.Arrange(doc.Save(), new[] { 1, 3 }).Pdf;

        Assert.Equal(new[] { "A one -> 1", "A two -> none", "  A two point one -> 2", "A three -> 2" },
            Outline(arranged));
    }

    [Fact]
    public void Arrange_DocumentWithoutBookmarks_GainsNoOutlineTree()
    {
        byte[] arranged = PageTools.Arrange(TestPdfs.MultiPage(2), new[] { 2, 1 }).Pdf;

        Assert.Null(PdfDocument.Open(arranged).Catalog!.Get(PdfName.Outlines));
    }

    [Fact]
    public void Merge_CyclicOutlineChain_IsCutRatherThanLoopingForever()
    {
        var doc = PdfDocument.Open(WithBookmarks("A"));
        var three = Item(doc, "A three")!;
        three.Put(PdfName.Next, Item(doc, "A one")!); // the chain now loops back on itself

        byte[] merged = Merger.Merge(new[] { doc.Save(), TestPdfs.MultiPage(1) });

        Assert.Equal(new[] { "A one -> 1", "A two -> 2", "  A two point one -> 2", "A three -> 3" },
            Outline(merged));
    }

    /// <summary>
    /// Three pages and four bookmarks reaching them every way a bookmark can: an explicit
    /// destination, a named destination, a GoTo action, plus one script-only bookmark that leads
    /// nowhere and must not survive a copy.
    /// </summary>
    private static byte[] WithBookmarks(string prefix)
    {
        var doc = PdfDocument.Open(TestPdfs.MultiPage(3, prefix));
        var catalog = doc.Catalog!;
        var root = doc.MakeIndirect(new PdfDictionary());
        root.Put(PdfName.Type, PdfName.Outlines);

        PdfDictionary Bookmark(string title, PdfDictionary parent)
        {
            var item = doc.MakeIndirect(new PdfDictionary());
            item.Put(PdfName.Title, PdfString.FromText(title));
            item.Put(PdfName.Parent, parent);
            return item;
        }

        var one = Bookmark($"{prefix} one", root);
        one.Put(PdfName.Dest, new PdfArray { doc.GetPage(1).Dictionary, PdfName.Of("Fit") });

        var two = Bookmark($"{prefix} two", root);
        two.Put(PdfName.Dest, new PdfString(Encoding.ASCII.GetBytes("chapter-two")));
        PdfNameTree.Write(catalog, PdfName.Of("Dests"), new[]
        {
            (new PdfString(Encoding.ASCII.GetBytes("chapter-two")),
                (PdfObject)new PdfArray { doc.GetPage(2).Dictionary, PdfName.Of("XYZ"), new PdfNumber(0), new PdfNumber(800), PdfNull.Instance }),
        });

        var twoOne = Bookmark($"{prefix} two point one", two);
        var goTo = new PdfDictionary();
        goTo.Put(PdfName.S, PdfName.GoTo);
        goTo.Put(PdfName.Of("D"), new PdfArray { doc.GetPage(2).Dictionary, PdfName.Of("Fit") });
        twoOne.Put(PdfName.A, goTo);
        two.Put(PdfName.First, twoOne);
        two.Put(PdfName.Of("Last"), twoOne);
        two.Put(PdfName.Count, new PdfNumber(1));

        var three = Bookmark($"{prefix} three", root);
        three.Put(PdfName.Dest, new PdfArray { doc.GetPage(3).Dictionary, PdfName.Of("Fit") });

        var script = Bookmark($"{prefix} script", root);
        var js = new PdfDictionary();
        js.Put(PdfName.S, PdfName.JavaScript);
        js.Put(PdfName.JS, PdfString.FromText("app.alert(1)"));
        script.Put(PdfName.A, js);

        one.Put(PdfName.Next, two);
        two.Put(PdfName.Prev, one);
        two.Put(PdfName.Next, three);
        three.Put(PdfName.Prev, two);
        three.Put(PdfName.Next, script);
        script.Put(PdfName.Prev, three);
        root.Put(PdfName.First, one);
        root.Put(PdfName.Of("Last"), script);
        root.Put(PdfName.Count, new PdfNumber(5));
        catalog.Put(PdfName.Outlines, root);
        return doc.Save();
    }

    private static PdfDictionary? Item(PdfDocument doc, string title)
    {
        for (var item = doc.Catalog!.GetAsDictionary(PdfName.Outlines)!.GetAsDictionary(PdfName.First);
             item != null; item = item.GetAsDictionary(PdfName.Next))
            if (item.GetText(PdfName.Title) == title) return item;
        return null;
    }

    /// <summary>The outline as "title -> page" lines, children indented, checking the links on the way.</summary>
    private static List<string> Outline(byte[] pdf)
    {
        var doc = PdfDocument.Open(pdf);
        var pages = Enumerable.Range(1, doc.PageCount).ToDictionary(n => (PdfObject)doc.GetPage(n).Dictionary, n => n,
            ReferenceEqualityComparer.Instance);
        var lines = new List<string>();
        var root = doc.Catalog!.GetAsDictionary(PdfName.Outlines);
        if (root == null) return lines;

        int Walk(PdfDictionary parent, int depth)
        {
            int visible = 0;
            PdfDictionary? previous = null;
            for (var item = parent.GetAsDictionary(PdfName.First); item != null; item = item.GetAsDictionary(PdfName.Next))
            {
                Assert.Same(parent, item.GetAsDictionary(PdfName.Parent));
                Assert.Same(previous, item.GetAsDictionary(PdfName.Prev));
                var page = item.GetAsArray(PdfName.Dest)?.Get(0);
                string target = page != null && pages.TryGetValue(page, out int n) ? n.ToString() : "none";
                lines.Add(new string(' ', depth * 2) + item.GetText(PdfName.Title) + " -> " + target);
                visible++;
                int below = Walk(item, depth + 1);
                if (below > 0)
                {
                    int count = item.GetAsNumber(PdfName.Count)!.IntValue();
                    Assert.Equal(below, Math.Abs(count));
                    if (count > 0) visible += below;
                }
                previous = item;
            }
            Assert.Same(previous, parent.GetAsDictionary(PdfName.Of("Last")));
            return visible;
        }

        int total = Walk(root, 0);
        Assert.Equal(total, root.GetAsNumber(PdfName.Count)!.IntValue());
        return lines;
    }
}
