using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace PdfEditor.Core;

/// <summary>Generates self-signed signing certificates for users who do not have one.</summary>
public static class CertificateFactory
{
    /// <summary>Creates a self-signed RSA-2048 certificate and returns it as PKCS#12 bytes.</summary>
    /// <remarks>
    /// Built with .NET's own cryptography rather than a third-party library. The key is shrouded
    /// and the file sealed with AES-256/SHA-256 (PBES2), which every current platform reads.
    /// </remarks>
    public static byte[] CreateSelfSignedPkcs12(string commonName, string password, int validYears = 5)
    {
        using var rsa = RSA.Create(2048);
        // Built, not parsed: the name is user text and goes into the certificate verbatim.
        var nameBuilder = new X500DistinguishedNameBuilder();
        nameBuilder.AddCommonName(string.IsNullOrWhiteSpace(commonName) ? "Signer" : commonName.Trim());
        var subject = nameBuilder.Build();
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        var serial = new byte[8];
        RandomNumberGenerator.Fill(serial);
        serial[0] &= 0x7F; // a positive serial number
        using var certificate = request.Create(subject, X509SignatureGenerator.CreateForRSA(rsa, RSASignaturePadding.Pkcs1),
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(validYears), serial);

        var pbe = new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000);
        var localKeyId = new Pkcs9LocalKeyId(certificate.GetCertHash());

        var keys = new Pkcs12SafeContents();
        keys.AddShroudedKey(rsa, password, pbe).Attributes.Add(localKeyId);
        var certs = new Pkcs12SafeContents();
        certs.AddCertificate(certificate).Attributes.Add(localKeyId);

        var builder = new Pkcs12Builder();
        builder.AddSafeContentsUnencrypted(keys); // the key bag inside is itself encrypted
        builder.AddSafeContentsEncrypted(certs, password, pbe);
        builder.SealWithMac(password, HashAlgorithmName.SHA256, 100_000);
        return builder.Encode();
    }
}
