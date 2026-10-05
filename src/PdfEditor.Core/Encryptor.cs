using PdfEditor.Core.Pdf;

namespace PdfEditor.Core;

/// <summary>Password protection using AES-256 encryption.</summary>
public static class Encryptor
{
    public static byte[] Encrypt(byte[] pdf, string userPassword, string? ownerPassword = null,
        string? currentPassword = null)
    {
        if (string.IsNullOrEmpty(userPassword))
            throw new ArgumentException("A user password is required.", nameof(userPassword));
        ownerPassword ??= userPassword;

        var doc = PdfIo.Open(pdf, currentPassword);
        return PdfIo.Save(doc, new PdfSaveOptions { Encryption = (userPassword, ownerPassword) });
    }

    /// <summary>Removes password protection (requires the current password).</summary>
    public static byte[] Decrypt(byte[] pdf, string password) => PdfIo.Save(PdfIo.Open(pdf, password));

    public static bool IsEncrypted(byte[] pdf)
    {
        try
        {
            PdfIo.Open(pdf);
            return false;
        }
        catch (PdfPasswordException)
        {
            return true;
        }
    }

    /// <summary>True when the supplied password opens the document.</summary>
    public static bool CanOpen(byte[] pdf, string? password)
    {
        try
        {
            PdfIo.OpenReadOnly(pdf, password);
            return true;
        }
        catch (PdfPasswordException)
        {
            return false;
        }
    }
}
