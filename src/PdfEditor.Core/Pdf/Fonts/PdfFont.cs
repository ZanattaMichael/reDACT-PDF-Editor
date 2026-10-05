using System.Runtime.CompilerServices;
using System.Text;

namespace PdfEditor.Core.Pdf.Fonts;

/// <summary>
/// A font as content streams use it: how a string's bytes split into character codes, how wide
/// each glyph is, what text it stands for, and the vertical metrics that bound it. Simple fonts
/// (Type 1, TrueType, Type 3) and composite (Type 0) fonts are both covered; glyph outlines are
/// not — the engine measures and edits text, it does not rasterise it.
/// </summary>
internal sealed class PdfFont
{
    private static readonly ConditionalWeakTable<PdfDictionary, PdfFont> Cache = new();

    private readonly double[]? _simpleWidths;
    private readonly string?[] _glyphNames = new string?[256];
    private readonly CMap? _toUnicode;
    private readonly CMap? _encoding;
    private readonly Dictionary<int, double>? _cidWidths;
    private readonly double _defaultCidWidth = 1000;
    private readonly bool _symbolic;
    private readonly bool _dingbats;
    private readonly AfmMetrics? _standard;
    private Dictionary<char, byte>? _winAnsiReverse;

    /// <summary>The font dictionary (null for the internal fallback font).</summary>
    public PdfDictionary? Dictionary { get; }

    /// <summary>The PostScript name, subset tag included (e.g. <c>ABCDEF+Calibri</c>).</summary>
    public string BaseFont { get; }

    public bool IsComposite { get; }

    public bool IsType3 { get; }

    /// <summary>Glyph space → text space. 1/1000 for everything but Type 3.</summary>
    public Matrix FontMatrix { get; } = new(0.001, 0, 0, 0.001, 0, 0);

    /// <summary>Ascender in 1000-unit em, as the font program or descriptor declares it.</summary>
    public float Ascent { get; }

    /// <summary>Descender (negative) in 1000-unit em.</summary>
    public float Descent { get; }

    /// <summary>Whether this is one of the standard 14 (or an alias the reader substitutes for one).</summary>
    public bool IsStandard => _standard != null;

    private PdfFont(PdfDictionary? dict, string baseFont)
    {
        Dictionary = dict;
        BaseFont = baseFont;
    }

    /// <summary>The font for a font dictionary, parsed once and cached.</summary>
    public static PdfFont Load(PdfDictionary dict) => Cache.GetValue(dict, d => Create(d));

    /// <summary>
    /// The font used when a content stream selects a font its resources do not define. Viewers
    /// substitute a default face and draw the text anyway, so the engine must still place it —
    /// text it could not locate is text it could not redact.
    /// </summary>
    public static PdfFont Fallback { get; } = Standard(StandardFonts.Helvetica, withDictionary: false);

    /// <summary>A standard-14 font for stamping new text: a Type 1 font in WinAnsiEncoding.</summary>
    public static PdfFont Standard(string name, bool withDictionary = true)
    {
        PdfDictionary? dict = null;
        if (withDictionary)
        {
            dict = new PdfDictionary();
            dict.Put(PdfName.Type, PdfName.Font);
            dict.Put(PdfName.Subtype, PdfName.Of("Type1"));
            dict.Put(PdfName.BaseFont, PdfName.Of(name));
            if (name is not (StandardFonts.Symbol or StandardFonts.ZapfDingbats))
                dict.Put(PdfName.Encoding, PdfName.WinAnsiEncoding);
            return Load(dict);
        }
        return Init(null, name);
    }

    private static PdfFont Create(PdfDictionary dict)
    {
        string baseFont = dict.GetAsName(PdfName.BaseFont)?.Value ?? "";
        string subtype = dict.GetAsName(PdfName.Subtype)?.Value ?? "Type1";
        if (subtype == "Type0") return CreateComposite(dict, baseFont);
        return Init(dict, baseFont);
    }

    // ------------------------------------------------------------------ simple fonts

    private static PdfFont Init(PdfDictionary? dict, string baseFont)
    {
        string subtype = dict?.GetAsName(PdfName.Subtype)?.Value ?? "Type1";
        bool type3 = subtype == "Type3";
        var descriptor = dict?.GetAsDictionary(PdfName.FontDescriptor);
        var standard = type3 ? null : StandardFonts.Get(baseFont);
        int flags = descriptor?.GetAsInt(PdfName.Of("Flags")) ?? 0;
        bool symbolicFlag = (flags & 4) != 0 && (flags & 32) == 0;
        string? stdName = StandardFonts.Resolve(baseFont);
        bool dingbats = stdName == StandardFonts.ZapfDingbats;
        bool symbolStd = stdName is StandardFonts.Symbol or StandardFonts.ZapfDingbats;

        var fontMatrix = type3 ? Matrix.FromArray(dict!.GetAsArray(PdfName.FontMatrix)) : new Matrix(0.001, 0, 0, 0.001, 0, 0);
        if (type3 && dict!.GetAsArray(PdfName.FontMatrix) == null) fontMatrix = new Matrix(0.001, 0, 0, 0.001, 0, 0);

        var (ascent, descent) = VerticalMetrics(descriptor, standard, type3 ? dict : null, fontMatrix);

        var font = new PdfFont(dict, baseFont, standard, symbolicFlag && !symbolStd, dingbats, fontMatrix, ascent, descent,
            type3, ReadToUnicode(dict));
        font.BuildSimpleEncoding(dict, standard, symbolStd);
        font.BuildSimpleWidths(dict, descriptor, standard);
        return font;
    }

    private PdfFont(PdfDictionary? dict, string baseFont, AfmMetrics? standard, bool symbolic, bool dingbats,
        Matrix fontMatrix, float ascent, float descent, bool type3, CMap? toUnicode) : this(dict, baseFont)
    {
        _standard = standard;
        _symbolic = symbolic;
        _dingbats = dingbats;
        FontMatrix = fontMatrix;
        Ascent = ascent;
        Descent = descent;
        IsType3 = type3;
        _toUnicode = toUnicode;
        _simpleWidths = new double[256];
    }

    private void BuildSimpleEncoding(PdfDictionary? dict, AfmMetrics? standard, bool symbolStd)
    {
        var encoding = dict?.Get(PdfName.Encoding);
        if (BaseEncoding(encoding, standard, symbolStd) is { } baseEncoding) Array.Copy(baseEncoding, _glyphNames, 256);
        if (encoding is PdfDictionary diffDict && diffDict.GetAsArray(PdfName.Differences) is { } differences)
            ApplyDifferences(differences);
    }

    private string?[]? BaseEncoding(PdfObject? encoding, AfmMetrics? standard, bool symbolStd)
    {
        var named = encoding switch
        {
            PdfName n => FontEncodings.ByName(n.Value),
            PdfDictionary ed => FontEncodings.ByName(ed.GetAsName(PdfName.BaseEncoding)?.Value),
            _ => null,
        };
        if (named != null) return named;
        if (symbolStd && standard != null) return standard.Encoding; // Symbol, ZapfDingbats: built in
        if (!IsType3 && !_symbolic) return FontEncodings.Standard;
        return null;
    }

    private void ApplyDifferences(PdfArray differences)
    {
        int code = 0;
        foreach (var item in differences)
        {
            if (item is PdfNumber num) code = num.IntValue();
            else if (item is PdfName glyph)
            {
                if (code is >= 0 and < 256) _glyphNames[code] = glyph.Value;
                code++;
            }
        }
    }

    private void BuildSimpleWidths(PdfDictionary? dict, PdfDictionary? descriptor, AfmMetrics? standard)
    {
        var widths = dict?.GetAsArray(PdfName.Widths);
        double missing = descriptor?.GetAsDouble(PdfName.MissingWidth) ?? 0;
        if (widths != null && widths.Count > 0)
            ReadWidthsArray(widths, dict!.GetAsInt(PdfName.FirstChar) ?? 0, missing);
        else
            EstimateWidths(standard, missing);
    }

    private void ReadWidthsArray(PdfArray widths, int first, double missing)
    {
        for (int code = 0; code < 256; code++)
        {
            int i = code - first;
            _simpleWidths![code] = i >= 0 && i < widths.Count ? widths.GetNumber(i, missing) : missing;
        }
    }

    private void EstimateWidths(AfmMetrics? standard, double missing)
    {
        // No /Widths: the standard fonts' metrics are known; anything else is measured as
        // Helvetica rather than as zero-width, because a zero-width glyph has no box and so
        // could never be found under a redaction region.
        var metrics = standard ?? StandardFonts.Get(StandardFonts.Helvetica)!;
        for (int code = 0; code < 256; code++)
        {
            string? name = _glyphNames[code];
            if (name != null && metrics.Widths.TryGetValue(name, out float w)) _simpleWidths![code] = w;
            else if (missing > 0) _simpleWidths![code] = missing;
            else _simpleWidths![code] = standard != null ? 0 : metrics.AverageWidth;
        }
    }

    // ------------------------------------------------------------------ composite fonts

    private PdfFont(PdfDictionary dict, string baseFont, CMap encoding, CMap? toUnicode, Dictionary<int, double> widths,
        double defaultWidth, float ascent, float descent) : this(dict, baseFont)
    {
        IsComposite = true;
        _encoding = encoding;
        _toUnicode = toUnicode;
        _cidWidths = widths;
        _defaultCidWidth = defaultWidth;
        Ascent = ascent;
        Descent = descent;
    }

    private static PdfFont CreateComposite(PdfDictionary dict, string baseFont)
    {
        CMap encoding = dict.Get(PdfName.Encoding) switch
        {
            PdfName { Value: "Identity-H" or "Identity-V" } => CMap.Identity(),
            PdfName name when name.Value.Contains("UCS2", StringComparison.Ordinal) || name.Value.Contains("UTF16", StringComparison.Ordinal) => CMap.Unicode(),
            PdfStream s => ParseCMap(s) is { HasCodespace: true } parsed ? parsed : CMap.Identity(),
            _ => CMap.Identity(),
        };
        var descendant = dict.GetAsArray(PdfName.DescendantFonts)?.GetAsDictionary(0);
        var widths = new Dictionary<int, double>();
        double dw = descendant?.GetAsDouble(PdfName.DW) ?? 1000;
        if (descendant?.GetAsArray(PdfName.W) is { } w) ReadCidWidths(w, widths);
        var descriptor = descendant?.GetAsDictionary(PdfName.FontDescriptor);
        var (ascent, descent) = VerticalMetrics(descriptor, null, null, Matrix.Identity);
        return new PdfFont(dict, baseFont, encoding, ReadToUnicode(dict), widths, dw, ascent, descent);
    }

    private static void ReadCidWidths(PdfArray w, Dictionary<int, double> widths)
    {
        int i = 0;
        int budget = 1 << 20;
        while (i < w.Count && budget > 0)
        {
            if (w.GetAsNumber(i) is not { } first) { i++; continue; }
            int used = ReadCidWidthEntry(w, i, first.IntValue(), widths, ref budget);
            if (used == 0) break;
            i += used;
        }
    }

    /// <summary>
    /// Reads the /W entry at <paramref name="i"/>, either <c>c [w1 w2 …]</c> or
    /// <c>cFirst cLast w</c>. Returns how many array items it took, or 0 when it is malformed.
    /// </summary>
    private static int ReadCidWidthEntry(PdfArray w, int i, int start, Dictionary<int, double> widths, ref int budget)
    {
        if (w.Get(i + 1) is PdfArray list)
        {
            for (int k = 0; k < list.Count && budget-- > 0; k++) widths[start + k] = list.GetNumber(k);
            return 2;
        }
        if (w.GetAsNumber(i + 1) is { } lastNum && w.GetAsNumber(i + 2) is { } width)
        {
            int last = lastNum.IntValue();
            for (int cid = start; cid <= last && budget-- > 0; cid++) widths[cid] = width.Value;
            return 3;
        }
        return 0;
    }

    // ------------------------------------------------------------------ shared helpers

    private static CMap? ReadToUnicode(PdfDictionary? dict) =>
        dict?.GetAsStream(PdfName.ToUnicode) is { } s ? ParseCMap(s) : null;

    private static CMap? ParseCMap(PdfStream stream)
    {
        try
        {
            return CMap.Parse(stream.GetDecodedBytes());
        }
        catch (PdfFormatException)
        {
            return null; // an unreadable CMap costs text extraction, not the document
        }
    }

    /// <summary>
    /// Ascender and descender in 1000-unit em: the descriptor's when it gives them, the standard
    /// font's AFM when it is one, the font bounding box after that, and a typical 800/-200
    /// otherwise — never zero, because zero-height glyphs have no box to redact.
    /// </summary>
    private static (float Ascent, float Descent) VerticalMetrics(PdfDictionary? descriptor, AfmMetrics? standard,
        PdfDictionary? type3, Matrix fontMatrix)
    {
        float ascent = (float)(descriptor?.GetAsDouble(PdfName.Ascent) ?? 0);
        float descent = (float)(descriptor?.GetAsDouble(PdfName.Descent) ?? 0);
        if (ascent != 0 || descent != 0) return (ascent, descent);
        if (standard != null) return (standard.Ascender, standard.Descender);
        var bbox = descriptor?.GetAsArray(PdfName.FontBBox) ?? type3?.GetAsArray(PdfName.FontBBox);
        if (bbox is { Count: 4 })
        {
            double scale = type3 != null ? fontMatrix.D * 1000 : 1;
            float top = (float)(bbox.GetNumber(3) * scale), bottom = (float)(bbox.GetNumber(1) * scale);
            if (top > bottom) return (top, Math.Min(0, bottom));
        }
        return (800, -200);
    }

    // ------------------------------------------------------------------ decoding

    /// <summary>Splits a shown string into character codes: (code, offset, length in bytes).</summary>
    public IEnumerable<(int Code, int Offset, int Length)> Codes(byte[] bytes)
    {
        if (!IsComposite)
        {
            for (int i = 0; i < bytes.Length; i++) yield return (bytes[i], i, 1);
            yield break;
        }
        int offset = 0;
        while (offset < bytes.Length)
        {
            var (code, length) = _encoding!.ReadCode(bytes, offset);
            yield return (code, offset, length);
            offset += length;
        }
    }

    /// <summary>A glyph's advance in glyph space (1000 per em for every font type but Type 3).</summary>
    public double GlyphWidth(int code)
    {
        if (IsComposite)
        {
            int cid = _encoding!.ToCid(code);
            return _cidWidths!.TryGetValue(cid, out double w) ? w : _defaultCidWidth;
        }
        return code is >= 0 and < 256 ? _simpleWidths![code] : 0;
    }

    /// <summary>The text a code stands for; empty when the font gives no way to know.</summary>
    public string Unicode(int code)
    {
        if (_toUnicode?.ToUnicode(code) is { } mapped) return mapped;
        if (IsComposite) return _encoding!.IsUnicode ? _encoding.ToUnicode(code) ?? "" : "";
        if (code is < 0 or > 255) return "";
        if (_glyphNames[code] is { } name && GlyphList.ToUnicode(name, _dingbats) is { } text) return text;
        if (_symbolic || IsType3) return code >= 32 ? ((char)code).ToString() : "";
        return "";
    }

    /// <summary>The advance of the font's space glyph in glyph space, or its average width when it has none.</summary>
    public double SpaceWidth()
    {
        if (IsComposite)
        {
            if (_toUnicode?.CodeFor(" ") is int code) return GlyphWidth(code);
            return _cidWidths!.Count > 0 ? _cidWidths.Values.Where(v => v > 0).DefaultIfEmpty(_defaultCidWidth).Average() : _defaultCidWidth;
        }
        for (int c = 0; c < 256; c++)
            if (_glyphNames[c] == "space" && _simpleWidths![c] > 0) return _simpleWidths[c];
        if (_toUnicode?.CodeFor(" ") is int sc && sc < 256 && _simpleWidths![sc] > 0) return _simpleWidths[sc];
        var nonZero = _simpleWidths!.Where(w => w > 0).ToList();
        return nonZero.Count > 0 ? nonZero.Average() : 0;
    }

    // ------------------------------------------------------------------ encoding new text

    /// <summary>
    /// Encodes <paramref name="text"/> for showing in this (standard, WinAnsi) font. Characters
    /// WinAnsi cannot represent become '?', so what is drawn always matches what is measured.
    /// </summary>
    public byte[] Encode(string text)
    {
        _winAnsiReverse ??= BuildWinAnsiReverse();
        var bytes = new byte[text.Length];
        for (int i = 0; i < text.Length; i++)
            bytes[i] = _winAnsiReverse.TryGetValue(text[i], out byte b) ? b : (byte)'?';
        return bytes;
    }

    private static Dictionary<char, byte> BuildWinAnsiReverse()
    {
        var map = new Dictionary<char, byte>();
        for (int code = 0x20; code < 256; code++)
        {
            if (FontEncodings.WinAnsi[code] is not { } name) continue;
            if (GlyphList.ToUnicode(name) is { Length: 1 } s) map.TryAdd(s[0], (byte)code);
        }
        map[' '] = 0xA0;
        map['­'] = 0xAD;
        return map;
    }

    /// <summary>The width of <paramref name="text"/> set at <paramref name="size"/> points, as it will be drawn.</summary>
    public double MeasureText(string text, double size)
    {
        double total = 0;
        foreach (byte b in Encode(text)) total += GlyphWidth(b);
        return total * FontMatrix.A * size;
    }

    public override string ToString() => BaseFont;
}
