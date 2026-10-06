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
    public static byte[] Decrypt(byte[] pdf, string password) =>
        PdfIo.Save(PdfIo.Open(pdf, password), new PdfSaveOptions { RemoveEncryption = true });

    /// <summary>
    /// <paramref name="pdf"/>, a document built from <paramref name="source"/> (an OCR'd copy of
    /// it, or a merge into it), encrypted exactly as the source is: same scheme, key, passwords and
    /// permissions. Edits keep a document's encryption on their own; this is for the operations that
    /// build a new document instead of editing the old one. Returns <paramref name="pdf"/> unchanged
    /// when the source is not encrypted.
    /// </summary>
    /// <param name="sourcePassword">The password that opens the source (and <paramref name="pdf"/>, if it is the source).</param>
    public static byte[] EncryptLike(byte[] pdf, byte[] source, string? sourcePassword)
    {
        var original = PdfIo.OpenReadOnly(source, sourcePassword);
        if (!original.WasEncrypted) return pdf;
        var doc = PdfIo.Open(pdf, sourcePassword);
        doc.AdoptEncryption(original);
        return PdfIo.Save(doc);
    }

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
