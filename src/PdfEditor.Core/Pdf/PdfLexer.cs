using System.Globalization;
using System.Text;

namespace PdfEditor.Core.Pdf;

/// <summary>Kinds of token the PDF syntax has (§7.2).</summary>
internal enum PdfTokenType
{
    Eof,
    Number,
    String,
    HexString,
    Name,
    /// <summary>A bare word: true, false, null, R, obj, stream, an operator…</summary>
    Keyword,
    ArrayStart,
    ArrayEnd,
    DictStart,
    DictEnd,
}

/// <summary>
/// Tokenises PDF syntax from a byte buffer. Shared by the file parser and the content-stream
/// parser, which differ only in what they do with the keywords. It never throws on malformed
/// input: an unterminated string ends at the end of the buffer, an unknown byte becomes a
/// one-character keyword. Deciding whether that is acceptable is the parser's job.
/// </summary>
internal sealed class PdfLexer
{
    private readonly byte[] _data;
    private readonly int _end;

    public PdfLexer(byte[] data, int start = 0, int end = -1)
    {
        _data = data;
        Position = start;
        _end = end < 0 ? data.Length : Math.Min(end, data.Length);
    }

    public byte[] Data => _data;
    public int Position { get; set; }
    public int End => _end;

    public PdfTokenType TokenType { get; private set; }

    /// <summary>The token's text for names (decoded), keywords and numbers.</summary>
    public string Text { get; private set; } = "";

    /// <summary>The token's bytes for strings.</summary>
    public byte[] StringBytes { get; private set; } = Array.Empty<byte>();

    /// <summary>The value of a number token.</summary>
    public double NumberValue { get; private set; }

    /// <summary>Whether the number token was spelled without a decimal point.</summary>
    public bool NumberIsInteger { get; private set; }

    /// <summary>Where the current token started.</summary>
    public int TokenStart { get; private set; }

    public static bool IsWhitespace(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;

    public static bool IsDelimiter(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>'
        or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    public static bool IsRegular(byte b) => !IsWhitespace(b) && !IsDelimiter(b);

    /// <summary>Skips whitespace and comments.</summary>
    public void SkipWhitespace()
    {
        while (Position < _end)
        {
            byte b = _data[Position];
            if (IsWhitespace(b)) { Position++; continue; }
            if (b == '%')
            {
                while (Position < _end && _data[Position] != '\n' && _data[Position] != '\r') Position++;
                continue;
            }
            break;
        }
    }

    /// <summary>Reads the next token. Returns false at end of input.</summary>
    public bool Next()
    {
        SkipWhitespace();
        TokenStart = Position;
        if (Position >= _end)
        {
            TokenType = PdfTokenType.Eof;
            Text = "";
            return false;
        }

        byte b = _data[Position];
        switch (b)
        {
            case (byte)'[':
                Position++;
                TokenType = PdfTokenType.ArrayStart;
                return true;
            case (byte)']':
                Position++;
                TokenType = PdfTokenType.ArrayEnd;
                return true;
            case (byte)'<':
                if (Position + 1 < _end && _data[Position + 1] == '<')
                {
                    Position += 2;
                    TokenType = PdfTokenType.DictStart;
                    return true;
                }
                ReadHexString();
                return true;
            case (byte)'>':
                if (Position + 1 < _end && _data[Position + 1] == '>')
                {
                    Position += 2;
                    TokenType = PdfTokenType.DictEnd;
                    return true;
                }
                Position++;
                TokenType = PdfTokenType.Keyword;
                Text = ">";
                return true;
            case (byte)'(':
                ReadLiteralString();
                return true;
            case (byte)'/':
                ReadName();
                return true;
            case (byte)')':
            case (byte)'{':
            case (byte)'}':
                Position++;
                TokenType = PdfTokenType.Keyword;
                Text = ((char)b).ToString();
                return true;
        }

        int start = Position;
        while (Position < _end && IsRegular(_data[Position])) Position++;
        string word = Encoding.Latin1.GetString(_data, start, Position - start);
        if (TryParseNumber(word, out double value, out bool isInteger))
        {
            TokenType = PdfTokenType.Number;
            NumberValue = value;
            NumberIsInteger = isInteger;
            Text = word;
            return true;
        }
        TokenType = PdfTokenType.Keyword;
        Text = word;
        return true;
    }

    /// <summary>
    /// Parses a PDF number. Accepts what real producers write beyond the grammar — a leading '+',
    /// a bare '.', a second minus sign ("--5", which Acrobat reads as -5) — and rejects the rest.
    /// </summary>
    internal static bool TryParseNumber(string word, out double value, out bool isInteger)
    {
        value = 0;
        isInteger = true;
        if (word.Length == 0) return false;
        int i = 0;
        bool negative = false;
        while (i < word.Length && (word[i] == '+' || word[i] == '-'))
        {
            negative |= word[i] == '-';
            i++;
        }
        if (i == word.Length) return word.Length > 0 && i > 0 && Zero(out value);

        bool sawDigit = false, sawDot = false;
        double intPart = 0, frac = 0, scale = 1;
        for (; i < word.Length; i++)
        {
            char c = word[i];
            if (c >= '0' && c <= '9')
            {
                sawDigit = true;
                if (sawDot)
                {
                    scale /= 10;
                    frac += (c - '0') * scale;
                }
                else
                {
                    intPart = intPart * 10 + (c - '0');
                }
            }
            else if (c == '.' && !sawDot)
            {
                sawDot = true;
            }
            else
            {
                return false;
            }
        }
        if (!sawDigit && !sawDot) return false;
        value = intPart + frac;
        if (negative) value = -value;
        if (!double.IsFinite(value)) value = 0;
        isInteger = !sawDot;
        return true;

        static bool Zero(out double v)
        {
            v = 0;
            return true;
        }
    }

    private void ReadName()
    {
        Position++; // the solidus
        var bytes = new List<byte>();
        while (Position < _end && IsRegular(_data[Position]))
        {
            byte b = _data[Position++];
            if (b == '#' && Position + 1 < _end && IsHex(_data[Position]) && IsHex(_data[Position + 1]))
            {
                bytes.Add((byte)(HexValue(_data[Position]) << 4 | HexValue(_data[Position + 1])));
                Position += 2;
            }
            else
            {
                bytes.Add(b);
            }
        }
        TokenType = PdfTokenType.Name;
        // Names are byte sequences; UTF-8 is the recommended interpretation (§7.3.5) and Latin-1 is
        // what anything that is not valid UTF-8 was meant to be.
        byte[] raw = bytes.ToArray();
        Text = IsValidUtf8(raw) ? Encoding.UTF8.GetString(raw) : Encoding.Latin1.GetString(raw);
    }

    private static bool IsValidUtf8(byte[] bytes)
    {
        foreach (byte b in bytes)
            if (b >= 0x80)
            {
                try
                {
                    new UTF8Encoding(false, true).GetString(bytes);
                    return true;
                }
                catch (DecoderFallbackException)
                {
                    return false;
                }
            }
        return true;
    }

    private void ReadHexString()
    {
        Position++; // '<'
        var bytes = new List<byte>();
        int high = -1;
        while (Position < _end)
        {
            byte b = _data[Position++];
            if (b == '>') break;
            if (!IsHex(b)) continue; // whitespace, or junk a lenient reader skips
            int v = HexValue(b);
            if (high < 0) high = v;
            else
            {
                bytes.Add((byte)(high << 4 | v));
                high = -1;
            }
        }
        if (high >= 0) bytes.Add((byte)(high << 4)); // odd digit count: final digit is followed by 0
        TokenType = PdfTokenType.HexString;
        StringBytes = bytes.ToArray();
    }

    private void ReadLiteralString()
    {
        Position++; // '('
        var bytes = new List<byte>();
        int depth = 1;
        while (Position < _end)
        {
            byte b = _data[Position++];
            if (b == '(')
            {
                depth++;
                bytes.Add(b);
            }
            else if (b == ')')
            {
                if (--depth == 0) break;
                bytes.Add(b);
            }
            else if (b == '\\')
            {
                if (Position >= _end) break;
                byte e = _data[Position++];
                switch (e)
                {
                    case (byte)'n': bytes.Add(10); break;
                    case (byte)'r': bytes.Add(13); break;
                    case (byte)'t': bytes.Add(9); break;
                    case (byte)'b': bytes.Add(8); break;
                    case (byte)'f': bytes.Add(12); break;
                    case (byte)'\r':
                        if (Position < _end && _data[Position] == '\n') Position++;
                        break; // line continuation
                    case (byte)'\n':
                        break;
                    default:
                        if (e >= '0' && e <= '7')
                        {
                            int v = e - '0';
                            for (int k = 0; k < 2 && Position < _end && _data[Position] >= '0' && _data[Position] <= '7'; k++)
                                v = v * 8 + (_data[Position++] - '0');
                            bytes.Add((byte)v);
                        }
                        else
                        {
                            bytes.Add(e); // \( \) \\ and any other escaped byte stand for themselves
                        }
                        break;
                }
            }
            else if (b == '\r')
            {
                // An end-of-line in a string is read as a single LF, whatever the producer wrote.
                if (Position < _end && _data[Position] == '\n') Position++;
                bytes.Add(10);
            }
            else
            {
                bytes.Add(b);
            }
        }
        TokenType = PdfTokenType.String;
        StringBytes = bytes.ToArray();
    }

    public static bool IsHex(byte b) => b is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';

    public static int HexValue(byte b) => b <= '9' ? b - '0' : (b | 0x20) - 'a' + 10;

    /// <summary>Whether the bytes at <paramref name="at"/> spell <paramref name="word"/>, as a whole token.</summary>
    public bool MatchesKeywordAt(int at, string word)
    {
        if (at < 0 || at + word.Length > _end) return false;
        for (int i = 0; i < word.Length; i++)
            if (_data[at + i] != word[i]) return false;
        return at + word.Length == _end || !IsRegular(_data[at + word.Length]);
    }
}

/// <summary>
/// Parses PDF objects from tokens. In file mode, <c>n g R</c> becomes a reference and a dictionary
/// followed by <c>stream</c> becomes a stream; in content mode neither applies.
/// </summary>
internal sealed class PdfObjectParser
{
    /// <summary>
    /// The deepest array/dictionary nesting accepted. Legitimate documents nest a handful of
    /// levels; without a bound, a file of ten thousand '[' would overflow the stack, which .NET
    /// cannot catch.
    /// </summary>
    public const int MaxNesting = 256;

    private readonly PdfLexer _lexer;
    private readonly PdfDocument? _document;

    public PdfObjectParser(PdfLexer lexer, PdfDocument? document)
    {
        _lexer = lexer;
        _document = document;
    }

    public PdfLexer Lexer => _lexer;

    /// <summary>Parses the next object. Returns null at end of input or on a token that starts no object.</summary>
    public PdfObject? ParseObject()
    {
        if (!_lexer.Next()) return null;
        return ParseFromCurrent(0);
    }

    /// <summary>Parses an object starting at the token the lexer has just read.</summary>
    public PdfObject? ParseFromCurrent(int depth)
    {
        if (depth > MaxNesting)
            throw new PdfFormatException(
                $"Arrays and dictionaries nest more than {MaxNesting} levels deep, which no legitimate document does.");

        switch (_lexer.TokenType)
        {
            case PdfTokenType.Number:
                return ParseNumberOrReference();
            case PdfTokenType.String:
                return new PdfString(_lexer.StringBytes);
            case PdfTokenType.HexString:
                return new PdfString(_lexer.StringBytes, isHex: true);
            case PdfTokenType.Name:
                return PdfName.Of(_lexer.Text);
            case PdfTokenType.ArrayStart:
                return ParseArray(depth);
            case PdfTokenType.DictStart:
                return ParseDictionary(depth);
            case PdfTokenType.Keyword:
                return _lexer.Text switch
                {
                    "true" => PdfBoolean.True,
                    "false" => PdfBoolean.False,
                    "null" => PdfNull.Instance,
                    _ => null,
                };
            default:
                return null;
        }
    }

    private PdfObject ParseNumberOrReference()
    {
        double value = _lexer.NumberValue;
        bool isInteger = _lexer.NumberIsInteger;
        var number = new PdfNumber(value, isInteger);
        if (_document == null || !isInteger || value < 0) return number;

        // "n g R": look two tokens ahead without consuming them unless they complete a reference.
        int save = _lexer.Position;
        if (_lexer.Next() && _lexer.TokenType == PdfTokenType.Number && _lexer.NumberIsInteger
            && _lexer.NumberValue >= 0)
        {
            double generation = _lexer.NumberValue;
            if (_lexer.Next() && _lexer.TokenType == PdfTokenType.Keyword && _lexer.Text == "R")
            {
                if (value > int.MaxValue || generation > int.MaxValue) return PdfNull.Instance;
                return _document.GetReference((int)value, (int)generation);
            }
        }
        _lexer.Position = save;
        return number;
    }

    private PdfArray ParseArray(int depth)
    {
        var array = new PdfArray();
        while (true)
        {
            if (!_lexer.Next()) return array; // unterminated at end of input: keep what was read
            if (_lexer.TokenType == PdfTokenType.ArrayEnd) return array;
            if (_lexer.TokenType == PdfTokenType.DictEnd) return array; // a mismatched close ends it too
            if (_lexer.TokenType == PdfTokenType.Keyword && IsObjectBoundary(_lexer.Text))
            {
                _lexer.Position = _lexer.TokenStart;
                return array;
            }
            var item = ParseFromCurrent(depth + 1);
            if (item != null) array.Add(item);
        }
    }

    private PdfDictionary ParseDictionary(int depth)
    {
        var dict = new PdfDictionary();
        while (true)
        {
            if (!_lexer.Next()) return dict;
            if (_lexer.TokenType == PdfTokenType.DictEnd) return dict;
            if (_lexer.TokenType == PdfTokenType.ArrayEnd) continue;
            if (_lexer.TokenType == PdfTokenType.Keyword && IsObjectBoundary(_lexer.Text))
            {
                _lexer.Position = _lexer.TokenStart;
                return dict;
            }
            if (_lexer.TokenType != PdfTokenType.Name)
            {
                // A key that is not a name: skip the token (and anything it opens) and resync.
                ParseFromCurrent(depth + 1);
                continue;
            }
            var key = PdfName.Of(_lexer.Text);
            if (!_lexer.Next()) return dict;
            if (_lexer.TokenType == PdfTokenType.DictEnd)
            {
                dict.Put(key, PdfNull.Instance);
                return dict;
            }
            var value = ParseFromCurrent(depth + 1);
            dict.Put(key, value ?? PdfNull.Instance);
        }
    }

    /// <summary>Keywords that can only appear between objects, so they end an unterminated container.</summary>
    private static bool IsObjectBoundary(string keyword) =>
        keyword is "endobj" or "stream" or "endstream" or "obj" or "xref" or "trailer" or "startxref";
}
