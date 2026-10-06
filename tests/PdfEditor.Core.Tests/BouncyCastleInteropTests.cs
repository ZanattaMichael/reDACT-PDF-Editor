using System.Formats.Asn1;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Nist;
using Org.BouncyCastle.Asn1.Pkcs;
using PdfEditor.Core;
using Xunit;
using BcContentInfo = Org.BouncyCastle.Asn1.Pkcs.ContentInfo;
using BcAlgorithmIdentifier = Org.BouncyCastle.Asn1.X509.AlgorithmIdentifier;

namespace PdfEditor.Tests;

/// <summary>
/// The signing path's cryptography is BouncyCastle; these tests hold it to what other software
/// reads and writes, with .NET's own CMS and PKCS#12 implementation as the second opinion. They
/// cover the cases where the two libraries have been seen to disagree: non-ASCII passwords, the
/// legacy PKCS#12 ciphers, empty passwords, and keys that are not RSA.
/// </summary>
public partial class BouncyCastleInteropTests
{
    private const string NonAscii = "pässwörd-ключ";

    // ------------------------------------------------------------ certificates written here

    [Fact]
    public void CreatedPkcs12_ProtectsTheKeyWithAes256AndSealsTheFileWithSha256_At100kIterations()
    {
        byte[] pfx = CertificateFactory.CreateSelfSignedPkcs12("Alice", "pw");

        var file = Pfx.GetInstance(Asn1Object.FromByteArray(pfx));
        Assert.Equal(NistObjectIdentifiers.IdSha256, file.MacData.Mac.DigestAlgorithm.Algorithm);
        Assert.Equal(100_000, file.MacData.Iterations.IntValueExact);

        BcContentInfo[] safes = AuthenticatedSafe.GetInstance(
            Asn1Object.FromByteArray(Asn1OctetString.GetInstance(file.AuthSafe.Content).GetOctets())).GetContentInfo();
        var keyBag = SafeBag.GetInstance(Asn1Sequence.GetInstance(
            Asn1Object.FromByteArray(Asn1OctetString.GetInstance(safes[0].Content).GetOctets()))[0]);
        Assert.Equal(PkcsObjectIdentifiers.Pkcs8ShroudedKeyBag, keyBag.BagID);
        AssertAes256Pbes2(EncryptedPrivateKeyInfo.GetInstance(keyBag.BagValue).EncryptionAlgorithm);
        Assert.Equal(PkcsObjectIdentifiers.EncryptedData, safes[1].ContentType);
        AssertAes256Pbes2(EncryptedData.GetInstance(safes[1].Content).EncryptionAlgorithm);
    }

    private static void AssertAes256Pbes2(BcAlgorithmIdentifier algorithm)
    {
        Assert.Equal(PkcsObjectIdentifiers.IdPbeS2, algorithm.Algorithm);
        var parameters = PbeS2Parameters.GetInstance(algorithm.Parameters);
        Assert.Equal(NistObjectIdentifiers.IdAes256Cbc, parameters.EncryptionScheme.Algorithm);
        var kdf = Pbkdf2Params.GetInstance(parameters.KeyDerivationFunc.Parameters);
        Assert.Equal(PkcsObjectIdentifiers.IdHmacWithSha256, kdf.Prf.Algorithm);
        Assert.Equal(100_000, kdf.IterationCountObject.IntValueExact);
    }

    [Fact]
    public void CreatedPkcs12_WithANonAsciiPassword_OpensInDotNet()
    {
        // BouncyCastle's own PKCS#12 writer encodes a PBES2 password as Latin-1, so a file it wrote
        // under this password would not open anywhere else.
        byte[] pfx = CertificateFactory.CreateSelfSignedPkcs12("Zoë Signer", NonAscii);

        using var certificate = new X509Certificate2(pfx, NonAscii, X509KeyStorageFlags.EphemeralKeySet);
        Assert.True(certificate.HasPrivateKey);
        Assert.Equal("Zoë Signer", certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false));
        using var rsa = certificate.GetRSAPrivateKey()!;
        Assert.Equal(2048, rsa.KeySize);
    }

    [Fact]
    public void CreatedCertificate_IsForSigning_AndNamesTheSignerVerbatim()
    {
        // The name is user text: commas and a leading '#' must stay part of one common name.
        byte[] pfx = CertificateFactory.CreateSelfSignedPkcs12("#1 Signer, O=Not An Org", "pw");

        using var certificate = new X509Certificate2(pfx, "pw", X509KeyStorageFlags.EphemeralKeySet);
        Assert.Equal("CN=\"#1 Signer, O=Not An Org\"", certificate.Subject);
        Assert.Equal(certificate.Subject, certificate.Issuer);
        var usage = Assert.Single(certificate.Extensions.OfType<X509KeyUsageExtension>());
        Assert.True(usage.Critical);
        Assert.Equal(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, usage.KeyUsages);
        Assert.Single(certificate.Extensions.OfType<X509SubjectKeyIdentifierExtension>());
        Assert.InRange(certificate.NotAfter, DateTime.Now.AddYears(5).AddDays(-2), DateTime.Now.AddYears(5).AddDays(1));
    }

    // ------------------------------------------------------------ certificates written elsewhere

    [Fact]
    public void SignDigitally_WithAnAesPkcs12UnderANonAsciiPassword_Signs()
    {
        // The default from OpenSSL 3, .NET and Java: PBES2 with AES, its password in UTF-8.
        using var rsa = RSA.Create(2048);
        byte[] pfx = DotNetPkcs12(SelfSigned(rsa, "CN=Zoë Signer"), rsa, NonAscii,
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 2048));

        var signature = SignAndCheck(pfx, NonAscii);
        Assert.Equal("Zoë Signer", signature.SignerName);
    }

    [Fact]
    public void SignDigitally_WithALegacyTripleDesPkcs12_Signs()
    {
        // The PKCS#12 ciphers take the password as a BMPString, not as UTF-8.
        using var rsa = RSA.Create(2048);
        byte[] pfx = DotNetPkcs12(SelfSigned(rsa, "CN=Legacy Signer"), rsa, NonAscii,
            new PbeParameters(PbeEncryptionAlgorithm.TripleDes3KeyPkcs12, HashAlgorithmName.SHA1, 2048),
            HashAlgorithmName.SHA1);

        Assert.Equal("Legacy Signer", SignAndCheck(pfx, NonAscii).SignerName);
    }

    [Fact]
    public void SignDigitally_WithAPkcs12UnderAnEmptyPassword_Signs()
    {
        using var rsa = RSA.Create(2048);
        byte[] pfx = DotNetPkcs12(SelfSigned(rsa, "CN=No Password"), rsa, "",
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 2048));

        Assert.Equal("No Password", SignAndCheck(pfx, "").SignerName);
    }

    [Fact]
    public void SignDigitally_WithAnEcdsaCertificate_ProducesASignatureDotNetVerifies()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Curve Signer", ecdsa, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        byte[] pfx = DotNetPkcs12(certificate, ecdsa, "pw",
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 2048));

        byte[] signed = Signer.SignDigitally(TestPdfs.WithText(("contract body", 72, 700, 12)), pfx, "pw");

        Assert.True(Assert.Single(Signer.GetSignatures(signed)).IntegrityValid);
        var cms = DotNetCms(signed);
        cms.CheckSignature(verifySignatureOnly: true);
        Assert.Equal("Curve Signer", cms.SignerInfos[0].Certificate!.GetNameInfo(X509NameType.SimpleName, false));
    }

    [Fact]
    public void SignDigitally_WithAPkcs12AskingForTooManyIterations_RefusesIt()
    {
        // Each iteration is work the host does before it can answer; a file can ask for billions.
        using var rsa = RSA.Create(2048);
        byte[] pfx = DotNetPkcs12(SelfSigned(rsa, "CN=Slow"), rsa, "pw",
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 2048),
            macIterations: 1_000_000);

        var ex = Assert.ThrowsAny<ArgumentException>(() =>
            Signer.SignDigitally(TestPdfs.WithText(("x", 72, 700, 12)), pfx, "pw"));
        Assert.Contains("1000000 key-derivation iterations", ex.Message);
    }

    [Fact]
    public void SignDigitally_WithTheWrongPassword_SaysSo()
    {
        byte[] pfx = CertificateFactory.CreateSelfSignedPkcs12("Alice", NonAscii);

        var ex = Assert.Throws<ArgumentException>(() =>
            Signer.SignDigitally(TestPdfs.WithText(("x", 72, 700, 12)), pfx, "pässwörd"));
        Assert.Contains("the password is wrong", ex.Message);
    }

    // ------------------------------------------------------------ CMS, each way

    [Fact]
    public void SignDigitally_WritesTheCmsDotNetExpects()
    {
        byte[] pfx = CertificateFactory.CreateSelfSignedPkcs12("Alice Example", "pw");
        byte[] signed = Signer.SignDigitally(TestPdfs.WithText(("contract body", 72, 700, 12)), pfx, "pw");

        var cms = DotNetCms(signed);
        cms.CheckSignature(verifySignatureOnly: true);
        var signer = Assert.Single(cms.SignerInfos.Cast<System.Security.Cryptography.Pkcs.SignerInfo>());
        Assert.Equal(SubjectIdentifierType.IssuerAndSerialNumber, signer.SignerIdentifier.Type);
        Assert.Equal("2.16.840.1.101.3.4.2.1", signer.DigestAlgorithm.Value); // SHA-256
        Assert.Contains(signer.SignedAttributes.Cast<CryptographicAttributeObject>(),
            a => a.Oid.Value == "1.2.840.113549.1.9.5"); // signing time
        Assert.Equal("Alice Example", signer.Certificate!.GetNameInfo(X509NameType.SimpleName, false));
    }

    [Fact]
    public void GetSignatures_VerifiesACmsMadeByDotNet_AndNoticesWhenItsBytesChange()
    {
        // Sign once to get the signature's place in the file, then put .NET's CMS in it instead.
        byte[] signed = Signer.SignDigitally(TestPdfs.WithText(("contract body", 72, 700, 12)),
            CertificateFactory.CreateSelfSignedPkcs12("Placeholder", "pw"), "pw");
        var (range, contentsAt, contentsLength) = SignaturePlace(signed);
        using var rsa = RSA.Create(2048);
        using var certificate = SelfSigned(rsa, "CN=DotNet Signer");
        var cms = new SignedCms(new System.Security.Cryptography.Pkcs.ContentInfo(SignedRange(signed, range)), detached: true);
        cms.ComputeSignature(new CmsSigner(certificate) { DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1") });
        byte[] hex = Encoding.ASCII.GetBytes(Convert.ToHexString(cms.Encode()).PadRight(contentsLength, '0'));
        hex.CopyTo(signed, contentsAt);

        var signature = Assert.Single(Signer.GetSignatures(signed));
        Assert.True(signature.IntegrityValid);
        Assert.Equal("DotNet Signer", signature.SignerName);

        // One signed byte changed (in the header's binary comment, so the file still parses): the
        // same CMS no longer matches.
        int comment = Array.IndexOf(signed, (byte)'\n') + 2;
        signed[comment] ^= 0x01;
        var tampered = Assert.Single(Signer.GetSignatures(signed));
        Assert.False(tampered.IntegrityValid);
        Assert.Equal("DotNet Signer", tampered.SignerName);
    }

    [Fact]
    public void GetSignatures_ContentsThatAreNotACms_AreInvalidAndUnnamed()
    {
        byte[] signed = Signer.SignDigitally(TestPdfs.WithText(("contract body", 72, 700, 12)),
            CertificateFactory.CreateSelfSignedPkcs12("Alice", "pw"), "pw");
        var (_, contentsAt, contentsLength) = SignaturePlace(signed);
        Encoding.ASCII.GetBytes("DEADBEEF".PadRight(contentsLength, '0')).CopyTo(signed, contentsAt);

        var signature = Assert.Single(Signer.GetSignatures(signed));
        Assert.False(signature.IntegrityValid);
        Assert.Null(signature.SignerName);
    }

    [Fact]
    public void GetSignatures_AByteRangeBeyondTheFile_IsInvalidButStillNamesTheSigner()
    {
        byte[] signed = Signer.SignDigitally(TestPdfs.WithText(("contract body", 72, 700, 12)),
            CertificateFactory.CreateSelfSignedPkcs12("Alice", "pw"), "pw");
        var match = ByteRange().Matches(Encoding.Latin1.GetString(signed))[^1];
        // The range is padded to a fixed width, so a longer number still fits in place.
        long beyondTheEnd = long.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) + 1_000_000;
        string beyond = $"/ByteRange [0 {match.Groups[2].Value} {match.Groups[3].Value} {beyondTheEnd}]";
        Encoding.ASCII.GetBytes(beyond).CopyTo(signed, match.Index);

        var signature = Assert.Single(Signer.GetSignatures(signed));
        Assert.False(signature.IntegrityValid);
        Assert.Equal("Alice", signature.SignerName);
    }

    // ------------------------------------------------------------ helpers

    private static SignatureInfo SignAndCheck(byte[] pfx, string password)
    {
        byte[] signed = Signer.SignDigitally(TestPdfs.WithText(("contract body", 72, 700, 12)), pfx, password);
        var signature = Assert.Single(Signer.GetSignatures(signed));
        Assert.True(signature.IntegrityValid);
        DotNetCms(signed).CheckSignature(verifySignatureOnly: true);
        return signature;
    }

    private static X509Certificate2 SelfSigned(RSA rsa, string subject) =>
        new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

    /// <summary>
    /// A PKCS#12 file as .NET writes it, with no localKeyId attributes, so the key is matched to
    /// its certificate by the public key alone.
    /// </summary>
    private static byte[] DotNetPkcs12(X509Certificate2 certificate, AsymmetricAlgorithm key, string password,
        PbeParameters pbe, HashAlgorithmName? macHash = null, int macIterations = 2048)
    {
        var keys = new Pkcs12SafeContents();
        keys.AddShroudedKey(key, password, pbe);
        var certs = new Pkcs12SafeContents();
        certs.AddCertificate(new X509Certificate2(certificate.RawData));
        var builder = new Pkcs12Builder();
        builder.AddSafeContentsUnencrypted(keys);
        builder.AddSafeContentsEncrypted(certs, password, pbe);
        builder.SealWithMac(password, macHash ?? HashAlgorithmName.SHA256, macIterations);
        return builder.Encode();
    }

    /// <summary>The document's (last) signature, decoded by .NET over the bytes it claims to cover.</summary>
    private static SignedCms DotNetCms(byte[] pdf)
    {
        var (range, contentsAt, contentsLength) = SignaturePlace(pdf);
        byte[] padded = Convert.FromHexString(Encoding.ASCII.GetString(pdf, contentsAt, contentsLength));
        AsnDecoder.ReadEncodedValue(padded, AsnEncodingRules.BER, out _, out _, out int length);
        var cms = new SignedCms(new System.Security.Cryptography.Pkcs.ContentInfo(SignedRange(pdf, range)), detached: true);
        cms.Decode(padded.AsSpan(0, length));
        return cms;
    }

    [GeneratedRegex(@"/ByteRange\s*\[\s*(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*\]")]
    private static partial Regex ByteRange();

    /// <summary>The last /ByteRange, and where the hex digits of its /Contents lie.</summary>
    private static (long[] Range, int ContentsAt, int ContentsLength) SignaturePlace(byte[] pdf)
    {
        var match = ByteRange().Matches(Encoding.Latin1.GetString(pdf))[^1];
        long[] range = Enumerable.Range(1, 4).Select(i => long.Parse(match.Groups[i].Value, CultureInfo.InvariantCulture)).ToArray();
        int contentsAt = (int)range[1] + 1; // after the '<'
        return (range, contentsAt, (int)(range[2] - range[1]) - 2);
    }

    private static byte[] SignedRange(byte[] pdf, long[] range) =>
        pdf.AsSpan((int)range[0], (int)range[1]).ToArray().Concat(pdf.AsSpan((int)range[2], (int)range[3]).ToArray()).ToArray();
}
