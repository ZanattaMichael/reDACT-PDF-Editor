using System.Text;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Nist;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities;
using Org.BouncyCastle.X509;

namespace PdfEditor.Core;

/// <summary>
/// Reads and writes PKCS#12 (.pfx/.p12) files (RFC 7292) with BouncyCastle's ASN.1 types and ciphers.
/// <para>
/// BouncyCastle's own <see cref="Pkcs12Store"/> is not used: it turns a PBES2 password into bytes
/// by truncating each character to eight bits, where OpenSSL, .NET and Java encode it as UTF-8. A
/// certificate protected with AES and a non-ASCII password — the default from each of them —
/// would not open, and one written here would not open anywhere else. It also writes only 1,024
/// iterations and a SHA-1 MAC. Here PBES2 passwords are UTF-8, and the PKCS#12 ones (the MAC
/// and the legacy ciphers) are a BMPString, as the RFC specifies.
/// </para>
/// </summary>
internal static class Pkcs12File
{
    /// <summary>PBKDF2 and MAC iterations protecting a file written here.</summary>
    private const int WriteIterations = 100_000;

    /// <summary>The most iterations a file may ask for; more is refused rather than left to stall the host (.NET's limit too).</summary>
    private const int MaxIterations = 600_000;

    private static readonly Dictionary<DerObjectIdentifier, int> KeyBitsByCipher = new()
    {
        [NistObjectIdentifiers.IdAes128Cbc] = 128,
        [NistObjectIdentifiers.IdAes192Cbc] = 192,
        [NistObjectIdentifiers.IdAes256Cbc] = 256,
        [PkcsObjectIdentifiers.DesEde3Cbc] = 192,
    };

    private const string Unreadable =
        "The certificate could not be opened: the password is wrong, or the file is not a PKCS#12 (.pfx/.p12) certificate.";

    /// <summary>The private keys and certificates a file holds, each with its localKeyId when it has one.</summary>
    public sealed record Contents(
        IReadOnlyList<(AsymmetricKeyParameter Key, byte[]? Id)> Keys,
        IReadOnlyList<(X509Certificate Certificate, byte[]? Id)> Certificates);

    // ------------------------------------------------------------------ writing

    /// <summary>
    /// A file holding <paramref name="key"/> and its <paramref name="certificate"/>: the key shrouded
    /// and the certificate encrypted with PBES2 (AES-256-CBC, PBKDF2 with HMAC-SHA-256), and the
    /// file sealed with an HMAC-SHA-256 MAC, which every current platform reads.
    /// </summary>
    public static byte[] Write(AsymmetricKeyParameter key, X509Certificate certificate, string password, SecureRandom random)
    {
        // The certificate's SHA-1 thumbprint pairs the key with its certificate, as other tools do.
        var localKeyId = new DerSet(new AttributePkcs(PkcsObjectIdentifiers.Pkcs9AtLocalKeyID,
            new DerSet(new DerOctetString(DigestUtilities.CalculateDigest("SHA-1", certificate.GetEncoded())))));

        var (keyAlgorithm, shroudedKey) = Pbes2Encrypt(
            PrivateKeyInfoFactory.CreatePrivateKeyInfo(key).GetEncoded(Asn1Encodable.Der), password, random);
        var keyBag = new SafeBag(PkcsObjectIdentifiers.Pkcs8ShroudedKeyBag,
            new EncryptedPrivateKeyInfo(keyAlgorithm, shroudedKey), localKeyId);
        var certBag = new SafeBag(PkcsObjectIdentifiers.CertBag,
            new CertBag(PkcsObjectIdentifiers.X509Certificate, new DerOctetString(certificate.GetEncoded())), localKeyId);
        var (certAlgorithm, certs) = Pbes2Encrypt(new DerSequence(certBag).GetEncoded(Asn1Encodable.Der), password, random);

        byte[] authenticatedSafe = new AuthenticatedSafe(new[]
        {
            // The key bag is itself encrypted, so its safe is not encrypted again.
            new ContentInfo(PkcsObjectIdentifiers.Data, new DerOctetString(new DerSequence(keyBag).GetEncoded(Asn1Encodable.Der))),
            new ContentInfo(PkcsObjectIdentifiers.EncryptedData,
                new EncryptedData(PkcsObjectIdentifiers.Data, certAlgorithm, new DerOctetString(certs))),
        }).GetEncoded(Asn1Encodable.Der);

        byte[] salt = SecureRandom.GetNextBytes(random, 32);
        var digest = new AlgorithmIdentifier(NistObjectIdentifiers.IdSha256, DerNull.Instance);
        byte[] mac = Mac(digest.Algorithm, Pkcs12Password(password, wrongZero: false), salt, WriteIterations, authenticatedSafe);
        return new Pfx(new ContentInfo(PkcsObjectIdentifiers.Data, new DerOctetString(authenticatedSafe)),
            new MacData(new DigestInfo(digest, mac), salt, WriteIterations)).GetEncoded(Asn1Encodable.Der);
    }

    private static (AlgorithmIdentifier Algorithm, byte[] Encrypted) Pbes2Encrypt(byte[] data, string password, SecureRandom random)
    {
        var parameters = PbeS2Parameters.GetInstance(PbeUtilities.GenerateAlgorithmParameters(NistObjectIdentifiers.IdAes256Cbc,
            PkcsObjectIdentifiers.IdHmacWithSha256, SecureRandom.GetNextBytes(random, 16), WriteIterations, random));
        return (new AlgorithmIdentifier(PkcsObjectIdentifiers.IdPbeS2, parameters), Pbes2(encrypt: true, parameters, password, data));
    }

    // ------------------------------------------------------------------ reading

    /// <summary>
    /// The keys and certificates in a file, after checking its MAC; throws <see cref="ArgumentException"/>
    /// when the password is wrong or the file cannot be read.
    /// </summary>
    public static Contents Read(byte[] pkcs12, string password)
    {
        try
        {
            var pfx = Pfx.GetInstance(Asn1Object.FromByteArray(pkcs12));
            if (!pfx.AuthSafe.ContentType.Equals(PkcsObjectIdentifiers.Data))
                throw new UnsupportedFileException("The certificate file is sealed with a public key rather than a password, which this editor cannot open.");
            byte[] authenticatedSafe = Asn1OctetString.GetInstance(pfx.AuthSafe.Content).GetOctets();
            bool wrongZero = VerifyMac(pfx.MacData, password, authenticatedSafe);

            var keys = new List<(AsymmetricKeyParameter, byte[]?)>();
            var certificates = new List<(X509Certificate, byte[]?)>();
            foreach (var info in AuthenticatedSafe.GetInstance(Asn1Object.FromByteArray(authenticatedSafe)).GetContentInfo())
            {
                byte[] safe;
                if (info.ContentType.Equals(PkcsObjectIdentifiers.Data))
                    safe = Asn1OctetString.GetInstance(info.Content).GetOctets();
                else if (info.ContentType.Equals(PkcsObjectIdentifiers.EncryptedData))
                {
                    var encrypted = EncryptedData.GetInstance(info.Content);
                    safe = Decrypt(encrypted.EncryptionAlgorithm, encrypted.Content.GetOctets(), password, wrongZero);
                }
                else
                    throw new UnsupportedFileException("The certificate file is encrypted with a public key rather than a password, which this editor cannot open.");
                ReadBags(Asn1Sequence.GetInstance(Asn1Object.FromByteArray(safe)), password, wrongZero, keys, certificates);
            }
            return new Contents(keys, certificates);
        }
        catch (Exception ex) when (ex is not UnsupportedFileException
                                   && ex is IOException or ArgumentException or InvalidCastException
                                       or InvalidOperationException or CryptoException or SecurityUtilityException)
        {
            throw new ArgumentException(Unreadable, ex);
        }
    }

    private static void ReadBags(Asn1Sequence safe, string password, bool wrongZero,
        List<(AsymmetricKeyParameter, byte[]?)> keys, List<(X509Certificate, byte[]?)> certificates)
    {
        foreach (var element in safe)
        {
            var bag = SafeBag.GetInstance(element);
            byte[]? id = LocalKeyId(bag.BagAttributes);
            if (bag.BagID.Equals(PkcsObjectIdentifiers.Pkcs8ShroudedKeyBag))
            {
                var shrouded = EncryptedPrivateKeyInfo.GetInstance(bag.BagValue);
                keys.Add((PrivateKeyFactory.CreateKey(Decrypt(shrouded.EncryptionAlgorithm, shrouded.GetEncryptedData(), password, wrongZero)), id));
            }
            else if (bag.BagID.Equals(PkcsObjectIdentifiers.KeyBag))
                keys.Add((PrivateKeyFactory.CreateKey(PrivateKeyInfo.GetInstance(bag.BagValue)), id));
            else if (bag.BagID.Equals(PkcsObjectIdentifiers.CertBag)
                     && CertBag.GetInstance(bag.BagValue) is var cert && cert.CertID.Equals(PkcsObjectIdentifiers.X509Certificate))
                certificates.Add((new X509CertificateParser().ReadCertificate(Asn1OctetString.GetInstance(cert.CertValue).GetOctets()), id));
            else if (bag.BagID.Equals(PkcsObjectIdentifiers.SafeContentsBag))
                ReadBags(Asn1Sequence.GetInstance(bag.BagValue), password, wrongZero, keys, certificates);
        }
    }

    private static byte[]? LocalKeyId(Asn1Set? attributes) =>
        attributes?.Select(AttributePkcs.GetInstance)
            .FirstOrDefault(a => a.AttrType.Equals(PkcsObjectIdentifiers.Pkcs9AtLocalKeyID))
            ?.AttrValues.Select(v => Asn1OctetString.GetInstance(v).GetOctets()).FirstOrDefault();

    /// <summary>
    /// Checks the file's MAC, and returns whether it matched an empty password written as a lone
    /// BMPString terminator (two zero bytes, as some tools write it) rather than as no bytes at all.
    /// </summary>
    private static bool VerifyMac(MacData? macData, string password, byte[] authenticatedSafe)
    {
        if (macData == null) return false;
        var algorithm = macData.Mac.DigestAlgorithm.Algorithm;
        byte[] salt = macData.MacSalt.GetOctets();
        int iterations = Iterations(macData.Iterations);
        byte[] expected = macData.Mac.Digest.GetOctets();
        var encodings = password.Length == 0 ? new[] { false, true } : new[] { false };
        bool? matched = encodings
            .Where(zero => Arrays.FixedTimeEquals(Mac(algorithm, Pkcs12Password(password, zero), salt, iterations, authenticatedSafe), expected))
            .Select(zero => (bool?)zero)
            .FirstOrDefault();
        return matched ?? throw new InvalidCipherTextException("The PKCS#12 MAC does not match: the password is wrong or the file is damaged.");
    }

    // ------------------------------------------------------------------ the ciphers

    /// <summary>Decrypts a safe or a shrouded key with PBES2 or with one of the PKCS#12 schemes (RFC 7292 appendix C).</summary>
    private static byte[] Decrypt(AlgorithmIdentifier algorithm, byte[] data, string password, bool wrongZero)
    {
        if (algorithm.Algorithm.Equals(PkcsObjectIdentifiers.IdPbeS2))
            return Pbes2(encrypt: false, PbeS2Parameters.GetInstance(algorithm.Parameters), password, data);

        Iterations(Pkcs12PbeParams.GetInstance(algorithm.Parameters).IterationsObject);
        var cipher = (IBufferedCipher)PbeUtilities.CreateEngine(algorithm);
        cipher.Init(false, PbeUtilities.GenerateCipherParameters(algorithm, password.ToCharArray(), wrongZero));
        return cipher.DoFinal(data);
    }

    /// <summary>PBES2 (RFC 8018 §6.2) with PBKDF2, the password encoded as UTF-8.</summary>
    private static byte[] Pbes2(bool encrypt, PbeS2Parameters parameters, string password, byte[] data)
    {
        if (!parameters.KeyDerivationFunc.Algorithm.Equals(PkcsObjectIdentifiers.IdPbkdf2))
            throw new UnsupportedFileException("The certificate file uses a key derivation other than PBKDF2, which this editor cannot open.");
        var kdf = Pbkdf2Params.GetInstance(parameters.KeyDerivationFunc.Parameters);
        var scheme = parameters.EncryptionScheme;
        var generator = new Pkcs5S2ParametersGenerator(DigestUtilities.GetDigest(kdf.Prf.Algorithm));
        generator.Init(Encoding.UTF8.GetBytes(password), kdf.GetSalt(), Iterations(kdf.IterationCountObject));
        int keyBits = kdf.KeyLengthObject is { } length ? length.IntValueExact * 8 : KeyBits(scheme.Algorithm);
        var cipher = CipherUtilities.GetCipher(scheme.Algorithm);
        cipher.Init(encrypt, new ParametersWithIV(generator.GenerateDerivedParameters(scheme.Algorithm.Id, keyBits),
            Asn1OctetString.GetInstance(scheme.Parameters).GetOctets()));
        return cipher.DoFinal(data);
    }

    /// <summary>The key size of a cipher PBES2 is used with in PKCS#12 files, when its parameters do not give one.</summary>
    private static int KeyBits(DerObjectIdentifier cipher) => KeyBitsByCipher.TryGetValue(cipher, out int bits)
        ? bits
        : throw new UnsupportedFileException($"The certificate file is encrypted with a cipher ({cipher.Id}) this editor cannot open.");

    /// <summary>The PKCS#12 MAC: HMAC keyed by the RFC 7292 appendix B derivation.</summary>
    private static byte[] Mac(DerObjectIdentifier digest, byte[] password, byte[] salt, int iterations, byte[] data)
    {
        var derivation = new Pkcs12ParametersGenerator(DigestUtilities.GetDigest(digest));
        derivation.Init(password, salt, iterations);
        var hmac = new HMac(DigestUtilities.GetDigest(digest));
        hmac.Init(derivation.GenerateDerivedMacParameters(hmac.GetUnderlyingDigest().GetDigestSize() * 8));
        hmac.BlockUpdate(data, 0, data.Length);
        var mac = new byte[hmac.GetMacSize()];
        hmac.DoFinal(mac, 0);
        return mac;
    }

    /// <summary>The password as RFC 7292 appendix B.1 has it: a BMPString with its terminator.</summary>
    private static byte[] Pkcs12Password(string password, bool wrongZero) =>
        PbeParametersGenerator.Pkcs12PasswordToBytes(password.ToCharArray(), wrongZero);

    private static int Iterations(DerInteger count)
    {
        if (!count.TryGetIntValueExact(out int iterations) || iterations < 1 || iterations > MaxIterations)
            throw new UnsupportedFileException(
                $"The certificate file asks for {count.Value} key-derivation iterations; at most {MaxIterations:N0} are accepted.");
        return iterations;
    }

    /// <summary>A file this editor reads correctly but will not open; its message says why.</summary>
    private sealed class UnsupportedFileException(string message) : ArgumentException(message);
}
