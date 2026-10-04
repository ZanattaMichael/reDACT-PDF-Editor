using System.Globalization;
using System.Reflection;
using System.Text;

namespace PdfEditor.Core.Pdf.Fonts;

/// <summary>Metrics of one of the standard 14 fonts, read from Adobe's AFM file.</summary>
internal sealed class AfmMetrics
{
    public required string FontName { get; init; }
    public float Ascender { get; init; }
    public float Descender { get; init; }
    public float CapHeight { get; init; }
    public float XHeight { get; init; }
    public bool IsFixedPitch { get; init; }
    public required float[] FontBBox { get; init; }

    /// <summary>Advance width (1000-unit em) by glyph name.</summary>
    public required Dictionary<string, float> Widths { get; init; }

    /// <summary>The font's built-in encoding: code → glyph name.</summary>
    public required string?[] Encoding { get; init; }

    public float AverageWidth => Widths.Count == 0 ? 500 : Widths.Values.Where(w => w > 0).DefaultIfEmpty(500).Average();
}

/// <summary>The standard 14 fonts every PDF reader carries (§9.6.2.2), with their common aliases.</summary>
internal static class StandardFonts
{
    public const string Helvetica = "Helvetica";
    public const string HelveticaBold = "Helvetica-Bold";
    public const string HelveticaOblique = "Helvetica-Oblique";
    public const string HelveticaBoldOblique = "Helvetica-BoldOblique";
    public const string TimesRoman = "Times-Roman";
    public const string TimesBold = "Times-Bold";
    public const string TimesItalic = "Times-Italic";
    public const string TimesBoldItalic = "Times-BoldItalic";
    public const string Courier = "Courier";
    public const string CourierBold = "Courier-Bold";
    public const string CourierOblique = "Courier-Oblique";
    public const string CourierBoldOblique = "Courier-BoldOblique";
    public const string Symbol = "Symbol";
    public const string ZapfDingbats = "ZapfDingbats";

    public static readonly IReadOnlyList<string> Names = new[]
    {
        Helvetica, HelveticaBold, HelveticaOblique, HelveticaBoldOblique,
        TimesRoman, TimesBold, TimesItalic, TimesBoldItalic,
        Courier, CourierBold, CourierOblique, CourierBoldOblique,
        Symbol, ZapfDingbats,
    };

    // The names Acrobat accepts for the standard fonts (PDF Reference 1.7, Appendix H.5, note 5).
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["Arial"] = Helvetica, ["Arial,Bold"] = HelveticaBold, ["Arial,Italic"] = HelveticaOblique,
        ["Arial,BoldItalic"] = HelveticaBoldOblique, ["Arial-Bold"] = HelveticaBold,
        ["Arial-Italic"] = HelveticaOblique, ["Arial-BoldItalic"] = HelveticaBoldOblique,
        ["ArialMT"] = Helvetica, ["Arial-BoldMT"] = HelveticaBold, ["Arial-ItalicMT"] = HelveticaOblique,
        ["Arial-BoldItalicMT"] = HelveticaBoldOblique,
        ["Helvetica,Bold"] = HelveticaBold, ["Helvetica,Italic"] = HelveticaOblique,
        ["Helvetica,BoldItalic"] = HelveticaBoldOblique, ["Helvetica-Italic"] = HelveticaOblique,
        ["Helvetica-BoldItalic"] = HelveticaBoldOblique,
        ["TimesNewRoman"] = TimesRoman, ["TimesNewRoman,Bold"] = TimesBold,
        ["TimesNewRoman,Italic"] = TimesItalic, ["TimesNewRoman,BoldItalic"] = TimesBoldItalic,
        ["TimesNewRomanPS"] = TimesRoman, ["TimesNewRomanPSMT"] = TimesRoman,
        ["TimesNewRomanPS-BoldMT"] = TimesBold, ["TimesNewRomanPS-ItalicMT"] = TimesItalic,
        ["TimesNewRomanPS-BoldItalicMT"] = TimesBoldItalic, ["Times"] = TimesRoman,
        ["Times,Bold"] = TimesBold, ["Times,Italic"] = TimesItalic, ["Times,BoldItalic"] = TimesBoldItalic,
        ["CourierNew"] = Courier, ["CourierNew,Bold"] = CourierBold, ["CourierNew,Italic"] = CourierOblique,
        ["CourierNew,BoldItalic"] = CourierBoldOblique, ["CourierNewPSMT"] = Courier,
        ["CourierNewPS-BoldMT"] = CourierBold, ["CourierNewPS-ItalicMT"] = CourierOblique,
        ["CourierNewPS-BoldItalicMT"] = CourierBoldOblique, ["Courier,Bold"] = CourierBold,
        ["Courier,Italic"] = CourierOblique, ["Courier,BoldItalic"] = CourierBoldOblique,
        ["Courier-Italic"] = CourierOblique, ["Courier-BoldItalic"] = CourierBoldOblique,
        ["Symbol,Bold"] = Symbol, ["Symbol,Italic"] = Symbol, ["Symbol,BoldItalic"] = Symbol,
    };

    private static readonly Dictionary<string, AfmMetrics> Loaded = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    /// <summary>The standard name a base font name stands for (subset tag ignored), or null.</summary>
    public static string? Resolve(string? baseFont)
    {
        if (string.IsNullOrEmpty(baseFont)) return null;
        string name = baseFont.Length > 7 && baseFont[6] == '+' ? baseFont[7..] : baseFont;
        if (Names.Contains(name, StringComparer.Ordinal)) return name;
        return Aliases.TryGetValue(name, out var std) ? std : null;
    }

    /// <summary>Metrics for a standard font (by any of its names), or null.</summary>
    public static AfmMetrics? Get(string? baseFont)
    {
        string? name = Resolve(baseFont);
        if (name == null) return null;
        lock (Gate)
        {
            if (!Loaded.TryGetValue(name, out var metrics))
            {
                metrics = ParseAfm(FontResources.ReadText(name + ".afm"));
                Loaded[name] = metrics;
            }
            return metrics;
        }
    }

    private static AfmMetrics ParseAfm(string text)
    {
        string fontName = "";
        float ascender = 0, descender = 0, cap = 0, x = 0;
        bool fixedPitch = false;
        float[] bbox = { 0, 0, 1000, 1000 };
        var widths = new Dictionary<string, float>(StringComparer.Ordinal);
        var encoding = new string?[256];
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("C ", StringComparison.Ordinal) || line.StartsWith("CH ", StringComparison.Ordinal))
            {
                int code = -1;
                float width = 0;
                string? name = null;
                foreach (var part in line.Split(';'))
                {
                    var tokens = part.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Length < 2) continue;
                    switch (tokens[0])
                    {
                        case "C": code = int.Parse(tokens[1], CultureInfo.InvariantCulture); break;
                        case "WX": width = float.Parse(tokens[1], CultureInfo.InvariantCulture); break;
                        case "N": name = tokens[1]; break;
                    }
                }
                if (name == null) continue;
                widths[name] = width;
                if (code is >= 0 and < 256) encoding[code] = name;
                continue;
            }
            var t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (t[0])
            {
                case "FontName": fontName = t[1]; break;
                case "Ascender": ascender = float.Parse(t[1], CultureInfo.InvariantCulture); break;
                case "Descender": descender = float.Parse(t[1], CultureInfo.InvariantCulture); break;
                case "CapHeight": cap = float.Parse(t[1], CultureInfo.InvariantCulture); break;
                case "XHeight": x = float.Parse(t[1], CultureInfo.InvariantCulture); break;
                case "IsFixedPitch": fixedPitch = t[1] == "true"; break;
                case "FontBBox":
                    bbox = t.Skip(1).Take(4).Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                    break;
            }
        }
        // Symbol and ZapfDingbats declare no Ascender/Descender; their bounding box stands in.
        if (ascender == 0 && descender == 0)
        {
            ascender = bbox[3];
            descender = bbox[1];
        }
        return new AfmMetrics
        {
            FontName = fontName, Ascender = ascender, Descender = descender, CapHeight = cap, XHeight = x,
            IsFixedPitch = fixedPitch, FontBBox = bbox, Widths = widths, Encoding = encoding,
        };
    }
}

/// <summary>Reads the embedded font resources.</summary>
internal static class FontResources
{
    public static string ReadText(string fileName)
    {
        var asm = typeof(FontResources).Assembly;
        using var stream = asm.GetManifestResourceStream("PdfEditor.Core.Fonts." + fileName)
            ?? throw new InvalidOperationException($"The font resource '{fileName}' is missing from the build.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

/// <summary>Glyph names → Unicode, per the Adobe Glyph List and its naming conventions.</summary>
internal static class GlyphList
{
    private static readonly Lazy<Dictionary<string, string>> Agl = new(() => Load("glyphlist.txt"));
    private static readonly Lazy<Dictionary<string, string>> Dingbats = new(() => Load("zapfdingbats.txt"));
    private static readonly Lazy<Dictionary<string, string>> Reverse = new(() =>
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, text) in Agl.Value) map.TryAdd(text, name);
        return map;
    });

    private static Dictionary<string, string> Load(string file)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in FontResources.ReadText(file).Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int semi = line.IndexOf(';');
            if (semi <= 0) continue;
            var sb = new StringBuilder();
            foreach (var hex in line[(semi + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int cp))
                    sb.Append(char.ConvertFromUtf32(cp));
            map[line[..semi]] = sb.ToString();
        }
        return map;
    }

    /// <summary>The text a glyph name stands for, or null when the name means nothing.</summary>
    public static string? ToUnicode(string name, bool dingbats = false)
    {
        if (dingbats && Dingbats.Value.TryGetValue(name, out var d)) return d;
        if (Agl.Value.TryGetValue(name, out var s)) return s;

        // Suffixed variants ("a.sc", "one.oldstyle") mean the base glyph.
        int dot = name.IndexOf('.');
        if (dot > 0) return ToUnicode(name[..dot], dingbats);

        // Ligatures spelled with underscores ("f_f_i").
        if (name.Contains('_'))
        {
            var parts = name.Split('_').Select(p => ToUnicode(p, dingbats)).ToList();
            return parts.All(p => p != null) ? string.Concat(parts) : null;
        }

        // "uniXXXX[XXXX…]": one or more BMP code points.
        if (name.StartsWith("uni", StringComparison.Ordinal) && name.Length >= 7 && (name.Length - 3) % 4 == 0)
        {
            var sb = new StringBuilder();
            for (int i = 3; i < name.Length; i += 4)
            {
                if (!int.TryParse(name.AsSpan(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int cp)
                    || cp is >= 0xD800 and <= 0xDFFF) return null;
                sb.Append((char)cp);
            }
            return sb.ToString();
        }
        // "uXXXX" to "uXXXXXX": one code point.
        if (name.Length is >= 5 and <= 7 && name[0] == 'u'
            && int.TryParse(name.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int u)
            && u <= 0x10FFFF && u is not (>= 0xD800 and <= 0xDFFF))
            return char.ConvertFromUtf32(u);
        return null;
    }

    /// <summary>The glyph name for a character, for looking up widths in an AFM; null when unnamed.</summary>
    public static string? NameOf(string text) => Reverse.Value.TryGetValue(text, out var name) ? name : null;
}

/// <summary>The simple-font encodings of Annex D: code → glyph name.</summary>
internal static class FontEncodings
{
    public static readonly string?[] WinAnsi = BuildWinAnsi();
    public static readonly string?[] MacRoman = BuildMacRoman();
    public static string?[] Standard => StandardFonts.Get(StandardFonts.Helvetica)!.Encoding;

    /// <summary>The named base encoding, or null when the name is not one of the predefined ones.</summary>
    public static string?[]? ByName(string? name) => name switch
    {
        "WinAnsiEncoding" => WinAnsi,
        "MacRomanEncoding" => MacRoman,
        "StandardEncoding" => Standard,
        "MacExpertEncoding" => Standard, // expert sets are vanishingly rare; Standard keeps ASCII readable
        _ => null,
    };

    private static string?[] Ascii(bool quotesingle)
    {
        var e = new string?[256];
        string[] printable =
        {
            "space", "exclam", "quotedbl", "numbersign", "dollar", "percent", "ampersand",
            quotesingle ? "quotesingle" : "quoteright", "parenleft", "parenright", "asterisk", "plus",
            "comma", "hyphen", "period", "slash", "zero", "one", "two", "three", "four", "five", "six",
            "seven", "eight", "nine", "colon", "semicolon", "less", "equal", "greater", "question", "at",
        };
        for (int i = 0; i < printable.Length; i++) e[0x20 + i] = printable[i];
        for (int i = 0; i < 26; i++)
        {
            e[0x41 + i] = ((char)('A' + i)).ToString();
            e[0x61 + i] = ((char)('a' + i)).ToString();
        }
        e[0x5B] = "bracketleft"; e[0x5C] = "backslash"; e[0x5D] = "bracketright"; e[0x5E] = "asciicircum";
        e[0x5F] = "underscore"; e[0x60] = quotesingle ? "grave" : "quoteleft"; e[0x7B] = "braceleft";
        e[0x7C] = "bar"; e[0x7D] = "braceright"; e[0x7E] = "asciitilde";
        return e;
    }

    private static string?[] BuildWinAnsi()
    {
        var e = Ascii(quotesingle: true);
        string[] high =
        {
            "Euro", "bullet", "quotesinglbase", "florin", "quotedblbase", "ellipsis", "dagger", "daggerdbl",
            "circumflex", "perthousand", "Scaron", "guilsinglleft", "OE", "bullet", "Zcaron", "bullet",
            "bullet", "quoteleft", "quoteright", "quotedblleft", "quotedblright", "bullet", "endash", "emdash",
            "tilde", "trademark", "scaron", "guilsinglright", "oe", "bullet", "zcaron", "Ydieresis",
            "space", "exclamdown", "cent", "sterling", "currency", "yen", "brokenbar", "section",
            "dieresis", "copyright", "ordfeminine", "guillemotleft", "logicalnot", "hyphen", "registered", "macron",
            "degree", "plusminus", "twosuperior", "threesuperior", "acute", "mu", "paragraph", "periodcentered",
            "cedilla", "onesuperior", "ordmasculine", "guillemotright", "onequarter", "onehalf", "threequarters", "questiondown",
            "Agrave", "Aacute", "Acircumflex", "Atilde", "Adieresis", "Aring", "AE", "Ccedilla",
            "Egrave", "Eacute", "Ecircumflex", "Edieresis", "Igrave", "Iacute", "Icircumflex", "Idieresis",
            "Eth", "Ntilde", "Ograve", "Oacute", "Ocircumflex", "Otilde", "Odieresis", "multiply",
            "Oslash", "Ugrave", "Uacute", "Ucircumflex", "Udieresis", "Yacute", "Thorn", "germandbls",
            "agrave", "aacute", "acircumflex", "atilde", "adieresis", "aring", "ae", "ccedilla",
            "egrave", "eacute", "ecircumflex", "edieresis", "igrave", "iacute", "icircumflex", "idieresis",
            "eth", "ntilde", "ograve", "oacute", "ocircumflex", "otilde", "odieresis", "divide",
            "oslash", "ugrave", "uacute", "ucircumflex", "udieresis", "yacute", "thorn", "ydieresis",
        };
        for (int i = 0; i < high.Length; i++) e[0x80 + i] = high[i];
        e[0x7F] = "bullet";
        return e;
    }

    private static string?[] BuildMacRoman()
    {
        var e = Ascii(quotesingle: true);
        string?[] high =
        {
            "Adieresis", "Aring", "Ccedilla", "Eacute", "Ntilde", "Odieresis", "Udieresis", "aacute",
            "agrave", "acircumflex", "adieresis", "atilde", "aring", "ccedilla", "eacute", "egrave",
            "ecircumflex", "edieresis", "iacute", "igrave", "icircumflex", "idieresis", "ntilde", "oacute",
            "ograve", "ocircumflex", "odieresis", "otilde", "uacute", "ugrave", "ucircumflex", "udieresis",
            "dagger", "degree", "cent", "sterling", "section", "bullet", "paragraph", "germandbls",
            "registered", "copyright", "trademark", "acute", "dieresis", "notequal", "AE", "Oslash",
            "infinity", "plusminus", "lessequal", "greaterequal", "yen", "mu", "partialdiff", "summation",
            "product", "pi", "integral", "ordfeminine", "ordmasculine", "Omega", "ae", "oslash",
            "questiondown", "exclamdown", "logicalnot", "radical", "florin", "approxequal", "Delta", "guillemotleft",
            "guillemotright", "ellipsis", "space", "Agrave", "Atilde", "Otilde", "OE", "oe",
            "endash", "emdash", "quotedblleft", "quotedblright", "quoteleft", "quoteright", "divide", "lozenge",
            "ydieresis", "Ydieresis", "fraction", "currency", "guilsinglleft", "guilsinglright", "fi", "fl",
            "daggerdbl", "periodcentered", "quotesinglbase", "quotedblbase", "perthousand", "Acircumflex", "Ecircumflex", "Aacute",
            "Edieresis", "Egrave", "Iacute", "Icircumflex", "Idieresis", "Igrave", "Oacute", "Ocircumflex",
            null, "Ograve", "Uacute", "Ucircumflex", "Ugrave", "dotlessi", "circumflex", "tilde",
            "macron", "breve", "dotaccent", "ring", "cedilla", "hungarumlaut", "ogonek", "caron",
        };
        for (int i = 0; i < high.Length; i++) e[0x80 + i] = high[i];
        return e;
    }
}
