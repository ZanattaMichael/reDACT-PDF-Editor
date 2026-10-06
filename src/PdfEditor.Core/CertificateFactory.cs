using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using Org.BouncyCastle.X509.Extension;

namespace PdfEditor.Core;

/// <summary>Generates self-signed signing certificates for users who do not have one.</summary>
public static class CertificateFactory
{
    /// <summary>Creates a self-signed RSA-2048 certificate and returns it as PKCS#12 bytes.</summary>
    /// <remarks>See <see cref="Pkcs12File.Write"/> for how the file protects the key.</remarks>
    public static byte[] CreateSelfSignedPkcs12(string commonName, string password, int validYears = 5)
    {
        var random = new SecureRandom();
        var keyGenerator = new RsaKeyPairGenerator();
        keyGenerator.Init(new RsaKeyGenerationParameters(BigInteger.ValueOf(65537), random, 2048, 100));
        var keys = keyGenerator.GenerateKeyPair();

        // Built, not parsed: the name is user text and goes into the certificate verbatim.
        string name = string.IsNullOrWhiteSpace(commonName) ? "Signer" : commonName.Trim();
        var subject = X509Name.GetInstance(new DerSequence(new DerSet(new DerSequence(X509Name.CN, new DerUtf8String(name)))));

        byte[] serial = SecureRandom.GetNextBytes(random, 8);
        serial[0] &= 0x7F; // a positive serial number
        var generator = new X509V3CertificateGenerator();
        generator.SetSerialNumber(new BigInteger(1, serial));
        generator.SetIssuerDN(subject);
        generator.SetSubjectDN(subject);
        generator.SetNotBefore(DateTime.UtcNow.AddDays(-1));
        generator.SetNotAfter(DateTime.UtcNow.AddYears(validYears));
        generator.SetPublicKey(keys.Public);
        generator.AddExtension(X509Extensions.KeyUsage, true,
            new KeyUsage(KeyUsage.DigitalSignature | KeyUsage.NonRepudiation));
        generator.AddExtension(X509Extensions.SubjectKeyIdentifier, false,
            X509ExtensionUtilities.CreateSubjectKeyIdentifier(keys.Public));
        var certificate = generator.Generate(new Asn1SignatureFactory("SHA256WITHRSA", keys.Private, random));

        return Pkcs12File.Write(keys.Private, certificate, password, random);
    }
}
