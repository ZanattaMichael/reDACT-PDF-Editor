using PdfEditor.Core.Pdf;

namespace PdfEditor.Core;

/// <summary>Freehand drawing: stamps user-drawn strokes onto a page as vector paths.</summary>
public static class InkTools
{
    /// <summary>
    /// Draws freehand strokes on <paramref name="page"/> (1-based). Each stroke is a polyline of
    /// points in PDF user space; a single-point stroke becomes a dot. Strokes are painted in the
    /// page's default user space (via <see cref="PdfContentGuard"/>) so a leftover page transform
    /// can't displace them, with round caps/joins in the given colour and line width.
    /// </summary>
    public static EditResult AddInk(byte[] pdf, int page,
        IReadOnlyList<IReadOnlyList<(float X, float Y)>> strokes,
        string? colorHex = null, float width = 2f, string? password = null)
    {
        if (strokes.Count == 0) return EditResult.Of(pdf);
        float lineWidth = width <= 0 ? 2f : Math.Min(width, 100f);
        var color = TextTools.ParseColor(colorHex) ?? PdfColor.Black;

        var doc = PdfIo.Open(pdf, password);
        if (page < 1 || page > doc.PageCount)
            throw new ArgumentOutOfRangeException(nameof(page), $"Page {page} does not exist.");

        var canvas = new ContentBuilder();
        canvas.StrokeColor(color).FillColor(color)
              .LineWidth(lineWidth).LineCap(1).LineJoin(1); // round caps and joins

        foreach (var stroke in strokes)
        {
            if (stroke.Count == 0) continue;
            if (stroke.Count == 1)
            {
                // A tap with no movement: draw a filled dot the width of the pen.
                var (x, y) = stroke[0];
                canvas.Circle(x, y, lineWidth / 2f).Fill();
                continue;
            }
            canvas.MoveTo(stroke[0].X, stroke[0].Y);
            for (int i = 1; i < stroke.Count; i++)
                canvas.LineTo(stroke[i].X, stroke[i].Y);
            canvas.Stroke();
        }
        PdfContentGuard.DrawInDefaultUserSpace(doc.GetPage(page), canvas.ToArray());
        return EditResult.Of(PdfIo.Save(doc));
    }
}
