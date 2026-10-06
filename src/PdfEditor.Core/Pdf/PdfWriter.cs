using System.Globalization;
using System.Text;

namespace PdfEditor.Core.Pdf;

/// <summary>How a document is written.</summary>
internal sealed record PdfSaveOptions
{
    /// <summary>
    /// When set, the output is encrypted with AES-256 (revision 6) under these passwords, replacing
    /// any encryption the document had.
    /// </summary>
    public (string User, string Owner)? Encryption { get; init; }

    /// <summary>
    /// Write the output unencrypted even if the document is encrypted. Without this (or
    /// <see cref="Encryption"/>) an encrypted document stays encrypted: in the same scheme, under
    /// the same key, so the same passwords open it and the same permissions apply.
    /// </summary>
    public bool RemoveEncryption { get; init; }

    /// <summary>Stamp /ModDate and /Producer into the Info dictionary (what every editor does on save).</summary>
    public bool StampInfo { get; init; } = true;

    /// <summary>
    /// Pack every non-stream object into an object stream behind a cross-reference stream
    /// (PDF 1.5's compact layout). Not combinable with <see cref="Encryption"/>.
    /// </summary>
    public bool ObjectStreams { get; init; }
}

/// <summary>
/// Text the serialiser copies verbatim — used only for the placeholders a signature patches
/// once the bytes around them are fixed.
/// </summary>
internal sealed class PdfLiteral : PdfObject
{
    public string Text { get; }
    public PdfLiteral(string text) => Text = text;
}

internal sealed partial class PdfDocument
{
    /// <summary>The producer string stamped into documents this engine writes.</summary>
    public const string ProducerName = "reDACT";

    /// <summary>
    /// Writes the whole document afresh: every object reachable from the trailer, renumbered
    /// densely, with a classic cross-reference table. Objects nothing refers to are not written —
    /// which is what makes deleting a page or an annotation actually remove its bytes.
    /// <para>
    /// An encrypted document is written encrypted, in its own scheme and under its own key, with
    /// its own encryption dictionary and first file identifier: the passwords that opened it open
    /// the output, and its permissions carry over. Editing a protected document used to write it
    /// out unencrypted. <see cref="PdfSaveOptions.Encryption"/> replaces the encryption instead,
    /// and <see cref="PdfSaveOptions.RemoveEncryption"/> drops it.
    /// </para>
    /// </summary>
    public byte[] Save(PdfSaveOptions? options = null)
    {
        options ??= new PdfSaveOptions();
        var catalog = Catalog ?? throw new PdfFormatException("The document has no catalog to write.");

        PdfSecurityHandler? security = null;
        PdfDictionary? encryptDict = null;
        if (options.Encryption is null && !options.RemoveEncryption && _saveSecurity != null
            && Trailer.GetAsDictionary(PdfName.Encrypt) is { } kept)
        {
            security = _saveSecurity;
            encryptDict = kept;
        }
        else if (options.Encryption is { } pw)
        {
            (security, encryptDict) = PdfSecurityHandler.CreateAes256(pw.User, pw.Owner);
            // Revision 6 is ISO 32000-2's handler, published for 1.7 readers as Adobe extension level 8.
            var extensions = catalog.GetAsDictionary(PdfName.Of("Extensions")) ?? new PdfDictionary();
            var adbe = new PdfDictionary();
            adbe.Put(PdfName.Of("BaseVersion"), PdfName.Of("1.7"));
            adbe.Put(PdfName.Of("ExtensionLevel"), new PdfNumber(8));
            extensions.Put(PdfName.Of("ADBE"), adbe);
            catalog.Put(PdfName.Of("Extensions"), extensions);
        }

        if (options.StampInfo) StampInfo();

        var trailer = new PdfDictionary();
        trailer.Put(PdfName.Root, catalog);
        if (Info is { } info) trailer.Put(PdfName.Info, info);
        trailer.Put(PdfName.ID, NewId());
        if (encryptDict != null) trailer.Put(PdfName.Encrypt, encryptDict);

        var writer = new ObjectWriter(security);
        if (encryptDict != null)
        {
            writer.Register(encryptDict);
            writer.Unencrypted.Add(encryptDict);
        }
        writer.Register(catalog);
        if (Info is { } infoDict) writer.Register(infoDict);

        // A kept handler is one the document's own version already declares; only a new one may need more.
        string version = options.Encryption != null && string.CompareOrdinal(Version, "1.7") < 0 ? "1.7" : Version;
        if (options.ObjectStreams)
        {
            if (encryptDict != null) throw new NotSupportedException("Object streams cannot be combined with encryption here.");
            return SaveWithObjectStreams(writer, trailer, string.CompareOrdinal(version, "1.5") < 0 ? "1.5" : version);
        }
        var output = new MemoryStream();
        WriteAscii(output, $"%PDF-{version}\n%âãÏÓ\n");
        var offsets = writer.WriteAll(output);

        long xref = output.Position;
        WriteAscii(output, $"xref\n0 {offsets.Count + 1}\n0000000000 65535 f\r\n");
        foreach (var (_, offset) in offsets)
            WriteAscii(output, offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n\r\n");

        trailer.Put(PdfName.Size, new PdfNumber(offsets.Count + 1));
        WriteAscii(output, "trailer\n");
        writer.WriteObject(output, trailer, 0, 0, encrypt: false, topLevel: true);
        WriteAscii(output, $"\nstartxref\n{xref.ToString(CultureInfo.InvariantCulture)}\n%%EOF\n");
        return output.ToArray();
    }

    /// <summary>
    /// The compact layout: streams are written as ordinary objects, everything else is packed
    /// into one object stream, and a cross-reference stream (rather than a table) indexes both.
    /// </summary>
    private static byte[] SaveWithObjectStreams(ObjectWriter writer, PdfDictionary trailer, string version)
    {
        var output = new MemoryStream();
        WriteAscii(output, $"%PDF-{version}\n%\u00E2\u00E3\u00CF\u00D3\n");
        var packed = new MemoryStream();
        var entries = new SortedDictionary<int, (int Type, long Field2, int Field3)>();
        var header = new StringBuilder();
        int index = 0;
        // Writing an object can reach (and so queue) more; drain until nothing new appears.
        for (int i = 0; i < writer.Queue.Count; i++)
        {
            var obj = writer.Queue[i];
            var (number, _) = writer.Register(obj);
            if (obj is PdfStream)
            {
                entries[number] = (1, output.Position, 0);
                writer.WriteIndirect(output, obj, number, 0);
                continue;
            }
            header.Append(CultureInfo.InvariantCulture, $"{number} {packed.Position} ");
            writer.WriteObject(packed, obj, number, 0, encrypt: false, topLevel: true);
            packed.WriteByte((byte)'\n');
            entries[number] = (2, -1, index++);
        }

        int objStmNumber = writer.Queue.Count + 1;
        byte[] headerBytes = Encoding.ASCII.GetBytes(header.ToString());
        var objStm = new PdfStream(headerBytes.Concat(packed.ToArray()).ToArray());
        objStm.Put(PdfName.Type, PdfName.Of("ObjStm"));
        objStm.Put(PdfName.N, new PdfNumber(index));
        objStm.Put(PdfName.First, new PdfNumber(headerBytes.Length));
        entries[objStmNumber] = (1, output.Position, 0);
        writer.WriteIndirect(output, objStm, objStmNumber, 0);
        foreach (var key in entries.Keys.ToList())
            if (entries[key].Type == 2) entries[key] = (2, objStmNumber, entries[key].Field3);

        int xrefNumber = objStmNumber + 1;
        long xrefOffset = output.Position;
        entries[xrefNumber] = (1, xrefOffset, 0);
        var rows = new MemoryStream();
        rows.Write(new byte[] { 0, 0, 0, 0, 0, 0xFF, 0xFF }); // object 0, the head of the free list
        for (int n = 1; n <= xrefNumber; n++)
        {
            var (type, f2, f3) = entries.TryGetValue(n, out var e) ? e : (0, 0L, 0);
            rows.WriteByte((byte)type);
            for (int k = 3; k >= 0; k--) rows.WriteByte((byte)(f2 >> (8 * k)));
            rows.WriteByte((byte)(f3 >> 8));
            rows.WriteByte((byte)f3);
        }
        var xref = new PdfStream(rows.ToArray());
        foreach (var key in trailer.Keys) xref.Put(key, trailer.GetRaw(key));
        xref.Put(PdfName.Type, PdfName.XRef);
        xref.Put(PdfName.Size, new PdfNumber(xrefNumber + 1));
        xref.Put(PdfName.W, new PdfArray(1, 4, 2));
        writer.WriteIndirect(output, xref, xrefNumber, 0);
        WriteAscii(output, $"startxref\n{xrefOffset.ToString(CultureInfo.InvariantCulture)}\n%%EOF\n");
        return output.ToArray();
    }

    /// <summary>Sets /ModDate and /Producer, and /CreationDate on a brand-new document.</summary>
    private void StampInfo()
    {
        var info = GetOrCreateInfo();
        var now = new PdfString(Encoding.ASCII.GetBytes(FormatDate(DateTimeOffset.Now)));
        if (IsNew && !info.ContainsKey(PdfName.CreationDate)) info.Put(PdfName.CreationDate, now);
        info.Put(PdfName.ModDate, new PdfString(now.Bytes));
        info.Put(PdfName.Producer, PdfString.FromText(ProducerName));
    }

    /// <summary>A PDF date string (§7.9.4): <c>D:YYYYMMDDHHmmSS+HH'mm'</c>.</summary>
    internal static string FormatDate(DateTimeOffset when)
    {
        var offset = when.Offset;
        string sign = offset < TimeSpan.Zero ? "-" : "+";
        offset = offset.Duration();
        return "D:" + when.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)
            + $"{sign}{offset.Hours:D2}'{offset.Minutes:D2}'";
    }

    /// <summary>
    /// The file identifier: the first half is kept from the source (it names the document across
    /// revisions), the second is fresh (it names this revision).
    /// </summary>
    private PdfArray NewId()
    {
        byte[] fresh = PdfCrypto.RandomBytes(16);
        byte[] first = Trailer.GetAsArray(PdfName.ID)?.GetAsString(0)?.Bytes is { Length: > 0 } existing ? existing : fresh;
        return new PdfArray(new PdfObject[] { new PdfString(first, isHex: true), new PdfString(fresh, isHex: true) });
    }

    private static void WriteAscii(Stream output, string text)
    {
        var bytes = Encoding.Latin1.GetBytes(text);
        output.Write(bytes, 0, bytes.Length);
    }

    // ------------------------------------------------------------------ incremental update

    /// <summary>
    /// Appends an incremental update to the original bytes (§7.5.6): <paramref name="changed"/>
    /// are written under their existing object numbers, along with any new objects they reach,
    /// and everything before is left byte-for-byte as it was — which is what keeps earlier digital
    /// signatures valid. An encrypted source stays encrypted, in its own scheme.
    /// </summary>
    public byte[] SaveIncremental(IEnumerable<PdfObject> changed)
    {
        if (IsNew) throw new InvalidOperationException("Only a document opened from bytes can be updated incrementally.");
        int prevXref = PreviousStartXref();
        int originalLimit = _originalObjectLimit; // objects numbered from here on were added since opening

        // Unchanged objects of the original are referred to, never rewritten; anything new is.
        var changedSet = new HashSet<PdfObject>(changed, ReferenceEqualityComparer.Instance);
        var writer = new ObjectWriter(_security, obj =>
        {
            bool original = obj.Reference is { } r && r.Owner == this && r.Number < originalLimit;
            if (!original) MakeIndirect(obj);
            var reference = obj.Reference!;
            return (reference.Number, reference.Generation, Write: !original || changedSet.Contains(obj));
        });
        foreach (var obj in changedSet) writer.Register(obj);

        var output = new MemoryStream();
        output.Write(_data, 0, _data.Length);
        if (_data.Length > 0 && _data[^1] != '\n') output.WriteByte((byte)'\n');
        var offsets = writer.WriteAll(output);

        int size = Math.Max(ObjectNumberLimit, offsets.Count == 0 ? 1 : offsets.Keys.Max() + 1);
        var trailer = new PdfDictionary();
        foreach (var key in new[] { PdfName.Root, PdfName.Info, PdfName.ID, PdfName.Encrypt })
            if (Trailer.GetRaw(key) is { } value) trailer.Put(key, value);
        trailer.Put(PdfName.Prev, new PdfNumber(prevXref));

        long xrefOffset = output.Position;
        if (PreviousSectionIsStream(prevXref))
            WriteXrefStream(output, offsets, trailer, size, writer);
        else
            WriteXrefTable(output, offsets, trailer, size, writer);
        WriteAscii(output, $"\nstartxref\n{xrefOffset.ToString(CultureInfo.InvariantCulture)}\n%%EOF\n");
        return output.ToArray();
    }

    /// <summary>A classic cross-reference table and trailer for the update.</summary>
    private static void WriteXrefTable(Stream output, SortedDictionary<int, long> offsets, PdfDictionary trailer,
        int size, ObjectWriter writer)
    {
        trailer.Put(PdfName.Size, new PdfNumber(size));
        WriteAscii(output, "xref\n");
        foreach (var (start, count) in Runs(offsets.Keys))
        {
            WriteAscii(output, $"{start} {count}\n");
            for (int n = start; n < start + count; n++)
                WriteAscii(output, offsets[n].ToString("D10", CultureInfo.InvariantCulture) + " 00000 n\r\n");
        }
        WriteAscii(output, "trailer\n");
        writer.WriteObject(output, trailer, 0, 0, encrypt: false, topLevel: true);
    }

    private int PreviousStartXref()
    {
        int at = LastIndexOf(_data, "startxref"u8, Math.Max(0, _data.Length - 4096));
        if (at < 0) at = LastIndexOf(_data, "startxref"u8, 0);
        var lexer = new PdfLexer(_data, Math.Max(0, at + 9));
        if (at < 0 || !lexer.Next() || lexer.TokenType != PdfTokenType.Number)
            throw new PdfFormatException("The document has no usable startxref, so it cannot be updated incrementally.");
        return (int)lexer.NumberValue;
    }

    private bool PreviousSectionIsStream(int offset)
    {
        if (offset <= 0 || offset >= _data.Length) return false;
        var lexer = new PdfLexer(_data, offset);
        lexer.SkipWhitespace();
        return !lexer.MatchesKeywordAt(lexer.Position, "xref");
    }

    private static IEnumerable<(int Start, int Count)> Runs(IEnumerable<int> sortedNumbers)
    {
        int start = -1, count = 0;
        foreach (int n in sortedNumbers)
        {
            if (start >= 0 && n == start + count) { count++; continue; }
            if (start >= 0) yield return (start, count);
            start = n;
            count = 1;
        }
        if (start >= 0) yield return (start, count);
    }

    /// <summary>A cross-reference stream for the update, for sources whose own table is a stream.</summary>
    private static void WriteXrefStream(Stream output, SortedDictionary<int, long> offsets, PdfDictionary trailer,
        int size, ObjectWriter writer)
    {
        int xrefNumber = size; // the stream is itself an object of the update
        offsets[xrefNumber] = output.Position;
        var rows = new MemoryStream();
        var index = new PdfArray();
        foreach (var (start, count) in Runs(offsets.Keys))
        {
            index.Add(new PdfNumber(start));
            index.Add(new PdfNumber(count));
            for (int n = start; n < start + count; n++)
            {
                long off = offsets[n];
                rows.WriteByte(1);
                for (int k = 3; k >= 0; k--) rows.WriteByte((byte)(off >> (8 * k)));
                rows.WriteByte(0);
                rows.WriteByte(0);
            }
        }
        var stream = new PdfStream(rows.ToArray(), compress: false);
        foreach (var key in trailer.Keys) stream.Put(key, trailer.GetRaw(key));
        stream.Put(PdfName.Type, PdfName.XRef);
        stream.Put(PdfName.Size, new PdfNumber(size + 1));
        stream.Put(PdfName.Index, index);
        stream.Put(PdfName.W, new PdfArray(1, 4, 2));
        writer.Unencrypted.Add(stream); // cross-reference streams are never encrypted
        writer.WriteIndirect(output, stream, xrefNumber, 0);
    }

    /// <summary>
    /// Serialises objects. Each indirect object is numbered the first time it is reached; a
    /// stream that was never made indirect becomes indirect here, since streams cannot be direct.
    /// </summary>
    private sealed class ObjectWriter
    {
        private readonly PdfSecurityHandler? _security;
        private readonly Dictionary<PdfObject, (int Number, int Generation)> _numbers = new(ReferenceEqualityComparer.Instance);
        private readonly List<PdfObject> _queue = new();
        private readonly Func<PdfObject, (int Number, int Generation, bool Write)>? _assign;

        /// <summary>Objects whose strings and streams are written in the clear (the encryption dictionary).</summary>
        public HashSet<PdfObject> Unencrypted { get; } = new(ReferenceEqualityComparer.Instance);

        /// <param name="assign">
        /// For incremental updates: the number an object keeps and whether it must be written.
        /// Without it, objects are numbered densely in the order they are reached.
        /// </param>
        public ObjectWriter(PdfSecurityHandler? security,
            Func<PdfObject, (int Number, int Generation, bool Write)>? assign = null)
        {
            _security = security;
            _assign = assign;
        }

        /// <summary>Everything queued for writing so far (it grows as written objects reach new ones).</summary>
        public IReadOnlyList<PdfObject> Queue => _queue;

        /// <summary>Gives <paramref name="obj"/> its object number (once), queueing it if it must be written.</summary>
        public (int Number, int Generation) Register(PdfObject obj)
        {
            if (_numbers.TryGetValue(obj, out var id)) return id;
            if (_assign == null)
            {
                id = (_numbers.Count + 1, 0);
                _queue.Add(obj);
            }
            else
            {
                var (number, generation, write) = _assign(obj);
                id = (number, generation);
                if (write) _queue.Add(obj);
            }
            _numbers[obj] = id;
            return id;
        }

        /// <summary>Writes every queued object (and everything new they reach); returns offsets by number.</summary>
        public SortedDictionary<int, long> WriteAll(Stream output)
        {
            var offsets = new SortedDictionary<int, long>();
            for (int i = 0; i < _queue.Count; i++)
            {
                var obj = _queue[i];
                var (number, generation) = _numbers[obj];
                offsets[number] = output.Position;
                WriteIndirect(output, obj, number, generation);
            }
            return offsets;
        }

        public void WriteIndirect(Stream output, PdfObject obj, int number, int generation)
        {
            WriteAscii(output, $"{number.ToString(CultureInfo.InvariantCulture)} {generation.ToString(CultureInfo.InvariantCulture)} obj\n");
            bool encrypt = _security != null && !Unencrypted.Contains(obj);
            if (obj is PdfStream stream)
            {
                byte[] data = encrypt && !stream.Is(PdfName.XRef) && !_security!.StoresInClear(stream)
                    ? _security.Encrypt(stream.RawData, number, generation, isStream: true)
                    : stream.RawData;
                var dict = new PdfDictionary();
                foreach (var key in stream.Keys)
                    if (!key.Equals(PdfName.Length)) dict.Put(key, stream.GetRaw(key));
                dict.Put(PdfName.Length, new PdfNumber(data.Length));
                WriteObject(output, dict, number, generation, encrypt, topLevel: true);
                WriteAscii(output, "\nstream\n");
                output.Write(data, 0, data.Length);
                WriteAscii(output, "\nendstream");
            }
            else
            {
                WriteObject(output, obj, number, generation, encrypt, topLevel: true);
            }
            WriteAscii(output, "\nendobj\n");
        }

        public void WriteObject(Stream output, PdfObject obj, int number, int generation, bool encrypt,
            bool topLevel, int depth = 0)
        {
            if (depth > PdfObjectParser.MaxNesting)
                throw new PdfFormatException("The document nests objects too deeply to be written.");

            if (!topLevel)
            {
                if (TryWriteAsReference(output, obj, out var direct)) return;
                obj = direct;
            }

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
                    if (encrypt && _security != null)
                        WriteString(output, _security.Encrypt(s.Bytes, number, generation, isStream: false), hex: true);
                    else
                        WriteString(output, s.Bytes, s.IsHex);
                    break;
                case PdfName name:
                    WriteName(output, name);
                    break;
                case PdfLiteral literal:
                    WriteAscii(output, literal.Text);
                    break;
                case PdfArray array:
                    WriteArray(output, array, number, generation, encrypt, depth);
                    break;
                case PdfDictionary dict:
                    WriteDictionary(output, dict, number, generation, encrypt, depth);
                    break;
            }
        }

        /// <summary>
        /// A nested value that is an indirect object (or a stream, which must be one) is written as
        /// a reference to it. Otherwise nothing is written and <paramref name="direct"/> is the
        /// value to write in place.
        /// </summary>
        private bool TryWriteAsReference(Stream output, PdfObject obj, out PdfObject direct)
        {
            var target = PdfReference.Deref(obj);
            direct = target;
            if (obj is PdfReference && target is PdfNull)
            {
                WriteAscii(output, "null");
                return true;
            }
            if (obj is PdfReference || target.IsIndirect || target is PdfStream || _numbers.ContainsKey(target))
            {
                var (n, g) = Register(target);
                WriteAscii(output, $"{n.ToString(CultureInfo.InvariantCulture)} {g.ToString(CultureInfo.InvariantCulture)} R");
                return true;
            }
            return false;
        }

        private void WriteArray(Stream output, PdfArray array, int number, int generation, bool encrypt, int depth)
        {
            output.WriteByte((byte)'[');
            for (int i = 0; i < array.Count; i++)
            {
                if (i > 0) output.WriteByte((byte)' ');
                WriteObject(output, array.GetRaw(i), number, generation, encrypt, false, depth + 1);
            }
            output.WriteByte((byte)']');
        }

        private void WriteDictionary(Stream output, PdfDictionary dict, int number, int generation, bool encrypt, int depth)
        {
            // A signature's /Contents is never encrypted: it signs the encrypted bytes.
            bool isSignature = dict.ContainsKey(PdfName.ByteRange) && dict.ContainsKey(PdfName.Contents);
            WriteAscii(output, "<<");
            foreach (var key in dict.Keys)
            {
                WriteName(output, key);
                output.WriteByte((byte)' ');
                bool encryptValue = encrypt && !(isSignature && key.Equals(PdfName.Contents));
                WriteObject(output, dict.GetRaw(key)!, number, generation, encryptValue, false, depth + 1);
            }
            WriteAscii(output, ">>");
        }

        private static void WriteString(Stream output, byte[] bytes, bool hex)
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
                        WriteAscii(output, "\\r"); // a raw CR would be read back as LF
                        break;
                    default:
                        output.WriteByte(b);
                        break;
                }
            }
            output.WriteByte((byte)')');
        }

        internal static void WriteName(Stream output, PdfName name)
        {
            output.WriteByte((byte)'/');
            foreach (byte b in Encoding.UTF8.GetBytes(name.Value))
            {
                if (b < 0x21 || b > 0x7E || b == '#' || PdfLexer.IsDelimiter(b))
                    WriteAscii(output, "#" + b.ToString("X2", CultureInfo.InvariantCulture));
                else
                    output.WriteByte(b);
            }
        }
    }
}
