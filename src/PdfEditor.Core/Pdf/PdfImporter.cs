namespace PdfEditor.Core.Pdf;

/// <summary>
/// Copies pages, with everything they draw, from one document into another — the basis of
/// merging and of re-arranging pages.
/// <para>
/// The copy is bounded by the pages asked for. Any reference that leads to a page of the source
/// that was <em>not</em> requested (a link destination, a widget's field tree, an annotation's
/// /P) becomes null rather than dragging that page across — which matters for a privacy tool:
/// "delete page 3" must not leave page 3 reachable, and therefore written, through a bookmark.
/// </para>
/// </summary>
internal sealed class PdfImporter
{
    private readonly PdfDocument _target;
    private readonly Dictionary<PdfObject, PdfObject> _copies = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<PdfObject> _sourcePages = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PdfObject, PdfDictionary> _pageCopies = new(ReferenceEqualityComparer.Instance);
    private readonly Queue<(PdfObject Source, PdfObject Copy)> _pending = new();

    private PdfImporter(PdfDocument target) => _target = target;

    /// <summary>
    /// Copies <paramref name="pageNumbers"/> (1-based; repeats allowed) of <paramref name="source"/>
    /// into <paramref name="target"/>, returning the new page dictionaries in order. They are not
    /// yet in the target's page tree — the caller places them.
    /// </summary>
    public static List<PdfDictionary> CopyPages(PdfDocument source, IReadOnlyList<int> pageNumbers, PdfDocument target)
        => CopyPages(source, pageNumbers, target, outlines: null);

    /// <summary>
    /// As <see cref="CopyPages(PdfDocument, IReadOnlyList{int}, PdfDocument)"/>, also copying the
    /// source's bookmarks into <paramref name="outlines"/> (the top-level items, unlinked; see
    /// <see cref="LinkOutlines"/>). Only bookmarks that lead to a copied page are kept, plus the
    /// parents needed to reach them — a bookmark to a dropped page would otherwise be a dead entry
    /// naming exactly what was removed.
    /// </summary>
    public static List<PdfDictionary> CopyPages(PdfDocument source, IReadOnlyList<int> pageNumbers,
        PdfDocument target, List<OutlineItem>? outlines)
    {
        var importer = new PdfImporter(target);
        foreach (var page in source.Pages) importer._sourcePages.Add(page.Dictionary);

        var result = new List<PdfDictionary>();
        foreach (int number in pageNumbers)
        {
            var sourcePage = source.GetPage(number);
            var copy = importer.CopyPage(sourcePage);
            result.Add(copy);
        }
        if (outlines != null)
            outlines.AddRange(importer.CopyOutlines(source));
        importer.Drain();
        return result;
    }

    // ------------------------------------------------------------------ outlines

    /// <summary>A copied bookmark, before it is linked into the target's outline tree.</summary>
    internal sealed record OutlineItem(PdfDictionary Dictionary, bool Open, List<OutlineItem> Children);

    /// <summary>Bound on bookmarks copied per document, against a hostile or cyclic tree.</summary>
    private const int MaxOutlineItems = 100_000;

    /// <summary>Bound on bookmark nesting, so a pathological tree cannot recurse the stack away.</summary>
    private const int MaxOutlineDepth = 64;

    private List<OutlineItem> CopyOutlines(PdfDocument source)
    {
        var first = source.Catalog?.GetAsDictionary(PdfName.Outlines)?.GetAsDictionary(PdfName.First);
        var visited = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
        var named = new Lazy<Dictionary<string, PdfObject>>(() => NamedDestinations(source));
        return CopyOutlineSiblings(first, visited, named, 0);
    }

    private List<OutlineItem> CopyOutlineSiblings(PdfDictionary? item, HashSet<PdfObject> visited,
        Lazy<Dictionary<string, PdfObject>> named, int depth)
    {
        var result = new List<OutlineItem>();
        for (; item != null && depth < MaxOutlineDepth && visited.Count < MaxOutlineItems && visited.Add(item);
             item = item.GetAsDictionary(PdfName.Next))
        {
            var children = CopyOutlineSiblings(item.GetAsDictionary(PdfName.First), visited, named, depth + 1);
            var destination = CopiedDestination(item, named);
            if (destination == null && children.Count == 0) continue;

            var copy = new PdfDictionary();
            _target.MakeIndirect(copy);
            copy.Put(PdfName.Title, item.Get(PdfName.Title) is PdfString title
                ? new PdfString((byte[])title.Bytes.Clone(), title.IsHex) : PdfString.FromText(""));
            if (destination != null) copy.Put(PdfName.Dest, destination);
            foreach (var key in new[] { PdfName.Of("C"), PdfName.F })
                if (item.Get(key) is PdfArray or PdfNumber) copy.Put(key, CopyDirect(item.Get(key)!, 0));
            bool open = (item.GetAsNumber(PdfName.Count)?.Value ?? 0) >= 0;
            result.Add(new OutlineItem(copy, open, children));
        }
        return result;
    }

    /// <summary>
    /// The bookmark's destination as an explicit array on a copied page, or null when it leads
    /// anywhere else: a page that was not copied, another file, a script. Named destinations are
    /// resolved here, so the copy does not need the source's name tree.
    /// </summary>
    private PdfArray? CopiedDestination(PdfDictionary item, Lazy<Dictionary<string, PdfObject>> named)
    {
        PdfObject? dest = item.Get(PdfName.Dest);
        if (dest == null && item.GetAsDictionary(PdfName.A) is { } action
            && PdfName.GoTo.Equals(action.GetAsName(PdfName.S)))
            dest = action.Get(PdfName.Of("D"));

        for (int hops = 0; hops < 4 && dest is PdfName or PdfString or PdfDictionary; hops++)
        {
            dest = dest switch
            {
                PdfName n => named.Value.GetValueOrDefault("/" + n.Value),
                PdfString str => named.Value.GetValueOrDefault(str.ToUnicodeString()),
                PdfDictionary d => d.Get(PdfName.Of("D")),
                _ => null,
            };
        }

        if (dest is not PdfArray array || array.Count == 0) return null;
        var page = array.Get(0);
        if (page == null || !_pageCopies.TryGetValue(page, out var pageCopy)) return null;
        var copy = new PdfArray { pageCopy };
        for (int i = 1; i < array.Count; i++)
            copy.Add(array.Get(i) is PdfName or PdfNumber or PdfNull ? CopyDirect(array.Get(i)!, 0) : PdfNull.Instance);
        return copy;
    }

    /// <summary>
    /// The document's named destinations: the PDF 1.2+ <c>/Names /Dests</c> tree (keyed by
    /// string) and the PDF 1.1 <c>/Dests</c> dictionary (keyed by name, prefixed with "/" so the
    /// two cannot collide).
    /// </summary>
    private static Dictionary<string, PdfObject> NamedDestinations(PdfDocument source)
    {
        var result = new Dictionary<string, PdfObject>(StringComparer.Ordinal);
        var catalog = source.Catalog;
        if (catalog?.GetAsDictionary(PdfName.Names)?.GetAsDictionary(PdfName.Of("Dests")) is { } tree)
            foreach (var (key, value) in PdfNameTree.Read(tree))
                result.TryAdd(key.ToUnicodeString(), value);
        if (catalog?.GetAsDictionary(PdfName.Of("Dests")) is { } legacy)
            foreach (var key in legacy.Keys)
                if (legacy.Get(key) is { } value) result.TryAdd("/" + key.Value, value);
        return result;
    }

    /// <summary>
    /// Links copied bookmarks into <paramref name="target"/>'s outline tree, replacing any it had.
    /// Does nothing when there are none, so a document without bookmarks gains no empty tree.
    /// </summary>
    public static void LinkOutlines(PdfDocument target, IReadOnlyList<OutlineItem> items)
    {
        if (items.Count == 0) return;
        var root = new PdfDictionary();
        root.Put(PdfName.Type, PdfName.Outlines);
        target.MakeIndirect(root);
        root.Put(PdfName.Count, new PdfNumber(Link(root, items)));
        target.Catalog!.Put(PdfName.Outlines, root);
    }

    /// <summary>Links <paramref name="items"/> under <paramref name="parent"/>; returns how many are visible.</summary>
    private static int Link(PdfDictionary parent, IReadOnlyList<OutlineItem> items)
    {
        int visible = 0;
        for (int i = 0; i < items.Count; i++)
        {
            var dict = items[i].Dictionary;
            dict.Put(PdfName.Parent, parent);
            if (i > 0) dict.Put(PdfName.Prev, items[i - 1].Dictionary);
            if (i + 1 < items.Count) dict.Put(PdfName.Next, items[i + 1].Dictionary);
            visible++;
            if (items[i].Children.Count == 0) continue;
            int below = Link(dict, items[i].Children);
            dict.Put(PdfName.Count, new PdfNumber(items[i].Open ? below : -below));
            if (items[i].Open) visible += below;
        }
        parent.Put(PdfName.First, items[0].Dictionary);
        parent.Put(PdfName.Of("Last"), items[^1].Dictionary);
        return visible;
    }

    private PdfDictionary CopyPage(PdfPage page)
    {
        var src = page.Dictionary;
        var copy = new PdfDictionary();
        _target.MakeIndirect(copy);
        // The first copy of a page is what references to it (a link on another copied page)
        // resolve to; a duplicate of the same page is a separate page with its own annotations.
        _pageCopies.TryAdd(src, copy);

        foreach (var key in src.Keys)
        {
            if (key.Equals(PdfName.Parent) || key.Equals(PdfName.Of("B")) || key.Equals(PdfName.Annots)) continue;
            copy.Put(key, Copy(src.GetRaw(key)!));
        }
        // Attributes the page inherited from its tree have to travel with it.
        foreach (var key in new[] { PdfName.Resources, PdfName.MediaBox, PdfName.CropBox, PdfName.Rotate })
            if (!copy.ContainsKey(key) && page.GetInherited(key) is { } inherited)
                copy.Put(key, Copy(inherited));

        if (src.GetAsArray(PdfName.Annots) is { } annots)
        {
            var copiedAnnots = new PdfArray();
            foreach (var annot in annots)
            {
                if (annot is not PdfDictionary a) continue;
                // Annotations belong to exactly one page, so each page copy gets its own.
                var annotCopy = new PdfDictionary();
                _target.MakeIndirect(annotCopy);
                foreach (var key in a.Keys)
                {
                    if (key.Equals(PdfName.P)) continue;
                    // A widget's /Parent is its form field, whose tree reaches the rest of the
                    // form; the widget keeps its own appearance and stops being a field here.
                    if (key.Equals(PdfName.Parent) && a.Is(PdfName.Widget, PdfName.Subtype)) continue;
                    if (key.Equals(PdfName.Popup)) continue;
                    annotCopy.Put(key, Copy(a.GetRaw(key)!));
                }
                annotCopy.Put(PdfName.P, copy);
                copiedAnnots.Add(annotCopy);
            }
            if (copiedAnnots.Count > 0) copy.Put(PdfName.Annots, copiedAnnots);
        }
        return copy;
    }

    /// <summary>
    /// Copies one value. Direct containers are copied in place (their depth is bounded by the
    /// parser); indirect objects become placeholders filled in later from a queue, so a long
    /// chain of references (an outline's /Next list) cannot recurse the stack away.
    /// </summary>
    private PdfObject Copy(PdfObject value, int depth = 0)
    {
        var source = PdfReference.Deref(value);
        if (source.IsShared) return source;
        if (_sourcePages.Contains(source))
            return _pageCopies.TryGetValue(source, out var mapped) ? mapped : PdfNull.Instance;
        if (_copies.TryGetValue(source, out var existing)) return existing;

        if (source.IsIndirect || value is PdfReference)
        {
            PdfObject placeholder = source switch
            {
                PdfStream s => PdfStream.FromFile(new PdfDictionary(), s.RawData),
                PdfDictionary => new PdfDictionary(),
                PdfArray => new PdfArray(),
                _ => CopyDirect(source, depth),
            };
            _copies[source] = placeholder;
            _target.MakeIndirect(placeholder);
            if (placeholder is PdfDictionary or PdfArray) _pending.Enqueue((source, placeholder));
            return placeholder;
        }
        return CopyDirect(source, depth);
    }

    private PdfObject CopyDirect(PdfObject source, int depth)
    {
        if (depth > PdfObjectParser.MaxNesting) return PdfNull.Instance;
        switch (source)
        {
            case PdfStream s:
                var stream = PdfStream.FromFile(new PdfDictionary(), s.RawData);
                Fill(s, stream, depth);
                return stream;
            case PdfDictionary d:
                var dict = new PdfDictionary();
                Fill(d, dict, depth);
                return dict;
            case PdfArray a:
                var array = new PdfArray();
                Fill(a, array, depth);
                return array;
            case PdfNumber n:
                return new PdfNumber(n.Value, n.IsInteger);
            case PdfString str:
                return new PdfString((byte[])str.Bytes.Clone(), str.IsHex);
            default:
                return source;
        }
    }

    private void Fill(PdfObject source, PdfObject copy, int depth)
    {
        if (source is PdfDictionary sd && copy is PdfDictionary cd)
        {
            foreach (var key in sd.Keys) cd.Put(key, Copy(sd.GetRaw(key)!, depth + 1));
        }
        else if (source is PdfArray sa && copy is PdfArray ca)
        {
            for (int i = 0; i < sa.Count; i++) ca.Add(Copy(sa.GetRaw(i), depth + 1));
        }
    }

    private void Drain()
    {
        while (_pending.Count > 0)
        {
            var (source, copy) = _pending.Dequeue();
            Fill(source, copy, 0);
        }
    }
}
