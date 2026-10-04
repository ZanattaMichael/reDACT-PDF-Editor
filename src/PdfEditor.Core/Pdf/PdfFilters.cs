using System.IO.Compression;

namespace PdfEditor.Core.Pdf;

/// <summary>
/// The stream filters of §7.4: the general-purpose decoders are implemented here, and the image
/// codecs (DCT, JPX, CCITT, JBIG2) end the chain — their output is an encoded image that the
/// image layer hands to an image decoder.
/// <para>
/// Decoding is <em>strict</em>: data that is truncated or corrupt is an error, not a short read.
/// A content stream decoded halfway would be re-written halfway by an edit, silently dropping
/// whatever came after the damage; refusing the document is the honest outcome.
/// </para>
/// </summary>
internal static class PdfFilters
{
    /// <summary>
    /// The most bytes one stream may decode to. A page image at print resolution is tens of
    /// megabytes; a stream that inflates past this is a decompression bomb, and expanding it
    /// would exhaust memory long before it produced anything useful.
    /// </summary>
    public const int MaxDecodedBytes = 256 * 1024 * 1024;

    /// <summary>Longest filter chain accepted. Real documents use one or two.</summary>
    private const int MaxChain = 8;

    private static readonly HashSet<string> ImageFilters = new(StringComparer.Ordinal)
    {
        "DCTDecode", "DCT", "JPXDecode", "CCITTFaxDecode", "CCF", "JBIG2Decode",
    };

    /// <summary>Whether <paramref name="name"/> is an image codec rather than a general filter.</summary>
    public static bool IsImageFilter(string name) => ImageFilters.Contains(name);

    /// <summary>Applies every general-purpose filter of <paramref name="stream"/>, stopping at an image codec.</summary>
    public static byte[] Decode(PdfStream stream)
    {
        var filters = stream.FilterNames();
        if (filters.Count == 0) return stream.RawData;
        if (filters.Count > MaxChain)
            throw new PdfFormatException($"The stream declares {filters.Count} filters; no legitimate stream chains more than {MaxChain}.");

        var parms = stream.Get(PdfName.DecodeParms);
        byte[] data = stream.RawData;
        for (int i = 0; i < filters.Count; i++)
        {
            string filter = filters[i];
            if (IsImageFilter(filter)) return data;
            var p = parms switch
            {
                PdfDictionary d when i == 0 => d,
                PdfArray a => a.GetAsDictionary(i),
                _ => null,
            };
            data = DecodeOne(filter, data, p);
        }
        return data;
    }

    private static byte[] DecodeOne(string filter, byte[] data, PdfDictionary? parms) => filter switch
    {
        "FlateDecode" or "Fl" => ApplyPredictor(Inflate(data), parms),
        "LZWDecode" or "LZW" => ApplyPredictor(LzwDecode(data, parms?.GetAsInt(PdfName.EarlyChange) ?? 1), parms),
        "ASCIIHexDecode" or "AHx" => AsciiHexDecode(data),
        "ASCII85Decode" or "A85" => Ascii85Decode(data),
        "RunLengthDecode" or "RL" => RunLengthDecode(data),
        // Encryption has already been undone when the stream was loaded; an Identity crypt
        // filter, or a named one on an unencrypted file, leaves the bytes as they are.
        "Crypt" => data,
        _ => throw new PdfFormatException($"The stream uses the filter /{filter}, which is not defined by the PDF specification."),
    };

    // ------------------------------------------------------------------ Flate

    /// <summary>Flate-compresses <paramref name="data"/> with a zlib wrapper.</summary>
    public static byte[] FlateEncode(byte[] data)
    {
        using var output = new MemoryStream(data.Length / 2 + 64);
        using (var z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data, 0, data.Length);
        return output.ToArray();
    }

    /// <summary>
    /// Inflates zlib (or, as some producers write, bare deflate) data. Throws when the data is
    /// corrupt or ends before its final block.
    /// </summary>
    public static byte[] Inflate(byte[] input)
    {
        if (input.Length == 0) return input; // an empty stream is legal, whatever its filter
        var result = Inflater.Run(input, MaxDecodedBytes);
        if (!result.Complete)
            throw new PdfFormatException(result.Error ?? "The Flate data ends before its final block: the stream is truncated.");
        return result.Output;
    }

    /// <summary>Inflates as much as can be inflated, for repair paths that would rather have part than nothing.</summary>
    public static byte[] InflateLenient(byte[] input) => Inflater.Run(input, MaxDecodedBytes).Output;

    /// <summary>A self-contained inflater (RFC 1950/1951) that knows whether the data really ended.</summary>
    private sealed class Inflater
    {
        public readonly record struct Result(byte[] Output, bool Complete, string? Error);

        private static readonly int[] LengthBase =
            { 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258 };
        private static readonly int[] LengthExtra =
            { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0 };
        private static readonly int[] DistBase =
            { 1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577 };
        private static readonly int[] DistExtra =
            { 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13 };
        private static readonly int[] CodeLengthOrder = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };

        private static readonly Huffman FixedLit = BuildFixedLit();
        private static readonly Huffman FixedDist = BuildFixedDist();

        private readonly byte[] _in;
        private int _pos;
        private uint _bitBuf;
        private int _bitCount;
        private byte[] _out;
        private int _outLen;
        private readonly int _max;

        private Inflater(byte[] input, int start, int max)
        {
            _in = input;
            _pos = start;
            _max = max;
            _out = new byte[(int)Math.Clamp((long)input.Length * 4, 256, max)];
        }

        /// <summary>Thrown internally when the input runs out; turns into an incomplete result.</summary>
        private sealed class EndOfInput : Exception { }

        public static Result Run(byte[] input, int max)
        {
            int start = 0;
            if (input.Length >= 2 && (input[0] & 0x0F) == 8 && (input[0] >> 4) <= 7 && ((input[0] << 8) | input[1]) % 31 == 0)
                start = (input[1] & 0x20) != 0 ? 6 : 2; // zlib header, with a preset-dictionary id if FDICT
            var inflater = new Inflater(input, start, max);
            try
            {
                bool final;
                do
                {
                    final = inflater.Bits(1) == 1;
                    int type = inflater.Bits(2);
                    switch (type)
                    {
                        case 0: inflater.Stored(); break;
                        case 1: inflater.Codes(FixedLit, FixedDist); break;
                        case 2: inflater.Dynamic(); break;
                        default: return inflater.Fail("The Flate data uses the reserved block type 3: it is corrupt.");
                    }
                } while (!final);
                return new Result(inflater.Output(), true, null);
            }
            catch (EndOfInput)
            {
                return new Result(inflater.Output(), false, null);
            }
            catch (CorruptData e)
            {
                return inflater.Fail(e.Message);
            }
        }

        private sealed class CorruptData : Exception
        {
            public CorruptData(string message) : base(message) { }
        }

        private Result Fail(string message) => new(Output(), false, message);

        private byte[] Output() => _out.AsSpan(0, _outLen).ToArray();

        private int Bits(int need)
        {
            while (_bitCount < need)
            {
                if (_pos >= _in.Length) throw new EndOfInput();
                _bitBuf |= (uint)_in[_pos++] << _bitCount;
                _bitCount += 8;
            }
            int value = (int)(_bitBuf & ((1u << need) - 1));
            _bitBuf >>= need;
            _bitCount -= need;
            return value;
        }

        private void Put(byte b)
        {
            if (_outLen == _out.Length) Grow(1);
            _out[_outLen++] = b;
        }

        private void Grow(int extra)
        {
            long wanted = (long)_outLen + extra;
            if (wanted > _max)
                throw new CorruptData($"The Flate data expands past {_max / (1024 * 1024)} MiB: it is a decompression bomb, not page content.");
            long size = Math.Max(wanted, Math.Min((long)_max, (long)_out.Length * 2));
            Array.Resize(ref _out, (int)size);
        }

        private void Stored()
        {
            // Stored blocks start on a byte boundary. The decoder's lookahead may already hold whole
            // bytes past the current one; give those back before discarding the partial byte.
            _pos -= _bitCount >> 3;
            _bitBuf = 0;
            _bitCount = 0;
            if (_pos + 4 > _in.Length) throw new EndOfInput();
            int len = _in[_pos] | _in[_pos + 1] << 8;
            int nlen = _in[_pos + 2] | _in[_pos + 3] << 8;
            _pos += 4;
            if (len != (~nlen & 0xFFFF)) throw new CorruptData("A stored Flate block's length check does not match: the data is corrupt.");
            if (_pos + len > _in.Length)
            {
                int available = _in.Length - _pos;
                if (_outLen + available > _out.Length) Grow(available);
                Array.Copy(_in, _pos, _out, _outLen, available);
                _outLen += available;
                _pos = _in.Length;
                throw new EndOfInput();
            }
            if (_outLen + len > _out.Length) Grow(len);
            Array.Copy(_in, _pos, _out, _outLen, len);
            _outLen += len;
            _pos += len;
        }

        private void Codes(Huffman lit, Huffman dist)
        {
            while (true)
            {
                int symbol = Decode(lit);
                if (symbol < 256)
                {
                    Put((byte)symbol);
                    continue;
                }
                if (symbol == 256) return;
                symbol -= 257;
                if (symbol >= 29) throw new CorruptData("The Flate data contains an invalid length code: it is corrupt.");
                int length = LengthBase[symbol] + Bits(LengthExtra[symbol]);
                int ds = Decode(dist);
                if (ds >= 30) throw new CorruptData("The Flate data contains an invalid distance code: it is corrupt.");
                int distance = DistBase[ds] + Bits(DistExtra[ds]);
                if (distance > _outLen) throw new CorruptData("The Flate data refers back past its own start: it is corrupt.");
                if (_outLen + length > _out.Length) Grow(length);
                int from = _outLen - distance;
                for (int i = 0; i < length; i++) _out[_outLen++] = _out[from + i];
            }
        }

        private void Dynamic()
        {
            int nlen = Bits(5) + 257;
            int ndist = Bits(5) + 1;
            int ncode = Bits(4) + 4;
            if (nlen > 286 || ndist > 30) throw new CorruptData("The Flate data declares too many codes: it is corrupt.");
            var lengths = new int[320];
            for (int i = 0; i < ncode; i++) lengths[CodeLengthOrder[i]] = Bits(3);
            var lencode = Huffman.Build(lengths, 0, 19)
                ?? throw new CorruptData("The Flate data's code-length table is invalid: it is corrupt.");

            int index = 0;
            while (index < nlen + ndist)
            {
                int symbol = Decode(lencode);
                if (symbol < 16)
                {
                    lengths[index++] = symbol;
                    continue;
                }
                int repeat, value = 0;
                if (symbol == 16)
                {
                    if (index == 0) throw new CorruptData("The Flate data repeats a code length before any was given: it is corrupt.");
                    value = lengths[index - 1];
                    repeat = 3 + Bits(2);
                }
                else if (symbol == 17) repeat = 3 + Bits(3);
                else repeat = 11 + Bits(7);
                if (index + repeat > nlen + ndist) throw new CorruptData("The Flate data's code lengths overrun their table: it is corrupt.");
                while (repeat-- > 0) lengths[index++] = value;
            }
            if (lengths[256] == 0) throw new CorruptData("The Flate data has no end-of-block code: it is corrupt.");

            var lit = Huffman.Build(lengths, 0, nlen) ?? throw new CorruptData("The Flate data's literal table is invalid: it is corrupt.");
            var dist = Huffman.Build(lengths, nlen, ndist) ?? throw new CorruptData("The Flate data's distance table is invalid: it is corrupt.");
            Codes(lit, dist);
        }

        private int Decode(Huffman h)
        {
            // Fast path: peek FastBits bits and look the code up directly.
            while (_bitCount < Huffman.FastBits && _pos < _in.Length)
            {
                _bitBuf |= (uint)_in[_pos++] << _bitCount;
                _bitCount += 8;
            }
            if (_bitCount >= Huffman.FastBits)
            {
                int entry = h.Fast[_bitBuf & ((1 << Huffman.FastBits) - 1)];
                if (entry >= 0)
                {
                    int len = entry >> 16;
                    _bitBuf >>= len;
                    _bitCount -= len;
                    return entry & 0xFFFF;
                }
            }
            // Slow path (long codes, or near the end of input): canonical decode bit by bit.
            int code = 0, first = 0, idx = 0;
            for (int length = 1; length <= 15; length++)
            {
                code |= Bits(1);
                int count = h.Count[length];
                if (code - count < first) return h.Symbol[idx + (code - first)];
                idx += count;
                first += count;
                first <<= 1;
                code <<= 1;
            }
            throw new CorruptData("The Flate data contains a code that is not in its table: it is corrupt.");
        }

        private static Huffman BuildFixedLit()
        {
            var l = new int[288];
            for (int i = 0; i < 144; i++) l[i] = 8;
            for (int i = 144; i < 256; i++) l[i] = 9;
            for (int i = 256; i < 280; i++) l[i] = 7;
            for (int i = 280; i < 288; i++) l[i] = 8;
            return Huffman.Build(l, 0, 288)!;
        }

        private static Huffman BuildFixedDist()
        {
            var l = new int[30];
            Array.Fill(l, 5);
            return Huffman.Build(l, 0, 30)!;
        }

        private sealed class Huffman
        {
            public const int FastBits = 9;
            public readonly int[] Count = new int[16];
            public int[] Symbol = Array.Empty<int>();
            public readonly int[] Fast = new int[1 << FastBits];

            /// <summary>Builds a canonical Huffman decoder; null when the lengths over-subscribe the code space.</summary>
            public static Huffman? Build(int[] lengths, int offset, int n)
            {
                var h = new Huffman();
                for (int i = 0; i < n; i++) h.Count[lengths[offset + i]]++;
                if (h.Count[0] == n) // no codes at all: legal for an unused distance table
                {
                    Array.Fill(h.Fast, -1);
                    return h;
                }
                int left = 1;
                for (int len = 1; len <= 15; len++)
                {
                    left <<= 1;
                    left -= h.Count[len];
                    if (left < 0) return null;
                }
                var offs = new int[16];
                for (int len = 1; len < 15; len++) offs[len + 1] = offs[len] + h.Count[len];
                h.Symbol = new int[n];
                for (int i = 0; i < n; i++)
                    if (lengths[offset + i] != 0) h.Symbol[offs[lengths[offset + i]]++] = i;

                // Fast table: for every code of length <= FastBits, fill all entries whose low bits
                // (in the reversed, LSB-first order the bit reader yields) match the code.
                Array.Fill(h.Fast, -1);
                int code = 0, symIndex = 0;
                for (int len = 1; len <= 15; len++)
                {
                    for (int k = 0; k < h.Count[len]; k++)
                    {
                        if (len <= FastBits)
                        {
                            int reversed = Reverse(code, len);
                            int entry = len << 16 | h.Symbol[symIndex];
                            for (int fill = reversed; fill < (1 << FastBits); fill += 1 << len) h.Fast[fill] = entry;
                        }
                        code++;
                        symIndex++;
                    }
                    code <<= 1;
                }
                return h;
            }

            private static int Reverse(int code, int len)
            {
                int r = 0;
                for (int i = 0; i < len; i++)
                {
                    r = r << 1 | (code & 1);
                    code >>= 1;
                }
                return r;
            }
        }
    }

    // ------------------------------------------------------------------ predictors

    /// <summary>Undoes a PNG (10–15) or TIFF (2) predictor, per the stream's /DecodeParms.</summary>
    internal static byte[] ApplyPredictor(byte[] data, PdfDictionary? parms)
    {
        int predictor = parms?.GetAsInt(PdfName.Predictor) ?? 1;
        if (predictor <= 1) return data;
        int colors = Math.Clamp(parms!.GetAsInt(PdfName.Colors) ?? 1, 1, 32);
        int bpc = parms.GetAsInt(PdfName.BitsPerComponent) ?? 8;
        if (bpc is not (1 or 2 or 4 or 8 or 16)) bpc = 8;
        int columns = Math.Clamp(parms.GetAsInt(PdfName.Columns) ?? 1, 1, 1 << 20);
        int bytesPerPixel = Math.Max(1, colors * bpc / 8);
        int rowBytes = (colors * bpc * columns + 7) / 8;

        if (predictor == 2) return TiffPredictor(data, colors, bpc, rowBytes);
        if (predictor < 10) return data; // unknown predictor: leave the data alone

        var output = new MemoryStream(data.Length);
        var prior = new byte[rowBytes];
        var row = new byte[rowBytes];
        int pos = 0;
        while (pos < data.Length)
        {
            int type = data[pos++];
            int n = Math.Min(rowBytes, data.Length - pos);
            Array.Clear(row);
            Array.Copy(data, pos, row, 0, n);
            pos += n;
            for (int i = 0; i < rowBytes; i++)
            {
                int left = i >= bytesPerPixel ? row[i - bytesPerPixel] : 0;
                int up = prior[i];
                int upLeft = i >= bytesPerPixel ? prior[i - bytesPerPixel] : 0;
                row[i] = type switch
                {
                    1 => (byte)(row[i] + left),
                    2 => (byte)(row[i] + up),
                    3 => (byte)(row[i] + (left + up) / 2),
                    4 => (byte)(row[i] + Paeth(left, up, upLeft)),
                    _ => row[i], // 0 = none; unknown types are left as they are
                };
            }
            output.Write(row, 0, n);
            (prior, row) = (row, prior);
        }
        return output.ToArray();
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static byte[] TiffPredictor(byte[] data, int colors, int bpc, int rowBytes)
    {
        if (bpc != 8) return data; // only the common 8-bit case is undone
        var output = (byte[])data.Clone();
        for (int rowStart = 0; rowStart < output.Length; rowStart += rowBytes)
        {
            int rowEnd = Math.Min(output.Length, rowStart + rowBytes);
            for (int i = rowStart + colors; i < rowEnd; i++)
                output[i] = (byte)(output[i] + output[i - colors]);
        }
        return output;
    }

    // ------------------------------------------------------------------ LZW

    internal static byte[] LzwDecode(byte[] data, int earlyChange)
    {
        var output = new MemoryStream(data.Length * 3);
        var table = new List<byte[]>(4096);
        void Reset()
        {
            table.Clear();
            for (int i = 0; i < 256; i++) table.Add(new[] { (byte)i });
            table.Add(Array.Empty<byte>()); // 256: clear
            table.Add(Array.Empty<byte>()); // 257: end of data
        }
        Reset();

        int codeLength = 9;
        long bitPos = 0;
        long totalBits = (long)data.Length * 8;
        byte[]? previous = null;
        while (true)
        {
            if (bitPos + codeLength > totalBits)
                throw new PdfFormatException("The LZW data ends without an end-of-data code: the stream is truncated.");
            int code = 0;
            for (int i = 0; i < codeLength; i++, bitPos++)
                code = code << 1 | (data[bitPos >> 3] >> (7 - (int)(bitPos & 7)) & 1);

            if (code == 256)
            {
                Reset();
                codeLength = 9;
                previous = null;
                continue;
            }
            if (code == 257) break;

            byte[] entry;
            if (code < table.Count && code != 256 && code != 257)
            {
                entry = table[code];
            }
            else if (code == table.Count && previous != null)
            {
                entry = new byte[previous.Length + 1];
                previous.CopyTo(entry, 0);
                entry[^1] = previous[0];
            }
            else
            {
                throw new PdfFormatException($"The LZW data contains the code {code}, which is not in its table: the stream is corrupt.");
            }

            output.Write(entry);
            if (output.Length > MaxDecodedBytes)
                throw new PdfFormatException("The LZW data expands past the decode limit: it is a decompression bomb, not page content.");
            if (previous != null && table.Count < 4096)
            {
                var added = new byte[previous.Length + 1];
                previous.CopyTo(added, 0);
                added[^1] = entry[0];
                table.Add(added);
            }
            previous = entry;
            int next = table.Count + (earlyChange == 0 ? 0 : 1);
            codeLength = next >= 2048 ? 12 : next >= 1024 ? 11 : next >= 512 ? 10 : 9;
        }
        return output.ToArray();
    }

    // ------------------------------------------------------------------ ASCII filters

    internal static byte[] AsciiHexDecode(byte[] data)
    {
        var output = new List<byte>(data.Length / 2);
        int high = -1;
        foreach (byte b in data)
        {
            if (b == '>') break;
            if (PdfLexer.IsWhitespace(b)) continue;
            if (!PdfLexer.IsHex(b))
                throw new PdfFormatException($"The ASCIIHex data contains the byte 0x{b:X2}, which is not a hex digit: the stream is corrupt.");
            int v = PdfLexer.HexValue(b);
            if (high < 0) high = v;
            else
            {
                output.Add((byte)(high << 4 | v));
                high = -1;
            }
        }
        if (high >= 0) output.Add((byte)(high << 4));
        return output.ToArray();
    }

    internal static byte[] Ascii85Decode(byte[] data)
    {
        var output = new MemoryStream(data.Length);
        Span<int> group = stackalloc int[5];
        int count = 0;
        int i = 0;
        if (data.Length >= 2 && data[0] == '<' && data[1] == '~') i = 2; // optional Adobe prefix
        for (; i < data.Length; i++)
        {
            byte b = data[i];
            if (PdfLexer.IsWhitespace(b)) continue;
            if (b == '~') break; // "~>" ends the data
            if (b == 'z')
            {
                if (count != 0) throw new PdfFormatException("The ASCII85 data has a 'z' inside a group: the stream is corrupt.");
                output.Write(stackalloc byte[4]);
                continue;
            }
            if (b < '!' || b > 'u')
                throw new PdfFormatException($"The ASCII85 data contains the byte 0x{b:X2}, which is outside its alphabet: the stream is corrupt.");
            group[count++] = b - '!';
            if (count == 5)
            {
                WriteGroup(output, group, 4);
                count = 0;
            }
        }
        if (count == 1) throw new PdfFormatException("The ASCII85 data ends with a single-character group, which encodes nothing: the stream is corrupt.");
        if (count > 1)
        {
            for (int k = count; k < 5; k++) group[k] = 84; // pad with 'u'
            WriteGroup(output, group, count - 1);
        }
        return output.ToArray();
    }

    private static void WriteGroup(MemoryStream output, Span<int> group, int bytes)
    {
        long value = 0;
        for (int k = 0; k < 5; k++) value = value * 85 + group[k];
        if (value > uint.MaxValue) throw new PdfFormatException("An ASCII85 group encodes a value over 2^32: the stream is corrupt.");
        for (int k = 0; k < bytes; k++) output.WriteByte((byte)(value >> (24 - 8 * k)));
    }

    internal static byte[] RunLengthDecode(byte[] data)
    {
        var output = new MemoryStream(data.Length * 2);
        int i = 0;
        while (i < data.Length)
        {
            int length = data[i++];
            if (length == 128) break;
            if (length < 128)
            {
                int n = length + 1;
                if (i + n > data.Length)
                    throw new PdfFormatException("A RunLength literal run promises more bytes than the stream holds: it is truncated.");
                output.Write(data, i, n);
                i += n;
            }
            else
            {
                if (i >= data.Length)
                    throw new PdfFormatException("A RunLength repeat run has no byte to repeat: the stream is truncated.");
                byte b = data[i++];
                for (int k = 0; k < 257 - length; k++) output.WriteByte(b);
            }
            if (output.Length > MaxDecodedBytes)
                throw new PdfFormatException("The RunLength data expands past the decode limit.");
        }
        return output.ToArray();
    }
}
