using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using iText.Kernel.Pdf.Xobject;

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
/// one of the supplied regions. Text is removed at per-string granularity (dropped strings
/// are replaced by equivalent-width TJ displacements so surrounding text does not shift),
/// image XObjects are dropped or pixel-scrubbed, and form XObjects are recursively edited
/// on a cloned copy. This achieves true removal: the data is gone from the file, not
/// merely covered.
/// </summary>
internal sealed class ContentStreamEditor : PdfCanvasProcessor
{
    private const float Epsilon = 0.05f;

    private readonly IList<Rectangle> _regions;
    private readonly PdfDocument _document;
    private readonly List<string> _warnings;
    private readonly CollectingListener _collector;
    private readonly int _depth;
    private readonly ContentKinds _kinds;

    private PdfCanvas _canvas = null!;
    private PdfResources _editResources = null!;

    // XObject names whose draw this edit dropped or redirected to an edited clone, and every name the
    // rewritten stream still draws. The difference is what gets pruned from the edited resources.
    private readonly HashSet<PdfName> _retired = new();
    private readonly HashSet<PdfName> _drawn = new();

    public bool RemovedAnything { get; private set; }

    private ContentStreamEditor(CollectingListener collector, IList<Rectangle> regions,
        PdfDocument document, List<string> warnings, int depth, ContentKinds kinds) : base(collector)
    {
        _collector = collector;
        _regions = regions;
        _document = document;
        _warnings = warnings;
        _depth = depth;
        _kinds = kinds;
    }

    public static ContentStreamEditor Create(IList<Rectangle> regions, PdfDocument document,
        List<string> warnings, int depth = 0, ContentKinds kinds = ContentKinds.All)
        => new(new CollectingListener(), regions, document, warnings, depth, kinds);

    /// <summary>Rewrites the content of <paramref name="page"/> in place.</summary>
    public void EditPage(PdfPage page)
    {
        var resources = page.GetResources();
        PdfStructureGuard.EnsureFormXObjectsTerminate(page);
        byte[] content = null!;
        PdfIo.Guarded("decoding the page content stream", () => content = page.GetContentBytes());
        var newStream = (PdfStream)new PdfStream().MakeIndirect(_document);
        _canvas = new PdfCanvas(newStream, resources, _document);
        _editResources = resources;
        PdfIo.Guarded("rewriting the page content stream", () => ProcessContent(content, resources));
        page.GetPdfObject().Put(PdfName.Contents, newStream);
        page.GetPdfObject().SetModified();
        PrunePageXObjects(page);
    }

    /// <summary>
    /// Rewrites the raw content of a form XObject stream in place. <paramref name="resources"/> must
    /// belong to <paramref name="formStream"/> alone: what the rewrite stops drawing is pruned from it.
    /// </summary>
    public void EditFormStream(PdfStream formStream, PdfResources resources)
    {
        byte[] content = null!;
        PdfIo.Guarded("decoding a form XObject stream", () => content = formStream.GetBytes());
        var scratch = new PdfStream();
        _canvas = new PdfCanvas(scratch, resources, _document);
        _editResources = resources;
        PdfIo.Guarded("rewriting a form XObject stream", () => ProcessContent(content, resources));
        formStream.SetData(_canvas.GetContentStream().GetBytes(false));
        var own = resources.GetPdfObject();
        RemoveXObjects(own, StaleXObjectNames(own));
    }

    // Wrap every default operator so we can decide, per operator, whether to copy it through.
    // iText's API predates nullable annotations: both the parameter and the return value
    // may be null in practice, hence the null-forgiving returns.
    public override IContentOperator RegisterContentOperator(string operatorString, IContentOperator op)
    {
        var wrapper = new OperatorWrapper(op);
        var former = base.RegisterContentOperator(operatorString, wrapper);
        return former is OperatorWrapper w ? w.Original! : former!;
    }

    private sealed class OperatorWrapper : IContentOperator
    {
        public IContentOperator? Original { get; }
        public OperatorWrapper(IContentOperator? original) => Original = original;

        public void Invoke(PdfCanvasProcessor processor, PdfLiteral oper, IList<PdfObject> operands)
        {
            var editor = (ContentStreamEditor)processor;
            switch (oper.ToString())
            {
                case "Tj":
                case "TJ":
                case "'":
                case "\"":
                    editor.HandleShowText(Original, oper, operands);
                    break;
                case "Do":
                    editor.HandleDo(Original, oper, operands);
                    break;
                case "EI":
                    editor.HandleInlineImage(Original, oper, operands);
                    break;
                default:
                    Original?.Invoke(processor, oper, operands);
                    editor.WriteOperands(operands);
                    break;
            }
        }
    }

    // ---------------------------------------------------------------- text

    private void HandleShowText(IContentOperator? original, PdfLiteral oper, IList<PdfObject> operands)
    {
        _collector.Texts.Clear();
        original?.Invoke(this, oper, operands);
        var shown = _collector.Texts;

        bool anyHit = shown.Any(s => IntersectsAnyRegion(s.BBox));
        string opName = oper.ToString();

        if (!anyHit)
        {
            WriteOperands(operands);
            return;
        }
        RemovedAnything = true;

        // Re-emit the side effects of ' and " (line advance, word/char spacing), then
        // re-emit the show-text call in TJ form with hit strings replaced by
        // equivalent-width displacements.
        var os = _canvas.GetContentStream().GetOutputStream();
        switch (opName)
        {
            case "'":
                os.Write(new PdfLiteral("T*")).WriteNewLine();
                break;
            case "\"":
                os.Write(operands[0]).WriteSpace().Write(new PdfLiteral("Tw")).WriteNewLine();
                os.Write(operands[1]).WriteSpace().Write(new PdfLiteral("Tc")).WriteNewLine();
                os.Write(new PdfLiteral("T*")).WriteNewLine();
                break;
        }

        var sourceItems = new List<PdfObject>();
        if (opName == "TJ" && operands[0] is PdfArray arr)
            sourceItems.AddRange(arr);
        else if (opName == "\"")
            sourceItems.Add(operands[2]);
        else
            sourceItems.Add(operands[0]);

        int stringCount = sourceItems.Count(o => o is PdfString);
        var replacement = new PdfArray();
        if (stringCount == shown.Count)
        {
            int textIdx = 0;
            foreach (var item in sourceItems)
            {
                if (item is not PdfString)
                {
                    replacement.Add(item);
                    continue;
                }
                var info = shown[textIdx++];
                if (!IntersectsAnyRegion(info.BBox))
                {
                    replacement.Add(item);
                    continue;
                }
                // Per-glyph split: glyphs outside the region survive, glyphs inside are
                // replaced by an equivalent-width displacement so the line does not shift.
                foreach (var ch in info.Chars)
                {
                    if (IntersectsAnyRegion(ch.BBox))
                        replacement.Add(new PdfNumber(DisplacementFor(ch.UnscaledWidth, info)));
                    else
                        replacement.Add(ch.Str);
                }
            }
        }
        else
        {
            // Event/string count mismatch (unusual encodings). Fall back to dropping the
            // whole operator, preserving total advance so later text does not shift.
            double total = shown.Sum(s => (double)DisplacementFor(s.UnscaledWidth, s));
            replacement.Add(new PdfNumber(total));
        }

        os.Write(replacement).WriteSpace().Write(new PdfLiteral("TJ")).WriteNewLine();
    }

    private static float DisplacementFor(float unscaledWidth, ShownText info)
    {
        // A number n inside TJ translates the text position by -n/1000 * fontSize * hScale.
        float fs = info.FontSize;
        float th = info.HorizontalScaling;
        if (th > 5f) th /= 100f;          // normalise: stored as percent in some paths
        if (th <= 0f) th = 1f;
        if (fs == 0f) return 0f;
        return -unscaledWidth * 1000f / (fs * th);
    }

    // ------------------------------------------------------------- XObjects

    private void HandleDo(IContentOperator? original, PdfLiteral oper, IList<PdfObject> operands)
    {
        var name = (PdfName)operands[0];
        var xobjects = GetResources().GetResource(PdfName.XObject);
        var stream = xobjects?.GetAsStream(name);
        var subtype = stream?.GetAsName(PdfName.Subtype);

        if (stream == null)
        {
            WriteDo(name, oper);
            return;
        }

        if (PdfName.Image.Equals(subtype))
        {
            _collector.Images.Clear();
            original?.Invoke(this, oper, operands);
            var bbox = _collector.Images.Count > 0 ? _collector.Images[0] : null;
            if (bbox != null && IntersectsAnyRegion(bbox) && _kinds != ContentKinds.TextOnly)
            {
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
                if (ImageScrubber.TryScrubPixels(stream, bbox, _regions, out var scrubFailure, fill))
                {
                    WriteDo(name, oper);
                    return;
                }
                _warnings.Add($"Image '{name.GetValue()}' partially overlaps a redaction region " +
                              $"and could not be pixel-scrubbed ({scrubFailure}); it was removed " +
                              "entirely.");
                _retired.Add(name);
                return;
            }
            WriteDo(name, oper);
            return;
        }

        if (PdfName.Form.Equals(subtype))
        {
            // Never let the base processor recurse into the form: our wrapped operators
            // would inline the form's content into the page stream. Handle it manually.
            var ctm = GetGraphicsState().GetCtm();
            var formMatrix = ReadMatrix(stream.GetAsArray(PdfName.Matrix));
            var full = formMatrix.Multiply(ctm);
            var formBBox = FormUserSpaceBBox(stream, full);
            if (formBBox != null && IntersectsAnyRegion(formBBox))
            {
                if (_depth >= 6)
                {
                    _warnings.Add("Form XObject nesting too deep; dropping the whole form inside the region.");
                    RemovedAnything = true;
                    _retired.Add(name);
                    return;
                }
                var cloned = (PdfStream)stream.Clone();
                cloned.MakeIndirect(_document);
                var innerRegions = TransformRegionsInto(full);
                var inner = Create(innerRegions, _document, _warnings, _depth + 1, _kinds);
                var formResources = new PdfResources(inner.OwnResources(cloned, _editResources.GetPdfObject()));
                inner.EditFormStream(cloned, formResources);
                RemovedAnything |= inner.RemovedAnything;
                _retired.Add(name);
                WriteDo(_editResources.AddForm(new PdfFormXObject(cloned)), oper);
                return;
            }
            WriteDo(name, oper);
            return;
        }

        WriteDo(name, oper);
    }

    private void WriteDo(PdfName name, PdfLiteral oper)
    {
        _drawn.Add(name);
        WriteOperands(new List<PdfObject> { name, oper });
    }

    /// <summary>
    /// Gives a cloned form a resource dictionary of its own before this editor rewrites it. A clone
    /// shares every indirect object with its original, so without this the names the edit adds, and
    /// the ones it prunes, would land in resources the original form still draws with.
    /// <para>
    /// A form with no <c>/Resources</c> draws with <paramref name="parentResources"/>. Its clone gets
    /// a copy, which also holds every original the parent is about to prune; nothing else uses the
    /// copy, so whatever in it the rewritten form doesn't draw is retired too.
    /// </para>
    /// </summary>
    private PdfDictionary OwnResources(PdfStream clone, PdfDictionary parentResources)
    {
        var resources = clone.GetAsDictionary(PdfName.Resources);
        if (resources == null)
        {
            resources = (PdfDictionary)parentResources.Clone();
            if (resources.GetAsDictionary(PdfName.XObject) is { } inherited)
                _retired.UnionWith(inherited.KeySet());
        }
        else if (resources.GetIndirectReference() != null)
        {
            resources = (PdfDictionary)resources.Clone();
        }
        clone.Put(PdfName.Resources, resources);
        OwnXObjects(resources);
        return resources;
    }

    /// <summary>
    /// Takes what the rewritten page no longer draws out of its resources. Otherwise the original of
    /// every edited form, and every dropped image, stays reachable from the page and the writer saves
    /// it, redacted content and all. An indirect or inherited resource dictionary may be shared with
    /// pages that still draw those originals, so the page gets a copy of its own to prune instead.
    /// </summary>
    private void PrunePageXObjects(PdfPage page)
    {
        var resources = _editResources.GetPdfObject();
        var stale = StaleXObjectNames(resources);
        if (stale.Count == 0) return;
        if (resources.GetIndirectReference() != null
            || !ReferenceEquals(page.GetPdfObject().Get(PdfName.Resources, false), resources))
            resources = (PdfDictionary)resources.Clone();
        RemoveXObjects(resources, stale);
        // Rewrapped even when pruned in place: the old wrapper still maps the removed objects to names.
        page.SetResources(new PdfResources(resources));
    }

    /// <summary>
    /// The names this edit stopped drawing that can come out of <paramref name="resources"/>. Nothing
    /// comes out while content with no <c>/Resources</c> of its own may be looking names up here (a
    /// form the rewritten stream still draws, or a Type 3 font, whose glyphs use the resources of
    /// whatever shows them): what that content draws can't be seen from this stream.
    /// </summary>
    private List<PdfName> StaleXObjectNames(PdfDictionary resources)
    {
        var xobjects = resources.GetAsDictionary(PdfName.XObject);
        var stale = _retired.Where(n => !_drawn.Contains(n) && xobjects?.ContainsKey(n) == true).ToList();
        if (stale.Count == 0) return stale;

        var fonts = resources.GetAsDictionary(PdfName.Font);
        bool borrowed =
            _drawn.Any(n => xobjects!.GetAsStream(n) is { } x
                && PdfName.Form.Equals(x.GetAsName(PdfName.Subtype))
                && x.GetAsDictionary(PdfName.Resources) == null)
            || (fonts != null && fonts.KeySet().Any(n => fonts.GetAsDictionary(n) is { } f
                && PdfName.Type3.Equals(f.GetAsName(PdfName.Subtype))
                && f.GetAsDictionary(PdfName.Resources) == null));
        return borrowed ? new List<PdfName>() : stale;
    }

    private static void RemoveXObjects(PdfDictionary resources, List<PdfName> names)
    {
        if (names.Count == 0) return;
        var xobjects = OwnXObjects(resources)!;
        foreach (var name in names)
            xobjects.Remove(name);
    }

    /// <summary>The <c>/XObject</c> dictionary, copied first if it is indirect and so may be shared.</summary>
    private static PdfDictionary? OwnXObjects(PdfDictionary resources)
    {
        var xobjects = resources.GetAsDictionary(PdfName.XObject);
        if (xobjects?.GetIndirectReference() == null) return xobjects;
        xobjects = (PdfDictionary)xobjects.Clone();
        resources.Put(PdfName.XObject, xobjects);
        return xobjects;
    }

    private void HandleInlineImage(IContentOperator? original, PdfLiteral oper, IList<PdfObject> operands)
    {
        _collector.Images.Clear();
        original?.Invoke(this, oper, operands);
        var bbox = _collector.Images.Count > 0 ? _collector.Images[0] : null;
        if (bbox != null && IntersectsAnyRegion(bbox) && _kinds != ContentKinds.TextOnly)
        {
            RemovedAnything = true;
            return; // drop inline image touching a region (safe over-redaction)
        }
        if (operands[0] is PdfStream img)
            WriteInlineImage(img);
    }

    private void WriteInlineImage(PdfStream img)
    {
        var os = _canvas.GetContentStream().GetOutputStream();
        os.Write(new PdfLiteral("BI")).WriteNewLine();
        foreach (var key in img.KeySet())
        {
            if (PdfName.Length.Equals(key)) continue;
            os.Write(key).WriteSpace().Write(img.Get(key)).WriteNewLine();
        }
        os.Write(new PdfLiteral("ID")).WriteNewLine();
        os.WriteBytes(img.GetBytes(false));
        os.WriteNewLine().Write(new PdfLiteral("EI")).WriteNewLine();
    }

    // ------------------------------------------------------------- plumbing

    private void WriteOperands(IList<PdfObject> operands)
    {
        var os = _canvas.GetContentStream().GetOutputStream();
        for (int i = 0; i < operands.Count; i++)
        {
            os.Write(operands[i]);
            if (i < operands.Count - 1) os.WriteSpace();
            else os.WriteNewLine();
        }
    }

    private bool IntersectsAnyRegion(Rectangle r) => _regions.Any(reg => Overlaps(reg, r));

    private bool ContainedInAnyRegion(Rectangle r) => _regions.Any(reg =>
        reg.GetLeft() <= r.GetLeft() + Epsilon && reg.GetRight() >= r.GetRight() - Epsilon &&
        reg.GetBottom() <= r.GetBottom() + Epsilon && reg.GetTop() >= r.GetTop() - Epsilon);

    private static bool Overlaps(Rectangle a, Rectangle b) =>
        a.GetLeft() < b.GetRight() - Epsilon && b.GetLeft() < a.GetRight() - Epsilon &&
        a.GetBottom() < b.GetTop() - Epsilon && b.GetBottom() < a.GetTop() - Epsilon;

    private static Matrix ReadMatrix(PdfArray? arr)
    {
        if (arr == null || arr.Size() != 6) return new Matrix();
        return new Matrix(arr.GetAsNumber(0).FloatValue(), arr.GetAsNumber(1).FloatValue(),
            arr.GetAsNumber(2).FloatValue(), arr.GetAsNumber(3).FloatValue(),
            arr.GetAsNumber(4).FloatValue(), arr.GetAsNumber(5).FloatValue());
    }

    private static Rectangle? FormUserSpaceBBox(PdfStream form, Matrix fullMatrix)
    {
        var bbox = form.GetAsArray(PdfName.BBox);
        if (bbox == null || bbox.Size() != 4) return null;
        float llx = bbox.GetAsNumber(0).FloatValue(), lly = bbox.GetAsNumber(1).FloatValue();
        float urx = bbox.GetAsNumber(2).FloatValue(), ury = bbox.GetAsNumber(3).FloatValue();
        return TransformBBox(llx, lly, urx, ury, fullMatrix);
    }

    private static Rectangle TransformBBox(float llx, float lly, float urx, float ury, Matrix m)
    {
        var pts = new[]
        {
            TransformPoint(llx, lly, m), TransformPoint(urx, lly, m),
            TransformPoint(llx, ury, m), TransformPoint(urx, ury, m)
        };
        float minX = pts.Min(p => p.x), maxX = pts.Max(p => p.x);
        float minY = pts.Min(p => p.y), maxY = pts.Max(p => p.y);
        return new Rectangle(minX, minY, maxX - minX, maxY - minY);
    }

    private static (float x, float y) TransformPoint(float x, float y, Matrix m) =>
        (x * m.Get(Matrix.I11) + y * m.Get(Matrix.I21) + m.Get(Matrix.I31),
         x * m.Get(Matrix.I12) + y * m.Get(Matrix.I22) + m.Get(Matrix.I32));

    /// <summary>Maps the page-space regions into the coordinate space of a form XObject.</summary>
    private IList<Rectangle> TransformRegionsInto(Matrix formToUser)
    {
        float a = formToUser.Get(Matrix.I11), b = formToUser.Get(Matrix.I12);
        float c = formToUser.Get(Matrix.I21), d = formToUser.Get(Matrix.I22);
        float e = formToUser.Get(Matrix.I31), f = formToUser.Get(Matrix.I32);
        float det = a * d - b * c;
        if (Math.Abs(det) < 1e-9)
            return new List<Rectangle>();
        var inv = new Matrix(d / det, -b / det, -c / det, a / det,
            (c * f - d * e) / det, (b * e - a * f) / det);
        return _regions.Select(r =>
            TransformBBox(r.GetLeft(), r.GetBottom(), r.GetRight(), r.GetTop(), inv)).ToList();
    }

    // ------------------------------------------------------------- listener

    internal sealed record ShownChar(Rectangle BBox, float UnscaledWidth, PdfString Str);

    internal sealed record ShownText(Rectangle BBox, float UnscaledWidth, float FontSize,
        float HorizontalScaling, IReadOnlyList<ShownChar> Chars);

    private sealed class CollectingListener : IEventListener
    {
        public List<ShownText> Texts { get; } = new();
        public List<Rectangle> Images { get; } = new();

        public void EventOccurred(IEventData data, EventType type)
        {
            // Geometry must be captured immediately: render infos reference mutable
            // graphics state that changes as processing continues.
            if (data is TextRenderInfo t)
            {
                var chars = new List<ShownChar>();
                foreach (var c in t.GetCharacterRenderInfos())
                    chars.Add(new ShownChar(BBoxOf(c), c.GetUnscaledWidth(), c.GetPdfString()));
                Texts.Add(new ShownText(BBoxOf(t), t.GetUnscaledWidth(), t.GetFontSize(),
                    t.GetHorizontalScaling(), chars));
            }
            else if (data is ImageRenderInfo i)
            {
                var m = i.GetImageCtm();
                Images.Add(TransformBBox(0, 0, 1, 1, m));
            }
        }

        private static Rectangle BBoxOf(TextRenderInfo t)
        {
            var asc = t.GetAscentLine();
            var desc = t.GetDescentLine();
            float minX = Math.Min(Math.Min(asc.GetStartPoint().Get(0), asc.GetEndPoint().Get(0)),
                                  Math.Min(desc.GetStartPoint().Get(0), desc.GetEndPoint().Get(0)));
            float maxX = Math.Max(Math.Max(asc.GetStartPoint().Get(0), asc.GetEndPoint().Get(0)),
                                  Math.Max(desc.GetStartPoint().Get(0), desc.GetEndPoint().Get(0)));
            float minY = Math.Min(Math.Min(asc.GetStartPoint().Get(1), asc.GetEndPoint().Get(1)),
                                  Math.Min(desc.GetStartPoint().Get(1), desc.GetEndPoint().Get(1)));
            float maxY = Math.Max(Math.Max(asc.GetStartPoint().Get(1), asc.GetEndPoint().Get(1)),
                                  Math.Max(desc.GetStartPoint().Get(1), desc.GetEndPoint().Get(1)));
            return new Rectangle(minX, minY, maxX - minX, maxY - minY);
        }

        public ICollection<EventType>? GetSupportedEvents() => null;
    }
}
