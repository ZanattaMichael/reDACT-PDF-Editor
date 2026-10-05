using PdfEditor.Core.Pdf;

namespace PdfEditor.Core;

/// <summary>Shared open/save helpers.</summary>
internal static class PdfIo
{
    /// <summary>Opens a document for reading or editing; failures name the step that failed.</summary>
    public static PdfDocument Open(byte[] pdf, string? password = null) =>
        Guarded("opening the document", () => PdfDocument.Open(pdf, string.IsNullOrEmpty(password) ? null : password));

    /// <summary>Opens a document that will only be read. (The engine has one mode; this names the intent.)</summary>
    public static PdfDocument OpenReadOnly(byte[] pdf, string? password = null) => Open(pdf, password);

    /// <summary>Writes the whole document afresh (unencrypted unless <paramref name="options"/> say otherwise).</summary>
    public static byte[] Save(PdfDocument doc, PdfSaveOptions? options = null) =>
        Guarded("saving the document", () => doc.Save(options));

    /// <summary>
    /// Runs a step that feeds raw document bytes through the PDF engine and converts any failure
    /// caused by the <em>document</em> into a typed, message-bearing one.
    /// <para>
    /// The engine reports malformed input as <see cref="PdfFormatException"/>; this wraps it in an
    /// <see cref="InvalidDataException"/> that says which step failed, keeping the original as the
    /// inner exception. Defect-class exceptions (a null dereference, an index or cast that went
    /// wrong) are translated the same way, because escaping as "Object reference not set to an
    /// instance of an object" tells a caller nothing and is indistinguishable from a genuine bug in
    /// this engine — the original stays in the stack trace for anyone diagnosing one.
    /// </para>
    /// </summary>
    public static void Guarded(string what, Action step) => Guarded(what, () =>
    {
        step();
        return true;
    });

    /// <inheritdoc cref="Guarded(string, Action)"/>
    public static T Guarded<T>(string what, Func<T> step)
    {
        try
        {
            return step();
        }
        catch (PdfFormatException ex)
        {
            throw new InvalidDataException(
                $"This PDF could not be read: {what} failed because the document is malformed or corrupt ({ex.Message})", ex);
        }
        catch (Exception ex) when (ex is NullReferenceException or IndexOutOfRangeException
                                       or InvalidCastException or KeyNotFoundException)
        {
            throw new InvalidDataException(
                $"This PDF could not be read: {what} failed because the document is malformed or corrupt ({ex.GetType().Name}).", ex);
        }
    }
}
