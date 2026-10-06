using System.Security.Cryptography;
using System.Text;

namespace PdfEditor.Core.Pdf;

/// <summary>
/// The PDF standard security handler (§7.6.4, ISO 32000-2 §7.6.4): RC4 and AES-128 for revisions
/// 2–4, AES-256 for revisions 5 and 6.
/// <para>
/// Permission bits are deliberately not enforced. They are an honour system: the file key is
/// derived from the user password alone, so any reader that can display a document can also
/// rewrite it. An editor that refused to touch a print-only PDF would not be protecting anyone —
/// it would just make the user find a tool that ignores the bits. (iText's equivalent switch was
/// "unethical reading"; this engine has always behaved that way.)
/// </para>
/// </summary>
internal sealed class PdfSecurityHandler
{
    /// <summary>The crypt filter that leaves data as it is (§7.6.6), and the default where none is named.</summary>
    private const string IdentityFilter = "Identity";

    private static readonly byte[] Padding =
    {
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    };

    private enum Cipher { None, Rc4, AesV2, AesV3 }

    private readonly byte[] _key;
    private readonly Cipher _streamCipher;
    private readonly Cipher _stringCipher;

    /// <summary>Whether the document's XMP metadata stream is encrypted (it usually is).</summary>
    public bool EncryptMetadata { get; }

    /// <summary>The revision of the handler that protects the document.</summary>
    public int Revision { get; }

    private PdfSecurityHandler(byte[] key, Cipher streams, Cipher strings, bool encryptMetadata, int revision)
    {
        _key = key;
        _streamCipher = streams;
        _stringCipher = strings;
        EncryptMetadata = encryptMetadata;
        Revision = revision;
    }

    // ------------------------------------------------------------------ opening

    /// <summary>
    /// Derives the file key from <paramref name="password"/> (tried as the user password, then as
    /// the owner password; an empty password opens documents that only restrict permissions).
    /// </summary>
    public static PdfSecurityHandler Open(PdfDictionary encrypt, byte[] firstId, string? password)
    {
        var filter = encrypt.GetAsName(PdfName.Filter)?.Value;
        if (filter != null && filter != "Standard")
            throw new PdfFormatException(
                $"The document is encrypted with the '{filter}' security handler (certificate or vendor encryption), which this editor cannot open.");

        int v = encrypt.GetAsInt(PdfName.Of("V")) ?? 0;
        int r = encrypt.GetAsInt(PdfName.Of("R")) ?? 2;
        byte[] o = encrypt.GetAsString(PdfName.Of("O"))?.Bytes ?? Array.Empty<byte>();
        byte[] u = encrypt.GetAsString(PdfName.U)?.Bytes ?? Array.Empty<byte>();
        int p = (int)(encrypt.GetAsNumber(PdfName.P)?.LongValue() ?? -1);
        bool encryptMetadata = encrypt.GetAsBool(PdfName.EncryptMetadata) ?? true;

        var (streams, strings) = Ciphers(encrypt, v);
        string pw = password ?? "";

        if (r >= 5)
        {
            byte[] oe = encrypt.GetAsString(PdfName.Of("OE"))?.Bytes ?? Array.Empty<byte>();
            byte[] ue = encrypt.GetAsString(PdfName.Of("UE"))?.Bytes ?? Array.Empty<byte>();
            if (o.Length < 48 || u.Length < 48 || oe.Length < 32 || ue.Length < 32)
                throw new PdfFormatException("The document's AES-256 encryption dictionary is incomplete; it cannot be decrypted.");
            byte[] pwBytes = Utf8Password(pw);
            byte[]? key = TryAes256Key(r, pwBytes, o, u, oe, ue);
            if (key == null) throw BadPassword(password);
            return new PdfSecurityHandler(key, streams, strings, encryptMetadata, r);
        }

        int keyLength = r == 2 ? 5 : Math.Clamp((encrypt.GetAsInt(PdfName.Length) ?? 40) / 8, 5, 16);
        foreach (var candidate in LegacyPasswordEncodings(pw))
        {
            // As the user password…
            byte[] key = LegacyKey(candidate, o, p, firstId, r, keyLength, encryptMetadata);
            if (UserPasswordMatches(key, u, firstId, r)) return new PdfSecurityHandler(key, streams, strings, encryptMetadata, r);

            // …and as the owner password, which unlocks the user password stored in /O.
            byte[] userPassword = RecoverUserPassword(candidate, o, r, keyLength);
            key = LegacyKey(userPassword, o, p, firstId, r, keyLength, encryptMetadata);
            if (UserPasswordMatches(key, u, firstId, r)) return new PdfSecurityHandler(key, streams, strings, encryptMetadata, r);
        }
        throw BadPassword(password);
    }

    private static PdfPasswordException BadPassword(string? password) => new(string.IsNullOrEmpty(password)
        ? "This PDF is password-protected. A password is required to open it."
        : "The password is incorrect: it does not open this PDF.");

    private static (Cipher Streams, Cipher Strings) Ciphers(PdfDictionary encrypt, int v)
    {
        if (v < 4) return (Cipher.Rc4, Cipher.Rc4);
        Cipher Lookup(PdfName key)
        {
            var name = encrypt.GetAsName(key)?.Value ?? IdentityFilter;
            if (name == IdentityFilter) return Cipher.None;
            var cfm = encrypt.GetAsDictionary(PdfName.Of("CF"))?.GetAsDictionary(PdfName.Of(name))
                ?.GetAsName(PdfName.Of("CFM"))?.Value;
            return cfm switch
            {
                "AESV2" => Cipher.AesV2,
                "AESV3" => Cipher.AesV3,
                "V2" => Cipher.Rc4,
                "None" => Cipher.None,
                _ => v >= 5 ? Cipher.AesV3 : Cipher.Rc4,
            };
        }
        return (Lookup(PdfName.Of("StmF")), Lookup(PdfName.Of("StrF")));
    }

    private static IEnumerable<byte[]> LegacyPasswordEncodings(string password)
    {
        // Revisions 2–4 specify PDFDocEncoding; producers that got that wrong wrote UTF-8, so a
        // password with non-ASCII characters is tried both ways.
        byte[] doc = PdfTextEncoding.Encode(password);
        if (doc.Length >= 2 && doc[0] == 0xFE && doc[1] == 0xFF) doc = Encoding.Latin1.GetBytes(password);
        yield return doc;
        byte[] utf8 = Encoding.UTF8.GetBytes(password);
        if (!utf8.AsSpan().SequenceEqual(doc)) yield return utf8;
    }

    private static byte[] Pad(byte[] password)
    {
        var padded = new byte[32];
        int n = Math.Min(32, password.Length);
        Array.Copy(password, padded, n);
        Array.Copy(Padding, 0, padded, n, 32 - n);
        return padded;
    }

    /// <summary>Algorithm 2: the file key for revisions 2–4.</summary>
    private static byte[] LegacyKey(byte[] password, byte[] o, int p, byte[] firstId, int r, int keyLength,
        bool encryptMetadata)
    {
        using var md5 = MD5.Create();
        var input = new List<byte>(Pad(password));
        input.AddRange(o.Take(32));
        input.AddRange(BitConverter.GetBytes(p).Take(4)); // little-endian, as the algorithm requires
        input.AddRange(firstId);
        if (r >= 4 && !encryptMetadata) input.AddRange(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
        byte[] hash = md5.ComputeHash(input.ToArray());
        if (r >= 3)
            for (int i = 0; i < 50; i++) hash = md5.ComputeHash(hash, 0, keyLength);
        return hash.Take(keyLength).ToArray();
    }

    /// <summary>Algorithms 4/5 then 6: whether <paramref name="key"/> reproduces the stored /U.</summary>
    private static bool UserPasswordMatches(byte[] key, byte[] u, byte[] firstId, int r)
    {
        if (r == 2)
        {
            byte[] expected = Rc4(key, Padding);
            return u.Length >= 32 && expected.AsSpan().SequenceEqual(u.AsSpan(0, 32));
        }
        using var md5 = MD5.Create();
        byte[] hash = md5.ComputeHash(Padding.Concat(firstId).ToArray());
        byte[] value = Rc4(key, hash);
        for (int i = 1; i <= 19; i++) value = Rc4(XorKey(key, i), value);
        return u.Length >= 16 && value.AsSpan(0, 16).SequenceEqual(u.AsSpan(0, 16));
    }

    /// <summary>Algorithm 7: decrypts /O with the owner password to recover the user password.</summary>
    private static byte[] RecoverUserPassword(byte[] ownerPassword, byte[] o, int r, int keyLength)
    {
        using var md5 = MD5.Create();
        byte[] hash = md5.ComputeHash(Pad(ownerPassword));
        if (r >= 3)
            for (int i = 0; i < 50; i++) hash = md5.ComputeHash(hash);
        byte[] key = hash.Take(r == 2 ? 5 : keyLength).ToArray();
        byte[] value = o.Take(32).ToArray();
        if (r == 2) return Rc4(key, value);
        for (int i = 19; i >= 0; i--) value = Rc4(XorKey(key, i), value);
        return value;
    }

    private static byte[] XorKey(byte[] key, int x)
    {
        var k = new byte[key.Length];
        for (int j = 0; j < key.Length; j++) k[j] = (byte)(key[j] ^ x);
        return k;
    }

    private static byte[] Utf8Password(string password)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormKC));
        return bytes.Length > 127 ? bytes.Take(127).ToArray() : bytes;
    }

    /// <summary>Algorithms 2.A/11/12: the AES-256 file key, or null when the password matches neither.</summary>
    private static byte[]? TryAes256Key(int r, byte[] pw, byte[] o, byte[] u, byte[] oe, byte[] ue)
    {
        byte[] u48 = u.Take(48).ToArray();
        // Owner first: an owner password that happens to equal the user password still unlocks.
        if (Hash(r, pw, o.AsSpan(32, 8), u48).AsSpan().SequenceEqual(o.AsSpan(0, 32)))
            return AesNoIv(Hash(r, pw, o.AsSpan(40, 8), u48), oe.Take(32).ToArray(), decrypt: true);
        if (Hash(r, pw, u.AsSpan(32, 8), Array.Empty<byte>()).AsSpan().SequenceEqual(u.AsSpan(0, 32)))
            return AesNoIv(Hash(r, pw, u.AsSpan(40, 8), Array.Empty<byte>()), ue.Take(32).ToArray(), decrypt: true);
        return null;
    }

    /// <summary>The revision-5 SHA-256 hash, or the revision-6 iterated hash (Algorithm 2.B).</summary>
    private static byte[] Hash(int r, byte[] password, ReadOnlySpan<byte> salt, byte[] userData)
    {
        byte[] input = password.Concat(salt.ToArray()).Concat(userData).ToArray();
        byte[] k = SHA256.HashData(input);
        if (r == 5) return k;

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        int round = 0;
        byte[] e;
        while (true)
        {
            int unit = password.Length + k.Length + userData.Length;
            var k1 = new byte[unit * 64];
            for (int i = 0; i < 64; i++)
            {
                int at = i * unit;
                password.CopyTo(k1, at);
                k.CopyTo(k1, at + password.Length);
                userData.CopyTo(k1, at + password.Length + k.Length);
            }
            aes.Key = k.AsSpan(0, 16).ToArray();
            e = aes.EncryptCbc(k1, k.AsSpan(16, 16), PaddingMode.None);
            int mod = 0;
            for (int i = 0; i < 16; i++) mod += e[i];
            k = (mod % 3) switch
            {
                0 => SHA256.HashData(e),
                1 => SHA384.HashData(e),
                _ => SHA512.HashData(e),
            };
            round++;
            if (round >= 64 && e[^1] <= round - 32) break;
        }
        return k.Take(32).ToArray();
    }

    private static byte[] AesNoIv(byte[] key, byte[] data, bool decrypt)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        var iv = new byte[16];
        return decrypt ? aes.DecryptCbc(data, iv, PaddingMode.None) : aes.EncryptCbc(data, iv, PaddingMode.None);
    }

    // ------------------------------------------------------------------ decrypting objects

    /// <summary>
    /// Whether a stream's data is stored unencrypted even though the document is encrypted: XMP
    /// metadata when /EncryptMetadata is false, and a stream whose /Crypt filter names the Identity
    /// filter. The reader leaves such data as it is, so the writer must write it as it is.
    /// </summary>
    public bool StoresInClear(PdfStream stream) =>
        (!EncryptMetadata && stream.Is(PdfName.Metadata))
        || (stream.FilterNames().Contains("Crypt")
            && (stream.GetAsDictionary(PdfName.DecodeParms)?.GetAsName(PdfName.Name)?.Value ?? IdentityFilter) == IdentityFilter);

    /// <summary>Decrypts a string or stream body belonging to object <paramref name="number"/>.</summary>
    public byte[] DecryptString(byte[] data, int number, int generation) =>
        Decrypt(data, number, generation, _stringCipher);

    public byte[] DecryptStream(byte[] data, int number, int generation) =>
        Decrypt(data, number, generation, _streamCipher);

    private byte[] Decrypt(byte[] data, int number, int generation, Cipher cipher)
    {
        switch (cipher)
        {
            case Cipher.None:
                return data;
            case Cipher.Rc4:
                return Rc4(ObjectKey(number, generation, aes: false), data);
            default:
                if (data.Length < 16) return Array.Empty<byte>(); // an IV with no ciphertext: empty
                byte[] key = cipher == Cipher.AesV3 ? _key : ObjectKey(number, generation, aes: true);
                using (var aes = Aes.Create())
                {
                    aes.Key = key;
                    int length = (data.Length - 16) / 16 * 16;
                    if (length == 0) return Array.Empty<byte>();
                    byte[] plain = aes.DecryptCbc(data.AsSpan(16, length), data.AsSpan(0, 16), PaddingMode.None);
                    // PKCS#7 padding, removed leniently: some producers pad incorrectly, and dropping
                    // a document over a padding byte would be pedantry, not safety.
                    int pad = plain[^1];
                    if (pad is >= 1 and <= 16 && pad <= plain.Length) return plain.AsSpan(0, plain.Length - pad).ToArray();
                    return plain;
                }
        }
    }

    /// <summary>Algorithm 1: the per-object key for RC4 and AES-128.</summary>
    private byte[] ObjectKey(int number, int generation, bool aes)
    {
        var input = new byte[_key.Length + 5 + (aes ? 4 : 0)];
        _key.CopyTo(input, 0);
        input[_key.Length] = (byte)number;
        input[_key.Length + 1] = (byte)(number >> 8);
        input[_key.Length + 2] = (byte)(number >> 16);
        input[_key.Length + 3] = (byte)generation;
        input[_key.Length + 4] = (byte)(generation >> 8);
        if (aes) "sAlT"u8.CopyTo(input.AsSpan(_key.Length + 5));
        byte[] hash = MD5.HashData(input);
        return hash.Take(Math.Min(_key.Length + 5, 16)).ToArray();
    }

    internal static byte[] Rc4(byte[] key, byte[] data)
    {
        var s = new byte[256];
        for (int i = 0; i < 256; i++) s[i] = (byte)i;
        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }
        var output = new byte[data.Length];
        for (int k = 0, i = 0, j = 0; k < data.Length; k++)
        {
            i = (i + 1) & 0xFF;
            j = (j + s[i]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
            output[k] = (byte)(data[k] ^ s[(s[i] + s[j]) & 0xFF]);
        }
        return output;
    }

    // ------------------------------------------------------------------ encrypting (writing)

    /// <summary>
    /// Builds an AES-256 (revision 6) handler for writing, and the encryption dictionary that
    /// describes it. Only printing is permitted to readers holding just the user password — the
    /// same permission set this editor has always written.
    /// </summary>
    public static (PdfSecurityHandler Handler, PdfDictionary Dictionary) CreateAes256(string userPassword, string ownerPassword)
    {
        byte[] fileKey = RandomNumberGenerator.GetBytes(32);
        byte[] user = Utf8Password(userPassword);
        byte[] owner = Utf8Password(ownerPassword);

        byte[] uvs = RandomNumberGenerator.GetBytes(8), uks = RandomNumberGenerator.GetBytes(8);
        byte[] u = Hash(6, user, uvs, Array.Empty<byte>()).Concat(uvs).Concat(uks).ToArray();
        byte[] ue = AesNoIv(Hash(6, user, uks, Array.Empty<byte>()), fileKey, decrypt: false);

        byte[] ovs = RandomNumberGenerator.GetBytes(8), oks = RandomNumberGenerator.GetBytes(8);
        byte[] o = Hash(6, owner, ovs, u).Concat(ovs).Concat(oks).ToArray();
        byte[] oe = AesNoIv(Hash(6, owner, oks, u), fileKey, decrypt: false);

        // Reserved bits set as §7.6.4.2 requires; bit 3 (print) and bit 12 (high-quality print).
        int p = unchecked((int)0xFFFFF0C0) | 4 | 2048;
        var perms = new byte[16];
        BitConverter.GetBytes(p).CopyTo(perms, 0);
        perms[4] = perms[5] = perms[6] = perms[7] = 0xFF;
        perms[8] = (byte)'T';
        perms[9] = (byte)'a';
        perms[10] = (byte)'d';
        perms[11] = (byte)'b';
        RandomNumberGenerator.GetBytes(4).CopyTo(perms, 12);
        byte[] permsEncrypted;
        using (var aes = Aes.Create())
        {
            aes.Key = fileKey;
            permsEncrypted = aes.EncryptEcb(perms, PaddingMode.None);
        }

        var stdCf = new PdfDictionary();
        stdCf.Put(PdfName.Of("AuthEvent"), PdfName.Of("DocOpen"));
        stdCf.Put(PdfName.Of("CFM"), PdfName.Of("AESV3"));
        stdCf.Put(PdfName.Length, new PdfNumber(32));
        var cf = new PdfDictionary();
        cf.Put(PdfName.Of("StdCF"), stdCf);

        var dict = new PdfDictionary();
        dict.Put(PdfName.Filter, PdfName.Of("Standard"));
        dict.Put(PdfName.Of("V"), new PdfNumber(5));
        dict.Put(PdfName.Of("R"), new PdfNumber(6));
        dict.Put(PdfName.Length, new PdfNumber(256));
        dict.Put(PdfName.Of("CF"), cf);
        dict.Put(PdfName.Of("StmF"), PdfName.Of("StdCF"));
        dict.Put(PdfName.Of("StrF"), PdfName.Of("StdCF"));
        dict.Put(PdfName.Of("O"), new PdfString(o, isHex: true));
        dict.Put(PdfName.U, new PdfString(u, isHex: true));
        dict.Put(PdfName.Of("OE"), new PdfString(oe, isHex: true));
        dict.Put(PdfName.Of("UE"), new PdfString(ue, isHex: true));
        dict.Put(PdfName.P, new PdfNumber(p));
        dict.Put(PdfName.Of("Perms"), new PdfString(permsEncrypted, isHex: true));
        dict.Put(PdfName.EncryptMetadata, PdfBoolean.True);

        return (new PdfSecurityHandler(fileKey, Cipher.AesV3, Cipher.AesV3, true, 6), dict);
    }

    /// <summary>
    /// Encrypts a string (or, with <paramref name="isStream"/>, a stream body) of object
    /// <paramref name="number"/> with this handler's cipher — so an incremental update to an RC4
    /// or AES-128 document is written in that document's own scheme.
    /// </summary>
    public byte[] Encrypt(byte[] data, int number, int generation, bool isStream)
    {
        var cipher = isStream ? _streamCipher : _stringCipher;
        switch (cipher)
        {
            case Cipher.None:
                return data;
            case Cipher.Rc4:
                return Rc4(ObjectKey(number, generation, aes: false), data);
            default:
                using (var aes = Aes.Create())
                {
                    aes.Key = cipher == Cipher.AesV3 ? _key : ObjectKey(number, generation, aes: true);
                    byte[] iv = RandomNumberGenerator.GetBytes(16);
                    byte[] encrypted = aes.EncryptCbc(data, iv, PaddingMode.PKCS7);
                    return iv.Concat(encrypted).ToArray();
                }
        }
    }
}
