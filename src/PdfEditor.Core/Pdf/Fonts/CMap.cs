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

    /// <summary>A CMap operand: a string, number, name, or array bracket.</summary>
    private readonly record struct CMapOperand(PdfTokenType Type, byte[] Bytes, double Number, string Text);

    public static CMap Parse(byte[] data)
    {
        var cmap = new CMap();
        var lexer = new PdfLexer(data);
        var operands = new List<CMapOperand>();
        int entries = 0;
        while (lexer.Next())
        {
            if (lexer.TokenType == PdfTokenType.Keyword)
                entries += cmap.ApplyKeyword(lexer.Text, operands, MaxRangeEntries - entries);
            else if (OperandAt(lexer) is { } operand)
                operands.Add(operand);
            if (operands.Count > 100_000) operands.Clear(); // garbage, not a CMap
        }
        return cmap;
    }

    private static CMapOperand? OperandAt(PdfLexer lexer) => lexer.TokenType switch
    {
        PdfTokenType.HexString or PdfTokenType.String => new(lexer.TokenType, lexer.StringBytes, 0, ""),
        PdfTokenType.Number => new(PdfTokenType.Number, Array.Empty<byte>(), lexer.NumberValue, ""),
        PdfTokenType.Name => new(PdfTokenType.Name, Array.Empty<byte>(), 0, lexer.Text),
        PdfTokenType.ArrayStart or PdfTokenType.ArrayEnd => new(lexer.TokenType, Array.Empty<byte>(), 0, ""),
        _ => null,
    };

    /// <summary>
    /// Applies a CMap operator to the operands collected before it. Returns how many bfrange
    /// entries it added, which count against <paramref name="budget"/>.
    /// </summary>
    private int ApplyKeyword(string keyword, List<CMapOperand> operands, int budget)
    {
        int added = 0;
        switch (keyword)
        {
            case "endcodespacerange":
                ReadCodespaceRanges(operands);
                break;
            case "endbfchar":
                ReadBfChars(operands);
                break;
            case "endbfrange":
                added = ReadBfRanges(operands, budget);
                break;
            case "endcidchar":
                ReadCidChars(operands);
                break;
            case "endcidrange":
                ReadCidRanges(operands);
                break;
        }
        if (keyword.StartsWith("end", StringComparison.Ordinal) || keyword.StartsWith("begin", StringComparison.Ordinal)
            || keyword is "def" or "usecmap")
            operands.Clear();
        return added;
    }

    private void ReadCodespaceRanges(List<CMapOperand> operands)
    {
        for (int i = 0; i + 1 < operands.Count; i += 2)
            if (operands[i].Bytes.Length is > 0 and <= 4 && operands[i].Bytes.Length == operands[i + 1].Bytes.Length)
                _codespaces.Add((operands[i].Bytes.Length, operands[i].Bytes, operands[i + 1].Bytes));
    }

    private void ReadBfChars(List<CMapOperand> operands)
    {
        for (int i = 0; i + 1 < operands.Count; i += 2)
            if (operands[i].Type is PdfTokenType.HexString or PdfTokenType.String)
                _unicode[ToInt(operands[i].Bytes)] = DecodeUtf16(operands[i + 1]);
    }

    private void ReadCidChars(List<CMapOperand> operands)
    {
        for (int i = 0; i + 1 < operands.Count; i += 2)
            if (operands[i + 1].Type == PdfTokenType.Number)
                _cids[ToInt(operands[i].Bytes)] = (int)operands[i + 1].Number;
    }

    private void ReadCidRanges(List<CMapOperand> operands)
    {
        for (int i = 0; i + 2 < operands.Count; i += 3)
            if (operands[i + 2].Type == PdfTokenType.Number)
                _cidRanges.Add((ToInt(operands[i].Bytes), ToInt(operands[i + 1].Bytes), (int)operands[i + 2].Number));
    }

    private int ReadBfRanges(List<CMapOperand> ops, int budget)
    {
        int added = 0;
        int i = 0;
        while (i + 2 < ops.Count && added < budget)
        {
            int low = ToInt(ops[i].Bytes), high = ToInt(ops[i + 1].Bytes);
            if (high < low || high - low > 0xFFFF) { i += 3; continue; }
            if (ops[i + 2].Type == PdfTokenType.ArrayStart)
            {
                i = ReadBfRangeArray(ops, i + 3, low, high, budget, ref added);
                continue;
            }
            ReadBfRangeFromStart(ops[i + 2].Bytes, low, high, budget, ref added);
            i += 3;
        }
        return added;
    }

    /// <summary>
    /// Maps <paramref name="low"/> upwards to the destinations listed from <paramref name="j"/>
    /// up to the closing bracket. Returns the index just past that bracket.
    /// </summary>
    private int ReadBfRangeArray(List<CMapOperand> ops, int j, int low, int high, int budget, ref int added)
    {
        for (int code = low; j < ops.Count && ops[j].Type != PdfTokenType.ArrayEnd; j++, code++)
        {
            if (code <= high && added++ < budget) _unicode[code] = DecodeUtf16(ops[j]);
        }
        return j + 1;
    }

    /// <summary>Maps each code in the range to <paramref name="start"/> advanced by the code's offset in it.</summary>
    private void ReadBfRangeFromStart(byte[] start, int low, int high, int budget, ref int added)
    {
        for (int code = low; code <= high && added < budget; code++, added++)
            _unicode[code] = DecodeUtf16Bytes(Advance(start, code - low));
    }

    /// <summary>Increments the destination's last byte by <paramref name="offset"/>, carrying into earlier bytes.</summary>
    private static byte[] Advance(byte[] start, int offset)
    {
        var dest = (byte[])start.Clone();
        for (int k = dest.Length - 1; k >= 0 && offset > 0; k--)
        {
            int v = dest[k] + offset;
            dest[k] = (byte)v;
            offset = v >> 8;
        }
        return dest;
    }

    private static int ToInt(byte[] bytes)
    {
        int v = 0;
        foreach (byte b in bytes.Take(4)) v = v << 8 | b;
        return v;
    }

    private static string DecodeUtf16(CMapOperand op) =>
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
