using System.Text;

namespace PdfEditor.Core.Pdf.Fonts;

/// <summary>
/// A CMap (§9.7.5, §9.10.3): how a string's bytes split into character codes, and what each code
/// means — a CID for an encoding CMap, text for a /ToUnicode CMap. Parsed from the PostScript-like
/// CMap syntax; unknown operators are skipped.
/// </summary>
internal sealed class CMap
{
    /// <summary>Codespace ranges: codes are this many bytes long when they fall inside one.</summary>
    private readonly List<(int Length, byte[] Low, byte[] High)> _codespaces = new();
    private readonly Dictionary<int, string> _unicode = new();
    private readonly Dictionary<int, int> _cids = new();
    private readonly List<(int Low, int High, int Cid)> _cidRanges = new();

    /// <summary>Bounds what a hostile bfrange can make the parser allocate.</summary>
    private const int MaxRangeEntries = 1 << 20;

    public bool HasCodespace => _codespaces.Count > 0;

    /// <summary>Identity-H/V: two-byte codes, each its own CID.</summary>
    public bool IsIdentity { get; private init; }

    public static CMap Identity() => new() { IsIdentity = true };

    /// <summary>A two-byte CMap whose codes are UTF-16 (the Uni…-UCS2/UTF16 predefined CMaps).</summary>
    public bool IsUnicode { get; private init; }

    public static CMap Unicode() => new() { IsUnicode = true };

    public static CMap Parse(byte[] data)
    {
        var cmap = new CMap();
        var lexer = new PdfLexer(data);
        var operands = new List<(PdfTokenType Type, byte[] Bytes, double Number, string Text)>();
        int entries = 0;
        while (lexer.Next())
        {
            switch (lexer.TokenType)
            {
                case PdfTokenType.HexString:
                case PdfTokenType.String:
                    operands.Add((lexer.TokenType, lexer.StringBytes, 0, ""));
                    break;
                case PdfTokenType.Number:
                    operands.Add((PdfTokenType.Number, Array.Empty<byte>(), lexer.NumberValue, ""));
                    break;
                case PdfTokenType.Name:
                    operands.Add((PdfTokenType.Name, Array.Empty<byte>(), 0, lexer.Text));
                    break;
                case PdfTokenType.ArrayStart:
                    operands.Add((PdfTokenType.ArrayStart, Array.Empty<byte>(), 0, ""));
                    break;
                case PdfTokenType.ArrayEnd:
                    operands.Add((PdfTokenType.ArrayEnd, Array.Empty<byte>(), 0, ""));
                    break;
                case PdfTokenType.Keyword:
                    switch (lexer.Text)
                    {
                        case "endcodespacerange":
                            for (int i = 0; i + 1 < operands.Count; i += 2)
                                if (operands[i].Bytes.Length is > 0 and <= 4 && operands[i].Bytes.Length == operands[i + 1].Bytes.Length)
                                    cmap._codespaces.Add((operands[i].Bytes.Length, operands[i].Bytes, operands[i + 1].Bytes));
                            break;
                        case "endbfchar":
                            for (int i = 0; i + 1 < operands.Count; i += 2)
                                if (operands[i].Type is PdfTokenType.HexString or PdfTokenType.String)
                                    cmap._unicode[ToInt(operands[i].Bytes)] = DecodeUtf16(operands[i + 1]);
                            break;
                        case "endbfrange":
                            entries += ReadBfRanges(cmap, operands, MaxRangeEntries - entries);
                            break;
                        case "endcidchar":
                            for (int i = 0; i + 1 < operands.Count; i += 2)
                                if (operands[i + 1].Type == PdfTokenType.Number)
                                    cmap._cids[ToInt(operands[i].Bytes)] = (int)operands[i + 1].Number;
                            break;
                        case "endcidrange":
                            for (int i = 0; i + 2 < operands.Count; i += 3)
                                if (operands[i + 2].Type == PdfTokenType.Number)
                                    cmap._cidRanges.Add((ToInt(operands[i].Bytes), ToInt(operands[i + 1].Bytes), (int)operands[i + 2].Number));
                            break;
                    }
                    if (lexer.Text.StartsWith("end", StringComparison.Ordinal) || lexer.Text.StartsWith("begin", StringComparison.Ordinal)
                        || lexer.Text is "def" or "usecmap")
                        operands.Clear();
                    break;
                case PdfTokenType.DictStart:
                case PdfTokenType.DictEnd:
                    break;
            }
            if (operands.Count > 100_000) operands.Clear(); // garbage, not a CMap
        }
        return cmap;
    }

    private static int ReadBfRanges(CMap cmap, List<(PdfTokenType Type, byte[] Bytes, double Number, string Text)> ops, int budget)
    {
        int added = 0;
        int i = 0;
        while (i + 2 < ops.Count && added < budget)
        {
            int low = ToInt(ops[i].Bytes), high = ToInt(ops[i + 1].Bytes);
            if (high < low || high - low > 0xFFFF) { i += 3; continue; }
            if (ops[i + 2].Type == PdfTokenType.ArrayStart)
            {
                int j = i + 3;
                for (int code = low; j < ops.Count && ops[j].Type != PdfTokenType.ArrayEnd; j++, code++)
                {
                    if (code <= high && added++ < budget) cmap._unicode[code] = DecodeUtf16(ops[j]);
                }
                i = j + 1;
                continue;
            }
            byte[] start = ops[i + 2].Bytes;
            for (int code = low; code <= high && added < budget; code++, added++)
            {
                // Increment the destination's last byte per code, carrying into earlier bytes.
                var dest = (byte[])start.Clone();
                int offset = code - low;
                for (int k = dest.Length - 1; k >= 0 && offset > 0; k--)
                {
                    int v = dest[k] + offset;
                    dest[k] = (byte)v;
                    offset = v >> 8;
                }
                cmap._unicode[code] = DecodeUtf16Bytes(dest);
            }
            i += 3;
        }
        return added;
    }

    private static int ToInt(byte[] bytes)
    {
        int v = 0;
        foreach (byte b in bytes.Take(4)) v = v << 8 | b;
        return v;
    }

    private static string DecodeUtf16((PdfTokenType Type, byte[] Bytes, double Number, string Text) op) =>
        op.Type == PdfTokenType.Name ? GlyphList.ToUnicode(op.Text) ?? "" : DecodeUtf16Bytes(op.Bytes);

    private static string DecodeUtf16Bytes(byte[] bytes)
    {
        if (bytes.Length == 1) return ((char)bytes[0]).ToString();
        return Encoding.BigEndianUnicode.GetString(bytes, 0, bytes.Length & ~1);
    }

    /// <summary>
    /// Reads one character code from <paramref name="bytes"/> at <paramref name="offset"/>,
    /// matching the codespace ranges. Returns the code and its length in bytes.
    /// </summary>
    public (int Code, int Length) ReadCode(byte[] bytes, int offset)
    {
        if (IsIdentity || IsUnicode || _codespaces.Count == 0)
        {
            if (offset + 1 < bytes.Length) return (bytes[offset] << 8 | bytes[offset + 1], 2);
            return (bytes[offset], 1);
        }
        int code = 0;
        for (int length = 1; length <= 4 && offset + length <= bytes.Length; length++)
        {
            code = code << 8 | bytes[offset + length - 1];
            foreach (var (len, low, high) in _codespaces)
                if (len == length && InRange(bytes, offset, low, high)) return (code, length);
        }
        // Outside every codespace: consume the shortest code length declared (§9.7.6.3).
        int shortest = _codespaces.Min(c => c.Length);
        shortest = Math.Min(shortest, bytes.Length - offset);
        int fallback = 0;
        for (int k = 0; k < shortest; k++) fallback = fallback << 8 | bytes[offset + k];
        return (fallback, Math.Max(1, shortest));
    }

    private static bool InRange(byte[] bytes, int offset, byte[] low, byte[] high)
    {
        for (int k = 0; k < low.Length; k++)
        {
            byte b = bytes[offset + k];
            if (b < low[k] || b > high[k]) return false;
        }
        return true;
    }

    /// <summary>The text mapped to <paramref name="code"/>, or null when unmapped.</summary>
    public string? ToUnicode(int code)
    {
        if (_unicode.TryGetValue(code, out var s)) return s;
        if (IsUnicode) return char.ConvertFromUtf32(code is >= 0xD800 and <= 0xDFFF ? 0xFFFD : code);
        return null;
    }

    /// <summary>The CID for <paramref name="code"/> (identity when the CMap does not say).</summary>
    public int ToCid(int code)
    {
        if (IsIdentity) return code;
        if (_cids.TryGetValue(code, out int cid)) return cid;
        foreach (var (low, high, start) in _cidRanges)
            if (code >= low && code <= high) return start + (code - low);
        return _cids.Count == 0 && _cidRanges.Count == 0 ? code : 0;
    }

    /// <summary>Every code mapped to <paramref name="text"/>, for finding a font's space glyph.</summary>
    public int? CodeFor(string text)
    {
        foreach (var (code, value) in _unicode)
            if (value == text) return code;
        return null;
    }

    public bool HasUnicodeMappings => _unicode.Count > 0;
}
