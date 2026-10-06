using System.Diagnostics;
using IOPath = System.IO.Path;

namespace PdfEditor.Core;

/// <summary>
/// Optical character recognition for scanned (image-only) PDFs, via a local Tesseract install.
/// Each page is rendered to an image and run through Tesseract to either extract plain text or
/// produce a searchable PDF (the original page image with an invisible, selectable text layer).
/// Like the Word-import path this needs an external tool; when Tesseract is absent the caller gets
/// a clear error rather than a crash.
/// </summary>
public static class OcrTool
{
    /// <summary>Whether OCR is possible here (i.e. the Tesseract binary is installed).</summary>
    public static bool CanOcr => FindTesseract() != null;

    /// <summary>Runs OCR over one page and returns the recognised plain text.</summary>
    public static string ExtractText(byte[] pdf, int page, string? password = null, int dpi = 300)
    {
        string tesseract = RequireTesseract();
        int pageCount = PdfInspector.GetInfo(pdf, password).PageCount;
        if (page < 1 || page > pageCount)
            throw new ArgumentOutOfRangeException(nameof(page), $"Page {page} does not exist.");

        string work = Directory.CreateTempSubdirectory("pdfeditor-ocr").FullName;
        try
        {
            string img = IOPath.Combine(work, "page.png");
            File.WriteAllBytes(img, PageRenderer.RenderPagePng(pdf, page, dpi, password));
            // "stdout" tells Tesseract to write the recognised text to standard output.
            var (stdout, _, ok) = Run(tesseract, work, img, "stdout", "-c", UserDefinedDpi(dpi));
            if (!ok) throw new InvalidOperationException("Tesseract could not read the page.");
            return stdout.Trim();
        }
        finally { TryDelete(work); }
    }

    /// <summary>
    /// Produces a searchable copy of the document: every page is OCR'd and rebuilt as the page
    /// image with an invisible text layer, so the result can be selected, copied, and searched.
    /// </summary>
    public static byte[] MakeSearchable(byte[] pdf, string? password = null, int dpi = 300)
    {
        string tesseract = RequireTesseract();
        int pageCount = PdfInspector.GetInfo(pdf, password).PageCount;

        string work = Directory.CreateTempSubdirectory("pdfeditor-ocr").FullName;
        try
        {
            var pages = new List<byte[]>(pageCount);
            for (int p = 1; p <= pageCount; p++)
            {
                string img = IOPath.Combine(work, $"page{p}.png");
                File.WriteAllBytes(img, PageRenderer.RenderPagePng(pdf, p, dpi, password));

                string outBase = IOPath.Combine(work, $"page{p}-ocr");
                var (_, stderr, ok) = Run(tesseract, work, img, outBase, "-c", UserDefinedDpi(dpi), "pdf");
                string outPdf = outBase + ".pdf";
                if (!ok || !File.Exists(outPdf))
                    throw new InvalidOperationException(
                        "Tesseract could not OCR the document." +
                        (string.IsNullOrWhiteSpace(stderr) ? "" : $" {stderr.Trim()}"));
                pages.Add(File.ReadAllBytes(outPdf));
            }
            // The searchable copy is built from page images, so it starts unencrypted; it is still
            // the same document, and gets the source's encryption back.
            return Encryptor.EncryptLike(Merger.Merge(pages), pdf, password);
        }
        finally { TryDelete(work); }
    }

    /// <summary>
    /// Tells Tesseract the resolution the page image was rendered at. PNGs written by Skia carry
    /// no pHYs resolution chunk, so without this Tesseract falls back to its 70 dpi guess: it then
    /// lays the searchable PDF out at pixels/70*72 points, i.e. 300/70 ≈ 4.3x the real page. That
    /// single wrong number caused both reported faults — the viewer sizes each page from its media
    /// box, so the OCR'd copy appeared hugely zoomed in (#21), and re-rendering a page that big
    /// (the viewer asks for up to 300 dpi, and a second OCR pass squares the error) needs a bitmap
    /// too large to allocate, leaving the scan blank/unviewable (#20). Passing the true DPI makes
    /// the output page match the input page exactly.
    /// </summary>
    private static string UserDefinedDpi(int dpi) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"user_defined_dpi={dpi}");

    private static string RequireTesseract() =>
        FindTesseract() ?? throw new InvalidOperationException(
            "OCR needs Tesseract installed (the 'tesseract' command). Install Tesseract OCR and a " +
            "language pack, then try again — on Windows: " +
            "winget install -e --id tesseract-ocr.tesseract");

    private static (string StdOut, string StdErr, bool Ok) Run(string exe, string work, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = work,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start Tesseract.");
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        if (!proc.WaitForExit(180_000))
        {
            try { proc.Kill(true); } catch { /* best effort */ }
            throw new InvalidOperationException("Tesseract timed out.");
        }
        return (stdout, stderr, proc.ExitCode == 0);
    }

    private static string? FindTesseract()
    {
        var candidates = new List<string>
        {
            "tesseract",
            "/usr/bin/tesseract", "/usr/local/bin/tesseract", "/opt/homebrew/bin/tesseract",
            @"C:\Program Files\Tesseract-OCR\tesseract.exe",
            @"C:\Program Files (x86)\Tesseract-OCR\tesseract.exe",
        };
        foreach (var candidate in candidates)
        {
            if (IOPath.IsPathRooted(candidate))
            {
                if (File.Exists(candidate)) return candidate;
                continue;
            }
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(IOPath.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var ext in OperatingSystem.IsWindows() ? new[] { ".exe", ".com", "" } : new[] { "" })
                {
                    string full = IOPath.Combine(dir.Trim(), candidate + ext);
                    if (File.Exists(full)) return full;
                }
            }
        }
        return null;
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* temp cleanup best effort */ }
    }
}
