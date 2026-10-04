using System.Globalization;
using System.Text;
using PdfEditor.Core.Pdf;
using PdfEditor.Core.Pdf.Fonts;
using SkiaSharp;

namespace PdfEditor.Tests;

/// <summary>A page under construction, for building test fixtures with the engine.</summary>
internal sealed class FixturePage
{
    public FixturePage(PdfDocument doc, PdfPage page)
    {
        Doc = doc;
        Page = page;
    }

    public PdfDocument Doc { get; }
    public PdfPage Page { get; }
    public ContentBuilder Content { get; } = new();

    /// <summary>Registers a standard-14 font on the page and returns its resource name.</summary>
    public PdfName Font(string name = StandardFonts.Helvetica) =>
        PdfResources.Add(Page.GetOrCreateResources(), PdfName.Font, "F", Doc.MakeIndirect(PdfFont.Standard(name).Dictionary!));

    /// <summary>Shows one line of text (WinAnsi-encoded) at a baseline position.</summary>
    public FixturePage Text(string text, float x, float y, float size, string font = StandardFonts.Helvetica, int renderMode = 0)
    {
        var name = Font(font);
        Content.BeginText().Font(name, size);
        if (renderMode != 0) Content.TextRenderingMode(renderMode);
        Content.MoveText(x, y).ShowText(PdfFont.Standard(font).Encode(text)).EndText();
        return this;
    }

    /// <summary>Draws an encoded image (PNG/JPEG) into the rectangle.</summary>
    public FixturePage Image(byte[] encoded, float x, float y, float width, float height)
    {
        var (image, _, _) = PdfImages.CreateXObject(encoded);
        var name = PdfResources.Add(Page.GetOrCreateResources(), PdfName.XObject, "Im", image);
        Content.SaveState().Transform(width, 0, 0, height, x, y).DrawXObject(name).RestoreState();
        return this;
    }

    /// <summary>Writes the accumulated drawing into the page as one more content stream.</summary>
    public void Flush()
    {
        if (!Content.IsEmpty) Page.AppendContent(Content.ToArray());
    }
}

/// <summary>Builds small, deterministic PDFs for tests.</summary>
public static class TestPdfs
{
    public const float PageWidth = 595;   // A4 in points
    public const float PageHeight = 842;

    /// <summary>Builds a document of <paramref name="pages"/> A4 pages, each drawn by <paramref name="draw"/>.</summary>
    internal static byte[] Build(int pages, Action<FixturePage, int> draw)
    {
        var doc = PdfDocument.CreateNew();
        for (int i = 1; i <= pages; i++)
        {
            var page = new FixturePage(doc, doc.AddNewPage(PageWidth, PageHeight));
            draw(page, i);
            page.Flush();
        }
        return doc.Save();
    }

    internal static byte[] Build(Action<FixturePage> draw) => Build(1, (p, _) => draw(p));

    private static byte[] SolidPng(int width, int height, SKColor colour)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var c = new SKCanvas(bitmap)) c.Clear(colour);
        using var img = SKImage.FromBitmap(bitmap);
        return img.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    /// <summary>A single page with absolutely positioned text lines.</summary>
    public static byte[] WithText(params (string Text, float X, float Y, float Size)[] lines) => Build(p =>
    {
        foreach (var (text, x, y, size) in lines) p.Text(text, x, y, size);
    });

    /// <summary>
    /// A single page holding one line of text set in a named standard-14 font at a known size.
    /// Used by the font-fidelity tests (#29), which need the type size the text was actually set
    /// in to be ground truth they can assert against.
    /// </summary>
    public static byte[] WithTextInFont(string standardFontName, string text, float size,
        float x = 72, float y = 700) => Build(p => p.Text(text, x, y, size, standardFontName));

    /// <summary>A document with the given number of pages, each labelled.</summary>
    public static byte[] MultiPage(int pages, string labelPrefix = "Page") =>
        Build(pages, (p, i) => p.Text($"{labelPrefix} {i}", 72, 770, 14));

    /// <summary>A page with a solid-colour raster image drawn into the given rectangle.</summary>
    public static byte[] WithImage(float x, float y, float width, float height) => Build(p =>
    {
        p.Image(SolidPng(60, 40, SKColors.Red), x, y, width, height);
        p.Text("Document with image", 72, 800, 12);
    });

    /// <summary>
    /// Text drawn on top of a solid-red image — the ordinary shape of a document with a
    /// letterhead, a watermark or a scanned background behind its text. Editing that text means
    /// touching a region that overlaps the image, so this is the fixture that shows whether an
    /// edit disturbs the artwork underneath it.
    /// </summary>
    public static byte[] WithTextOverImage(string text, float x, float y, float size) => Build(p =>
    {
        // The image spans the whole band the text sits in, so any region around the text is
        // necessarily inside the image too.
        p.Image(SolidPng(120, 80, SKColors.Red), x - 40, y - 40, 300, 140);
        p.Text(text, x, y, size);
    });

    /// <summary>
    /// A <em>searchable</em> scan, the shape OCR leaves behind: the words are pixels in a page
    /// image, and the only real text is an invisible (rendering mode 3) layer laid over them so the
    /// page can be selected and searched. Editing one of those words has to deal with the pixels,
    /// because removing the invisible layer changes nothing anybody can see.
    /// </summary>
    public static byte[] SearchableScan(string words, float x, float y, float size)
    {
        using var bitmap = new SKBitmap(600, 100);
        using (var c = new SKCanvas(bitmap))
        {
            c.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            using var f = new SKFont(SKTypeface.Default, 48);
            c.DrawText(words, 10, 60, SKTextAlign.Left, f, paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        byte[] png = image.Encode(SKEncodedImageFormat.Png, 100).ToArray();

        return Build(p =>
        {
            // The 600x100 bitmap is drawn at half scale, so its 48px type lands as 24pt and its
            // baseline (bitmap y=60) lands 20pt up from the image's bottom edge. Placing the image
            // from that baseline keeps the invisible layer sitting on the scanned words, the way
            // real OCR output does — a layer offset from the pixels it describes would make this
            // fixture prove nothing.
            p.Image(png, x - 5, y - 20, 300, 50);
            p.Text(words, x, y, size, renderMode: 3);
        });
    }

    /// <summary>
    /// A "scan": one page whose entire surface is a JPEG (DCTDecode) photo of some text — the
    /// shape of document people actually run OCR over.
    /// </summary>
    public static byte[] JpegScan(string text = "HELLO OCR")
    {
        const int pixelWidth = 1240, pixelHeight = 1754; // A4 at ~150 dpi
        using var bitmap = new SKBitmap(pixelWidth, pixelHeight);
        using (var c = new SKCanvas(bitmap))
        {
            c.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            using var font = new SKFont(SKTypeface.Default, 96);
            c.DrawText(text, 120, 400, SKTextAlign.Left, font, paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        byte[] jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 85).ToArray();
        return Build(p => p.Image(jpeg, 0, 0, PageWidth, PageHeight));
    }

    /// <summary>A page with a solid blue square drawn as a genuine inline (BI/ID/EI) image.</summary>
    public static byte[] WithInlineImage(float x, float y, float width, float height)
    {
        const int size = 20;
        byte[] pixels = new byte[size * size * 3];
        for (int i = 0; i < pixels.Length; i += 3)
            pixels[i + 2] = 255; // solid blue (R=0, G=0, B=255)

        return Build(p =>
        {
            var inline = new PdfDictionary();
            inline.Put(PdfName.Of("W"), new PdfNumber(size));
            inline.Put(PdfName.Of("H"), new PdfNumber(size));
            inline.Put(PdfName.Of("CS"), PdfName.Of("RGB"));
            inline.Put(PdfName.Of("BPC"), new PdfNumber(8));
            p.Content.SaveState().Transform(width, 0, 0, height, x, y)
                .Operation(new ContentOperation("BI", new List<PdfObject>()) { InlineImageDictionary = inline, InlineImageData = pixels })
                .RestoreState();
            p.Text("Document with inline image", 72, 800, 12);
        });
    }

    /// <summary>A form XObject showing <paramref name="text"/>, its bounding box width x height.</summary>
    private static PdfStream FormWithText(FixturePage p, string text, float width, float height, float size, float tx, float ty)
    {
        var resources = new PdfDictionary();
        var font = PdfResources.Add(resources, PdfName.Font, "F", p.Doc.MakeIndirect(PdfFont.Standard(StandardFonts.Helvetica).Dictionary!));
        var content = new ContentBuilder().BeginText().Font(font, size).MoveText(tx, ty)
            .ShowText(PdfFont.Standard(StandardFonts.Helvetica).Encode(text)).EndText();
        return Form(p, content.ToArray(), resources, width, height);
    }

    private static PdfStream Form(FixturePage p, byte[] content, PdfDictionary resources, float width, float height)
    {
        var form = new PdfStream(content);
        form.Put(PdfName.Type, PdfName.XObject);
        form.Put(PdfName.Subtype, PdfName.Form);
        form.Put(PdfName.BBox, new PdfArray(0, 0, width, height));
        form.Put(PdfName.Resources, resources);
        return p.Doc.MakeIndirect(form);
    }

    private static void DrawForm(FixturePage p, PdfStream form, float x, float y)
    {
        var name = PdfResources.Add(p.Page.GetOrCreateResources(), PdfName.XObject, "Fm", form);
        p.Content.SaveState().Transform(1, 0, 0, 1, x, y).DrawXObject(name).RestoreState();
    }

    /// <summary>
    /// A page that draws a form XObject at (x, y) whose own content shows <paramref name="formText"/>.
    /// The form's bounding box occupies exactly the given width/height in page space.
    /// </summary>
    public static byte[] WithForm(string formText, float x, float y, float width, float height) => Build(p =>
    {
        DrawForm(p, FormWithText(p, formText, width, height, 14, 4, height / 2 - 5), x, y);
        p.Text("Document with a form", 72, 800, 12);
    });

    /// <summary>
    /// A chain of <paramref name="depth"/> nested form XObjects, each drawing the next via Do,
    /// all sharing the same bounding box in page space — used to exercise the recursion
    /// depth guard in <c>ContentStreamEditor</c>.
    /// </summary>
    public static byte[] WithNestedForms(int depth, float x, float y, float width, float height) => Build(p =>
    {
        PdfStream? previous = null;
        for (int level = depth; level >= 1; level--)
        {
            if (previous == null)
            {
                previous = FormWithText(p, "innermost", width, height, 10, 2, 2);
                continue;
            }
            var resources = new PdfDictionary();
            var inner = PdfResources.Add(resources, PdfName.XObject, "Fm", previous);
            var content = new ContentBuilder().SaveState().DrawXObject(inner).RestoreState();
            previous = Form(p, content.ToArray(), resources, width, height);
        }
        DrawForm(p, previous!, x, y);
        p.Text("Document with nested forms", 72, 800, 12);
    });

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    /// <summary>A page whose content stream is written by hand, with Helvetica registered as the given name.</summary>
    private static byte[] RawContent(Func<string, string> content) => Build(p =>
    {
        var font = p.Font().Value;
        p.Content.Raw(content(font));
    });

    /// <summary>
    /// A page whose content stream is written by hand so it can use the low-level
    /// <c>'</c> and <c>"</c> text-showing operators.
    /// </summary>
    public static byte[] WithQuoteOperators(string firstLine, string secondLine, string thirdLine) =>
        RawContent(font =>
            $"BT\n/{font} 12 Tf\n14 TL\n72 700 Td\n" +
            $"({Escape(firstLine)}) Tj\n" +
            $"({Escape(secondLine)}) '\n" +
            $"0 0 ({Escape(thirdLine)}) \"\n" +
            "ET");

    /// <summary>
    /// A page whose content stream shows two strings via a single low-level <c>TJ</c>
    /// operator (an explicit array of string/number operands), with a real kerning
    /// number between them.
    /// </summary>
    public static byte[] WithTjArray(string first, string second, float x, float y, float size) =>
        RawContent(font => string.Create(CultureInfo.InvariantCulture,
            $"BT\n/{font} {size} Tf\n{x} {y} Td\n[({Escape(first)}) -250 ({Escape(second)})] TJ\nET"));

    /// <summary>
    /// A page whose content stream shows a <c>TJ</c> array containing an empty string
    /// alongside two real words — the scenario <c>ContentStreamEditor</c>'s encoding-mismatch
    /// fallback guards against.
    /// </summary>
    public static byte[] WithTjArrayContainingEmptyString(string first, string second, float x, float y, float size) =>
        RawContent(font => string.Create(CultureInfo.InvariantCulture,
            $"BT\n/{font} {size} Tf\n{x} {y} Td\n[({Escape(first)}) -200 () -200 ({Escape(second)})] TJ\nET"));

    /// <summary>A page whose content stream calls <c>Do</c> for an XObject name that is not
    /// registered in the page's resources at all (a dangling/invalid reference).</summary>
    public static byte[] WithDanglingXObjectReference(string visibleText, float x, float y, float size) =>
        RawContent(font => string.Create(CultureInfo.InvariantCulture,
            $"q /Ghost Do Q\nBT /{font} {size} Tf {x} {y} Td ({visibleText}) Tj ET"));

    /// <summary>
    /// A page with an XObject whose Subtype is neither Image nor Form (a made-up
    /// subtype), used to exercise the redactor's passthrough for unrecognised XObjects.
    /// </summary>
    public static byte[] WithUnknownXObjectSubtype(string visibleText, float x, float y, float size) => Build(p =>
    {
        var weird = new PdfStream(Array.Empty<byte>(), compress: false);
        weird.Put(PdfName.Type, PdfName.XObject);
        weird.Put(PdfName.Subtype, PdfName.Of("Mystery"));
        var resources = p.Page.GetOrCreateResources();
        var xobjects = new PdfDictionary();
        xobjects.Put(PdfName.Of("Weird1"), p.Doc.MakeIndirect(weird));
        resources.Put(PdfName.XObject, xobjects);
        string font = p.Font().Value;
        p.Content.Raw(string.Create(CultureInfo.InvariantCulture,
            $"q /Weird1 Do Q\nBT /{font} {size} Tf {x} {y} Td ({visibleText}) Tj ET"));
    });

    /// <summary>
    /// An otherwise-normal image XObject whose declared bit depth is invalid for its
    /// color space (PDF only allows 1/2/4/8/16 bits per component; this sets 3) — the
    /// structure looks fine but decoding fails, exercising the pixel-scrubber's
    /// failure path.
    /// </summary>
    public static byte[] WithCorruptImage(float x, float y, float width, float height)
    {
        var doc = PdfDocument.Open(WithImage(x, y, width, height));
        var xobjects = doc.GetPage(1).Resources!.GetAsDictionary(PdfName.XObject)!;
        foreach (var key in xobjects.Keys)
        {
            var stream = xobjects.GetAsStream(key);
            if (stream != null && PdfName.Image.Equals(stream.GetAsName(PdfName.Subtype)))
                stream.Put(PdfName.BitsPerComponent, new PdfNumber(3));
        }
        return doc.Save();
    }

    /// <summary>
    /// A document loaded with several kinds of "hidden information": Info metadata, an embedded
    /// file attachment, a document-level JavaScript, a comment (Text) annotation, a bookmark, and
    /// an optional-content layer — for exercising the sanitiser.
    /// </summary>
    public static byte[] WithHiddenData()
    {
        var doc = PdfDocument.CreateNew();
        var page = new FixturePage(doc, doc.AddNewPage(PageWidth, PageHeight));
        page.Text("Visible content", 72, 750, 12);
        page.Flush();
        var catalog = doc.Catalog!;

        // Metadata (author/title/custom key).
        var info = doc.GetOrCreateInfo();
        info.Put(PdfName.Author, PdfString.FromText("Jane Author"));
        info.Put(PdfName.Title, PdfString.FromText("Internal Draft"));
        info.Put(PdfName.Of("Department"), PdfString.FromText("Legal"));

        // Embedded file attachment.
        var file = new PdfStream(Encoding.UTF8.GetBytes("secret spreadsheet"));
        file.Put(PdfName.Type, PdfName.Of("EmbeddedFile"));
        var spec = new PdfDictionary();
        spec.Put(PdfName.Type, PdfName.Of("Filespec"));
        spec.Put(PdfName.F, PdfString.FromText("data.txt"));
        spec.Put(PdfName.Of("UF"), PdfString.FromText("data.txt"));
        spec.Put(PdfName.Of("Desc"), PdfString.FromText("hidden data"));
        var ef = new PdfDictionary();
        ef.Put(PdfName.F, doc.MakeIndirect(file));
        spec.Put(PdfName.Of("EF"), ef);
        PdfNameTree.Write(catalog, PdfName.EmbeddedFiles, new[] { (PdfString.FromText("data.txt"), (PdfObject)doc.MakeIndirect(spec)) });

        // Document-level JavaScript.
        var js = new PdfDictionary();
        js.Put(PdfName.S, PdfName.JavaScript);
        js.Put(PdfName.JS, PdfString.FromText("app.alert(1);"));
        PdfNameTree.Write(catalog, PdfName.JavaScript, new[] { (PdfString.FromText("track"), (PdfObject)js) });

        // A comment (sticky-note) annotation.
        var note = new PdfDictionary();
        note.Put(PdfName.Type, PdfName.Annot);
        note.Put(PdfName.Subtype, PdfName.Of("Text"));
        note.Put(PdfName.Rect, new PdfArray(200, 700, 220, 720));
        note.Put(PdfName.Contents, PdfString.FromText("reviewer's private note"));
        page.Page.AddAnnotation(note);

        // A bookmark / outline entry.
        var outlines = doc.MakeIndirect(new PdfDictionary());
        var item = doc.MakeIndirect(new PdfDictionary());
        item.Put(PdfName.Title, PdfString.FromText("Confidential section"));
        item.Put(PdfName.Parent, outlines);
        outlines.Put(PdfName.Type, PdfName.Outlines);
        outlines.Put(PdfName.First, item);
        outlines.Put(PdfName.Of("Last"), item);
        outlines.Put(PdfName.Count, new PdfNumber(1));
        catalog.Put(PdfName.Outlines, outlines);

        // An optional-content layer (OCG).
        var ocg = doc.MakeIndirect(new PdfDictionary());
        ocg.Put(PdfName.Type, PdfName.Of("OCG"));
        ocg.Put(PdfName.Name, PdfString.FromText("Watermark layer"));
        var properties = new PdfDictionary();
        properties.Put(PdfName.OCGs, new PdfArray(new PdfObject[] { ocg }));
        var d = new PdfDictionary();
        d.Put(PdfName.Of("ON"), new PdfArray(new PdfObject[] { ocg }));
        properties.Put(PdfName.Of("D"), d);
        catalog.Put(PdfName.OCProperties, properties);
        return doc.Save();
    }

    /// <summary>A single-page document with a text form field (built by hand, not by FormTools).</summary>
    public static byte[] WithTextField(string fieldName, string initialValue = "")
    {
        var doc = PdfDocument.CreateNew();
        var page = doc.AddNewPage(PageWidth, PageHeight);
        var field = AcroForm.NewWidget(new PdfRect(100, 600, 200, 24), styled: false);
        field.Put(PdfName.FT, PdfName.Tx);
        field.Put(PdfName.DA, PdfString.FromText("/Helv 12 Tf 0 g"));
        var node = AcroForm.AddMergedField(doc, page, field, fieldName);
        AcroForm.SetValue(doc, node, initialValue);
        return doc.Save();
    }

    /// <summary>A single-page document carrying a document-level JavaScript open action.</summary>
    public static byte[] WithOpenActionJavaScript(string script = "app.alert('hello');")
    {
        var doc = PdfDocument.CreateNew();
        doc.AddNewPage(PageWidth, PageHeight);
        var action = new PdfDictionary();
        action.Put(PdfName.S, PdfName.JavaScript);
        action.Put(PdfName.JS, PdfString.FromText(script));
        doc.Catalog!.Put(PdfName.OpenAction, action);
        return doc.Save();
    }

    private static PdfDictionary UriLink(float x, float y, float width, float height, string url)
    {
        var action = new PdfDictionary();
        action.Put(PdfName.S, PdfName.URI);
        action.Put(PdfName.URI, new PdfString(Encoding.ASCII.GetBytes(url)));
        var link = new PdfDictionary();
        link.Put(PdfName.Type, PdfName.Annot);
        link.Put(PdfName.Subtype, PdfName.Link);
        link.Put(PdfName.Rect, new PdfRect(x, y, width, height).ToArray());
        link.Put(PdfName.A, action);
        return link;
    }

    /// <summary>A single-page document with a link annotation pointing at the given URL.</summary>
    public static byte[] WithLinkTo(string url)
    {
        var doc = PdfDocument.CreateNew();
        doc.AddNewPage(PageWidth, PageHeight).AddAnnotation(UriLink(72, 700, 200, 20, url));
        return doc.Save();
    }

    /// <summary>A page with a link annotation covering the given rectangle.</summary>
    public static byte[] WithLinkAnnotation(float x, float y, float width, float height) => Build(p =>
    {
        p.Text("clickable link", x, y + 4, 12);
        p.Page.AddAnnotation(UriLink(x, y, width, height, "https://example.com"));
    });

    /// <summary>
    /// A raw, uncompressed page that mimics how Chrome / Skia print-to-PDF (what Google Docs
    /// "Download as PDF" produces) structures its content: a top-level scale + Y-flip matrix
    /// applied <em>outside</em> any q/Q, so it is never restored and stays active at the end of the
    /// content stream. Text is drawn under that transform via a text matrix, so it renders upright
    /// at a normal absolute position — but anything naively appended to the page inherits the
    /// leftover matrix. Used to regression-test that redaction/edit draw in the page's default
    /// user space regardless. The single Helvetica word renders around absolute (50, 300).
    /// </summary>
    public static byte[] ChromeStyleLeftoverCtm(string word = "SECRET")
    {
        // Top-level CTM: scale by 0.5, flip Y, translate up by 600  ->  maps (x,y) to (0.5x, 600-0.5y).
        // Inside it: clip to the page, then a text object whose text matrix flips glyphs upright.
        string content =
            "0.5 0 0 -0.5 0 600 cm\n" +   // unbalanced top-level transform (never restored)
            "q\n" +
            "0 0 800 1200 re W n\n" +      // clip to the page (in the scaled space)
            "BT\n/F1 48 Tf\n1 0 0 -1 100 600 Tm\n(" + word + ") Tj\nET\n" +
            "Q\n";
        byte[] contentBytes = Encoding.ASCII.GetBytes(content);

        var objs = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 600] " +
                "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {contentBytes.Length} >>\nstream\n{content}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int> { 0 };
        for (int i = 0; i < objs.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objs[i]}\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append(CultureInfo.InvariantCulture, $"xref\n0 {objs.Count + 1}\n0000000000 65535 f \n");
        for (int i = 1; i <= objs.Count; i++)
            sb.Append(CultureInfo.InvariantCulture, $"{offsets[i].ToString(CultureInfo.InvariantCulture).PadLeft(10, '0')} 00000 n \n");
        sb.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objs.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
