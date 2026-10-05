using System.Collections;
using System.Globalization;
using System.Text;

namespace PdfEditor.Core.Pdf;

// The PDF object model (ISO 32000-1 §7.3). Every value a PDF file holds is one of these: a
// boolean, number, string, name, array, dictionary, stream, the null object, or a reference to an
// indirect object. Container accessors resolve references for the caller (the Get* family); the
// raw, possibly-a-reference value is available through GetRaw for code that needs to preserve
// sharing — the writer, and the page importer.

/// <summary>Base of every PDF object.</summary>
internal abstract class PdfObject
{
    /// <summary>
    /// The reference this object is stored under in its document, when it is an indirect object;
    /// null for a direct object. Shared singletons (null, true, false, names) never carry one.
    /// </summary>
    public PdfReference? Reference { get; internal set; }

    /// <summary>True when this object lives in its document's object table.</summary>
    public bool IsIndirect => Reference != null;

    /// <summary>True for the singletons that may be shared across documents and positions.</summary>
    internal virtual bool IsShared => false;
}

/// <summary>The PDF null object.</summary>
internal sealed class PdfNull : PdfObject
{
    public static readonly PdfNull Instance = new();
    private PdfNull() { }
    internal override bool IsShared => true;
    public override string ToString() => "null";
}

/// <summary>A PDF boolean.</summary>
internal sealed class PdfBoolean : PdfObject
{
    public static readonly PdfBoolean True = new(true);
    public static readonly PdfBoolean False = new(false);
    public bool Value { get; }
    private PdfBoolean(bool value) => Value = value;
    public static PdfBoolean Of(bool value) => value ? True : False;
    internal override bool IsShared => true;
    public override string ToString() => Value ? "true" : "false";
}

/// <summary>
/// A PDF number. Integers and reals are one type here, as in PostScript; <see cref="IsInteger"/>
/// remembers which spelling the value had so it is written back the way it was read.
/// </summary>
internal sealed class PdfNumber : PdfObject
{
    public double Value { get; }
    public bool IsInteger { get; }

    public PdfNumber(double value)
    {
        Value = double.IsFinite(value) ? value : 0;
        IsInteger = double.IsInteger(Value) && Math.Abs(Value) < 1e15;
    }

    public PdfNumber(long value)
    {
        Value = value;
        IsInteger = true;
    }

    public PdfNumber(int value) : this((long)value) { }

    internal PdfNumber(double value, bool isInteger)
    {
        Value = double.IsFinite(value) ? value : 0;
        IsInteger = isInteger;
    }

    public int IntValue() => Value >= int.MaxValue ? int.MaxValue
        : Value <= int.MinValue ? int.MinValue : (int)Value;

    public long LongValue() => Value >= long.MaxValue ? long.MaxValue
        : Value <= long.MinValue ? long.MinValue : (long)Value;

    public float FloatValue() => (float)Value;

    public override string ToString() => Format(Value);

    /// <summary>Spells a number the way PDF wants it: invariant, no exponent, trailing zeros trimmed.</summary>
    public static string Format(double value)
    {
        if (!double.IsFinite(value)) return "0";
        if (double.IsInteger(value) && Math.Abs(value) < 1e15)
            return ((long)value).ToString(CultureInfo.InvariantCulture);
        string s = value.ToString("0.######", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }
}

/// <summary>
/// A PDF string: a sequence of bytes. Whether it holds text, and in which encoding, depends on
/// where it appears — text strings outside content streams are PDFDocEncoding or UTF-16BE with a
/// byte-order mark; strings inside content streams are in the encoding of the current font.
/// </summary>
internal sealed class PdfString : PdfObject
{
    public byte[] Bytes { get; internal set; }

    /// <summary>Whether the string was spelled in hex (&lt;…&gt;); kept so it is written back the same way.</summary>
    public bool IsHex { get; }

    public PdfString(byte[] bytes, bool isHex = false)
    {
        Bytes = bytes;
        IsHex = isHex;
    }

    /// <summary>A text string: PDFDocEncoding when every character fits, otherwise UTF-16BE with a BOM.</summary>
    public static PdfString FromText(string text) => new(PdfTextEncoding.Encode(text));

    /// <summary>The string decoded as a PDF text string (§7.9.2.2).</summary>
    public string ToUnicodeString() => PdfTextEncoding.Decode(Bytes);

    public override string ToString() => ToUnicodeString();
}

/// <summary>A PDF name. Names are interned, so two names with the same value are the same object.</summary>
internal sealed class PdfName : PdfObject, IEquatable<PdfName>
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, PdfName> Interned =
        new(StringComparer.Ordinal);

    /// <summary>The name's characters, without the leading solidus and with #xx escapes decoded.</summary>
    public string Value { get; }

    private PdfName(string value) => Value = value;

    public static PdfName Of(string value)
    {
        // Interning is unbounded only in the number of distinct names a process ever sees; a
        // hostile document could mint names freely, so very long ones are not interned.
        if (value.Length > 128) return new PdfName(value);
        return Interned.GetOrAdd(value, v => new PdfName(v));
    }

    internal override bool IsShared => true;

    public bool Equals(PdfName? other) => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is PdfName n && Equals(n);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
    public override string ToString() => "/" + Value;

    // The names this codebase uses. Keeping them as fields avoids a dictionary lookup per access
    // and keeps call sites short.
    public static readonly PdfName A = Of("A");
    public static readonly PdfName AA = Of("AA");
    public static readonly PdfName AcroForm = Of("AcroForm");
    public static readonly PdfName AF = Of("AF");
    public static readonly PdfName Annot = Of("Annot");
    public static readonly PdfName Annots = Of("Annots");
    public static readonly PdfName AP = Of("AP");
    public static readonly PdfName AS = Of("AS");
    public static readonly PdfName Ascent = Of("Ascent");
    public static readonly PdfName Author = Of("Author");
    public static readonly PdfName BaseEncoding = Of("BaseEncoding");
    public static readonly PdfName BaseFont = Of("BaseFont");
    public static readonly PdfName BBox = Of("BBox");
    public static readonly PdfName BC = Of("BC");
    public static readonly PdfName BG = Of("BG");
    public static readonly PdfName BitsPerComponent = Of("BitsPerComponent");
    public static readonly PdfName BM = Of("BM");
    public static readonly PdfName Border = Of("Border");
    public static readonly PdfName BS = Of("BS");
    public static readonly PdfName Btn = Of("Btn");
    public static readonly PdfName ByteRange = Of("ByteRange");
    public static readonly PdfName CA = Of("CA");
    public static readonly PdfName ca = Of("ca");
    public static readonly PdfName Catalog = Of("Catalog");
    public static readonly PdfName Ch = Of("Ch");
    public static readonly PdfName CharProcs = Of("CharProcs");
    public static readonly PdfName CIDSystemInfo = Of("CIDSystemInfo");
    public static readonly PdfName CIDToGIDMap = Of("CIDToGIDMap");
    public static readonly PdfName ColorSpace = Of("ColorSpace");
    public static readonly PdfName Colors = Of("Colors");
    public static readonly PdfName Columns = Of("Columns");
    public static readonly PdfName Contents = Of("Contents");
    public static readonly PdfName Count = Of("Count");
    public static readonly PdfName CreationDate = Of("CreationDate");
    public static readonly PdfName Creator = Of("Creator");
    public static readonly PdfName CropBox = Of("CropBox");
    public static readonly PdfName CS = Of("CS");
    public static readonly PdfName DA = Of("DA");
    public static readonly PdfName Decode = Of("Decode");
    public static readonly PdfName DecodeParms = Of("DecodeParms");
    public static readonly PdfName Descent = Of("Descent");
    public static readonly PdfName DescendantFonts = Of("DescendantFonts");
    public static readonly PdfName Dest = Of("Dest");
    public static readonly PdfName DeviceCMYK = Of("DeviceCMYK");
    public static readonly PdfName DeviceGray = Of("DeviceGray");
    public static readonly PdfName DeviceRGB = Of("DeviceRGB");
    public static readonly PdfName Differences = Of("Differences");
    public static readonly PdfName DP = Of("DP");
    public static readonly PdfName DR = Of("DR");
    public static readonly PdfName DW = Of("DW");
    public static readonly PdfName EarlyChange = Of("EarlyChange");
    public static readonly PdfName EmbeddedFiles = Of("EmbeddedFiles");
    public static readonly PdfName Encoding = Of("Encoding");
    public static readonly PdfName Encrypt = Of("Encrypt");
    public static readonly PdfName EncryptMetadata = Of("EncryptMetadata");
    public static readonly PdfName ExtGState = Of("ExtGState");
    public static readonly PdfName F = Of("F");
    public static readonly PdfName Ff = Of("Ff");
    public static readonly PdfName Fields = Of("Fields");
    public static readonly PdfName FileAttachment = Of("FileAttachment");
    public static readonly PdfName Filter = Of("Filter");
    public static readonly PdfName First = Of("First");
    public static readonly PdfName FirstChar = Of("FirstChar");
    public static readonly PdfName FlateDecode = Of("FlateDecode");
    public static readonly PdfName Font = Of("Font");
    public static readonly PdfName FontBBox = Of("FontBBox");
    public static readonly PdfName FontDescriptor = Of("FontDescriptor");
    public static readonly PdfName FontFile = Of("FontFile");
    public static readonly PdfName FontFile2 = Of("FontFile2");
    public static readonly PdfName FontFile3 = Of("FontFile3");
    public static readonly PdfName FontMatrix = Of("FontMatrix");
    public static readonly PdfName FontName = Of("FontName");
    public static readonly PdfName Form = Of("Form");
    public static readonly PdfName FT = Of("FT");
    public static readonly PdfName G = Of("G");
    public static readonly PdfName GoTo = Of("GoTo");
    public static readonly PdfName GoToR = Of("GoToR");
    public static readonly PdfName Group = Of("Group");
    public static readonly PdfName H = Of("H");
    public static readonly PdfName Height = Of("Height");
    public static readonly PdfName I = Of("I");
    public static readonly PdfName ID = Of("ID");
    public static readonly PdfName Image = Of("Image");
    public static readonly PdfName ImageMask = Of("ImageMask");
    public static readonly PdfName ImportData = Of("ImportData");
    public static readonly PdfName Index = Of("Index");
    public static readonly PdfName Info = Of("Info");
    public static readonly PdfName JavaScript = Of("JavaScript");
    public static readonly PdfName JS = Of("JS");
    public static readonly PdfName K = Of("K");
    public static readonly PdfName Keywords = Of("Keywords");
    public static readonly PdfName Kids = Of("Kids");
    public static readonly PdfName Launch = Of("Launch");
    public static readonly PdfName Length = Of("Length");
    public static readonly PdfName Link = Of("Link");
    public static readonly PdfName M = Of("M");
    public static readonly PdfName Mask = Of("Mask");
    public static readonly PdfName Matrix = Of("Matrix");
    public static readonly PdfName MaxLen = Of("MaxLen");
    public static readonly PdfName MediaBox = Of("MediaBox");
    public static readonly PdfName Metadata = Of("Metadata");
    public static readonly PdfName MissingWidth = Of("MissingWidth");
    public static readonly PdfName MK = Of("MK");
    public static readonly PdfName ModDate = Of("ModDate");
    public static readonly PdfName Multiply = Of("Multiply");
    public static readonly PdfName N = Of("N");
    public static readonly PdfName Name = Of("Name");
    public static readonly PdfName Named = Of("Named");
    public static readonly PdfName Names = Of("Names");
    public static readonly PdfName NeedAppearances = Of("NeedAppearances");
    public static readonly PdfName Next = Of("Next");
    public static readonly PdfName OCGs = Of("OCGs");
    public static readonly PdfName OCProperties = Of("OCProperties");
    public static readonly PdfName Off = Of("Off");
    public static readonly PdfName OpenAction = Of("OpenAction");
    public static readonly PdfName Opt = Of("Opt");
    public static readonly PdfName Ordering = Of("Ordering");
    public static readonly PdfName Outlines = Of("Outlines");
    public static readonly PdfName P = Of("P");
    public static readonly PdfName Page = Of("Page");
    public static readonly PdfName Pages = Of("Pages");
    public static readonly PdfName Parent = Of("Parent");
    public static readonly PdfName Popup = Of("Popup");
    public static readonly PdfName Predictor = Of("Predictor");
    public static readonly PdfName Prev = Of("Prev");
    public static readonly PdfName Producer = Of("Producer");
    public static readonly PdfName Q = Of("Q");
    public static readonly PdfName QuadPoints = Of("QuadPoints");
    public static readonly PdfName Reason = Of("Reason");
    public static readonly PdfName Rect = Of("Rect");
    public static readonly PdfName Resources = Of("Resources");
    public static readonly PdfName Root = Of("Root");
    public static readonly PdfName Rotate = Of("Rotate");
    public static readonly PdfName S = Of("S");
    public static readonly PdfName Shading = Of("Shading");
    public static readonly PdfName ShadingType = Of("ShadingType");
    public static readonly PdfName Sig = Of("Sig");
    public static readonly PdfName SigFlags = Of("SigFlags");
    public static readonly PdfName Size = Of("Size");
    public static readonly PdfName SMask = Of("SMask");
    public static readonly PdfName StructParent = Of("StructParent");
    public static readonly PdfName StructParents = Of("StructParents");
    public static readonly PdfName StructTreeRoot = Of("StructTreeRoot");
    public static readonly PdfName SubFilter = Of("SubFilter");
    public static readonly PdfName Subject = Of("Subject");
    public static readonly PdfName SubmitForm = Of("SubmitForm");
    public static readonly PdfName Subtype = Of("Subtype");
    public static readonly PdfName T = Of("T");
    public static readonly PdfName Title = Of("Title");
    public static readonly PdfName ToUnicode = Of("ToUnicode");
    public static readonly PdfName TR = Of("TR");
    public static readonly PdfName Trapped = Of("Trapped");
    public static readonly PdfName Tx = Of("Tx");
    public static readonly PdfName Type = Of("Type");
    public static readonly PdfName U = Of("U");
    public static readonly PdfName URI = Of("URI");
    public static readonly PdfName V = Of("V");
    public static readonly PdfName W = Of("W");
    public static readonly PdfName Widget = Of("Widget");
    public static readonly PdfName Width = Of("Width");
    public static readonly PdfName Widths = Of("Widths");
    public static readonly PdfName WinAnsiEncoding = Of("WinAnsiEncoding");
    public static readonly PdfName XObject = Of("XObject");
    public static readonly PdfName XRef = Of("XRef");
    public static readonly PdfName XRefStm = Of("XRefStm");
    public static readonly PdfName ZaDb = Of("ZaDb");
}

/// <summary>A PDF array.</summary>
internal sealed class PdfArray : PdfObject, IEnumerable<PdfObject>
{
    private readonly List<PdfObject> _items;

    public PdfArray() => _items = new List<PdfObject>();
    public PdfArray(IEnumerable<PdfObject> items) => _items = new List<PdfObject>(items);
    public PdfArray(params double[] numbers) => _items = numbers.Select(n => (PdfObject)new PdfNumber(n)).ToList();

    public int Count => _items.Count;

    /// <summary>The item at <paramref name="index"/>, with a reference resolved; null when out of range.</summary>
    public PdfObject? Get(int index) => index >= 0 && index < _items.Count ? PdfReference.Deref(_items[index]) : null;

    /// <summary>The item exactly as stored (possibly a reference).</summary>
    public PdfObject GetRaw(int index) => _items[index];

    public PdfDictionary? GetAsDictionary(int index) => Get(index) as PdfDictionary;
    public PdfArray? GetAsArray(int index) => Get(index) as PdfArray;
    public PdfName? GetAsName(int index) => Get(index) as PdfName;
    public PdfNumber? GetAsNumber(int index) => Get(index) as PdfNumber;
    public PdfString? GetAsString(int index) => Get(index) as PdfString;
    public PdfStream? GetAsStream(int index) => Get(index) as PdfStream;

    /// <summary>The numeric value at <paramref name="index"/>, or <paramref name="fallback"/>.</summary>
    public double GetNumber(int index, double fallback = 0) => GetAsNumber(index)?.Value ?? fallback;

    public void Add(PdfObject item) => _items.Add(item);
    public void Insert(int index, PdfObject item) => _items.Insert(index, item);
    public void Set(int index, PdfObject item) => _items[index] = item;
    public void RemoveAt(int index) => _items.RemoveAt(index);
    public void Clear() => _items.Clear();

    /// <summary>Removes every item that is, or refers to, <paramref name="target"/>.</summary>
    public int RemoveAll(PdfObject target) => _items.RemoveAll(i => ReferenceEquals(PdfReference.Deref(i), target));

    /// <summary>The items as numbers; non-numbers read as 0.</summary>
    public double[] ToDoubleArray() => Enumerable.Range(0, Count).Select(i => GetNumber(i)).ToArray();

    /// <summary>Enumerates the items with references resolved.</summary>
    public IEnumerator<PdfObject> GetEnumerator()
    {
        for (int i = 0; i < _items.Count; i++)
            yield return PdfReference.Deref(_items[i]);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>The raw items, references unresolved.</summary>
    internal IReadOnlyList<PdfObject> RawItems => _items;
}

/// <summary>A PDF dictionary. Keys keep their insertion order so documents round-trip stably.</summary>
internal class PdfDictionary : PdfObject
{
    private readonly Dictionary<PdfName, PdfObject> _map = new();
    private readonly List<PdfName> _order = new();

    public PdfDictionary() { }

    public int Count => _map.Count;

    public IReadOnlyList<PdfName> Keys => _order;

    public bool ContainsKey(PdfName key) => _map.ContainsKey(key);

    /// <summary>The value for <paramref name="key"/>, with a reference resolved; null when absent or null.</summary>
    public PdfObject? Get(PdfName key)
    {
        if (!_map.TryGetValue(key, out var value)) return null;
        var resolved = PdfReference.Deref(value);
        return resolved is PdfNull ? null : resolved;
    }

    /// <summary>The value exactly as stored (possibly a reference).</summary>
    public PdfObject? GetRaw(PdfName key) => _map.TryGetValue(key, out var value) ? value : null;

    public PdfDictionary? GetAsDictionary(PdfName key) => Get(key) as PdfDictionary;
    public PdfArray? GetAsArray(PdfName key) => Get(key) as PdfArray;
    public PdfName? GetAsName(PdfName key) => Get(key) as PdfName;
    public PdfNumber? GetAsNumber(PdfName key) => Get(key) as PdfNumber;
    public PdfString? GetAsString(PdfName key) => Get(key) as PdfString;
    public PdfStream? GetAsStream(PdfName key) => Get(key) as PdfStream;
    public PdfBoolean? GetAsBoolean(PdfName key) => Get(key) as PdfBoolean;

    public bool? GetAsBool(PdfName key) => GetAsBoolean(key)?.Value;
    public int? GetAsInt(PdfName key) => GetAsNumber(key)?.IntValue();
    public double? GetAsDouble(PdfName key) => GetAsNumber(key)?.Value;

    /// <summary>The value of a text-string entry, decoded; null when absent or not a string.</summary>
    public string? GetText(PdfName key) => GetAsString(key)?.ToUnicodeString();

    /// <summary>Sets <paramref name="key"/>; a null value removes the entry.</summary>
    public void Put(PdfName key, PdfObject? value)
    {
        if (value is null)
        {
            Remove(key);
            return;
        }
        if (!_map.ContainsKey(key)) _order.Add(key);
        _map[key] = value;
    }

    public void Put(PdfName key, double value) => Put(key, new PdfNumber(value));
    public void Put(PdfName key, string text) => Put(key, PdfString.FromText(text));

    public bool Remove(PdfName key)
    {
        if (!_map.Remove(key)) return false;
        _order.Remove(key);
        return true;
    }

    /// <summary>True when <c>/Type</c> (or <paramref name="key"/>) names <paramref name="value"/>.</summary>
    public bool Is(PdfName value, PdfName? key = null) => value.Equals(GetAsName(key ?? PdfName.Type));
}

/// <summary>
/// A PDF stream: a dictionary plus a sequence of bytes. <see cref="RawData"/> is the data as stored,
/// still encoded by the stream's filters (but already decrypted); <see cref="GetDecodedBytes"/>
/// applies the filters.
/// </summary>
internal sealed class PdfStream : PdfDictionary
{
    public byte[] RawData { get; private set; }

    public PdfStream() => RawData = Array.Empty<byte>();

    /// <summary>A new stream holding <paramref name="data"/>, Flate-compressed unless told otherwise.</summary>
    public PdfStream(byte[] data, bool compress = true) : this() => SetData(data, compress);

    /// <summary>Wraps bytes read from a file, still encoded as the dictionary's filters say.</summary>
    internal static PdfStream FromFile(PdfDictionary dictionary, byte[] raw)
    {
        var stream = new PdfStream { RawData = raw };
        foreach (var key in dictionary.Keys) stream.Put(key, dictionary.GetRaw(key));
        return stream;
    }

    /// <summary>The stream's bytes with every non-image filter applied.</summary>
    public byte[] GetDecodedBytes() => PdfFilters.Decode(this);

    /// <summary>Replaces the stream's content with <paramref name="data"/> (unencoded).</summary>
    public void SetData(byte[] data, bool compress = true)
    {
        Remove(PdfName.DecodeParms);
        if (compress && data.Length > 0)
        {
            RawData = PdfFilters.FlateEncode(data);
            Put(PdfName.Filter, PdfName.FlateDecode);
        }
        else
        {
            RawData = data;
            Remove(PdfName.Filter);
        }
        Put(PdfName.Length, new PdfNumber(RawData.Length));
    }

    /// <summary>Replaces the encoded bytes, leaving <c>/Filter</c> and <c>/DecodeParms</c> to the caller.</summary>
    public void SetRawData(byte[] raw)
    {
        RawData = raw;
        Put(PdfName.Length, new PdfNumber(raw.Length));
    }

    /// <summary>Swaps in decrypted bytes; /Length still describes the stored (encrypted) form until the next write.</summary>
    internal void SetRawDataPreservingLength(byte[] raw) => RawData = raw;

    /// <summary>The names in <c>/Filter</c>, in application order.</summary>
    public IReadOnlyList<string> FilterNames() => Get(PdfName.Filter) switch
    {
        PdfName single => new[] { single.Value },
        PdfArray many => many.OfType<PdfName>().Select(n => n.Value).ToArray(),
        _ => Array.Empty<string>(),
    };
}

/// <summary>A reference to an indirect object (<c>n g R</c>).</summary>
internal sealed class PdfReference : PdfObject
{
    public int Number { get; }
    public int Generation { get; }
    internal PdfDocument Owner { get; }

    internal PdfReference(PdfDocument owner, int number, int generation)
    {
        Owner = owner;
        Number = number;
        Generation = generation;
    }

    /// <summary>The object this reference names; the null object when it does not exist.</summary>
    public PdfObject Resolve() => Owner.Resolve(this);

    /// <summary>Resolves <paramref name="obj"/> if it is a reference; returns it unchanged otherwise.</summary>
    public static PdfObject Deref(PdfObject obj) => obj is PdfReference r ? r.Resolve() : obj;

    public override string ToString() => $"{Number} {Generation} R";
}

/// <summary>
/// The two text-string encodings PDF allows outside content streams (§7.9.2.2): PDFDocEncoding,
/// a superset of ISO Latin-1 for most of its range, and UTF-16BE introduced by a byte-order mark.
/// PDF 2.0 adds UTF-8 with its own mark, which is read but never written.
/// </summary>
internal static class PdfTextEncoding
{
    // PDFDocEncoding differs from Latin-1 only in 0x18–0x1F and 0x80–0xA0. Entries of '\0' are
    // undefined code points.
    private static readonly char[] DocToUnicode = BuildDocTable();
    private static readonly Dictionary<char, byte> UnicodeToDoc = BuildReverse();

    private static char[] BuildDocTable()
    {
        var table = new char[256];
        for (int i = 0; i < 256; i++) table[i] = (char)i;
        char[] low = { '˘', 'ˇ', 'ˆ', '˙', '˝', '˛', '˚', '˜' };
        for (int i = 0; i < low.Length; i++) table[0x18 + i] = low[i];
        char[] high =
        {
            '•', '†', '‡', '…', '—', '–', 'ƒ', '⁄',
            '‹', '›', '−', '‰', '„', '“', '”', '‘',
            '’', '‚', '™', 'ﬁ', 'ﬂ', 'Ł', 'Œ', 'Š',
            'Ÿ', 'Ž', 'ı', 'ł', 'œ', 'š', 'ž', '\0',
            '€',
        };
        for (int i = 0; i < high.Length; i++) table[0x80 + i] = high[i];
        table[0x7F] = '\0';
        return table;
    }

    private static Dictionary<char, byte> BuildReverse()
    {
        var map = new Dictionary<char, byte>();
        for (int i = 0; i < 256; i++)
            if (DocToUnicode[i] != '\0' || i == 0) map.TryAdd(DocToUnicode[i], (byte)i);
        return map;
    }

    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return System.Text.Encoding.BigEndianUnicode.GetString(bytes, 2, (bytes.Length - 2) & ~1);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) // seen in the wild, though not legal
            return System.Text.Encoding.Unicode.GetString(bytes, 2, (bytes.Length - 2) & ~1);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        var sb = new StringBuilder(bytes.Length);
        foreach (byte b in bytes)
        {
            char c = DocToUnicode[b];
            sb.Append(c == '\0' && b != 0 ? (char)b : c);
        }
        return sb.ToString();
    }

    public static byte[] Encode(string text)
    {
        var bytes = new byte[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            if (!UnicodeToDoc.TryGetValue(text[i], out byte b))
            {
                var utf16 = System.Text.Encoding.BigEndianUnicode.GetBytes(text);
                var withBom = new byte[utf16.Length + 2];
                withBom[0] = 0xFE;
                withBom[1] = 0xFF;
                utf16.CopyTo(withBom, 2);
                return withBom;
            }
            bytes[i] = b;
        }
        return bytes;
    }
}

/// <summary>
/// The document is not well-formed PDF in a way the engine cannot work around. The public entry
/// points translate it into an <see cref="InvalidDataException"/> that names the step that
/// failed (see <c>PdfIo.Guarded</c>), keeping this as the inner exception.
/// </summary>
internal class PdfFormatException : FormatException
{
    public PdfFormatException(string message) : base(message) { }
    public PdfFormatException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>The document is encrypted and the supplied password (if any) does not open it.</summary>
public sealed class PdfPasswordException : UnauthorizedAccessException
{
    public PdfPasswordException(string message) : base(message) { }
}
