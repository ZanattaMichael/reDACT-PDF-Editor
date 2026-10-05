using PdfEditor.Core.Pdf.Fonts;

namespace PdfEditor.Core.Pdf;

/// <summary>A point in user space.</summary>
internal readonly record struct Point2(double X, double Y);

/// <summary>A line segment in user space.</summary>
internal readonly record struct LineSegment(Point2 Start, Point2 End)
{
    public double Length => Math.Sqrt((End.X - Start.X) * (End.X - Start.X) + (End.Y - Start.Y) * (End.Y - Start.Y));

    public static LineSegment Transform(double x0, double y0, double x1, double y1, Matrix m)
    {
        var a = m.Transform(x0, y0);
        var b = m.Transform(x1, y1);
        return new LineSegment(new Point2(a.X, a.Y), new Point2(b.X, b.Y));
    }
}

/// <summary>The graphics-state parameters the interpreter tracks (§8.4, with the text state of §9.3).</summary>
internal sealed class GraphicsState
{
    public Matrix Ctm = Matrix.Identity;
    public PdfFont? Font;
    public double FontSize = 0;
    public double CharSpacing;
    public double WordSpacing;
    /// <summary>Horizontal scaling as a fraction (Tz 100 = 1.0).</summary>
    public double HorizontalScaling = 1;
    public double Leading;
    public double Rise;
    public int RenderMode;

    public GraphicsState Clone() => (GraphicsState)MemberwiseClone();
}

/// <summary>
/// One shown string — the operand of Tj, or one string element of a TJ array — with the state it
/// was shown in, and the geometry of the whole run and of each glyph in it.
/// <para>
/// The geometry follows the long-standing convention of PDF text extractors (and of the engine
/// this project used before): a run's box spans from its descent line to its ascent line, the
/// ascender/descender normalised so they span at most one em, and its baseline stops short of the
/// trailing character and word spacing, which position the <em>next</em> glyph rather than belong
/// to this one.
/// </para>
/// </summary>
internal sealed class TextRenderInfo
{
    private readonly GraphicsState _state;

    internal TextRenderInfo(GraphicsState state, Matrix textMatrix, byte[] bytes, int itemIndex)
    {
        _state = state;
        TextMatrix = textMatrix;
        Bytes = bytes;
        ItemIndex = itemIndex;
        Glyphs = BuildGlyphs();
    }

    public PdfFont Font => _state.Font ?? PdfFont.Fallback;
    public string FontName => Font.BaseFont;
    public double FontSize => _state.FontSize;
    public double CharSpacing => _state.CharSpacing;
    public double WordSpacing => _state.WordSpacing;
    public double HorizontalScaling => _state.HorizontalScaling;
    public double Rise => _state.Rise;
    public int RenderMode => _state.RenderMode;
    public Matrix Ctm => _state.Ctm;

    /// <summary>The text matrix at the start of the run.</summary>
    public Matrix TextMatrix { get; }

    /// <summary>Text space → user space at the start of the run.</summary>
    public Matrix TextToUser => TextMatrix.Multiply(Ctm);

    /// <summary>The string's bytes, as the content stream holds them.</summary>
    public byte[] Bytes { get; }

    /// <summary>Which string this is: 0 for Tj, ', "; the element index for TJ.</summary>
    public int ItemIndex { get; }

    public IReadOnlyList<GlyphRenderInfo> Glyphs { get; }

    public string Text => string.Concat(Glyphs.Select(g => g.Text));

    /// <summary>The run's total advance in (unscaled) text space, spacing and Tz included.</summary>
    public double UnscaledWidth => Glyphs.Sum(g => g.UnscaledWidth);

    private List<GlyphRenderInfo> BuildGlyphs()
    {
        var glyphs = new List<GlyphRenderInfo>();
        double offset = 0;
        foreach (var (code, start, length) in Font.Codes(Bytes))
        {
            double w0 = Font.GlyphWidth(code) * Font.FontMatrix.A;
            bool isSpace = length == 1 && Bytes[start] == 32; // word spacing applies to code 32 only
            double advance = (w0 * FontSize + CharSpacing + (isSpace ? WordSpacing : 0)) * HorizontalScaling;
            var glyphMatrix = Matrix.Translation(offset, 0).Multiply(TextMatrix);
            glyphs.Add(new GlyphRenderInfo(this, code, Bytes.AsSpan(start, length).ToArray(), Font.Unicode(code),
                advance, glyphMatrix));
            offset += advance;
        }
        return glyphs;
    }

    /// <summary>
    /// The ascender and descender scaled to the font size. A font whose ascender-to-descender span
    /// is under 700 units is treated as spanning exactly one em — a convention that keeps boxes
    /// sane for fonts that under-report their metrics.
    /// </summary>
    internal (double Ascent, double Descent) AscentDescent()
    {
        double ascent = Font.Ascent, descent = Font.Descent;
        if (descent > 0) descent = -descent;
        double scale = ascent - descent < 700 ? ascent - descent : 1000;
        if (scale <= 0) scale = 1000;
        return (ascent / scale * FontSize, descent / scale * FontSize);
    }

    /// <summary>The run's advance less the spacing that follows its last glyph.</summary>
    internal double CorrectedWidth(double unscaledWidth, string text) =>
        unscaledWidth - (CharSpacing + (text.Length > 0 && text[^1] == ' ' ? WordSpacing : 0)) * HorizontalScaling;

    public LineSegment Baseline => Line(Rise);
    public LineSegment AscentLine => Line(AscentDescent().Ascent + Rise);
    public LineSegment DescentLine => Line(AscentDescent().Descent + Rise);

    private LineSegment Line(double y) =>
        LineSegment.Transform(0, y, CorrectedWidth(UnscaledWidth, Text), y, TextToUser);

    /// <summary>The box spanned by the run's ascent and descent lines, in user space.</summary>
    public PdfRect BoundingBox => Bounds(AscentLine, DescentLine);

    internal static PdfRect Bounds(LineSegment ascent, LineSegment descent)
    {
        double minX = Math.Min(Math.Min(ascent.Start.X, ascent.End.X), Math.Min(descent.Start.X, descent.End.X));
        double maxX = Math.Max(Math.Max(ascent.Start.X, ascent.End.X), Math.Max(descent.Start.X, descent.End.X));
        double minY = Math.Min(Math.Min(ascent.Start.Y, ascent.End.Y), Math.Min(descent.Start.Y, descent.End.Y));
        double maxY = Math.Max(Math.Max(ascent.Start.Y, ascent.End.Y), Math.Max(descent.Start.Y, descent.End.Y));
        return PdfRect.FromCorners(minX, minY, maxX, maxY);
    }

    /// <summary>The width of a space in this run's font and state, in user space.</summary>
    public double SingleSpaceWidth
    {
        get
        {
            double w = Font.SpaceWidth() * Font.FontMatrix.A;
            double unscaled = (w * FontSize + CharSpacing + WordSpacing) * HorizontalScaling;
            return LineSegment.Transform(0, 0, unscaled, 0, TextToUser).Length;
        }
    }
}

/// <summary>One glyph of a <see cref="TextRenderInfo"/>.</summary>
internal sealed class GlyphRenderInfo
{
    private readonly TextRenderInfo _run;

    internal GlyphRenderInfo(TextRenderInfo run, int code, byte[] bytes, string text, double unscaledWidth, Matrix textMatrix)
    {
        _run = run;
        Code = code;
        Bytes = bytes;
        Text = text;
        UnscaledWidth = unscaledWidth;
        TextMatrix = textMatrix;
    }

    public int Code { get; }

    /// <summary>The glyph's bytes in the shown string.</summary>
    public byte[] Bytes { get; }

    public string Text { get; }

    /// <summary>The glyph's advance in unscaled text space, its spacing included.</summary>
    public double UnscaledWidth { get; }

    /// <summary>The text matrix at the glyph's origin.</summary>
    public Matrix TextMatrix { get; }

    public TextRenderInfo Run => _run;

    private Matrix TextToUser => TextMatrix.Multiply(_run.Ctm);

    private LineSegment Line(double y) =>
        LineSegment.Transform(0, y, _run.CorrectedWidth(UnscaledWidth, Text), y, TextToUser);

    public LineSegment Baseline => Line(_run.Rise);
    public LineSegment AscentLine => Line(_run.AscentDescent().Ascent + _run.Rise);
    public LineSegment DescentLine => Line(_run.AscentDescent().Descent + _run.Rise);
    public PdfRect BoundingBox => TextRenderInfo.Bounds(AscentLine, DescentLine);
}

/// <summary>An image drawn on the page: an XObject (<c>Do</c>) or an inline image (<c>BI…EI</c>).</summary>
internal sealed class ImageRenderInfo
{
    /// <summary>Maps the image's unit square to user space.</summary>
    public required Matrix Ctm { get; init; }

    /// <summary>The resource name, for an XObject.</summary>
    public PdfName? Name { get; init; }

    /// <summary>The image XObject, or null for an inline image.</summary>
    public PdfStream? Stream { get; init; }

    public PdfDictionary? InlineDictionary { get; init; }
    public byte[]? InlineData { get; init; }

    public bool IsInline => Stream == null;

    /// <summary>The user-space box the image covers.</summary>
    public PdfRect BoundingBox => Ctm.TransformRect(0, 0, 1, 1);
}

/// <summary>Receives what a <see cref="ContentProcessor"/> finds.</summary>
internal interface IContentListener
{
    void OnText(TextRenderInfo info) { }
    void OnImage(ImageRenderInfo info) { }
}

/// <summary>
/// Interprets a content stream: maintains the graphics and text state through q/Q, cm, the text
/// operators and nested form XObjects, and reports every shown string and drawn image to a
/// listener. This is the single source of text geometry in the engine — search, extraction,
/// redaction and the redaction report all measure text through it, so what a search finds is
/// exactly what a redaction over the same area removes.
/// </summary>
internal class ContentProcessor
{
    /// <summary>Deepest form-XObject nesting followed. PdfStructureGuard refuses anything deeper first.</summary>
    public const int MaxFormDepth = 32;

    /// <summary>Saved-state depth beyond which q is ignored (a hostile stream of q's cannot exhaust memory).</summary>
    private const int MaxStateDepth = 4096;

    private readonly Stack<GraphicsState> _stack = new();
    private readonly HashSet<PdfStream> _formsInProgress = new(ReferenceEqualityComparer.Instance);
    private int _formDepth;

    protected IContentListener Listener { get; set; }
    protected GraphicsState State { get; private set; } = new();
    protected Matrix TextMatrix { get; set; } = Matrix.Identity;
    protected Matrix TextLineMatrix { get; set; } = Matrix.Identity;
    protected PdfDictionary? Resources { get; private set; }

    public ContentProcessor(IContentListener listener) => Listener = listener;

    /// <summary>Interprets a page's content.</summary>
    public void ProcessPage(PdfPage page) => ProcessContent(page.GetContentBytes(), page.Resources);

    /// <summary>Interprets <paramref name="content"/> against <paramref name="resources"/>.</summary>
    public void ProcessContent(byte[] content, PdfDictionary? resources)
    {
        var saved = Resources;
        Resources = resources;
        try
        {
            foreach (var op in ContentParser.Parse(content)) Invoke(op);
        }
        finally
        {
            Resources = saved;
        }
    }

    /// <summary>Handles one operation. Subclasses override to observe or rewrite operations.</summary>
    protected virtual void Invoke(ContentOperation op) => Execute(op);

    /// <summary>Applies an operation's effect on the state, and reports what it draws.</summary>
    protected void Execute(ContentOperation op)
    {
        var o = op.Operands;
        switch (op.Operator)
        {
            case "q" or "Q" or "cm" or "gs":
                ExecuteGraphicsState(op.Operator, o);
                break;
            case "Tc" or "Tw" or "Tz" or "TL" or "Ts" or "Tr":
                ExecuteTextState(op.Operator, o);
                break;
            case "BT" or "Tf" or "Td" or "TD" or "Tm" or "T*":
                ExecuteTextPositioning(op.Operator, o);
                break;
            case "Tj" or "'" or "\"" or "TJ":
                ExecuteShowText(op.Operator, o);
                break;
            case "Do":
                if (LastOperand<PdfName>(o) is { } xobject) DoXObject(xobject);
                break;
            case "BI":
                ReportInlineImage(op);
                break;
        }
    }

    private void ExecuteGraphicsState(string oper, List<PdfObject> o)
    {
        switch (oper)
        {
            case "q":
                if (_stack.Count < MaxStateDepth) _stack.Push(State.Clone());
                break;
            case "Q":
                if (_stack.Count > 0) State = _stack.Pop();
                break;
            case "cm":
                if (Num(o, 6) is { } m) State.Ctm = new Matrix(m[0], m[1], m[2], m[3], m[4], m[5]).Multiply(State.Ctm);
                break;
            case "gs":
                ApplyExtGState(LastOperand<PdfName>(o));
                break;
        }
    }

    private void ExecuteTextState(string oper, List<PdfObject> o)
    {
        switch (oper)
        {
            case "Tc":
                if (Num(o, 1) is { } tc) State.CharSpacing = tc[0];
                break;
            case "Tw":
                if (Num(o, 1) is { } tw) State.WordSpacing = tw[0];
                break;
            case "Tz":
                if (Num(o, 1) is { } tz) State.HorizontalScaling = tz[0] / 100.0;
                break;
            case "TL":
                if (Num(o, 1) is { } tl) State.Leading = tl[0];
                break;
            case "Ts":
                if (Num(o, 1) is { } ts) State.Rise = ts[0];
                break;
            case "Tr":
                if (Num(o, 1) is { } tr) State.RenderMode = (int)tr[0];
                break;
        }
    }

    private void ExecuteTextPositioning(string oper, List<PdfObject> o)
    {
        switch (oper)
        {
            case "BT":
                TextMatrix = TextLineMatrix = Matrix.Identity;
                break;
            case "Tf":
                if (o.Count >= 2 && o[^2] is PdfName fontName && o[^1] is PdfNumber size)
                {
                    State.Font = ResolveFont(fontName);
                    State.FontSize = size.Value;
                }
                break;
            case "Td":
                if (Num(o, 2) is { } td) MoveText(td[0], td[1]);
                break;
            case "TD":
                if (Num(o, 2) is { } tdd)
                {
                    State.Leading = -tdd[1];
                    MoveText(tdd[0], tdd[1]);
                }
                break;
            case "Tm":
                if (Num(o, 6) is { } tm) TextMatrix = TextLineMatrix = new Matrix(tm[0], tm[1], tm[2], tm[3], tm[4], tm[5]);
                break;
            case "T*":
                MoveText(0, -State.Leading);
                break;
        }
    }

    private void ExecuteShowText(string oper, List<PdfObject> o)
    {
        switch (oper)
        {
            case "Tj":
                if (LastOperand<PdfString>(o) is { } tj) ShowString(tj.Bytes, 0);
                break;
            case "'":
                MoveText(0, -State.Leading);
                if (LastOperand<PdfString>(o) is { } q1) ShowString(q1.Bytes, 0);
                break;
            case "\"":
                if (o.Count >= 3 && o[^3] is PdfNumber aw && o[^2] is PdfNumber ac)
                {
                    State.WordSpacing = aw.Value;
                    State.CharSpacing = ac.Value;
                }
                MoveText(0, -State.Leading);
                if (LastOperand<PdfString>(o) is { } q2) ShowString(q2.Bytes, 0);
                break;
            case "TJ":
                if (LastOperand<PdfArray>(o) is { } items) ShowArray(items);
                break;
        }
    }

    private void ReportInlineImage(ContentOperation op)
    {
        if (op.InlineImageDictionary == null) return;
        Listener.OnImage(new ImageRenderInfo
        {
            Ctm = State.Ctm, InlineDictionary = op.InlineImageDictionary, InlineData = op.InlineImageData,
        });
    }

    private static T? LastOperand<T>(List<PdfObject> operands) where T : PdfObject =>
        operands.Count > 0 ? operands[^1] as T : null;

    private static double[]? Num(List<PdfObject> operands, int count)
    {
        if (operands.Count < count) return null;
        var values = new double[count];
        for (int i = 0; i < count; i++)
        {
            if (operands[operands.Count - count + i] is not PdfNumber n) return null;
            values[i] = n.Value;
        }
        return values;
    }

    private void MoveText(double tx, double ty)
    {
        TextLineMatrix = Matrix.Translation(tx, ty).Multiply(TextLineMatrix);
        TextMatrix = TextLineMatrix;
    }

    /// <summary>Shows one string: reports it, then advances the text matrix past it.</summary>
    protected TextRenderInfo ShowString(byte[] bytes, int itemIndex)
    {
        var info = new TextRenderInfo(State.Clone(), TextMatrix, bytes, itemIndex);
        Listener.OnText(info);
        TextMatrix = Matrix.Translation(info.UnscaledWidth, 0).Multiply(TextMatrix);
        return info;
    }

    private void ShowArray(PdfArray items)
    {
        for (int i = 0; i < items.Count; i++)
        {
            switch (items.Get(i))
            {
                case PdfString s:
                    ShowString(s.Bytes, i);
                    break;
                case PdfNumber n:
                    // A number moves the next glyph left by n thousandths of the font size.
                    double tx = -n.Value / 1000.0 * State.FontSize * State.HorizontalScaling;
                    TextMatrix = Matrix.Translation(tx, 0).Multiply(TextMatrix);
                    break;
            }
        }
    }

    protected PdfFont ResolveFont(PdfName name)
    {
        var dict = Resources?.GetAsDictionary(PdfName.Font)?.GetAsDictionary(name);
        return dict == null ? PdfFont.Fallback : PdfFont.Load(dict);
    }

    private void ApplyExtGState(PdfName? name)
    {
        if (name == null) return;
        var gs = Resources?.GetAsDictionary(PdfName.ExtGState)?.GetAsDictionary(name);
        if (gs?.GetAsArray(PdfName.Font) is { Count: >= 2 } font && font.GetAsDictionary(0) is { } fontDict)
        {
            State.Font = PdfFont.Load(fontDict);
            State.FontSize = font.GetNumber(1);
        }
    }

    /// <summary>Handles <c>Do</c>: reports an image, or interprets a form XObject in place.</summary>
    protected virtual void DoXObject(PdfName name)
    {
        var stream = Resources?.GetAsDictionary(PdfName.XObject)?.GetAsStream(name);
        if (stream == null) return;
        var subtype = stream.GetAsName(PdfName.Subtype)?.Value;
        if (subtype == "Image")
        {
            Listener.OnImage(new ImageRenderInfo { Ctm = State.Ctm, Name = name, Stream = stream });
            return;
        }
        if (subtype != "Form" || _formDepth >= MaxFormDepth || !_formsInProgress.Add(stream)) return;

        var savedState = State.Clone();
        var savedTm = TextMatrix;
        var savedTlm = TextLineMatrix;
        _formDepth++;
        try
        {
            State.Ctm = Matrix.FromArray(stream.GetAsArray(PdfName.Matrix)).Multiply(State.Ctm);
            ProcessContent(stream.GetDecodedBytes(), stream.GetAsDictionary(PdfName.Resources) ?? Resources);
        }
        finally
        {
            _formDepth--;
            _formsInProgress.Remove(stream);
            State = savedState;
            TextMatrix = savedTm;
            TextLineMatrix = savedTlm;
        }
    }
}
