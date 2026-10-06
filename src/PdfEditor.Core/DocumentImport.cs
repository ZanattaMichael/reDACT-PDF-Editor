using System.Diagnostics;
using PdfEditor.Core.Pdf;
using IOPath = System.IO.Path;

namespace PdfEditor.Core;

/// <summary>
/// Converts non-PDF inputs to single- or multi-page PDFs so they can be merged: raster images
/// (each image, and each page of a multi-page TIFF, becomes an A4 page with the image fitted to
/// it) and Word documents (via LibreOffice, when available).
/// </summary>
public static class DocumentImport
{
    /// <summary>Whether Word conversion is possible here (i.e. LibreOffice is installed).</summary>
    public static bool CanConvertWord => FindSoffice() != null;

    /// <summary>Converts <paramref name="data"/> of the given kind (pdf/image/docx) to PDF bytes.</summary>
    public static byte[] ToPdf(byte[] data, string kind) => kind switch
    {
        "image" => ImageToPdf(data),
        "docx" or "word" => DocxToPdf(data),
        _ => data, // already a PDF
    };

    /// <summary>
    /// Wraps a raster image (PNG/JPEG/…) in a PDF: one page per image, so a multi-page TIFF (a
    /// scanned document, a fax) becomes a page for each of its pages, in order. Each page is a
    /// standard A4 sheet, in its image's orientation, with the image scaled to fit inside a small
    /// margin (aspect preserved, centred) — so a merged photo is a normal document page, not a
    /// page as large as the image's pixel count.
    /// </summary>
    public static byte[] ImageToPdf(byte[] image)
    {
        var pdf = PdfDocument.CreateNew();
        foreach (var (xobject, pixelWidth, pixelHeight) in PdfImages.CreateXObjects(image))
        {
            if (pixelWidth <= 0 || pixelHeight <= 0)
                throw new ArgumentException("The image has no usable dimensions.", nameof(image));
            AddFittedPage(pdf, xobject, pixelWidth, pixelHeight);
        }
        return PdfIo.Save(pdf);
    }

    /// <summary>Adds an A4 page in the image's orientation, with the image fitted and centred on it.</summary>
    private static void AddFittedPage(PdfDocument pdf, PdfStream xobject, float iw, float ih)
    {
        const float a4Short = 595f, a4Long = 842f, margin = 18f; // ~0.25 inch margin
        bool landscape = iw > ih;
        float pw = landscape ? a4Long : a4Short;
        float ph = landscape ? a4Short : a4Long;

        float scale = Math.Min((pw - 2 * margin) / iw, (ph - 2 * margin) / ih);
        float w = iw * scale, h = ih * scale;
        float x = (pw - w) / 2f, y = (ph - h) / 2f;

        var page = pdf.AddNewPage(pw, ph);
        var name = PdfResources.Add(page.GetOrCreateResources(), PdfName.XObject, "Im", xobject);
        page.SetContent(new ContentBuilder().SaveState().Transform(w, 0, 0, h, x, y).DrawXObject(name).RestoreState().ToArray());
    }

    /// <summary>
    /// Converts a Word document to PDF using a local LibreOffice install (headless). Throws a clear
    /// error if LibreOffice isn't available — there is no reliable in-process Word renderer.
    /// </summary>
    public static byte[] DocxToPdf(byte[] docx)
    {
        string? soffice = FindSoffice();
        if (soffice == null)
            throw new InvalidOperationException(
                "Merging Word documents needs LibreOffice installed (the 'soffice' command). " +
                "Install LibreOffice, or export the document to PDF first and merge that.");

        string work = Directory.CreateTempSubdirectory("pdfeditor-docx").FullName;
        try
        {
            string input = IOPath.Combine(work, "input.docx");
            File.WriteAllBytes(input, docx);

            var psi = new ProcessStartInfo(soffice)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // A private profile dir keeps concurrent / first-run invocations from clashing.
            foreach (var arg in new[]
            {
                $"-env:UserInstallation=file://{IOPath.Combine(work, "profile")}",
                "--headless", "--norestore", "--convert-to", "pdf", "--outdir", work, input,
            })
                psi.ArgumentList.Add(arg);

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Could not start LibreOffice.");
            string stderr = proc.StandardError.ReadToEnd();
            proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(120_000))
            {
                try { proc.Kill(true); } catch { /* best effort */ }
                throw new InvalidOperationException("LibreOffice timed out converting the document.");
            }

            string outPdf = IOPath.Combine(work, "input.pdf");
            if (!File.Exists(outPdf))
                throw new InvalidOperationException(
                    "LibreOffice could not convert the document." +
                    (string.IsNullOrWhiteSpace(stderr) ? "" : $" {stderr.Trim()}"));
            return File.ReadAllBytes(outPdf);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* temp cleanup best effort */ }
        }
    }

    private static string? FindSoffice()
    {
        var candidates = new List<string> { "soffice", "libreoffice" };
        // Common absolute locations on the three desktop platforms.
        candidates.AddRange(new[]
        {
            "/usr/bin/soffice", "/usr/bin/libreoffice", "/opt/libreoffice/program/soffice",
            "/Applications/LibreOffice.app/Contents/MacOS/soffice",
            @"C:\Program Files\LibreOffice\program\soffice.exe",
            @"C:\Program Files (x86)\LibreOffice\program\soffice.exe",
        });

        foreach (var candidate in candidates)
        {
            if (IOPath.IsPathRooted(candidate))
            {
                if (File.Exists(candidate)) return candidate;
                continue;
            }
            // Bare name: search PATH.
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
}
