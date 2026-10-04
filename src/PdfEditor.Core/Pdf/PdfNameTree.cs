namespace PdfEditor.Core.Pdf;

/// <summary>
/// A name tree (§7.9.6): string keys to values, spread over /Names leaves and /Kids nodes.
/// Reading walks the whole tree (cycle-guarded); writing replaces it with a single sorted leaf,
/// which is valid at any size this editor produces.
/// </summary>
internal static class PdfNameTree
{
    /// <summary>Every (key, value) pair in the tree, in tree order.</summary>
    public static List<(PdfString Key, PdfObject Value)> Read(PdfDictionary? root)
    {
        var entries = new List<(PdfString, PdfObject)>();
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        Walk(root, 0);
        return entries;

        void Walk(PdfDictionary? node, int depth)
        {
            if (node == null || depth > 64 || !seen.Add(node)) return;
            if (node.GetAsArray(PdfName.Names) is { } names)
                for (int i = 0; i + 1 < names.Count; i += 2)
                    if (names.Get(i) is PdfString key) entries.Add((key, names.Get(i + 1)!));
            if (node.GetAsArray(PdfName.Kids) is { } kids)
                foreach (var kid in kids)
                    if (kid is PdfDictionary k) Walk(k, depth + 1);
        }
    }

    /// <summary>Writes <paramref name="entries"/> as the tree under catalog /Names /<paramref name="tree"/>.</summary>
    public static void Write(PdfDictionary catalog, PdfName tree, IEnumerable<(PdfString Key, PdfObject Value)> entries)
    {
        var names = catalog.GetAsDictionary(PdfName.Names);
        if (names == null)
        {
            names = new PdfDictionary();
            catalog.Put(PdfName.Names, names);
        }
        var array = new PdfArray();
        foreach (var (key, value) in entries.OrderBy(e => e.Key.Bytes, ByteOrder.Instance))
        {
            array.Add(key);
            array.Add(value);
        }
        var node = new PdfDictionary();
        node.Put(PdfName.Names, array);
        names.Put(tree, node);
    }

    private sealed class ByteOrder : IComparer<byte[]>
    {
        public static readonly ByteOrder Instance = new();
        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y.AsSpan());
    }
}
