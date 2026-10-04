using PdfEditor.Core.Pdf;

namespace PdfEditor.Core;

/// <summary>Concatenates multiple PDF documents into one.</summary>
public static class Merger
{
    public static byte[] Merge(IReadOnlyList<byte[]> pdfs, IReadOnlyList<string?>? passwords = null)
    {
        if (pdfs.Count == 0) throw new ArgumentException("At least one document is required.", nameof(pdfs));
        if (pdfs.Count == 1) return pdfs[0];

        var target = PdfDocument.CreateNew();
        var pages = new List<PdfDictionary>();
        for (int i = 0; i < pdfs.Count; i++)
        {
            string? password = passwords != null && i < passwords.Count ? passwords[i] : null;
            var source = PdfIo.OpenReadOnly(pdfs[i], password);
            var numbers = Enumerable.Range(1, source.PageCount).ToList();
            pages.AddRange(PdfIo.Guarded("copying pages", () => PdfImporter.CopyPages(source, numbers, target)));
        }
        target.SetPages(pages);
        return PdfIo.Save(target);
    }
}
