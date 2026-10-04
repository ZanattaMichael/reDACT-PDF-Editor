using PdfEditor.Core.Pdf;
using PdfEditor.Core;
using Xunit;

namespace PdfEditor.Tests;

public class EncryptorTests
{
    [Fact]
    public void Encrypt_ProducesPasswordProtectedDocument()
    {
        byte[] pdf = TestPdfs.WithText(("private", 72, 700, 12));

        byte[] encrypted = Encryptor.Encrypt(pdf, "user-pw", "owner-pw");

        Assert.True(Encryptor.IsEncrypted(encrypted));
        Assert.True(Encryptor.CanOpen(encrypted, "user-pw"));
        Assert.True(Encryptor.CanOpen(encrypted, "owner-pw"));
        Assert.False(Encryptor.CanOpen(encrypted, "wrong"));
        Assert.False(Encryptor.CanOpen(encrypted, null));
    }

    [Fact]
    public void EncryptedContent_IsStillReadableWithPassword()
    {
        byte[] pdf = TestPdfs.WithText(("private payload", 72, 700, 12));
        byte[] encrypted = Encryptor.Encrypt(pdf, "pw");

        Assert.Contains("private payload", TestPdfAssert.ExtractText(encrypted, 1, "pw"));
        Assert.Throws<PdfPasswordException>(() => TestPdfAssert.ExtractText(encrypted));
    }

    [Fact]
    public void Decrypt_RemovesProtection()
    {
        byte[] pdf = TestPdfs.WithText(("private", 72, 700, 12));
        byte[] encrypted = Encryptor.Encrypt(pdf, "pw");

        byte[] decrypted = Encryptor.Decrypt(encrypted, "pw");

        Assert.False(Encryptor.IsEncrypted(decrypted));
        Assert.Contains("private", TestPdfAssert.ExtractText(decrypted));
    }

    [Fact]
    public void Encrypt_WithoutUserPassword_Throws()
    {
        byte[] pdf = TestPdfs.WithText(("x", 72, 700, 12));
        Assert.Throws<ArgumentException>(() => Encryptor.Encrypt(pdf, ""));
    }

    [Fact]
    public void ReEncrypt_WithDifferentPassword()
    {
        byte[] pdf = TestPdfs.WithText(("x", 72, 700, 12));
        byte[] first = Encryptor.Encrypt(pdf, "one");

        byte[] second = Encryptor.Encrypt(first, "two", currentPassword: "one");

        Assert.True(Encryptor.CanOpen(second, "two"));
        Assert.False(Encryptor.CanOpen(second, "one"));
    }
}
