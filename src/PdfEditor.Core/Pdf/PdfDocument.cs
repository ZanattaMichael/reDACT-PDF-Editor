using System.Globalization;
using System.Text;

namespace PdfEditor.Core.Pdf;

/// <summary>
/// A PDF document: the object table, the trailer, and the page list. Opens existing files —
/// tolerating the damage real files carry, rebuilding a broken cross-reference table by scanning —
/// and creates new ones. Objects load lazily on first access and are cached.
/// </summary>
internal sealed partial class PdfDocument
{
    private readonly byte[] _data;
    private readonly Dictionary<int, XrefEntry> _xref = new();
    private readonly Dictionary<int, PdfObject> _objects = new();
    private readonly Dictionary<(int, int), PdfReference> _references = new();
    private readonly HashSet<int> _loading = new();
    private readonly Dictionary<int, ObjectStream> _objectStreams = new();
    private readonly List<int> _pendingObjectStreams = new();
    private PdfSecurityHandler? _security;
    private int _encryptObjectNumber = -1;
    private int _nextObjectNumber = 1;
    private int _originalObjectLimit;
    private List<PdfPage>? _pages;

    private readonly record struct XrefEntry(int Type, long Offset, int Generation, int StreamNumber, int Index);

    private sealed class ObjectStream
    {
        public required byte[] Data { get; init; }
        public required int First { get; init; }
        public required List<(int Number, int Offset)> Entries { get; init; }
    }

    private PdfDocument(byte[] data)
    {
        _data = data;
        Trailer = new PdfDictionary();
    }

    /// <summary>The trailer dictionary (merged across incremental updates, newest first).</summary>
    public PdfDictionary Trailer { get; private set; }

    /// <summary>The header version, e.g. "1.7".</summary>
    public string Version { get; set; } = "1.7";

    /// <summary>True when the cross-reference data was unusable and was rebuilt by scanning the file.</summary>
    public bool XrefRebuilt { get; private set; }

    /// <summary>Whether the source file was encrypted.</summary>
    public bool WasEncrypted => _security != null;

    /// <summary>The bytes the document was opened from (empty for a new document).</summary>
    internal byte[] SourceBytes => _data;

    /// <summary>The highest object number in use, plus one.</summary>
    public int ObjectNumberLimit => Math.Max(_nextObjectNumber, _xref.Count == 0 ? 1 : _xref.Keys.Max() + 1);

    // ------------------------------------------------------------------ creating and opening

    /// <summary>A new, empty document: a catalog and an empty page tree.</summary>
    public static PdfDocument CreateNew()
    {
        var doc = new PdfDocument(Array.Empty<byte>());
        var pages = new PdfDictionary();
        pages.Put(PdfName.Type, PdfName.Pages);
        pages.Put(PdfName.Kids, new PdfArray());
        pages.Put(PdfName.Count, new PdfNumber(0));
        doc.MakeIndirect(pages);
        var catalog = new PdfDictionary();
        catalog.Put(PdfName.Type, PdfName.Catalog);
        catalog.Put(PdfName.Pages, pages);
        doc.MakeIndirect(catalog);
        doc.Trailer.Put(PdfName.Root, catalog);
        doc._pages = new List<PdfPage>();
        doc.IsNew = true;
        return doc;
    }

    /// <summary>True for a document created in memory rather than opened from bytes.</summary>
    public bool IsNew { get; private set; }

    /// <summary>
    /// Opens a PDF. <paramref name="password"/> may be the user or the owner password; documents
    /// encrypted only to restrict permissions open without one.
    /// </summary>
    public static PdfDocument Open(byte[] data, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        var doc = new PdfDocument(data);
        doc.ReadHeader();

        bool readXref;
        try
        {
            readXref = doc.ReadXrefChain();
        }
        catch (PdfFormatException)
        {
            readXref = false;
        }
        if (!readXref || !doc.XrefLooksValid()) doc.Rebuild();

        doc.SetUpSecurity(password);
        if (doc.Catalog == null && !doc.XrefRebuilt)
        {
            doc.Rebuild();
            doc.SetUpSecurity(password);
        }
        if (doc.Catalog == null)
            throw new PdfFormatException("The document has no usable catalog (its trailer's /Root does not name a dictionary).");
        doc.IndexPendingObjectStreams();
        doc._originalObjectLimit = doc.ObjectNumberLimit;
        return doc;
    }

    private void ReadHeader()
    {
        int limit = Math.Min(_data.Length, 1024);
        int at = IndexOf(_data, "%PDF-"u8, 0, limit);
        if (at < 0)
        {
            if (IndexOf(_data, "obj"u8, 0, Math.Min(_data.Length, 4096)) < 0)
                throw new PdfFormatException("The file does not start with a PDF header ('%PDF-') and contains no PDF objects; it is not a PDF.");
            return; // no header, but objects: let repair try
        }
        int p = at + 5;
        var sb = new StringBuilder();
        while (p < _data.Length && sb.Length < 4 && (char.IsAsciiDigit((char)_data[p]) || _data[p] == '.'))
            sb.Append((char)_data[p++]);
        if (sb.Length >= 3) Version = sb.ToString();
    }

    // ------------------------------------------------------------------ cross-reference data

    private bool ReadXrefChain()
    {
        int tail = Math.Max(0, _data.Length - 4096);
        int at = LastIndexOf(_data, "startxref"u8, tail);
        if (at < 0) at = LastIndexOf(_data, "startxref"u8, 0);
        if (at < 0) return false;

        var lexer = new PdfLexer(_data, at + 9);
        if (!lexer.Next() || lexer.TokenType != PdfTokenType.Number) return false;
        long offset = (long)lexer.NumberValue;

        var seen = new HashSet<long>();
        bool first = true;
        while (offset > 0 && offset < _data.Length && seen.Add(offset) && seen.Count < 1024)
        {
            PdfDictionary? section = ReadXrefSection(offset);
            if (section == null) return false;
            MergeTrailer(section, first);
            first = false;

            // A hybrid-reference file also lists its compressed objects in a stream (§7.5.8.4).
            if (section.GetAsNumber(PdfName.XRefStm) is { } stm && stm.LongValue() > 0 && seen.Add(stm.LongValue()))
                ReadXrefSection(stm.LongValue());

            var prev = section.GetRaw(PdfName.Prev) as PdfNumber;
            offset = prev?.LongValue() ?? 0;
        }
        return Trailer.ContainsKey(PdfName.Root);
    }

    private void MergeTrailer(PdfDictionary section, bool newest)
    {
        foreach (var key in section.Keys)
        {
            if (key.Equals(PdfName.Prev) || key.Equals(PdfName.XRefStm) || key.Equals(PdfName.Filter)
                || key.Equals(PdfName.DecodeParms) || key.Equals(PdfName.Length) || key.Equals(PdfName.Index)
                || key.Equals(PdfName.W) || key.Equals(PdfName.Type))
                continue;
            if (newest || !Trailer.ContainsKey(key)) Trailer.Put(key, section.GetRaw(key));
        }
    }

    /// <summary>Reads a classic table or a cross-reference stream at <paramref name="offset"/>; returns its trailer.</summary>
    private PdfDictionary? ReadXrefSection(long offset)
    {
        var lexer = new PdfLexer(_data, (int)offset);
        lexer.SkipWhitespace();
        if (lexer.MatchesKeywordAt(lexer.Position, "xref")) return ReadXrefTable(lexer);
        return ReadXrefStream((int)offset);
    }

    private PdfDictionary? ReadXrefTable(PdfLexer lexer)
    {
        lexer.Next(); // "xref"
        while (true)
        {
            if (!lexer.Next()) return null;
            if (lexer.TokenType == PdfTokenType.Keyword && lexer.Text == "trailer") break;
            if (!ReadXrefSubsection(lexer)) return null;
        }
        var parser = new PdfObjectParser(lexer, this);
        return parser.ParseObject() as PdfDictionary;
    }

    /// <summary>Reads one subsection: "start count", then count entries. False when it is malformed.</summary>
    private bool ReadXrefSubsection(PdfLexer lexer)
    {
        if (lexer.TokenType != PdfTokenType.Number) return false;
        long start = (long)lexer.NumberValue;
        if (!NextIs(lexer, PdfTokenType.Number)) return false;
        long count = (long)lexer.NumberValue;
        if (start < 0 || count < 0 || count > 10_000_000 || start + count > int.MaxValue) return false;

        for (long i = 0; i < count; i++)
            if (!ReadXrefTableEntry(lexer, (int)(start + i))) return false;
        return true;
    }

    /// <summary>Reads one "offset generation n|f" entry. False when it is malformed.</summary>
    private bool ReadXrefTableEntry(PdfLexer lexer, int number)
    {
        if (!NextIs(lexer, PdfTokenType.Number)) return false;
        long entryOffset = (long)lexer.NumberValue;
        if (!NextIs(lexer, PdfTokenType.Number)) return false;
        int generation = (int)Math.Min(lexer.NumberValue, 65535);
        if (!NextIs(lexer, PdfTokenType.Keyword)) return false;
        // Older sections are read after newer ones: an entry already present is newer.
        if (_xref.ContainsKey(number)) return true;
        if (lexer.Text == "n") _xref[number] = new XrefEntry(1, entryOffset, generation, 0, 0);
        else if (lexer.Text == "f") _xref[number] = new XrefEntry(0, 0, generation, 0, 0);
        else return false;
        return true;
    }

    private static bool NextIs(PdfLexer lexer, PdfTokenType type) => lexer.Next() && lexer.TokenType == type;

    private PdfDictionary? ReadXrefStream(int offset)
    {
        var (obj, _) = ParseIndirectObjectAt(offset, expectedNumber: null, decrypt: false);
        if (obj is not PdfStream stream || !PdfName.XRef.Equals(stream.GetAsName(PdfName.Type))) return null;
        if (XrefFieldWidths(stream) is not { } widths) return null;

        byte[] rows;
        try
        {
            rows = stream.GetDecodedBytes();
        }
        catch (PdfFormatException)
        {
            return null;
        }

        ReadXrefStreamRows(rows, XrefRanges(stream), widths);
        // The stream dictionary doubles as the trailer.
        var trailer = new PdfDictionary();
        foreach (var key in stream.Keys) trailer.Put(key, stream.GetRaw(key));
        return trailer;
    }

    /// <summary>The /W field widths; null when they are missing, out of range, or all zero.</summary>
    private static (int W0, int W1, int W2)? XrefFieldWidths(PdfStream stream)
    {
        var w = stream.GetAsArray(PdfName.W);
        if (w == null || w.Count < 3) return null;
        int w0 = w.GetAsNumber(0)?.IntValue() ?? 0, w1 = w.GetAsNumber(1)?.IntValue() ?? 0, w2 = w.GetAsNumber(2)?.IntValue() ?? 0;
        if (w0 is < 0 or > 8 || w1 is < 0 or > 8 || w2 is < 0 or > 8) return null;
        if (w0 + w1 + w2 == 0) return null;
        return (w0, w1, w2);
    }

    /// <summary>The object number ranges the rows cover: /Index, or every object up to /Size.</summary>
    private static List<(long Start, long Count)> XrefRanges(PdfStream stream)
    {
        int size = stream.GetAsInt(PdfName.Size) ?? 0;
        var index = stream.GetAsArray(PdfName.Index);
        var ranges = new List<(long Start, long Count)>();
        if (index != null && index.Count >= 2)
            for (int i = 0; i + 1 < index.Count; i += 2)
                ranges.Add(((long)index.GetNumber(i), (long)index.GetNumber(i + 1)));
        else
            ranges.Add((0, size));
        return ranges;
    }

    private void ReadXrefStreamRows(byte[] rows, List<(long Start, long Count)> ranges, (int W0, int W1, int W2) widths)
    {
        int rowSize = widths.W0 + widths.W1 + widths.W2;
        int pos = 0;
        foreach (var (start, count) in ranges)
        {
            for (long i = 0; i < count && pos + rowSize <= rows.Length; i++, pos += rowSize)
            {
                long number = start + i;
                if (number < 0 || number > int.MaxValue || _xref.ContainsKey((int)number)) continue;
                _xref[(int)number] = XrefStreamEntry(rows, pos, widths);
            }
        }
    }

    private static XrefEntry XrefStreamEntry(byte[] rows, int pos, (int W0, int W1, int W2) widths)
    {
        long type = XrefField(rows, pos, widths.W0, 1);
        long f2 = XrefField(rows, pos + widths.W0, widths.W1, 0);
        long f3 = XrefField(rows, pos + widths.W0 + widths.W1, widths.W2, 0);
        return type switch
        {
            0 => new XrefEntry(0, 0, (int)Math.Min(f3, 65535), 0, 0),
            1 => new XrefEntry(1, f2, (int)Math.Min(f3, 65535), 0, 0),
            2 => new XrefEntry(2, 0, 0, (int)Math.Min(f2, int.MaxValue), (int)Math.Min(f3, int.MaxValue)),
            _ => new XrefEntry(0, 0, 0, 0, 0), // unknown types are free entries (§7.5.8.3)
        };
    }

    private static long XrefField(byte[] rows, int at, int width, long fallback)
    {
        if (width == 0) return fallback;
        long v = 0;
        for (int k = 0; k < width; k++) v = v << 8 | rows[at + k];
        return v;
    }

    /// <summary>
    /// Checks that every in-use entry points at the header of the object it claims. Parsers that
    /// repair silently hide a stale table; this one rebuilds instead, and records that it did.
    /// </summary>
    private bool XrefLooksValid()
    {
        foreach (var (number, entry) in _xref)
        {
            if (entry.Type != 1) continue;
            if (entry.Offset <= 0 || entry.Offset >= _data.Length) return false;
            var lexer = new PdfLexer(_data, (int)entry.Offset);
            if (!lexer.Next() || lexer.TokenType != PdfTokenType.Number || (int)lexer.NumberValue != number) return false;
            if (!lexer.Next() || lexer.TokenType != PdfTokenType.Number) return false;
            if (!lexer.Next() || lexer.TokenType != PdfTokenType.Keyword || lexer.Text != "obj") return false;
        }
        return true;
    }

    /// <summary>
    /// Rebuilds the object table by scanning the whole file for <c>n g obj</c> headers — the
    /// standard recovery when the cross-reference data is missing, stale or corrupt. Each object
    /// found is parsed so a stream's body is skipped rather than scanned: a PDF attached inside
    /// this one carries its own "1 0 obj", and treating that as a definition would let an
    /// attachment silently replace this document's objects.
    /// </summary>
    private void Rebuild()
    {
        XrefRebuilt = true;
        _xref.Clear();
        _objects.Clear();
        _objectStreams.Clear();
        _pendingObjectStreams.Clear();
        var scan = new RebuildScan();

        int pos = 0;
        while (pos < _data.Length)
        {
            int obj = IndexOf(_data, "obj"u8, pos, _data.Length);
            int trailerAt = IndexOf(_data, "trailer"u8, pos, _data.Length);
            if (trailerAt >= 0 && (obj < 0 || trailerAt < obj))
                pos = RecoverTrailerAt(trailerAt, scan);
            else if (obj < 0)
                break;
            else
                pos = RecoverObjectAt(obj, scan);
        }

        // A file with neither a trailer nor a cross-reference stream anywhere has lost its end: it
        // is truncated, and whatever objects survive are a fragment, not a document to edit.
        if (!scan.FoundTrailer)
            throw new PdfFormatException("The file has no trailer and no cross-reference data anywhere: it is truncated or is not a complete PDF.");
        Trailer = scan.Trailer;
        if (!IsUsableRoot(Trailer.GetRaw(PdfName.Root)) && scan.LastCatalog is int catalog)
            Trailer.Put(PdfName.Root, GetReference(catalog, _xref[catalog].Generation));
        if (_xref.Count == 0)
            throw new PdfFormatException("No PDF objects could be found anywhere in the file; it is not a readable PDF.");
    }

    /// <summary>What a rebuild has found so far besides the objects themselves.</summary>
    private sealed class RebuildScan
    {
        public PdfDictionary Trailer { get; } = new();
        public bool FoundTrailer { get; set; }
        public int? LastCatalog { get; set; }
    }

    /// <summary>Merges the trailer dictionary after the keyword at <paramref name="trailerAt"/>; returns where the scan resumes.</summary>
    private int RecoverTrailerAt(int trailerAt, RebuildScan scan)
    {
        var lexer = new PdfLexer(_data, trailerAt + 7);
        if (new PdfObjectParser(lexer, this).ParseObject() is PdfDictionary t)
        {
            scan.FoundTrailer = true;
            foreach (var key in t.Keys) scan.Trailer.Put(key, t.GetRaw(key));
        }
        return Math.Max(lexer.Position, trailerAt + 7);
    }

    /// <summary>
    /// Records the object whose "obj" keyword is at <paramref name="obj"/>, when that keyword
    /// really opens one; returns where the scan resumes.
    /// </summary>
    private int RecoverObjectAt(int obj, RebuildScan scan)
    {
        int pos = obj + 3;
        if (obj > 0 && _data[obj - 1] == 'd') return pos; // "endobj"
        if (obj + 3 < _data.Length && PdfLexer.IsRegular(_data[obj + 3])) return pos;
        int headerStart = HeaderStart(obj, out int number, out int generation);
        if (headerStart < 0) return pos;

        PdfObject? parsed;
        int end;
        try
        {
            (parsed, end) = ParseIndirectObjectAt(headerStart, number, decrypt: false, scanning: true);
        }
        catch (PdfFormatException)
        {
            return pos;
        }
        _xref[number] = new XrefEntry(1, headerStart, generation, 0, 0);
        if (parsed is PdfDictionary d) NoteRecoveredDictionary(d, number, scan);
        return Math.Max(pos, end);
    }

    private void NoteRecoveredDictionary(PdfDictionary d, int number, RebuildScan scan)
    {
        if (d.Is(PdfName.Catalog)) scan.LastCatalog = number;
        if (d is PdfStream s && s.Is(PdfName.Of("ObjStm"))) _pendingObjectStreams.Add(number);
        if (d is not PdfStream x || !x.Is(PdfName.XRef)) return;
        scan.FoundTrailer = true;
        foreach (var key in x.Keys)
            if (key.Equals(PdfName.Root) || key.Equals(PdfName.Info) || key.Equals(PdfName.ID) || key.Equals(PdfName.Encrypt))
                scan.Trailer.Put(key, x.GetRaw(key));
    }

    private bool IsUsableRoot(PdfObject? root) =>
        root is PdfReference r && _xref.TryGetValue(r.Number, out var e) && e.Type != 0;

    /// <summary>Walks back from an "obj" keyword over "n g"; returns where the header starts, or -1.</summary>
    private int HeaderStart(int objKeyword, out int number, out int generation)
    {
        number = generation = 0;
        int p = objKeyword - 1;
        while (p >= 0 && PdfLexer.IsWhitespace(_data[p])) p--;
        int genEnd = p + 1;
        while (p >= 0 && char.IsAsciiDigit((char)_data[p])) p--;
        if (p + 1 == genEnd || genEnd - (p + 1) > 5) return -1;
        generation = int.Parse(Encoding.ASCII.GetString(_data, p + 1, genEnd - p - 1), CultureInfo.InvariantCulture);
        int afterNum = p;
        while (p >= 0 && PdfLexer.IsWhitespace(_data[p])) p--;
        if (p == afterNum) return -1; // no whitespace between the numbers
        int numEnd = p + 1;
        while (p >= 0 && char.IsAsciiDigit((char)_data[p])) p--;
        if (p + 1 == numEnd || numEnd - (p + 1) > 9) return -1;
        if (p >= 0 && PdfLexer.IsRegular(_data[p])) return -1;
        number = int.Parse(Encoding.ASCII.GetString(_data, p + 1, numEnd - p - 1), CultureInfo.InvariantCulture);
        return p + 1;
    }

    // ------------------------------------------------------------------ security

    private void SetUpSecurity(string? password)
    {
        _security = null;
        _encryptObjectNumber = -1;
        var raw = Trailer.GetRaw(PdfName.Encrypt);
        if (raw == null) return;
        if (raw is PdfReference r) _encryptObjectNumber = r.Number;
        // The encryption dictionary itself is never encrypted, so it is loaded before the handler exists.
        if (PdfReference.Deref(raw) is not PdfDictionary encrypt) return;

        byte[] id = (Trailer.GetAsArray(PdfName.ID)?.GetAsString(0)?.Bytes) ?? Array.Empty<byte>();
        // Objects loaded so far (the catalog check, the encrypt dictionary) were read undecrypted.
        var keep = _encryptObjectNumber >= 0 && _objects.TryGetValue(_encryptObjectNumber, out var e) ? e : null;
        _objects.Clear();
        if (keep != null) _objects[_encryptObjectNumber] = keep;
        _security = PdfSecurityHandler.Open(encrypt, id, password);
    }

    private void Decrypt(PdfObject obj, int number, int generation, int depth = 0)
    {
        if (_security == null || number == _encryptObjectNumber || depth > PdfObjectParser.MaxNesting) return;
        switch (obj)
        {
            case PdfString s:
                s.Bytes = _security.DecryptString(s.Bytes, number, generation);
                break;
            case PdfStream stream:
                DecryptDictionaryEntries(stream, number, generation, depth);
                bool isXref = stream.Is(PdfName.XRef);
                bool isPlainMetadata = !_security.EncryptMetadata && stream.Is(PdfName.Metadata);
                bool identityCrypt = stream.FilterNames().Contains("Crypt")
                    && (stream.GetAsDictionary(PdfName.DecodeParms)?.GetAsName(PdfName.Name)?.Value ?? "Identity") == "Identity";
                if (!isXref && !isPlainMetadata && !identityCrypt)
                    stream.SetRawDataPreservingLength(_security.DecryptStream(stream.RawData, number, generation));
                break;
            case PdfDictionary dict:
                DecryptDictionaryEntries(dict, number, generation, depth);
                break;
            case PdfArray array:
                for (int i = 0; i < array.Count; i++)
                    if (array.GetRaw(i) is not PdfReference) Decrypt(array.GetRaw(i), number, generation, depth + 1);
                break;
        }
    }

    private void DecryptDictionaryEntries(PdfDictionary dict, int number, int generation, int depth)
    {
        // A signature's /Contents is excluded from encryption (ISO 32000-2 §7.6.2): it is the
        // signature over the encrypted bytes, so encrypting it would make it unverifiable.
        bool isSignature = dict.ContainsKey(PdfName.ByteRange) && dict.ContainsKey(PdfName.Contents);
        foreach (var key in dict.Keys)
        {
            var value = dict.GetRaw(key);
            if (value is PdfReference) continue;
            if (isSignature && key.Equals(PdfName.Contents)) continue;
            Decrypt(value!, number, generation, depth + 1);
        }
    }

    internal PdfSecurityHandler? Security => _security;

    // ------------------------------------------------------------------ object access

    /// <summary>The reference object for (number, generation); the same instance every time.</summary>
    internal PdfReference GetReference(int number, int generation)
    {
        if (!_references.TryGetValue((number, generation), out var r))
        {
            r = new PdfReference(this, number, generation);
            _references[(number, generation)] = r;
        }
        return r;
    }

    /// <summary>Resolves a reference, loading the object on first access.</summary>
    internal PdfObject Resolve(PdfReference reference)
    {
        if (_objects.TryGetValue(reference.Number, out var cached)) return cached;
        var loaded = Load(reference.Number);
        if (!loaded.IsShared && loaded.Reference == null) loaded.Reference = GetReference(reference.Number, reference.Generation);
        _objects[reference.Number] = loaded;
        return loaded;
    }

    /// <summary>The object with <paramref name="number"/>, or null when there is none.</summary>
    public PdfObject? GetObject(int number)
    {
        if (!_objects.ContainsKey(number) && (!_xref.TryGetValue(number, out var entry) || entry.Type == 0))
            return null;
        int generation = _xref.TryGetValue(number, out var g) ? g.Generation : 0;
        var obj = Resolve(GetReference(number, generation));
        return obj is PdfNull ? null : obj;
    }

    /// <summary>The numbers of every object the table knows, loaded or not.</summary>
    public IEnumerable<int> ObjectNumbers =>
        _xref.Where(kv => kv.Value.Type != 0).Select(kv => kv.Key).Union(_objects.Keys).OrderBy(n => n);

    private PdfObject Load(int number)
    {
        if (!_xref.TryGetValue(number, out var entry) || entry.Type == 0) return PdfNull.Instance;
        if (!_loading.Add(number)) return PdfNull.Instance; // a /Length that refers back to its own stream
        try
        {
            if (entry.Type == 1)
            {
                var (obj, _) = ParseIndirectObjectAt((int)entry.Offset, number, decrypt: true);
                return obj ?? PdfNull.Instance;
            }
            return LoadFromObjectStream(entry.StreamNumber, entry.Index, number);
        }
        finally
        {
            _loading.Remove(number);
        }
    }

    /// <summary>
    /// Parses <c>n g obj … endobj</c> at <paramref name="offset"/>. Returns the object and the
    /// offset just past it.
    /// </summary>
    private (PdfObject? Obj, int End) ParseIndirectObjectAt(int offset, int? expectedNumber, bool decrypt,
        bool scanning = false)
    {
        if (offset < 0 || offset >= _data.Length) return (null, offset);
        var lexer = new PdfLexer(_data, offset);
        if (!ReadObjectHeader(lexer, out int number, out int generation)) return (null, offset);
        if (expectedNumber is int expected && expected != number) return (null, offset);

        // References are parsed even while scanning — they cost nothing until resolved — but a
        // scan never resolves one: /Length is taken only when it is a direct number.
        var parser = new PdfObjectParser(lexer, this);
        var obj = parser.ParseObject() ?? PdfNull.Instance;

        int afterObject = lexer.Position;
        if (obj is PdfDictionary dict && lexer.Next() && lexer.TokenType == PdfTokenType.Keyword && lexer.Text == "stream")
        {
            obj = ReadStreamBody(dict, lexer, scanning);
        }
        else
        {
            lexer.Position = afterObject;
        }
        int save = lexer.Position;
        if (!(lexer.Next() && lexer.TokenType == PdfTokenType.Keyword && lexer.Text == "endobj")) lexer.Position = save;

        if (decrypt) Decrypt(obj, number, generation);
        return (obj, lexer.Position);
    }

    /// <summary>Reads "n g obj". False when the tokens at the lexer are not that.</summary>
    private static bool ReadObjectHeader(PdfLexer lexer, out int number, out int generation)
    {
        number = generation = 0;
        if (!NextIs(lexer, PdfTokenType.Number)) return false;
        number = (int)Math.Clamp(lexer.NumberValue, 0, int.MaxValue);
        if (!NextIs(lexer, PdfTokenType.Number)) return false;
        generation = (int)Math.Clamp(lexer.NumberValue, 0, 65535);
        return NextIs(lexer, PdfTokenType.Keyword) && lexer.Text == "obj";
    }

    private PdfStream ReadStreamBody(PdfDictionary dict, PdfLexer lexer, bool scanning)
    {
        int start = lexer.Position;
        // The keyword is followed by CRLF or LF; a lone CR is tolerated, as every reader does.
        if (start < _data.Length && _data[start] == '\r') start++;
        if (start < _data.Length && _data[start] == '\n') start++;

        long declared = DeclaredLength(dict, scanning);
        int end = declared >= 0 && start + declared <= _data.Length && EndstreamFollows((int)(start + declared))
            ? (int)(start + declared)
            : FindStreamEnd(start);
        byte[] raw = _data.AsSpan(start, end - start).ToArray();
        var stream = PdfStream.FromFile(dict, raw);

        int after = end;
        int kw = IndexOf(_data, "endstream"u8, end, Math.Min(_data.Length, end + 64));
        lexer.Position = kw >= 0 ? kw + 9 : after;
        return stream;
    }

    /// <summary>
    /// The stream's /Length, or -1 when it has none. A scan never resolves a reference, so while
    /// scanning /Length counts only when it is a direct number.
    /// </summary>
    private static long DeclaredLength(PdfDictionary dict, bool scanning)
    {
        var length = scanning ? dict.GetRaw(PdfName.Length) : dict.Get(PdfName.Length);
        return length is PdfNumber n ? n.LongValue() : -1;
    }

    /// <summary>/Length is missing, indirect and unreadable, or wrong: find the keyword instead.</summary>
    private int FindStreamEnd(int start)
    {
        int keyword = IndexOf(_data, "endstream"u8, start, _data.Length);
        if (keyword < 0)
        {
            int endobj = IndexOf(_data, "endobj"u8, start, _data.Length);
            keyword = endobj < 0 ? _data.Length : endobj;
        }
        int end = keyword;
        if (end > start && _data[end - 1] == '\n') end--;
        if (end > start && _data[end - 1] == '\r') end--;
        return end;
    }

    private bool EndstreamFollows(int at)
    {
        int p = at;
        while (p < _data.Length && PdfLexer.IsWhitespace(_data[p]) && p - at < 4) p++;
        return p + 9 <= _data.Length && _data.AsSpan(p, 9).SequenceEqual("endstream"u8);
    }

    private PdfObject LoadFromObjectStream(int streamNumber, int index, int number)
    {
        var os = GetObjectStream(streamNumber);
        if (os == null) return PdfNull.Instance;
        // The index from the table is a hint; the header pairs say which object is where.
        int offset = -1;
        if (index >= 0 && index < os.Entries.Count && os.Entries[index].Number == number) offset = os.Entries[index].Offset;
        else
            foreach (var (n, o) in os.Entries)
                if (n == number) { offset = o; break; }
        if (offset < 0) return PdfNull.Instance;
        int at = os.First + offset;
        if (at < 0 || at >= os.Data.Length) return PdfNull.Instance;
        var lexer = new PdfLexer(os.Data, at);
        return new PdfObjectParser(lexer, this).ParseObject() ?? PdfNull.Instance;
    }

    private ObjectStream? GetObjectStream(int streamNumber)
    {
        if (_objectStreams.TryGetValue(streamNumber, out var cached)) return cached;
        if (_loading.Contains(streamNumber)) return null;
        var stream = Resolve(GetReference(streamNumber, 0)) as PdfStream;
        if (stream == null) return null;
        byte[] data;
        try
        {
            data = stream.GetDecodedBytes();
        }
        catch (PdfFormatException)
        {
            // Salvage what precedes the damage: the objects there are intact.
            data = stream.FilterNames().FirstOrDefault() is "FlateDecode" or "Fl"
                ? PdfFilters.InflateLenient(stream.RawData) : Array.Empty<byte>();
        }
        int n = Math.Clamp(stream.GetAsInt(PdfName.N) ?? 0, 0, 1_000_000);
        int first = Math.Max(0, stream.GetAsInt(PdfName.First) ?? 0);
        var entries = new List<(int, int)>(n);
        var lexer = new PdfLexer(data, 0, Math.Min(first, data.Length));
        for (int i = 0; i < n; i++)
        {
            if (!lexer.Next() || lexer.TokenType != PdfTokenType.Number) break;
            int num = (int)Math.Clamp(lexer.NumberValue, 0, int.MaxValue);
            if (!lexer.Next() || lexer.TokenType != PdfTokenType.Number) break;
            entries.Add((num, (int)Math.Clamp(lexer.NumberValue, 0, int.MaxValue)));
        }
        var os = new ObjectStream { Data = data, First = first, Entries = entries };
        _objectStreams[streamNumber] = os;
        return os;
    }

    /// <summary>After a rebuild, registers the objects held in the object streams the scan found.</summary>
    private void IndexPendingObjectStreams()
    {
        foreach (int streamNumber in _pendingObjectStreams)
        {
            var os = GetObjectStream(streamNumber);
            if (os == null) continue;
            for (int i = 0; i < os.Entries.Count; i++)
            {
                int number = os.Entries[i].Number;
                if (!_xref.ContainsKey(number)) _xref[number] = new XrefEntry(2, 0, 0, streamNumber, i);
            }
        }
        _pendingObjectStreams.Clear();
    }

    // ------------------------------------------------------------------ new objects

    /// <summary>Adds <paramref name="obj"/> to the object table (if it is not there already) and returns it.</summary>
    public T MakeIndirect<T>(T obj) where T : PdfObject
    {
        if (obj.IsShared) throw new ArgumentException("Shared singletons (names, booleans, null) cannot be made indirect.");
        if (obj.Reference != null && obj.Reference.Owner == this) return obj;
        int number = Math.Max(_nextObjectNumber, ObjectNumberLimit);
        _nextObjectNumber = number + 1;
        var reference = GetReference(number, 0);
        obj.Reference = reference;
        _objects[number] = obj;
        return obj;
    }

    // ------------------------------------------------------------------ catalog, info, pages

    /// <summary>The document catalog, or null when the trailer does not name one.</summary>
    public PdfDictionary? Catalog => Trailer.GetAsDictionary(PdfName.Root);

    /// <summary>The Info dictionary, or null.</summary>
    public PdfDictionary? Info => Trailer.GetAsDictionary(PdfName.Info);

    /// <summary>The Info dictionary, created (as an indirect object) when absent.</summary>
    public PdfDictionary GetOrCreateInfo()
    {
        if (Info is { } info) return info;
        var created = MakeIndirect(new PdfDictionary());
        Trailer.Put(PdfName.Info, created);
        return created;
    }

    /// <summary>The pages, in document order.</summary>
    public IReadOnlyList<PdfPage> Pages => _pages ??= CollectPages();

    public int PageCount => Pages.Count;

    /// <summary>Page <paramref name="number"/> (1-based).</summary>
    public PdfPage GetPage(int number)
    {
        if (number < 1 || number > Pages.Count)
            throw new ArgumentOutOfRangeException(nameof(number), $"Page {number} does not exist.");
        return Pages[number - 1];
    }

    /// <summary>
    /// Walks the page tree, guarding against cycles and absurd depth. /Count is not trusted:
    /// the pages are what the /Kids actually reach.
    /// </summary>
    private List<PdfPage> CollectPages()
    {
        var pages = new List<PdfPage>();
        var root = Catalog?.GetAsDictionary(PdfName.Pages);
        if (root == null) return pages;
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        Walk(root, 0);
        return pages;

        void Walk(PdfDictionary node, int depth)
        {
            if (depth > 256 || !seen.Add(node) || pages.Count >= 1_000_000) return;
            var kids = node.GetAsArray(PdfName.Kids);
            bool isPage = node.Is(PdfName.Page) || (kids == null && !node.Is(PdfName.Pages));
            if (isPage)
            {
                pages.Add(new PdfPage(this, node, pages.Count + 1));
                return;
            }
            if (kids == null) return;
            foreach (var kid in kids)
                if (kid is PdfDictionary k) Walk(k, depth + 1);
        }
    }

    /// <summary>
    /// Replaces the page tree with a single flat node holding <paramref name="pageDictionaries"/>,
    /// pushing the inheritable attributes down into each page first so nothing is lost.
    /// </summary>
    public void SetPages(IReadOnlyList<PdfDictionary> pageDictionaries)
    {
        var catalog = Catalog ?? throw new PdfFormatException("The document has no catalog.");
        var root = MakeIndirect(new PdfDictionary());
        root.Put(PdfName.Type, PdfName.Pages);
        var kids = new PdfArray();
        foreach (var page in pageDictionaries)
        {
            MakeIndirect(page);
            kids.Add(page);
            page.Put(PdfName.Parent, root);
        }
        root.Put(PdfName.Kids, kids);
        root.Put(PdfName.Count, new PdfNumber(pageDictionaries.Count));
        catalog.Put(PdfName.Pages, root);
        _pages = null;
    }

    /// <summary>Appends a new blank page of the given size and returns it.</summary>
    public PdfPage AddNewPage(double width, double height)
    {
        var page = new PdfDictionary();
        page.Put(PdfName.Type, PdfName.Page);
        page.Put(PdfName.MediaBox, new PdfArray(0, 0, width, height));
        page.Put(PdfName.Resources, new PdfDictionary());
        var list = Pages.Select(p => p.Dictionary).ToList();
        foreach (var existing in list) PdfPage.PushDownInheritedAttributes(existing);
        list.Add(page);
        SetPages(list);
        return Pages[^1];
    }

    // ------------------------------------------------------------------ helpers

    internal static int IndexOf(byte[] data, ReadOnlySpan<byte> pattern, int from, int to)
    {
        if (from < 0) from = 0;
        to = Math.Min(to, data.Length);
        if (from >= to) return -1;
        int i = data.AsSpan(from, to - from).IndexOf(pattern);
        return i < 0 ? -1 : from + i;
    }

    internal static int LastIndexOf(byte[] data, ReadOnlySpan<byte> pattern, int from)
    {
        if (from < 0) from = 0;
        if (from >= data.Length) return -1;
        int i = data.AsSpan(from).LastIndexOf(pattern);
        return i < 0 ? -1 : from + i;
    }
}
