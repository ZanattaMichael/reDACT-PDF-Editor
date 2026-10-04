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
        importer.Drain();
        return result;
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
