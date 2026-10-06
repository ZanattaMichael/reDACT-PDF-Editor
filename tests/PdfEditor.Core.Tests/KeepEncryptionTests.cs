using PdfEditor.Core;
using PdfEditor.Core.Pdf;
using Xunit;

namespace PdfEditor.Tests;

/// <summary>
/// Editing a password-protected document keeps it protected. Every edit used to write the
/// document out unencrypted, while the viewer still showed it as encrypted, so Save quietly
/// produced a plaintext copy. Now each edit writes the document in its own scheme under its own
/// key: the encryption dictionary and the first file identifier come through byte for byte, so the
/// same passwords open it and the same permissions apply.
/// </summary>
public class KeepEncryptionTests
{
    private const string User = "user-pw";
    private const string Owner = "owner-pw";

    // A minimal valid 1x1 PNG, for the placed signature.
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private static byte[] Protected() =>
        Encryptor.Encrypt(TestPdfs.WithText(("Original words", 72, 700, 14), ("Second line", 72, 600, 14)), User, Owner);

    public static TheoryData<string> Edits => new()
    {
        "add text", "replace all", "redact", "highlight", "ink", "watermark", "bates", "rotate",
        "arrange", "add field", "flatten", "add script", "strip active content", "sanitize", "signature image",
    };

    private static byte[] Apply(string edit, byte[] pdf) => edit switch
    {
        "add text" => TextTools.AddText(pdf, new RectRegion(1, 72, 400, 200, 20), "Added words", 12, password: User).Pdf,
        "replace all" => TextTools.ReplaceAll(pdf, "Original", "Edited", User).Result.Pdf,
        "redact" => Redactor.Redact(pdf, new[] { new RectRegion(1, 300, 300, 60, 20) }, User).Pdf,
        "highlight" => HighlightTool.AddHighlight(pdf, 1, new[] { new RectRegion(1, 72, 695, 100, 16) }, password: User).Pdf,
        "ink" => InkTools.AddInk(pdf, 1, new[] { new[] { (100f, 300f), (200f, 320f) } }, password: User).Pdf,
        "watermark" => WatermarkTool.AddTextWatermark(pdf, "DRAFT", password: User).Pdf,
        "bates" => BatesTool.AddBatesNumbers(pdf, new BatesOptions(Prefix: "ACME"), User).Pdf,
        "rotate" => PageTools.Rotate(pdf, new[] { 1 }, 90, User).Pdf,
        "arrange" => PageTools.Arrange(pdf, new[] { 1, 1 }, User).Pdf,
        "add field" => FormTools.AddTextField(pdf, 1, new RectRegion(1, 72, 200, 200, 24), "name", password: User).Pdf,
        "flatten" => FlattenTool.Flatten(pdf, FlattenTool.Mode.Everything, User).Pdf,
        "add script" => JavaScriptTool.AddDocumentScript(pdf, "greet", "app.alert('hi');", User).Pdf,
        "strip active content" => PdfSafety.StripActive(pdf, User).Pdf,
        "sanitize" => Sanitizer.Sanitize(pdf, new SanitizeOptions(), User).Pdf,
        "signature image" => Signer.AddImageSignature(pdf, new RectRegion(1, 300, 100, 120, 40), OnePixelPng, User),
        _ => throw new ArgumentOutOfRangeException(nameof(edit)),
    };

    /// <summary>The encryption entries that fix the key, passwords and permissions, as hex.</summary>
    private static string[] Protection(byte[] pdf)
    {
        var doc = PdfDocument.Open(pdf, Owner);
        var encrypt = doc.Trailer.GetAsDictionary(PdfName.Encrypt)!;
        string Hex(string key) => Convert.ToHexString(encrypt.GetAsString(PdfName.Of(key))?.Bytes ?? Array.Empty<byte>());
        return new[]
        {
            Hex("O"), Hex("U"), Hex("OE"), Hex("UE"), Hex("Perms"),
            encrypt.GetAsNumber(PdfName.P)!.LongValue().ToString(System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToHexString(doc.Trailer.GetAsArray(PdfName.ID)!.GetAsString(0)!.Bytes),
        };
    }

    [Theory]
    [MemberData(nameof(Edits))]
    public void AnEdit_KeepsTheDocumentsOwnEncryption(string edit)
    {
        byte[] original = Protected();

        byte[] edited = Apply(edit, original);

        Assert.True(Encryptor.IsEncrypted(edited));
        Assert.Equal(Protection(original), Protection(edited));
        Assert.True(Encryptor.CanOpen(edited, User));
        Assert.True(Encryptor.CanOpen(edited, Owner));
        Assert.False(Encryptor.CanOpen(edited, "wrong"));
        // The document is the edited one, not the original kept as it was.
        Assert.NotEqual(original, edited);
        Assert.Contains("Second line", TestPdfAssert.ExtractText(edited, 1, User));
    }

    [Fact]
    public void AnEditsContent_IsReadableWithThePassword_AndNotWithout()
    {
        byte[] edited = TextTools.AddText(Protected(), new RectRegion(1, 72, 400, 200, 20), "Added words", 12, password: User).Pdf;

        Assert.Contains("Added words", TestPdfAssert.ExtractText(edited, 1, User));
        Assert.Throws<PdfPasswordException>(() => TestPdfAssert.ExtractText(edited));
    }

    [Fact]
    public void RemovingEncryption_StillWritesItUnencrypted()
    {
        byte[] edited = PageTools.Rotate(Protected(), new[] { 1 }, 90, User).Pdf;

        byte[] open = Encryptor.Decrypt(edited, User);

        Assert.False(Encryptor.IsEncrypted(open));
        Assert.Contains("Original words", TestPdfAssert.ExtractText(open));
    }

    [Fact]
    public void ProtectingAgain_ReplacesTheEncryption()
    {
        byte[] reprotected = Encryptor.Encrypt(Protected(), "new-pw", currentPassword: User);

        Assert.True(Encryptor.CanOpen(reprotected, "new-pw"));
        Assert.False(Encryptor.CanOpen(reprotected, User));
        Assert.False(Encryptor.CanOpen(reprotected, Owner));
    }

    [Fact]
    public void EncryptLike_GivesARebuiltDocumentTheSourcesEncryption()
    {
        byte[] source = Protected();
        byte[] rebuilt = TestPdfs.WithText(("Rebuilt copy", 72, 700, 14));

        byte[] result = Encryptor.EncryptLike(rebuilt, source, User);

        Assert.Equal(Protection(source), Protection(result));
        Assert.Contains("Rebuilt copy", TestPdfAssert.ExtractText(result, 1, Owner));
        Assert.False(Encryptor.CanOpen(result, null));
    }

    [Fact]
    public void EncryptLike_LeavesTheDocumentAlone_WhenTheSourceIsNotEncrypted()
    {
        byte[] rebuilt = TestPdfs.WithText(("Rebuilt copy", 72, 700, 14));

        Assert.Same(rebuilt, Encryptor.EncryptLike(rebuilt, TestPdfs.WithText(("open", 72, 700, 14)), null));
    }

    [Fact]
    public void EncryptLike_AcceptsTheSourceItself()
    {
        // A merge of the protected document with nothing else returns the document as it was.
        byte[] source = Protected();

        byte[] result = Encryptor.EncryptLike(source, source, User);

        Assert.Equal(Protection(source), Protection(result));
        Assert.Contains("Original words", TestPdfAssert.ExtractText(result, 1, User));
    }
}
