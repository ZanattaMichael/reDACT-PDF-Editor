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
    private static readonly PdfName Type3 = PdfName.Of("Type3");

    private readonly IList<PdfRect> _regions;
    private readonly PdfDocument _document;
    private readonly List<string> _warnings;
    private readonly CollectingListener _collector;
    private readonly int _depth;
    private readonly ContentKinds _kinds;

    private MemoryStream _out = new();
    private PdfDictionary _editResources = new();

    // The /XObject dictionary the rewritten stream draws from. It starts as a copy of the edited
    // resources' own and replaces it only once the stream is done, and only if the edit changed it:
    // the original may be shared with pages that still draw everything in it.
    private PdfDictionary _xobjects = new();
    private bool _xobjectsChanged;

    // XObject names whose draw this edit dropped or redirected to an edited clone, and every name the
    // rewritten stream still draws. The difference is what gets pruned from the edited resources.
    private readonly HashSet<PdfName> _retired = new();
    private readonly HashSet<PdfName> _drawn = new();

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
        var resources = page.GetOrCreateResources();
        Begin(resources);
        PdfIo.Guarded("rewriting the page content stream", () => ProcessContent(content, resources));
        page.SetContent(_out.ToArray());
        if (!PruneXObjects()) return;
        // An indirect resource dictionary may be shared with pages that still draw what this one
        // stopped drawing, so the page gets a copy of its own.
        if (resources.IsIndirect)
        {
            resources = ShallowCopy(resources);
            page.Dictionary.Put(PdfName.Resources, resources);
        }
        resources.Put(PdfName.XObject, _xobjects);
    }

    /// <summary>
    /// Rewrites the raw content of a form XObject stream in place. <paramref name="resources"/> must
    /// belong to <paramref name="formStream"/> alone: what the rewrite stops drawing is pruned from it.
    /// </summary>
    public void EditFormStream(PdfStream formStream, PdfDictionary resources)
    {
        byte[] content = PdfIo.Guarded("decoding a form XObject stream", formStream.GetDecodedBytes);
        Begin(resources);
        PdfIo.Guarded("rewriting a form XObject stream", () => ProcessContent(content, resources));
        formStream.SetData(_out.ToArray());
        if (PruneXObjects()) resources.Put(PdfName.XObject, _xobjects);
    }

    private void Begin(PdfDictionary resources)
    {
        _editResources = resources;
        _xobjects = ShallowCopy(resources.GetAsDictionary(PdfName.XObject) ?? new PdfDictionary());
        _out = new MemoryStream();
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
        WriteQuoteSideEffects(op);
        var replacement = ReplacementFor(ShownItems(op), shown);
        Write(new ContentOperation("TJ", new List<PdfObject> { replacement }));
    }

    private void WriteQuoteSideEffects(ContentOperation op)
    {
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
    }

    /// <summary>The strings and kerning numbers a show-text operator shows, in order.</summary>
    private static List<PdfObject> ShownItems(ContentOperation op)
    {
        var operands = op.Operands;
        var items = new List<PdfObject>();
        if (op.Operator == "TJ" && operands.Count > 0 && operands[^1] is PdfArray arr)
            items.AddRange(arr);
        else if (operands.Count > 0)
            items.Add(operands[^1]);
        return items;
    }

    private PdfArray ReplacementFor(List<PdfObject> sourceItems, List<TextRenderInfo> shown)
    {
        var replacement = new PdfArray();
        if (sourceItems.Count(o => o is PdfString) != shown.Count)
        {
            // Event/string count mismatch: drop the whole operator, preserving the total advance
            // so later text does not shift.
            replacement.Add(new PdfNumber(shown.Sum(s => DisplacementFor(s.UnscaledWidth, s))));
            return replacement;
        }

        int textIdx = 0;
        foreach (var item in sourceItems)
        {
            if (item is not PdfString str)
            {
                replacement.Add(item);
                continue;
            }
            var info = shown[textIdx++];
            if (IntersectsAnyRegion(info.BoundingBox))
                AddGlyphsSplitAtRegions(replacement, str, info);
            else
                replacement.Add(item);
        }
        return replacement;
    }

    // Per-glyph split: glyphs outside the region survive, glyphs inside are replaced by an
    // equivalent-width displacement so the line does not shift.
    private void AddGlyphsSplitAtRegions(PdfArray replacement, PdfString str, TextRenderInfo info)
    {
        foreach (var glyph in info.Glyphs)
        {
            if (IntersectsAnyRegion(glyph.BoundingBox))
                replacement.Add(new PdfNumber(DisplacementFor(glyph.UnscaledWidth, info)));
            else
                replacement.Add(new PdfString(glyph.Bytes, str.IsHex));
        }
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

        if (name == null)
            Write(op);
        else if (stream == null)
            WriteDo(op, name);
        else if (PdfName.Image.Equals(subtype))
            HandleImageDo(op, name, stream);
        else if (PdfName.Form.Equals(subtype))
            HandleFormDo(op, name, stream);
        else
            WriteDo(op, name);
    }

    private void HandleImageDo(ContentOperation op, PdfName name, PdfStream stream)
    {
        var bbox = State.Ctm.TransformRect(0, 0, 1, 1);
        if (!IntersectsAnyRegion(bbox) || _kinds == ContentKinds.TextOnly)
        {
            WriteDo(op, name);
            return;
        }
        RemovedAnything = true;
        // Erasing for an edit never drops the image: the region is a few words on a scanned
        // page, and losing the whole page image to replace one of them is not a trade any
        // user would make. Redaction still drops it, because leaving it is a disclosure.
        if (ContainedInAnyRegion(bbox) && _kinds != ContentKinds.TextAndPixelsBeneath)
        {
            _retired.Add(name);
            return; // fully covered: drop the draw call entirely
        }
        var fill = _kinds == ContentKinds.TextAndPixelsBeneath
            ? ScrubFill.SurroundingPaper : ScrubFill.Black;
        if (ImageScrubber.TryScrubPixels(stream, bbox, _regions, out var scrubFailure, fill, Resources))
        {
            WriteDo(op, name);
            return;
        }
        _warnings.Add($"Image '{name.Value}' partially overlaps a redaction region " +
                      $"and could not be pixel-scrubbed ({scrubFailure}); it was removed " +
                      "entirely.");
        _retired.Add(name);
    }

    private void HandleFormDo(ContentOperation op, PdfName name, PdfStream stream)
    {
        // Never let the interpreter recurse into the form: it would replay the form's content
        // through this editor and inline it into the page. Handle it on a copy instead.
        var full = Matrix.FromArray(stream.GetAsArray(PdfName.Matrix)).Multiply(State.Ctm);
        var formBBox = PdfRect.FromArray(stream.GetAsArray(PdfName.BBox)) is { } box ? full.TransformRect(box) : (PdfRect?)null;
        if (formBBox is not { } fb || !IntersectsAnyRegion(fb))
        {
            WriteDo(op, name);
            return;
        }
        _retired.Add(name);
        if (_depth >= 6)
        {
            _warnings.Add("Form XObject nesting too deep; dropping the whole form inside the region.");
            RemovedAnything = true;
            return;
        }
        var cloned = PdfStream.FromFile(stream, stream.RawData);
        _document.MakeIndirect(cloned);
        var inner = Create(TransformRegionsInto(full), _document, _warnings, _depth + 1, _kinds);
        inner.EditFormStream(cloned, inner.ResourcesForClone(cloned, _editResources));
        RemovedAnything |= inner.RemovedAnything;
        var newName = PdfResources.AddEntry(_xobjects, "Fm", cloned);
        _xobjectsChanged = true;
        WriteDo(new ContentOperation("Do", new List<PdfObject> { newName }), newName);
    }

    private void WriteDo(ContentOperation op, PdfName name)
    {
        _drawn.Add(name);
        Write(op);
    }

    /// <summary>
    /// Gives a cloned form a resource dictionary of its own before this editor rewrites it. The
    /// clone shares every entry with its original, so without this the names the edit adds, and the
    /// ones it prunes, would land in resources the original, which other pages may still draw, uses.
    /// <para>
    /// A form with no <c>/Resources</c> draws with <paramref name="parentResources"/>. Its clone gets
    /// a copy, which also names every original the parent is about to prune; nothing else uses the
    /// copy, so whatever in it the rewritten form doesn't draw is retired too.
    /// </para>
    /// </summary>
    private PdfDictionary ResourcesForClone(PdfStream cloned, PdfDictionary parentResources)
    {
        var own = cloned.GetAsDictionary(PdfName.Resources);
        if (own == null && parentResources.GetAsDictionary(PdfName.XObject) is { } inherited)
            _retired.UnionWith(inherited.Keys);
        var resources = ShallowCopy(own ?? parentResources);
        cloned.Put(PdfName.Resources, resources);
        return resources;
    }

    /// <summary>
    /// Takes what the rewritten stream no longer draws out of <see cref="_xobjects"/>, and says
    /// whether that now differs from the dictionary it was copied from. Otherwise the original of
    /// every edited form, and every dropped image, stays reachable from the resources, and the
    /// writer saves it, redacted content and all.
    /// </summary>
    private bool PruneXObjects()
    {
        var stale = _retired.Where(n => !_drawn.Contains(n) && _xobjects.ContainsKey(n)).ToList();
        if (stale.Count == 0 || BorrowsResources()) return _xobjectsChanged;
        foreach (var name in stale) _xobjects.Remove(name);
        return true;
    }

    /// <summary>
    /// Whether content with no <c>/Resources</c> of its own may be looking names up in the edited
    /// resources: a form the rewritten stream still draws, or a Type 3 font, whose glyphs use the
    /// resources of whatever shows them. What that content draws can't be seen from this stream,
    /// so nothing is pruned.
    /// </summary>
    private bool BorrowsResources()
    {
        bool formBorrows = _drawn.Any(n => _xobjects.GetAsStream(n) is { } x
            && PdfName.Form.Equals(x.GetAsName(PdfName.Subtype))
            && x.GetAsDictionary(PdfName.Resources) == null);
        var fonts = _editResources.GetAsDictionary(PdfName.Font);
        return formBorrows || (fonts != null && fonts.Keys.Any(n => fonts.GetAsDictionary(n) is { } f
            && f.Is(Type3, PdfName.Subtype)
            && f.GetAsDictionary(PdfName.Resources) == null));
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
