namespace PdfEditor.Core.Pdf;

/// <summary>
/// A page of a <see cref="PdfDocument"/>: its dictionary, plus the attributes it inherits from
/// the page tree and helpers for its content streams and annotations.
/// </summary>
internal sealed class PdfPage
{
    private static readonly PdfName[] Inheritable =
        { PdfName.Resources, PdfName.MediaBox, PdfName.CropBox, PdfName.Rotate };

    /// <summary>US Letter, the size viewers assume for a page that declares none.</summary>
    private static readonly PdfRect DefaultMediaBox = new(0, 0, 612, 792);

    public PdfPage(PdfDocument document, PdfDictionary dictionary, int number)
    {
        Document = document;
        Dictionary = dictionary;
        Number = number;
    }

    public PdfDocument Document { get; }
    public PdfDictionary Dictionary { get; }

    /// <summary>1-based position in the document at the time the page list was read.</summary>
    public int Number { get; }

    /// <summary>An attribute from this page or, failing that, the nearest ancestor that has it.</summary>
    public PdfObject? GetInherited(PdfName key) => GetInherited(Dictionary, key);

    internal static PdfObject? GetInherited(PdfDictionary page, PdfName key)
    {
        var node = page;
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        for (int depth = 0; node != null && depth < 64 && seen.Add(node); depth++)
        {
            if (node.Get(key) is { } value) return value;
            node = node.GetAsDictionary(PdfName.Parent);
        }
        return null;
    }

    /// <summary>Copies inherited attributes into the page itself, so it survives being re-parented.</summary>
    internal static void PushDownInheritedAttributes(PdfDictionary page)
    {
        foreach (var key in Inheritable)
        {
            if (page.ContainsKey(key)) continue;
            var parent = page.GetAsDictionary(PdfName.Parent);
            if (parent == null) continue;
            var value = GetInherited(parent, key);
            if (value != null) page.Put(key, value.IsIndirect ? value : value);
        }
    }

    public PdfRect MediaBox => PdfRect.FromArray(GetInherited(PdfName.MediaBox) as PdfArray) ?? DefaultMediaBox;

    public PdfRect CropBox => PdfRect.FromArray(GetInherited(PdfName.CropBox) as PdfArray) ?? MediaBox;

    /// <summary>The page rotation, normalised to 0, 90, 180 or 270.</summary>
    public int Rotation
    {
        get
        {
            int raw = (GetInherited(PdfName.Rotate) as PdfNumber)?.IntValue() ?? 0;
            int r = ((raw % 360) + 360) % 360;
            return r - r % 90;
        }
        set => Dictionary.Put(PdfName.Rotate, new PdfNumber(((value % 360) + 360) % 360));
    }

    /// <summary>The page's resources (possibly inherited); null when it has none.</summary>
    public PdfDictionary? Resources => GetInherited(PdfName.Resources) as PdfDictionary;

    /// <summary>
    /// The page's resource dictionary, made the page's own: an inherited one is copied onto the
    /// page first, so adding to it cannot leak a resource into every sibling page.
    /// </summary>
    public PdfDictionary GetOrCreateResources()
    {
        if (Dictionary.Get(PdfName.Resources) is PdfDictionary own) return own;
        var copy = new PdfDictionary();
        if (Resources is { } inherited)
            foreach (var key in inherited.Keys) copy.Put(key, inherited.GetRaw(key));
        Dictionary.Put(PdfName.Resources, copy);
        return copy;
    }

    /// <summary>The page's content streams, in drawing order.</summary>
    public IReadOnlyList<PdfStream> ContentStreams => Dictionary.Get(PdfName.Contents) switch
    {
        PdfStream single => new[] { single },
        PdfArray array => array.OfType<PdfStream>().ToArray(),
        _ => Array.Empty<PdfStream>(),
    };

    /// <summary>
    /// The page's content, decoded and concatenated. Streams are joined with a newline, since the
    /// specification treats a content array as one stream split at token boundaries.
    /// </summary>
    public byte[] GetContentBytes()
    {
        var streams = ContentStreams;
        if (streams.Count == 1) return streams[0].GetDecodedBytes();
        using var output = new MemoryStream();
        foreach (var stream in streams)
        {
            output.Write(stream.GetDecodedBytes());
            output.WriteByte((byte)'\n');
        }
        return output.ToArray();
    }

    /// <summary>Replaces the page's content with one stream holding <paramref name="content"/>.</summary>
    public void SetContent(byte[] content)
    {
        var stream = Document.MakeIndirect(new PdfStream(content));
        Dictionary.Put(PdfName.Contents, stream);
    }

    /// <summary>Adds a content stream before every existing one.</summary>
    public void PrependContent(byte[] content) => InsertContent(content, atStart: true);

    /// <summary>Adds a content stream after every existing one.</summary>
    public void AppendContent(byte[] content) => InsertContent(content, atStart: false);

    private void InsertContent(byte[] content, bool atStart)
    {
        var stream = Document.MakeIndirect(new PdfStream(content));
        var array = new PdfArray();
        switch (Dictionary.GetRaw(PdfName.Contents))
        {
            case PdfReference r when r.Resolve() is PdfArray existing:
                foreach (var item in existing.RawItems) array.Add(item);
                break;
            case PdfArray existing:
                foreach (var item in existing.RawItems) array.Add(item);
                break;
            case PdfObject single when PdfReference.Deref(single) is PdfStream:
                array.Add(single);
                break;
        }
        if (atStart) array.Insert(0, stream);
        else array.Add(stream);
        Dictionary.Put(PdfName.Contents, array);
    }

    /// <summary>The page's annotation dictionaries.</summary>
    public IReadOnlyList<PdfDictionary> Annotations =>
        Dictionary.GetAsArray(PdfName.Annots)?.OfType<PdfDictionary>().ToList() ?? new List<PdfDictionary>();

    /// <summary>Adds <paramref name="annotation"/> to the page (as an indirect object, with /P set).</summary>
    public void AddAnnotation(PdfDictionary annotation)
    {
        Document.MakeIndirect(annotation);
        annotation.Put(PdfName.P, Dictionary);
        var annots = Dictionary.GetAsArray(PdfName.Annots);
        if (annots == null)
        {
            annots = new PdfArray();
            Dictionary.Put(PdfName.Annots, annots);
        }
        annots.Add(annotation);
    }

    /// <summary>Removes <paramref name="annotation"/> from the page's /Annots.</summary>
    public void RemoveAnnotation(PdfDictionary annotation)
    {
        var annots = Dictionary.GetAsArray(PdfName.Annots);
        if (annots == null) return;
        annots.RemoveAll(annotation);
        if (annots.Count == 0) Dictionary.Remove(PdfName.Annots);
    }
}
