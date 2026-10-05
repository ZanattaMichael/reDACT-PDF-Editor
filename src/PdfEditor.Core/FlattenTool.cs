using PdfEditor.Core.Pdf;

namespace PdfEditor.Core;

/// <summary>
/// Flattens interactive layers into static page content, with a choice of how much to flatten (#44):
/// form fields only, non-form annotations (comments/markup/stamps) only, or everything. Flattening
/// bakes the visible appearance into the page so it prints and renders identically everywhere and can
/// no longer be edited or removed as an object.
/// </summary>
public static class FlattenTool
{
    /// <summary>What to flatten.</summary>
    public enum Mode
    {
        /// <summary>AcroForm fields → their appearances; the fields stop being interactive.</summary>
        Forms,
        /// <summary>Markup/comment annotations (highlight, ink, stamp, note…) → page content.</summary>
        AnnotationsOnly,
        /// <summary>Both: forms and annotations, leaving a fully static page.</summary>
        Everything,
    }

    /// <summary>Subtypes that carry no bakeable page appearance and are left alone.</summary>
    private static readonly HashSet<PdfName> Skip = new() { PdfName.Link, PdfName.Popup };

    /// <summary>Flattens per <paramref name="mode"/>; returns the counts flattened.</summary>
    public static FlattenResult Flatten(byte[] pdf, Mode mode, string? password = null)
    {
        int forms = 0, annotations = 0;
        var doc = PdfIo.Open(pdf, password);
        if (mode is Mode.Forms or Mode.Everything)
            forms = AcroForm.Flatten(doc); // draws each field's appearance, drops the widgets

        if (mode is Mode.AnnotationsOnly or Mode.Everything)
            annotations = FlattenAnnotations(doc);
        return new FlattenResult(PdfIo.Save(doc), forms, annotations);
    }

    /// <summary>
    /// Draws each non-form annotation's normal appearance onto its page and removes the annotation.
    /// Annotations with no bakeable appearance stream (or that are links/popups, or widgets — those
    /// are the form path) are left untouched, so nothing visible is silently dropped.
    /// </summary>
    private static int FlattenAnnotations(PdfDocument doc)
    {
        int count = 0;
        foreach (var page in doc.Pages)
        {
            // Snapshot: removing an annotation mutates the page's annotation list.
            var annotations = page.GetAnnotations().ToArray();
            ContentBuilder? canvas = null;
            foreach (var annot in annotations)
            {
                var subtype = annot.GetAsName(PdfName.Subtype);
                if (subtype == null || subtype.Equals(PdfName.Widget) || Skip.Contains(subtype)) continue;

                var normal = AcroForm.NormalAppearance(annot);
                var rect = PdfRect.FromArray(annot.GetAsArray(PdfName.Rect));
                if (normal == null || rect == null) continue; // nothing to bake at a known place

                // Only open a drawing when there is something to draw.
                canvas ??= new ContentBuilder();
                AcroForm.DrawFitted(canvas, page, normal, rect.Value);
                page.RemoveAnnotation(annot);
                count++;
            }
            if (canvas != null) PdfContentGuard.DrawInDefaultUserSpace(page, canvas.ToArray());
        }
        return count;
    }
}

/// <summary>Result of a flatten run: the edited PDF and how many fields/annotations were baked in.</summary>
public sealed record FlattenResult(byte[] Pdf, int FormFieldsFlattened, int AnnotationsFlattened);
