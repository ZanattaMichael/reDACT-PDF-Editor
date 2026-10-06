using System.Security.Cryptography;
using System.Text;
using PdfEditor.Core;
using PdfEditor.Core.Pdf;
using Xunit;

namespace PdfEditor.Tests;

/// <summary>
/// Documents encrypted with the older standard security handlers: RC4 40-bit (R2), RC4 128-bit
/// (R3) and AES-128 (R4). reDACT never protects a document with these, but plenty of files in the
/// wild use them, and editing one writes it back in its own scheme. The fixtures are encrypted
/// here, by a separate implementation of the algorithms in ISO 32000-1 §7.6.3, so the handler is
/// checked against the specification and not against itself.
/// </summary>
public class LegacyEncryptionTests
{
    private const string UserPassword = "user pw";
    private const string OwnerPassword = "owner pw";

    public static TheoryData<string> Schemes => new() { "RC4-40", "RC4-128", "AES-128" };

    [Theory]
    [MemberData(nameof(Schemes))]
    public void OpensWithTheUserPassword(string scheme)
    {
        byte[] pdf = LegacyPdf.Build(scheme, UserPassword, OwnerPassword);

        var doc = PdfDocument.Open(pdf, UserPassword);

        Assert.True(doc.WasEncrypted);
        Assert.Equal("Quarterly figures", doc.Info!.GetText(PdfName.Title));
        Assert.Contains("Secret words", LocationTextExtraction.ExtractPage(doc.GetPage(1)));
    }

    [Theory]
    [MemberData(nameof(Schemes))]
    public void OpensWithTheOwnerPassword(string scheme)
    {
        byte[] pdf = LegacyPdf.Build(scheme, UserPassword, OwnerPassword);

        Assert.Contains("Secret words", TestPdfAssert.ExtractText(pdf, 1, OwnerPassword));
    }

    [Theory]
    [MemberData(nameof(Schemes))]
    public void WrongOrMissingPassword_IsAPasswordError(string scheme)
    {
        byte[] pdf = LegacyPdf.Build(scheme, UserPassword, OwnerPassword);

        var wrong = Assert.Throws<PdfPasswordException>(() => PdfDocument.Open(pdf, "guess"));
        Assert.Contains("incorrect", wrong.Message);
        var missing = Assert.Throws<PdfPasswordException>(() => PdfDocument.Open(pdf));
        Assert.Contains("password is required", missing.Message);
    }

    [Theory]
    [MemberData(nameof(Schemes))]
    public void PermissionsOnlyDocument_OpensWithoutAPassword(string scheme)
    {
        byte[] pdf = LegacyPdf.Build(scheme, userPassword: "", OwnerPassword);

        Assert.Contains("Secret words", TestPdfAssert.ExtractText(pdf));
    }

    [Theory]
    [MemberData(nameof(Schemes))]
    public void Redacting_RemovesTheText_AndKeepsTheEncryption(string scheme)
    {
        byte[] pdf = LegacyPdf.Build(scheme, UserPassword, OwnerPassword);
        var hit = Assert.Single(TextTools.FindText(pdf, "Secret", password: UserPassword));

        var result = Redactor.Redact(pdf, new[] { new RectRegion(1, hit.X, hit.Y, hit.Width, hit.Height) }, UserPassword);

        // The edit is written in the document's own scheme, under its own passwords. (2.x wrote it
        // out unencrypted, leaving re-protection to a separate step that was easy to forget.)
        Assert.True(Encryptor.IsEncrypted(result.Pdf));
        Assert.True(Encryptor.CanOpen(result.Pdf, OwnerPassword));
        string text = TestPdfAssert.ExtractText(result.Pdf, password: UserPassword);
        Assert.DoesNotContain("Secret", text);
        Assert.Contains("words", text);
    }

    [Fact]
    public void AesDocument_WithUnencryptedMetadata_ReadsBoth()
    {
        byte[] pdf = LegacyPdf.Build("AES-128", UserPassword, OwnerPassword, encryptMetadata: false);

        var doc = PdfDocument.Open(pdf, UserPassword);

        var metadata = doc.Catalog!.GetAsStream(PdfName.Metadata)!;
        Assert.Contains("<x:xmpmeta", Encoding.ASCII.GetString(metadata.GetDecodedBytes()));
        Assert.Contains("Secret words", LocationTextExtraction.ExtractPage(doc.GetPage(1)));
    }

    [Theory]
    [MemberData(nameof(Schemes))]
    public void AnEdit_IsWrittenInTheDocumentsOwnScheme_UnderItsOwnKey(string scheme)
    {
        byte[] pdf = LegacyPdf.Build(scheme, UserPassword, OwnerPassword);

        byte[] edited = PageTools.Rotate(pdf, new[] { 1 }, 90, UserPassword).Pdf;

        // The same /Encrypt entries and first file identifier: the key these schemes derive from
        // them, the passwords and the permissions are all unchanged.
        static string[] Protection(byte[] bytes)
        {
            var doc = PdfDocument.Open(bytes, OwnerPassword);
            var e = doc.Trailer.GetAsDictionary(PdfName.Encrypt)!;
            return new[]
            {
                $"V{e.GetAsInt(PdfName.Of("V"))} R{e.GetAsInt(PdfName.Of("R"))} P{e.GetAsNumber(PdfName.P)!.LongValue()}",
                Convert.ToHexString(e.GetAsString(PdfName.Of("O"))!.Bytes),
                Convert.ToHexString(e.GetAsString(PdfName.U)!.Bytes),
                Convert.ToHexString(doc.Trailer.GetAsArray(PdfName.ID)!.GetAsString(0)!.Bytes),
            };
        }
        Assert.Equal(Protection(pdf), Protection(edited));
        Assert.Equal(90, PdfDocument.Open(edited, UserPassword).GetPage(1).Rotation);
        Assert.Contains("Secret words", TestPdfAssert.ExtractText(edited, 1, OwnerPassword));
        Assert.Throws<PdfPasswordException>(() => PdfDocument.Open(edited));
    }

    [Fact]
    public void AnEdit_LeavesMetadataStoredInTheClear_InTheClear()
    {
        // With /EncryptMetadata false the XMP stream is plaintext in an encrypted file. Writing it
        // back encrypted would leave readers, which take it as plaintext, holding ciphertext.
        byte[] pdf = LegacyPdf.Build("AES-128", UserPassword, OwnerPassword, encryptMetadata: false);

        byte[] edited = PageTools.Rotate(pdf, new[] { 1 }, 90, UserPassword).Pdf;

        var doc = PdfDocument.Open(edited, UserPassword);
        var metadata = doc.Catalog!.GetAsStream(PdfName.Metadata)!;
        Assert.Contains("<x:xmpmeta", Encoding.ASCII.GetString(metadata.GetDecodedBytes()));
        Assert.Contains("<x:xmpmeta", Encoding.Latin1.GetString(edited));
        Assert.Contains("Secret words", LocationTextExtraction.ExtractPage(doc.GetPage(1)));
    }

    [Fact]
    public void NonStandardSecurityHandler_IsRefusedByName()
    {
        byte[] pdf = LegacyPdf.Build("RC4-128", UserPassword, OwnerPassword, filter: "Adobe.PubSec");

        var ex = Assert.ThrowsAny<FormatException>(() => PdfDocument.Open(pdf, UserPassword));
        Assert.Contains("Adobe.PubSec", ex.Message);
    }

    /// <summary>Builds and encrypts a one-page document, following ISO 32000-1 §7.6.3 directly.</summary>
    private static class LegacyPdf
    {
        private static readonly byte[] Padding =
        {
            0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
            0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
        };

        private const int Permissions = -3904 | 4; // reserved bits, plus "print"

        public static byte[] Build(string scheme, string userPassword, string ownerPassword,
            bool encryptMetadata = true, string filter = "Standard")
        {
            var (v, r, keyBytes, aes) = scheme switch
            {
                "RC4-40" => (1, 2, 5, false),
                "RC4-128" => (2, 3, 16, false),
                "AES-128" => (4, 4, 16, true),
                _ => throw new ArgumentOutOfRangeException(nameof(scheme)),
            };
            byte[] id = MD5.HashData(Encoding.ASCII.GetBytes("legacy-fixture-" + scheme));
            byte[] o = OwnerEntry(Pad(ownerPassword), Pad(userPassword), r, keyBytes);
            byte[] key = FileKey(Pad(userPassword), o, id, r, keyBytes, encryptMetadata || r < 4);
            byte[] u = UserEntry(key, id, r);

            string Str(int num, string text) => "<" + Convert.ToHexString(Encrypt(key, num, Encoding.ASCII.GetBytes(text), aes)) + ">";
            byte[] content = Encoding.ASCII.GetBytes("BT /F1 12 Tf 72 700 Td (Secret words) Tj ET");
            byte[] contentEnc = Encrypt(key, 4, content, aes);
            byte[] xmp = Encoding.ASCII.GetBytes("<?xpacket begin=''?><x:xmpmeta xmlns:x='adobe:ns:meta/'/><?xpacket end='w'?>");
            byte[] xmpStored = encryptMetadata ? Encrypt(key, 8, xmp, aes) : xmp;

            string cf = aes
                ? $" /CF << /StdCF << /CFM /AESV2 /Length 16 /AuthEvent /DocOpen >> >> /StmF /StdCF /StrF /StdCF{(encryptMetadata ? "" : " /EncryptMetadata false")}"
                : "";
            string length = r >= 3 ? $" /Length {keyBytes * 8}" : "";
            var objects = new List<byte[]>
            {
                Ascii("<< /Type /Catalog /Pages 2 0 R /Metadata 8 0 R >>"),
                Ascii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
                Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>"),
                Stream($"<< /Length {contentEnc.Length} >>", contentEnc),
                Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"),
                Ascii($"<< /Title {Str(6, "Quarterly figures")} >>"),
                Ascii($"<< /Filter /{filter} /V {v} /R {r}{length} /O <{Convert.ToHexString(o)}> /U <{Convert.ToHexString(u)}> /P {Permissions}{cf} >>"),
                Stream($"<< /Type /Metadata /Subtype /XML /Length {xmpStored.Length} >>", xmpStored),
            };

            var output = new MemoryStream();
            output.Write(Ascii("%PDF-1.6\n%\xE2\xE3\xCF\xD3\n"));
            var offsets = new List<long>();
            for (int i = 0; i < objects.Count; i++)
            {
                offsets.Add(output.Position);
                output.Write(Ascii($"{i + 1} 0 obj\n"));
                output.Write(objects[i]);
                output.Write(Ascii("\nendobj\n"));
            }
            long xref = output.Position;
            var table = new StringBuilder($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
            foreach (long offset in offsets) table.Append(offset.ToString("D10")).Append(" 00000 n \n");
            string hexId = Convert.ToHexString(id);
            table.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R /Info 6 0 R /Encrypt 7 0 R /ID [<{hexId}> <{hexId}>] >>\nstartxref\n{xref}\n%%EOF\n");
            output.Write(Ascii(table.ToString()));
            return output.ToArray();
        }

        private static byte[] Ascii(string s) => Encoding.Latin1.GetBytes(s);

        private static byte[] Stream(string dict, byte[] data) =>
            Ascii(dict + "\nstream\n").Concat(data).Concat(Ascii("\nendstream")).ToArray();

        private static byte[] Pad(string password)
        {
            byte[] pw = Encoding.ASCII.GetBytes(password);
            return pw.Take(32).Concat(Padding).Take(32).ToArray();
        }

        /// <summary>Algorithm 3: the /O entry.</summary>
        private static byte[] OwnerEntry(byte[] ownerPadded, byte[] userPadded, int r, int keyBytes)
        {
            byte[] hash = MD5.HashData(ownerPadded);
            if (r >= 3) for (int i = 0; i < 50; i++) hash = MD5.HashData(hash);
            byte[] key = hash[..keyBytes];
            byte[] result = Rc4(key, userPadded);
            if (r >= 3)
                for (int i = 1; i <= 19; i++)
                    result = Rc4(key.Select(b => (byte)(b ^ i)).ToArray(), result);
            return result;
        }

        /// <summary>Algorithm 2: the file encryption key.</summary>
        private static byte[] FileKey(byte[] userPadded, byte[] o, byte[] id, int r, int keyBytes, bool metadataEncrypted)
        {
            var input = new List<byte>(userPadded);
            input.AddRange(o);
            input.AddRange(BitConverter.GetBytes(Permissions));
            input.AddRange(id);
            if (r >= 4 && !metadataEncrypted) input.AddRange(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
            byte[] hash = MD5.HashData(input.ToArray());
            if (r >= 3) for (int i = 0; i < 50; i++) hash = MD5.HashData(hash[..keyBytes]);
            return hash[..keyBytes];
        }

        /// <summary>Algorithms 4 (R2) and 5 (R3+): the /U entry.</summary>
        private static byte[] UserEntry(byte[] key, byte[] id, int r)
        {
            if (r == 2) return Rc4(key, Padding);
            byte[] result = Rc4(key, MD5.HashData(Padding.Concat(id).ToArray()));
            for (int i = 1; i <= 19; i++) result = Rc4(key.Select(b => (byte)(b ^ i)).ToArray(), result);
            return result.Concat(new byte[16]).ToArray();
        }

        /// <summary>Algorithm 1: encrypts one string or stream of object <paramref name="number"/> (generation 0).</summary>
        private static byte[] Encrypt(byte[] fileKey, int number, byte[] data, bool aes)
        {
            var input = new List<byte>(fileKey) { (byte)number, (byte)(number >> 8), (byte)(number >> 16), 0, 0 };
            if (aes) input.AddRange(Encoding.ASCII.GetBytes("sAlT"));
            byte[] objectKey = MD5.HashData(input.ToArray())[..Math.Min(fileKey.Length + 5, 16)];
            if (!aes) return Rc4(objectKey, data);

            using var cipher = Aes.Create();
            cipher.Key = objectKey;
            byte[] iv = MD5.HashData(BitConverter.GetBytes(number)); // fixed, so fixtures are reproducible
            return iv.Concat(cipher.EncryptCbc(data, iv, PaddingMode.PKCS7)).ToArray();
        }

        private static byte[] Rc4(byte[] key, byte[] data)
        {
            var s = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
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
    }
}
