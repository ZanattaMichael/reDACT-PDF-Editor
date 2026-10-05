using PdfEditor.Core.Pdf;

namespace PdfEditor.Core;

/// <summary>
/// Structural pre-checks that run before a page's content is interpreted, for the failures that
/// must be refused up front rather than handled half-way through an edit.
/// </summary>
internal static class PdfStructureGuard
{
    /// <summary>Deepest legitimate form-XObject nesting. Real documents use a handful of levels.</summary>
    private const int MaxFormNesting = 32;

    /// <summary>Upper bound on nodes inspected, so a wide (rather than deep) graph cannot stall the walk.</summary>
    private const int MaxFormsInspected = 4096;

    /// <summary>
    /// Rejects a page whose form-XObject graph does not terminate.
    /// <para>
    /// A form XObject that lists itself in its own <c>/Resources /XObject</c> draws itself forever.
    /// The content interpreter stops at a cycle, but a document like that is either broken or
    /// hostile, and an edit that silently skipped the recursion would rewrite the page from a
    /// partial reading of it. (The engine this project used before had no cycle check at all and
    /// overflowed the stack — process-fatal on .NET — so this guard also pins a fixed bug.)
    /// </para>
    /// </summary>
    public static void EnsureFormXObjectsTerminate(PdfPage page)
    {
        var onPath = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
        int budget = MaxFormsInspected;
        Walk(page.Resources?.GetAsDictionary(PdfName.XObject), onPath, 0, ref budget);
    }

    private static void Walk(PdfDictionary? xobjects, HashSet<PdfObject> onPath, int depth, ref int budget)
    {
        if (xobjects == null || budget <= 0) return;
        if (depth > MaxFormNesting)
            throw new InvalidDataException(
                $"This PDF could not be read: its form XObjects nest more than {MaxFormNesting} " +
                "levels deep, which no legitimate document does.");

        foreach (var key in xobjects.Keys.ToList())
        {
            if (--budget <= 0) return;
            var form = xobjects.GetAsStream(key);
            if (form == null || !PdfName.Form.Equals(form.GetAsName(PdfName.Subtype))) continue;

            if (!onPath.Add(form))
                throw new InvalidDataException(
                    "This PDF could not be read: a form XObject draws itself, so its content never " +
                    "terminates. The document is malformed or deliberately hostile.");
            Walk(form.GetAsDictionary(PdfName.Resources)?.GetAsDictionary(PdfName.XObject),
                onPath, depth + 1, ref budget);
            onPath.Remove(form);
        }
    }
}
