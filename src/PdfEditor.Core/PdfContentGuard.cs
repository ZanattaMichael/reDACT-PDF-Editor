using PdfEditor.Core.Pdf;

namespace PdfEditor.Core;

/// <summary>
/// Helpers for adding content to an existing page without inheriting whatever graphics state that
/// page's content leaves behind.
/// </summary>
internal static class PdfContentGuard
{
    /// <summary>
    /// Appends <paramref name="drawing"/> to the page so that it draws in the page's
    /// <em>default</em> (identity) user space, isolated from any leftover CTM or clip path the
    /// existing page content left in effect.
    /// <para>
    /// Content appended to a page runs under whatever transform is still active at the end of the
    /// existing stream. Real-world generators — notably Chrome / Skia print-to-PDF, which is what
    /// "Download as PDF" from Google Docs produces — apply a top-level scale + Y-flip matrix (e.g.
    /// <c>.24 0 0 -.24 0 792 cm</c>) that is never wrapped in a <c>q</c>/<c>Q</c> pair, so it is
    /// still active at the stream's end. Anything drawn there is scaled and flipped and lands in
    /// the wrong place.
    /// </para>
    /// <para>
    /// So the existing content is bracketed with a balanced <c>q</c> … <c>Q</c> (two new streams,
    /// one before and one after it), and the new drawing goes in a third stream after that, where
    /// the graphics state is back to the page's default.
    /// </para>
    /// </summary>
    internal static void DrawInDefaultUserSpace(PdfPage page, byte[] drawing)
    {
        // 'q' before all existing content saves the clean default (identity) graphics state.
        page.PrependContent("q\n"u8.ToArray());
        // 'Q' after it pops back to that clean state, discarding the content's leftover CTM/clip.
        page.AppendContent("Q\n"u8.ToArray());
        // The new drawing, in its own stream so its own q/Q stay self-balanced.
        page.AppendContent(drawing);
    }
}
