using System.Globalization;
using System.Text;

namespace PdfEditor.Core.Pdf;

/// <summary>One operator of a content stream with its operands (§7.8.2).</summary>
internal sealed class ContentOperation
{
    public ContentOperation(string op, List<PdfObject> operands)
    {
        Operator = op;
        Operands = operands;
    }

    public string Operator { get; }
    public List<PdfObject> Operands { get; }

    /// <summary>For an inline image (<c>BI … ID … EI</c>): its dictionary, with abbreviations as written.</summary>
    public PdfDictionary? InlineImageDictionary { get; init; }

    /// <summary>For an inline image: the raw bytes between ID and EI.</summary>
    public byte[]? InlineImageData { get; init; }

    public override string ToString() => Operator;
}

/// <summary>
/// Splits a content stream into operations. Unknown operators are kept — the content editor
/// copies whatever it does not need to change through unaltered, so nothing it does not
/// understand is lost. The parser never throws on malformed content; a token that cannot start
/// an operand becomes an operator of its own.
/// </summary>
internal static class ContentParser
{
    /// <summary>Operand stacks longer than this are garbage, not content; the oldest are dropped.</summary>
    private const int MaxOperands = 4096;

    public static List<ContentOperation> Parse(byte[] content)
    {
        var ops = new List<ContentOperation>();
        var lexer = new PdfLexer(content);
        var parser = new PdfObjectParser(lexer, document: null);
        var operands = new List<PdfObject>();
        while (lexer.Next())
        {
            if (lexer.TokenType == PdfTokenType.Keyword && lexer.Text is not ("true" or "false" or "null"))
            {
                string op = lexer.Text;
                if (op == "BI")
                {
                    ops.Add(ReadInlineImage(lexer, parser));
                    operands = new List<PdfObject>();
                    continue;
                }
                ops.Add(new ContentOperation(op, operands));
                operands = new List<PdfObject>();
                continue;
            }
            if (lexer.TokenType is PdfTokenType.ArrayEnd or PdfTokenType.DictEnd)
            {
                // A stray closer: keep it as an operator so it round-trips, rather than vanish.
                ops.Add(new ContentOperation(lexer.TokenType == PdfTokenType.ArrayEnd ? "]" : ">>", operands));
                operands = new List<PdfObject>();
                continue;
            }
            var operand = parser.ParseFromCurrent(0);
            if (operand == null) continue;
            if (operands.Count >= MaxOperands) operands.RemoveAt(0);
            operands.Add(operand);
        }
        return ops;
    }

    /// <summary>Reads <c>BI &lt;dict entries&gt; ID &lt;data&gt; EI</c>.</summary>
    private static ContentOperation ReadInlineImage(PdfLexer lexer, PdfObjectParser parser)
    {
        var dict = new PdfDictionary();
        while (lexer.Next())
        {
            if (lexer.TokenType == PdfTokenType.Keyword && lexer.Text == "ID") break;
            if (lexer.TokenType != PdfTokenType.Name) continue;
            var key = PdfName.Of(lexer.Text);
            if (!lexer.Next()) break;
            if (lexer.TokenType == PdfTokenType.Keyword && lexer.Text == "ID")
            {
                dict.Put(key, PdfNull.Instance);
                break;
            }
            var value = parser.ParseFromCurrent(0);
            if (value != null) dict.Put(key, value);
        }

        byte[] data = lexer.Data;
        int start = lexer.Position;
        if (start < lexer.End && PdfLexer.IsWhitespace(data[start])) start++; // the single separator after ID
        int end = FindInlineImageEnd(dict, data, start, lexer.End, out int afterEi);
        lexer.Position = afterEi;
        return new ContentOperation("BI", new List<PdfObject>())
        {
            InlineImageDictionary = dict,
            InlineImageData = data.AsSpan(start, Math.Max(0, end - start)).ToArray(),
        };
    }

    /// <summary>
    /// Finds where an inline image's data ends. When the data is unfiltered its length follows from
    /// the dictionary; otherwise the first "EI" that stands as a token and is followed by something
    /// that parses as content is taken — the heuristic every reader uses, since EI may also occur
    /// inside compressed data.
    /// </summary>
    private static int FindInlineImageEnd(PdfDictionary dict, byte[] data, int start, int end, out int afterEi)
    {
        int? expected = ExpectedLength(dict);
        if (expected is int len && start + len <= end)
        {
            int p = start + len;
            while (p < end && PdfLexer.IsWhitespace(data[p])) p++;
            if (p + 2 <= end && data[p] == 'E' && data[p + 1] == 'I' && (p + 2 == end || !PdfLexer.IsRegular(data[p + 2])))
            {
                afterEi = p + 2;
                return start + len;
            }
        }

        for (int p = start; p + 2 <= end; p++)
        {
            if (data[p] != 'E' || data[p + 1] != 'I') continue;
            bool before = p == start || PdfLexer.IsWhitespace(data[p - 1]);
            bool after = p + 2 == end || PdfLexer.IsWhitespace(data[p + 2]);
            if (!before || !after || !LooksLikeContentFollows(data, p + 2, end)) continue;
            afterEi = p + 2;
            int dataEnd = p;
            if (dataEnd > start && PdfLexer.IsWhitespace(data[dataEnd - 1])) dataEnd--;
            return dataEnd;
        }
        afterEi = end;
        return end;
    }

    private static bool LooksLikeContentFollows(byte[] data, int from, int end)
    {
        var lexer = new PdfLexer(data, from, Math.Min(end, from + 64));
        for (int i = 0; i < 3; i++)
        {
            if (!lexer.Next()) return true;
            if (lexer.TokenType == PdfTokenType.Keyword)
                return lexer.Text.Length <= 3 && lexer.Text.All(c => c < 128 && (char.IsLetter(c) || c is '*' or '\'' or '"' or '0' or '1'));
            if (lexer.Text.Any(c => c > 126)) return false;
        }
        return true;
    }

    private static int? ExpectedLength(PdfDictionary dict)
    {
        if (dict.ContainsKey(PdfName.Of("F")) || dict.ContainsKey(PdfName.Filter)) return null;
        int w = (dict.Get(PdfName.Of("W")) ?? dict.Get(PdfName.Width)) is PdfNumber wn ? wn.IntValue() : -1;
        int h = (dict.Get(PdfName.Of("H")) ?? dict.Get(PdfName.Height)) is PdfNumber hn ? hn.IntValue() : -1;
        if (w <= 0 || h <= 0) return null;
        bool mask = (dict.Get(PdfName.Of("IM")) ?? dict.Get(PdfName.ImageMask)) is PdfBoolean { Value: true };
        int bpc = mask ? 1 : (dict.Get(PdfName.Of("BPC")) ?? dict.Get(PdfName.BitsPerComponent)) is PdfNumber b ? b.IntValue() : 8;
        int colors = 1;
        if (!mask)
        {
            var cs = dict.Get(PdfName.Of("CS")) ?? dict.Get(PdfName.ColorSpace);
            colors = cs switch
            {
                PdfName n when n.Value is "RGB" or "DeviceRGB" or "CalRGB" => 3,
                PdfName n when n.Value is "CMYK" or "DeviceCMYK" => 4,
                PdfName n when n.Value is "G" or "DeviceGray" or "CalGray" or "I" or "Indexed" => 1,
                PdfArray a when a.GetAsName(0)?.Value is "I" or "Indexed" => 1,
                null => 1,
                _ => -1,
            };
        }
        if (colors < 0 || bpc is not (1 or 2 or 4 or 8 or 16)) return null;
        long length = (long)((w * colors * bpc + 7) / 8) * h;
        return length > int.MaxValue ? null : (int)length;
    }
}

/// <summary>Serialises content operations and the direct objects they carry.</summary>
internal static class ContentWriter
{
    public static byte[] Write(IEnumerable<ContentOperation> ops)
    {
        using var output = new MemoryStream();
        foreach (var op in ops) WriteOperation(output, op);
        return output.ToArray();
    }

    public static void WriteOperation(Stream output, ContentOperation op)
    {
        if (op.InlineImageDictionary != null)
        {
            WriteInlineImage(output, op.InlineImageDictionary, op.InlineImageData ?? Array.Empty<byte>());
            return;
        }
        foreach (var operand in op.Operands)
        {
            WriteObject(output, operand);
            output.WriteByte((byte)' ');
        }
        WriteAscii(output, op.Operator);
        output.WriteByte((byte)'\n');
    }

    public static void WriteInlineImage(Stream output, PdfDictionary dict, byte[] data)
    {
        WriteAscii(output, "BI\n");
        foreach (var key in dict.Keys)
        {
            if (key.Equals(PdfName.Length)) continue;
            WriteObject(output, PdfName.Of(key.Value));
            output.WriteByte((byte)' ');
            WriteObject(output, dict.GetRaw(key)!);
            output.WriteByte((byte)'\n');
        }
        WriteAscii(output, "ID\n");
        output.Write(data, 0, data.Length);
        WriteAscii(output, "\nEI\n");
    }

    public static void WriteObject(Stream output, PdfObject obj, int depth = 0)
    {
        if (depth > PdfObjectParser.MaxNesting) return;
        switch (obj)
        {
            case PdfNull:
                WriteAscii(output, "null");
                break;
            case PdfBoolean b:
                WriteAscii(output, b.Value ? "true" : "false");
                break;
            case PdfNumber n:
                WriteAscii(output, n.IsInteger ? n.LongValue().ToString(CultureInfo.InvariantCulture) : PdfNumber.Format(n.Value));
                break;
            case PdfString s:
                WriteString(output, s.Bytes, s.IsHex);
                break;
            case PdfName name:
                output.WriteByte((byte)'/');
                foreach (byte b in Encoding.UTF8.GetBytes(name.Value))
                {
                    if (b < 0x21 || b > 0x7E || b == '#' || PdfLexer.IsDelimiter(b))
                        WriteAscii(output, "#" + b.ToString("X2", CultureInfo.InvariantCulture));
                    else
                        output.WriteByte(b);
                }
                break;
            case PdfArray a:
                output.WriteByte((byte)'[');
                for (int i = 0; i < a.Count; i++)
                {
                    if (i > 0) output.WriteByte((byte)' ');
                    WriteObject(output, a.GetRaw(i), depth + 1);
                }
                output.WriteByte((byte)']');
                break;
            case PdfDictionary d:
                WriteAscii(output, "<<");
                foreach (var key in d.Keys)
                {
                    WriteObject(output, key, depth + 1);
                    output.WriteByte((byte)' ');
                    WriteObject(output, d.GetRaw(key)!, depth + 1);
                }
                WriteAscii(output, ">>");
                break;
            case PdfReference r:
                // Content streams cannot hold references; a resolved value is the only sane output.
                WriteObject(output, r.Resolve(), depth + 1);
                break;
        }
    }

    public static void WriteString(Stream output, byte[] bytes, bool hex)
    {
        if (hex)
        {
            output.WriteByte((byte)'<');
            WriteAscii(output, Convert.ToHexString(bytes));
            output.WriteByte((byte)'>');
            return;
        }
        output.WriteByte((byte)'(');
        foreach (byte b in bytes)
        {
            switch (b)
            {
                case (byte)'(':
                case (byte)')':
                case (byte)'\\':
                    output.WriteByte((byte)'\\');
                    output.WriteByte(b);
                    break;
                case (byte)'\r':
                    WriteAscii(output, "\\r");
                    break;
                case (byte)'\n':
                    WriteAscii(output, "\\n");
                    break;
                default:
                    output.WriteByte(b);
                    break;
            }
        }
        output.WriteByte((byte)')');
    }

    private static void WriteAscii(Stream output, string text)
    {
        foreach (char c in text) output.WriteByte((byte)c);
    }
}

/// <summary>
/// Builds a content stream operator by operator, the way a drawing API would. Numbers are written
/// in invariant culture with trailing zeros trimmed.
/// </summary>
internal sealed class ContentBuilder
{
    private readonly MemoryStream _out = new();

    public byte[] ToArray() => _out.ToArray();

    public bool IsEmpty => _out.Length == 0;

    private ContentBuilder Op(string op, params double[] operands)
    {
        foreach (double d in operands)
        {
            Raw(PdfNumber.Format(d));
            _out.WriteByte((byte)' ');
        }
        Raw(op);
        _out.WriteByte((byte)'\n');
        return this;
    }

    /// <summary>Writes text verbatim (must be valid content syntax).</summary>
    public ContentBuilder Raw(string text)
    {
        foreach (char c in text) _out.WriteByte((byte)c);
        return this;
    }

    public ContentBuilder SaveState() => Op("q");
    public ContentBuilder RestoreState() => Op("Q");
    public ContentBuilder Transform(Matrix m) => Op("cm", m.A, m.B, m.C, m.D, m.E, m.F);
    public ContentBuilder Transform(double a, double b, double c, double d, double e, double f) => Op("cm", a, b, c, d, e, f);

    public ContentBuilder Rectangle(double x, double y, double w, double h) => Op("re", x, y, w, h);
    public ContentBuilder MoveTo(double x, double y) => Op("m", x, y);
    public ContentBuilder LineTo(double x, double y) => Op("l", x, y);
    public ContentBuilder CurveTo(double x1, double y1, double x2, double y2, double x3, double y3) => Op("c", x1, y1, x2, y2, x3, y3);
    public ContentBuilder ClosePath() => Op("h");
    public ContentBuilder Fill() => Op("f");
    public ContentBuilder Stroke() => Op("S");
    public ContentBuilder FillStroke() => Op("B");
    public ContentBuilder EndPath() => Op("n");
    public ContentBuilder Clip() => Op("W");

    /// <summary>A circle as four Bézier arcs (the usual 0.5523 control-point approximation).</summary>
    public ContentBuilder Circle(double x, double y, double r)
    {
        const double k = 0.5523;
        MoveTo(x + r, y);
        CurveTo(x + r, y + r * k, x + r * k, y + r, x, y + r);
        CurveTo(x - r * k, y + r, x - r, y + r * k, x - r, y);
        CurveTo(x - r, y - r * k, x - r * k, y - r, x, y - r);
        CurveTo(x + r * k, y - r, x + r, y - r * k, x + r, y);
        return this;
    }

    public ContentBuilder LineWidth(double w) => Op("w", w);
    public ContentBuilder LineCap(int style) => Op("J", style);
    public ContentBuilder LineJoin(int style) => Op("j", style);

    public ContentBuilder FillRgb(double r, double g, double b) => Op("rg", r, g, b);
    public ContentBuilder StrokeRgb(double r, double g, double b) => Op("RG", r, g, b);
    public ContentBuilder FillGray(double g) => Op("g", g);
    public ContentBuilder StrokeGray(double g) => Op("G", g);
    public ContentBuilder FillColor(PdfColor c) => c.IsGray ? FillGray(c.R) : FillRgb(c.R, c.G, c.B);
    public ContentBuilder StrokeColor(PdfColor c) => c.IsGray ? StrokeGray(c.R) : StrokeRgb(c.R, c.G, c.B);

    public ContentBuilder GraphicsState(PdfName name) => Named(name, "gs");
    public ContentBuilder DrawXObject(PdfName name) => Named(name, "Do");

    private ContentBuilder Named(PdfName name, string op)
    {
        ContentWriter.WriteObject(_out, name);
        _out.WriteByte((byte)' ');
        return Raw(op + "\n");
    }

    public ContentBuilder BeginText() => Op("BT");
    public ContentBuilder EndText() => Op("ET");

    public ContentBuilder Font(PdfName name, double size)
    {
        ContentWriter.WriteObject(_out, name);
        _out.WriteByte((byte)' ');
        return Op("Tf", size);
    }

    public ContentBuilder MoveText(double x, double y) => Op("Td", x, y);
    public ContentBuilder TextMatrix(double a, double b, double c, double d, double e, double f) => Op("Tm", a, b, c, d, e, f);
    public ContentBuilder Leading(double l) => Op("TL", l);
    public ContentBuilder TextRenderingMode(int mode) => Op("Tr", mode);

    public ContentBuilder ShowText(byte[] encoded)
    {
        ContentWriter.WriteString(_out, encoded, hex: false);
        return Raw(" Tj\n");
    }

    /// <summary>Moves to the next line (by the leading) and shows <paramref name="encoded"/>.</summary>
    public ContentBuilder NextLineShowText(byte[] encoded)
    {
        ContentWriter.WriteString(_out, encoded, hex: false);
        return Raw(" '\n");
    }

    public ContentBuilder Operation(ContentOperation op)
    {
        ContentWriter.WriteOperation(_out, op);
        return this;
    }
}

/// <summary>A device colour: gray (one component) or RGB, each component 0–1.</summary>
internal readonly record struct PdfColor(double R, double G, double B, bool IsGray = false)
{
    public static readonly PdfColor Black = new(0, 0, 0);

    public static PdfColor Rgb(int r, int g, int b) => new(r / 255.0, g / 255.0, b / 255.0);

    public static PdfColor Gray(double g) => new(g, g, g, IsGray: true);

    /// <summary>Parses "#rrggbb" (the leading # optional); null for anything else.</summary>
    public static PdfColor? FromHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        string h = hex.Trim().TrimStart('#');
        if (h.Length != 6 || !int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
            return null;
        return Rgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
    }
}

/// <summary>Adds entries to a resource dictionary under fresh names.</summary>
internal static class PdfResources
{
    /// <summary>
    /// Adds <paramref name="value"/> to <paramref name="resources"/>[<paramref name="category"/>]
    /// under the first unused name <paramref name="prefix"/>1, 2, …, and returns the name. An entry
    /// that already holds the very same object is reused.
    /// </summary>
    public static PdfName Add(PdfDictionary resources, PdfName category, string prefix, PdfObject value)
    {
        var dict = resources.GetAsDictionary(category);
        if (dict == null)
        {
            dict = new PdfDictionary();
            resources.Put(category, dict);
        }
        foreach (var key in dict.Keys)
            if (ReferenceEquals(dict.Get(key), value)) return key;
        for (int i = 1; ; i++)
        {
            var name = PdfName.Of(prefix + i.ToString(CultureInfo.InvariantCulture));
            if (dict.ContainsKey(name)) continue;
            dict.Put(name, value);
            return name;
        }
    }
}
