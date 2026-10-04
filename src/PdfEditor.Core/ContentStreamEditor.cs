using PdfEditor.Core.Pdf;

namespace PdfEditor.Core;

/// <summary>
/// Which kinds of content an edit is allowed to take out of a region.
/// </summary>
internal enum ContentKinds
{
    /// <summary>Text and images alike — what redaction means: nothing in the region survives.</summary>
    All,

    /// <summary>
    /// Text only; images in the region are left exactly as they are. Editing ordinary text needs
    /// this: the region under a word usually overlaps whatever sits behind it, and scrubbing that
    /// too punches a rectangle through a letterhead or watermark.
    /// </summary>
    TextOnly,

    /// <summary>
    /// Text, plus the region erased from any image beneath it and filled with the surrounding paper
    /// colour. This is for editing a <em>searchable scan</em>, where the words on screen are pixels
    /// in the page image and the only real text is an invisible OCR layer over them. Removing just
    /// that layer leaves the old words visibly in place with the replacement stamped on top, so the
    /// pixels have to go too — but painted out in paper, not the black redaction uses.
    /// </summary>
    TextAndPixelsBeneath,
}

/// <summary>
/// Rewrites a page's content stream, dropping every piece of content that falls inside
/// one of the supplied regions. Text is removed at per-glyph granularity (dropped glyphs
/// are replaced by equivalent-width TJ displacements so surrounding text does not shift),
/// image XObjects are dropped or pixel-scrubbed, and form XObjects are recursively edited
/// on a cloned copy. This achieves true removal: the data is gone from the file, not
/// merely covered.
/// <para>
/// The glyph geometry comes from <see cref="ContentProcessor"/>, the same interpreter text search
/// and extraction use — so a region drawn over a search hit removes exactly the glyphs the search
/// reported, measured the same way.
/// </para>
/// </summary>
internal sealed class ContentStreamEditor : ContentProcessor
{
    private const float Epsilon = 0.05f;

    private readonly IList<PdfRect> _regions;
    private readonly PdfDocument _document;
    private readonly List<string> _warnings;
    private readonly CollectingListener _collector;
    private readonly int _depth;
    private readonly ContentKinds _kinds;

    private MemoryStream _out = new();
    private PdfDictionary _editResources = new();

    public bool RemovedAnything { get; private set; }

    private ContentStreamEditor(CollectingListener collector, IList<PdfRect> regions,
        PdfDocument document, List<string> warnings, int depth, ContentKinds kinds) : base(collector)
    {
        _collector = collector;
        _regions = regions;
        _document = document;
        _warnings = warnings;
        _depth = depth;
        _kinds = kinds;
    }

    public static ContentStreamEditor Create(IList<PdfRect> regions, PdfDocument document,
        List<string> warnings, int depth = 0, ContentKinds kinds = ContentKinds.All)
        => new(new CollectingListener(), regions, document, warnings, depth, kinds);

    /// <summary>Rewrites the content of <paramref name="page"/> in place.</summary>
    public void EditPage(PdfPage page)
    {
        PdfStructureGuard.EnsureFormXObjectsTerminate(page);
        byte[] content = PdfIo.Guarded("decoding the page content stream", page.GetContentBytes);
        _editResources = page.GetOrCreateResources();
        _out = new MemoryStream();
        PdfIo.Guarded("rewriting the page content stream", () => ProcessContent(content, _editResources));
        page.SetContent(_out.ToArray());
    }

    /// <summary>Rewrites the raw content of a form XObject stream in place.</summary>
    public void EditFormStream(PdfStream formStream, PdfDictionary resources)
    {
        byte[] content = PdfIo.Guarded("decoding a form XObject stream", formStream.GetDecodedBytes);
        _editResources = resources;
        _out = new MemoryStream();
        PdfIo.Guarded("rewriting a form XObject stream", () => ProcessContent(content, resources));
        formStream.SetData(_out.ToArray());
    }

    protected override void Invoke(ContentOperation op)
    {
        switch (op.Operator)
        {
            case "Tj":
            case "TJ":
            case "'":
            case "\"":
                HandleShowText(op);
                break;
            case "Do":
                HandleDo(op);
                break;
            case "BI":
                HandleInlineImage(op);
                break;
            default:
                Execute(op);
                Write(op);
                break;
        }
    }

    private void Write(ContentOperation op) => ContentWriter.WriteOperation(_out, op);

    // ---------------------------------------------------------------- text

    private void HandleShowText(ContentOperation op)
    {
        _collector.Texts.Clear();
        Execute(op);
        var shown = _collector.Texts.ToList();

        if (!shown.Any(s => IntersectsAnyRegion(s.BoundingBox)))
        {
            Write(op);
            return;
        }
        RemovedAnything = true;

        // Re-emit the side effects of ' and " (line advance, word/char spacing), then
        // re-emit the show-text call in TJ form with hit glyphs replaced by
        // equivalent-width displacements.
        var operands = op.Operands;
        var builder = new ContentBuilder();
        switch (op.Operator)
        {
            case "'":
                builder.Raw("T*\n");
                break;
            case "\"" when operands.Count >= 3:
                Write(new ContentOperation("Tw", new List<PdfObject> { operands[^3] }));
                Write(new ContentOperation("Tc", new List<PdfObject> { operands[^2] }));
                builder.Raw("T*\n");
                break;
        }
        _out.Write(builder.ToArray());

        var sourceItems = new List<PdfObject>();
        if (op.Operator == "TJ" && operands.Count > 0 && operands[^1] is PdfArray arr)
            sourceItems.AddRange(arr);
        else if (operands.Count > 0)
            sourceItems.Add(operands[^1]);

        int stringCount = sourceItems.Count(o => o is PdfString);
        var replacement = new PdfArray();
        if (stringCount == shown.Count)
        {
            int textIdx = 0;
            foreach (var item in sourceItems)
            {
                if (item is not PdfString str)
                {
                    replacement.Add(item);
                    continue;
                }
                var info = shown[textIdx++];
                if (!IntersectsAnyRegion(info.BoundingBox))
                {
                    replacement.Add(item);
                    continue;
                }
                // Per-glyph split: glyphs outside the region survive, glyphs inside are
                // replaced by an equivalent-width displacement so the line does not shift.
                foreach (var glyph in info.Glyphs)
                {
                    if (IntersectsAnyRegion(glyph.BoundingBox))
                        replacement.Add(new PdfNumber(DisplacementFor(glyph.UnscaledWidth, info)));
                    else
                        replacement.Add(new PdfString(glyph.Bytes, str.IsHex));
                }
            }
        }
        else
        {
            // Event/string count mismatch: drop the whole operator, preserving the total advance
            // so later text does not shift.
            replacement.Add(new PdfNumber(shown.Sum(s => DisplacementFor(s.UnscaledWidth, s))));
        }

        Write(new ContentOperation("TJ", new List<PdfObject> { replacement }));
    }

    private static double DisplacementFor(double unscaledWidth, TextRenderInfo info)
    {
        // A number n inside TJ translates the text position by -n/1000 * fontSize * hScale.
        double fs = info.FontSize;
        double th = info.HorizontalScaling;
        if (th <= 0) th = 1;
        if (fs == 0) return 0;
        return -unscaledWidth * 1000 / (fs * th);
    }

    // ------------------------------------------------------------- XObjects

    private void HandleDo(ContentOperation op)
    {
        var name = op.Operands.Count > 0 ? op.Operands[^1] as PdfName : null;
        var stream = name == null ? null : Resources?.GetAsDictionary(PdfName.XObject)?.GetAsStream(name);
        var subtype = stream?.GetAsName(PdfName.Subtype);

        if (stream == null || name == null)
        {
            Write(op);
            return;
        }

        if (PdfName.Image.Equals(subtype))
        {
            var bbox = State.Ctm.TransformRect(0, 0, 1, 1);
            if (IntersectsAnyRegion(bbox) && _kinds != ContentKinds.TextOnly)
            {
                RemovedAnything = true;
                // Erasing for an edit never drops the image: the region is a few words on a scanned
                // page, and losing the whole page image to replace one of them is not a trade any
                // user would make. Redaction still drops it, because leaving it is a disclosure.
                if (ContainedInAnyRegion(bbox) && _kinds != ContentKinds.TextAndPixelsBeneath)
                    return; // fully covered: drop the draw call entirely
                var fill = _kinds == ContentKinds.TextAndPixelsBeneath
                    ? ScrubFill.SurroundingPaper : ScrubFill.Black;
                if (ImageScrubber.TryScrubPixels(stream, bbox, _regions, out var scrubFailure, fill, Resources))
                {
                    Write(op);
                    return;
                }
                _warnings.Add($"Image '{name.Value}' partially overlaps a redaction region " +
                              $"and could not be pixel-scrubbed ({scrubFailure}); it was removed " +
                              "entirely.");
                return;
            }
            Write(op);
            return;
        }

        if (PdfName.Form.Equals(subtype))
        {
            // Never let the interpreter recurse into the form: it would replay the form's content
            // through this editor and inline it into the page. Handle it on a copy instead.
            var full = Matrix.FromArray(stream.GetAsArray(PdfName.Matrix)).Multiply(State.Ctm);
            var formBBox = PdfRect.FromArray(stream.GetAsArray(PdfName.BBox)) is { } box ? full.TransformRect(box) : (PdfRect?)null;
            if (formBBox is { } fb && IntersectsAnyRegion(fb))
            {
                if (_depth >= 6)
                {
                    _warnings.Add("Form XObject nesting too deep; dropping the whole form inside the region.");
                    RemovedAnything = true;
                    return;
                }
                var cloned = PdfStream.FromFile(stream, stream.RawData);
                _document.MakeIndirect(cloned);
                var formResources = _editResources;
                if (cloned.GetAsDictionary(PdfName.Resources) is { } own)
                {
                    // The clone gets its own resource dictionaries: editing it registers new
                    // XObjects there, and those must not appear in the original form, which other
                    // pages may still draw.
                    formResources = ShallowCopy(own);
                    if (own.GetAsDictionary(PdfName.XObject) is { } xobjects)
                        formResources.Put(PdfName.XObject, ShallowCopy(xobjects));
                    cloned.Put(PdfName.Resources, formResources);
                }
                var inner = Create(TransformRegionsInto(full), _document, _warnings, _depth + 1, _kinds);
                inner.EditFormStream(cloned, formResources);
                RemovedAnything |= inner.RemovedAnything;
                var newName = PdfResources.Add(_editResources, PdfName.XObject, "Fm", cloned);
                Write(new ContentOperation("Do", new List<PdfObject> { newName }));
                return;
            }
            Write(op);
            return;
        }

        Write(op);
    }

    private void HandleInlineImage(ContentOperation op)
    {
        var bbox = State.Ctm.TransformRect(0, 0, 1, 1);
        if (IntersectsAnyRegion(bbox) && _kinds != ContentKinds.TextOnly)
        {
            RemovedAnything = true;
            return; // drop inline image touching a region (safe over-redaction)
        }
        Write(op);
    }

    // ------------------------------------------------------------- plumbing

    private static PdfDictionary ShallowCopy(PdfDictionary source)
    {
        var copy = new PdfDictionary();
        foreach (var key in source.Keys) copy.Put(key, source.GetRaw(key));
        return copy;
    }

    private bool IntersectsAnyRegion(PdfRect r) => _regions.Any(reg => Overlaps(reg, r));

    private bool ContainedInAnyRegion(PdfRect r) => _regions.Any(reg =>
        reg.Left <= r.Left + Epsilon && reg.Right >= r.Right - Epsilon &&
        reg.Bottom <= r.Bottom + Epsilon && reg.Top >= r.Top - Epsilon);

    private static bool Overlaps(PdfRect a, PdfRect b) =>
        a.Left < b.Right - Epsilon && b.Left < a.Right - Epsilon &&
        a.Bottom < b.Top - Epsilon && b.Bottom < a.Top - Epsilon;

    /// <summary>Maps the page-space regions into the coordinate space of a form XObject.</summary>
    private List<PdfRect> TransformRegionsInto(Matrix formToUser)
    {
        if (formToUser.Inverse() is not { } inverse) return new List<PdfRect>();
        return _regions.Select(r => inverse.TransformRect(r)).ToList();
    }

    private sealed class CollectingListener : IContentListener
    {
        public List<TextRenderInfo> Texts { get; } = new();
        public void OnText(TextRenderInfo info) => Texts.Add(info);
    }
}
