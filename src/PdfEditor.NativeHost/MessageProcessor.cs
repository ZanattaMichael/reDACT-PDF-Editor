using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PdfEditor.Core;

namespace PdfEditor.NativeHost;

/// <summary>
/// Stateless JSON dispatcher. Every request carries the document(s) as base64; every
/// response returns the transformed document the same way. Responses larger than the
/// native-messaging outgoing limit are split into chunk frames that the extension's
/// background worker reassembles.
/// </summary>
public static class MessageProcessor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private const string RegionKey = "region";
    private const string ColorKey = "color";

    /// <summary>Handles one request and returns the JSON frames to emit (1..n).</summary>
    public static IReadOnlyList<string> Handle(string requestJson)
    {
        string id = "";
        try
        {
            var request = JsonNode.Parse(requestJson)?.AsObject()
                ?? throw new InvalidDataException("Request is not a JSON object.");
            id = request["id"]?.GetValue<string>() ?? "";
            string action = request["action"]?.GetValue<string>()
                ?? throw new InvalidDataException("Missing 'action'.");
            var payload = request["payload"]?.AsObject() ?? new JsonObject();

            object result = Dispatch(action, payload);
            return Frame(id, ok: true, JsonSerializer.SerializeToNode(result, JsonOptions));
        }
        catch (Exception ex)
        {
            return Frame(id, ok: false, new JsonObject { ["error"] = ex.Message });
        }
    }

    private static object Dispatch(string action, JsonObject p) => action switch
    {
        "ping" => new { pong = true, version = typeof(MessageProcessor).Assembly.GetName().Version?.ToString() },
        "diagnostics" => HostDiagnostics.Collect(),
        "info" => Info(p),
        "render" => Render(p),
        "redact" => Redact(p),
        "rotate" => RotateAction(p),
        "arrange-pages" => ArrangePagesAction(p),
        "add-text" => AddTextAction(p),
        "move-text" => MoveTextAction(p),
        "move-image" => MoveImageAction(p),
        "add-drawing" => AddDrawingAction(p),
        "add-highlight" => AddHighlightAction(p),
        "form-fields" => FormFieldsAction(p),
        "fill-form" => FillFormAction(p),
        "add-form-field" => AddFormFieldAction(p),
        "validate" => ValidateAction(p),
        "scan-safety" => ScanSafetyAction(p),
        "js-sources" => new { sources = PdfSafety.JavaScriptSources(Pdf(p), Password(p)) },
        "strip-active" => StripActiveAction(p),
        "list-scripts" => ListScriptsAction(p),
        "add-script" => AddScriptAction(p),
        "remove-script" => RemoveScriptAction(p),
        "inspect-hidden" => InspectHiddenAction(p),
        "sanitize" => SanitizeAction(p),
        "compare" => CompareAction(p),
        "visual-diff" => VisualDiffAction(p),
        "ocr-available" => new { available = OcrTool.CanOcr },
        "ocr-text" => OcrTextAction(p),
        "ocr-searchable" => OcrSearchableAction(p),
        "list-urls" => ListUrlsAction(p),
        "list-link-hotspots" => ListLinkHotspotsAction(p),
        "scan-urls" => ScanUrlsAction(p),
        "get-region-text" => GetRegionText(p),
        "replace-region-text" => ReplaceRegionText(p),
        "find-text" => FindTextAction(p),
        "page-text" => PageTextAction(p),
        "replace-all" => ReplaceAllAction(p),
        "merge" => MergeAction(p),
        "merge-files" => MergeFilesAction(p),
        "import" => ImportAction(p),
        "encrypt" => EncryptAction(p),
        "decrypt" => DecryptAction(p),
        "watermark" => WatermarkAction(p),
        "bates" => BatesAction(p),
        "flatten" => FlattenAction(p),
        "sign-image" => SignImage(p),
        "sign-digital" => SignDigital(p),
        "signatures" => Signatures(p),
        "create-cert" => CreateCert(p),
        _ => throw new InvalidDataException($"Unknown action '{action}'.")
    };

    // ------------------------------------------------------------- actions

    private static object Info(JsonObject p)
    {
        var info = PdfInspector.GetInfo(Pdf(p), Password(p));
        return new
        {
            pageCount = info.PageCount,
            encrypted = info.IsEncrypted,
            pages = info.Pages.Select(pg => new { number = pg.Number, x = pg.X, y = pg.Y, width = pg.Width, height = pg.Height, rotation = pg.Rotation })
        };
    }

    private static object Render(JsonObject p)
    {
        int page = p["page"]!.GetValue<int>();
        int dpi = p["dpi"]?.GetValue<int>() ?? 144;
        // Bitmap memory grows with the square of dpi; an unbounded value here is a DoS
        // vector (the UI only ever asks for 110/144, so this range is generous).
        if (dpi < 1 || dpi > 600)
            throw new InvalidDataException($"dpi {dpi} is out of the supported range (1-600).");
        byte[] png = PageRenderer.RenderPagePng(Pdf(p), page, dpi, Password(p));
        return new { png = Convert.ToBase64String(png) };
    }

    private static object Redact(JsonObject p)
    {
        var pdf = Pdf(p);
        var password = Password(p);
        int globalIntensity = p["intensity"]?.GetValue<int>() ?? 0;
        // Each region may carry its own Privacy level (per-redaction); anything without one uses the
        // global level. Group by effective level and prepare each group — merging/widening only make
        // sense within a single level. The prepared boxes drive both the removal and the report, so
        // nothing live is ever left hidden under a box.
        var groups = new Dictionary<int, List<RectRegion>>();
        foreach (var n in p["regions"]!.AsArray())
        {
            var o = n!.AsObject();
            int gi = o["intensity"]?.GetValue<int>() ?? globalIntensity;
            if (!groups.TryGetValue(gi, out var list)) groups[gi] = list = new List<RectRegion>();
            list.Add(Region(o));
        }
        var regions = new List<RectRegion>();
        foreach (var (gi, regs) in groups)
            regions.AddRange(RedactionBox.Prepare(pdf, regs, gi, password));
        // The top level (4) paints a textured hatch instead of flat black; apply it if any box asks.
        var fill = groups.Keys.Any(k => k >= 4) ? Redactor.Fill.Hatch : Redactor.Fill.Solid;
        // Audit the original document before the content is removed: what was under the boxes is
        // exactly what the redaction takes out (#48).
        var report = RedactionReporter.Analyze(pdf, regions, password);
        var result = Redactor.Redact(pdf, regions, password, fill);
        // Integrity: SHA-256 of the document before and after, so the downloadable compliance
        // report can be tied to the exact files it describes.
        return new
        {
            pdf = Convert.ToBase64String(result.Pdf),
            warnings = result.Warnings,
            report,
            originalSha256 = Sha256Hex(pdf),
            outputSha256 = Sha256Hex(result.Pdf),
            generatedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    private static string Sha256Hex(byte[] data) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();

    private static object RotateAction(JsonObject p)
    {
        var pages = (p["pages"]?.AsArray().Select(n => n!.GetValue<int>()) ?? Enumerable.Empty<int>()).ToList();
        int degrees = p["degrees"]?.GetValue<int>() ?? 90;
        var result = PageTools.Rotate(Pdf(p), pages, degrees, Password(p));
        return new { pdf = Convert.ToBase64String(result.Pdf), warnings = result.Warnings };
    }

    private static object ArrangePagesAction(JsonObject p)
    {
        var order = p["order"]!.AsArray().Select(n => n!.GetValue<int>()).ToList();
        var result = PageTools.Arrange(Pdf(p), order, Password(p));
        return new { pdf = Convert.ToBase64String(result.Pdf), warnings = result.Warnings };
    }

    private static object AddTextAction(JsonObject p)
    {
        var result = TextTools.AddText(Pdf(p), Region(p[RegionKey]!.AsObject()),
            p["text"]?.GetValue<string>() ?? "",
            p["fontSize"]?.GetValue<float>() ?? 14f,
            p["fontFamily"]?.GetValue<string>(),
            p["bold"]?.GetValue<bool>() ?? false,
            p["italic"]?.GetValue<bool>() ?? false,
            p[ColorKey]?.GetValue<string>(),
            Password(p));
        return new { pdf = Convert.ToBase64String(result.Pdf), warnings = result.Warnings };
    }

    private static object MoveTextAction(JsonObject p)
    {
        var result = TextTools.MoveText(Pdf(p), Region(p[RegionKey]!.AsObject()),
            p["dx"]!.GetValue<float>(), p["dy"]!.GetValue<float>(), Password(p));
        return new { pdf = Convert.ToBase64String(result.Pdf), warnings = result.Warnings };
    }

    private static object MoveImageAction(JsonObject p)
    {
        var region = Region(p[RegionKey]!.AsObject());
        var result = ImageTools.MoveImage(Pdf(p), region.Page, region,
            p["dx"]!.GetValue<float>(), p["dy"]!.GetValue<float>(), Password(p));
        return new { pdf = Convert.ToBase64String(result.Pdf), warnings = result.Warnings };
    }

    private static object AddDrawingAction(JsonObject p)
    {
        int page = p["page"]!.GetValue<int>();
        var strokes = (p["strokes"]?.AsArray() ?? new JsonArray())
            .Select(s => (IReadOnlyList<(float X, float Y)>)s!.AsArray()
                .Select(pt => (pt!["x"]!.GetValue<float>(), pt["y"]!.GetValue<float>())).ToList())
            .ToList();
        var result = InkTools.AddInk(Pdf(p), page, strokes,
            p[ColorKey]?.GetValue<string>(), p["width"]?.GetValue<float>() ?? 2f, Password(p));
        return new { pdf = Convert.ToBase64String(result.Pdf), warnings = result.Warnings };
    }

    private static object AddHighlightAction(JsonObject p)
    {
        int page = p["page"]!.GetValue<int>();
        var rects = p["rects"]!.AsArray().Select(n =>
        {
            var o = n!.AsObject();
            return new RectRegion(page, o["x"]!.GetValue<float>(), o["y"]!.GetValue<float>(),
                o["width"]!.GetValue<float>(), o["height"]!.GetValue<float>());
        }).ToList();
        var result = HighlightTool.AddHighlight(Pdf(p), page, rects,
            p[ColorKey]?.GetValue<string>(), Password(p));
        return new { pdf = Convert.ToBase64String(result.Pdf), warnings = result.Warnings };
    }

    private static object FormFieldsAction(JsonObject p)
    {
        var fields = FormTools.ListFields(Pdf(p), Password(p));
        return new
        {
            fields = fields.Select(f => new
            {
                name = f.Name, type = f.Type, value = f.Value, options = f.Options, readOnly = f.ReadOnly,
                page = f.Page, x = f.X, y = f.Y, width = f.Width, height = f.Height, script = f.Script
            })
        };
    }

    private static object FillFormAction(JsonObject p)
    {
        var values = new Dictionary<string, string>();
        if (p["values"]?.AsObject() is { } obj)
            foreach (var kv in obj) values[kv.Key] = kv.Value?.GetValue<string>() ?? "";
        var result = FormTools.FillFields(Pdf(p), values, p["flatten"]?.GetValue<bool>() ?? false, Password(p));
        return new { pdf = Convert.ToBase64String(result.Pdf), warnings = result.Warnings };
    }

    private static object AddFormFieldAction(JsonObject p)
    {
        var region = Region(p[RegionKey]!.AsObject());
        string type = p["fieldType"]?.GetValue<string>() ?? "text";
        string? name = p["name"]?.GetValue<string>();
        string? script = p["script"]?.GetValue<string>();
        var result = type switch
        {
            "checkbox" => FormTools.AddCheckbox(Pdf(p), region.Page, region, name,
                password: Password(p), script: script),
            "dropdown" => FormTools.AddDropdown(Pdf(p), region.Page, region, name,
                (p["options"]?.AsArray() ?? new JsonArray()).Select(o => o!.GetValue<string>()).ToList(),
                Password(p), script),
            "radio" or "option" => FormTools.AddRadioGroup(Pdf(p), region.Page, region, name,
                (p["options"]?.AsArray() ?? new JsonArray()).Select(o => o!.GetValue<string>()).ToList(),
                Password(p), script),
            "multiline" => FormTools.AddTextField(Pdf(p), region.Page, region, name,
                p["value"]?.GetValue<string>(), Password(p), multiline: true, script: script),
            "button" => FormTools.AddButton(Pdf(p), region.Page, region, name,
                p["caption"]?.GetValue<string>(), script, Password(p)),
            _ => FormTools.AddTextField(Pdf(p), region.Page, region, name,
                p["value"]?.GetValue<string>(), Password(p), script: script),
        };
        return new { pdf = Convert.ToBase64String(result.Pdf), warnings = result.Warnings };
    }

    /// <summary>
    /// Re-reads an exported document and reports what is structurally wrong with it. This is a
    /// diagnostic: it never rewrites the document and never turns a successful export into a
    /// failure — the caller decides what to do with the findings.
    /// </summary>
    private static object ValidateAction(JsonObject p)
    {
        var report = ExportValidator.Validate(Pdf(p), Password(p));
        return new
        {
            valid = report.IsValid,
            errorCount = report.ErrorCount,
            warningCount = report.WarningCount,
            findings = report.Findings.Select(f => new
            {
                code = f.Code,
                severity = f.Severity.ToString().ToLowerInvariant(),
                location = f.Location,
                message = f.Message
            }),
            log = report.ToLogText()
        };
    }

    private static object ScanSafetyAction(JsonObject p)
    {
        var report = PdfSafety.Scan(Pdf(p), Password(p));
        return new
        {
            javaScriptCount = report.JavaScriptCount,
            urlCount = report.UrlCount,
            hasActiveContent = report.HasActiveContent,
            samples = report.Samples
        };
    }

    private static object StripActiveAction(JsonObject p)
    {
        // Default both true (strip everything) when the flags are omitted.
        bool js = p["javaScript"]?.GetValue<bool>() ?? true;
        bool urls = p["urls"]?.GetValue<bool>() ?? true;
        var result = PdfSafety.StripActive(Pdf(p), js, urls, Password(p));
        return new { pdf = Convert.ToBase64String(result.Pdf), warnings = result.Warnings };
    }

    private static object ListScriptsAction(JsonObject p)
    {
        var scripts = JavaScriptTool.ListScripts(Pdf(p), Password(p));
        return new { scripts = scripts.Select(s => new { name = s.Name, script = s.Script }) };
    }

    private static object AddScriptAction(JsonObject p)
    {
        var result = JavaScriptTool.AddDocumentScript(Pdf(p),
            p["name"]?.GetValue<string>() ?? "",
            p["script"]?.GetValue<string>() ?? "", Password(p));
        return new { pdf = Convert.ToBase64String(result.Pdf), warnings = result.Warnings };
    }

    private static object RemoveScriptAction(JsonObject p)
    {
        var result = JavaScriptTool.RemoveScript(Pdf(p),
            p["name"]?.GetValue<string>() ?? "", Password(p));
        return new { pdf = Convert.ToBase64String(result.Pdf), warnings = result.Warnings };
    }

    private static object OcrTextAction(JsonObject p)
    {
        string text = OcrTool.ExtractText(Pdf(p), p["page"]?.GetValue<int>() ?? 1, Password(p));
        return new { text };
    }

    private static object OcrSearchableAction(JsonObject p)
    {
        byte[] result = OcrTool.MakeSearchable(Pdf(p), Password(p));
        return new { pdf = Convert.ToBase64String(result) };
    }

    private static object CompareAction(JsonObject p)
    {
        // 'other' is the previous/other version to diff against; the loaded document is the newer.
        byte[] other = Convert.FromBase64String(p["other"]?.GetValue<string>()
            ?? throw new InvalidDataException("Missing 'other' document."));
        string? otherPassword = p["otherPassword"]?.GetValue<string>();
        var report = DocComparer.Compare(other, Pdf(p), otherPassword, Password(p));
        return new
        {
            pagesOld = report.PagesOld,
            pagesNew = report.PagesNew,
            changedPages = report.ChangedPages,
            addedWords = report.AddedWords,
            removedWords = report.RemovedWords,
            identical = report.Identical,
            pages = report.Pages.Where(pg => pg.Changed).Select(pg => new
            {
                page = pg.Page, added = pg.Added, removed = pg.Removed
            })
        };
    }

    private static object VisualDiffAction(JsonObject p)
    {
        // Same orientation as compare: 'other' is the previous version, the loaded document is newer.
        byte[] other = Convert.FromBase64String(p["other"]?.GetValue<string>()
            ?? throw new InvalidDataException("Missing 'other' document."));
        string? otherPassword = p["otherPassword"]?.GetValue<string>();
        int page = p["page"]!.GetValue<int>();
        int dpi = p["dpi"]?.GetValue<int>() ?? 120;
        var diff = VisualDiff.DiffPage(other, Pdf(p), page, dpi, otherPassword, Password(p));
        return new
        {
            page = diff.Page,
            changedFraction = diff.ChangedFraction,
            png = Convert.ToBase64String(diff.Png),
        };
    }

    private static object InspectHiddenAction(JsonObject p)
    {
        var r = Sanitizer.Inspect(Pdf(p), Password(p));
        return new
        {
            metadataFields = r.MetadataFields,
            attachments = r.Attachments,
            scriptsAndActions = r.ScriptsAndActions,
            annotations = r.Annotations,
            bookmarks = r.Bookmarks,
            hiddenLayers = r.HiddenLayers,
            hasAny = r.HasAny
        };
    }

    private static object SanitizeAction(JsonObject p)
    {
        bool Opt(string key) => p[key]?.GetValue<bool>() ?? true; // default: remove everything
        var options = new SanitizeOptions(
            Metadata: Opt("metadata"),
            Attachments: Opt("attachments"),
            ScriptsAndActions: Opt("scriptsAndActions"),
            Annotations: Opt("annotations"),
            Bookmarks: Opt("bookmarks"),
            HiddenLayers: Opt("hiddenLayers"));
        var result = Sanitizer.Sanitize(Pdf(p), options, Password(p));
        return new { pdf = Convert.ToBase64String(result.Pdf), warnings = result.Warnings };
    }

    private static object ListUrlsAction(JsonObject p)
    {
        var links = UrlTools.ExtractLinks(Pdf(p), Password(p));
        return new
        {
            links = links.Select(l => new
            {
                page = l.Page, url = l.Url,
                x = l.X, y = l.Y, width = l.Width, height = l.Height
            })
        };
    }

    private static object ListLinkHotspotsAction(JsonObject p)
    {
        var links = UrlTools.ExtractLinkAnnotations(Pdf(p), Password(p));
        return new
        {
            links = links.Select(l => new
            {
                page = l.Page, url = l.Url, kind = l.Kind,
                x = l.X, y = l.Y, width = l.Width, height = l.Height
            })
        };
    }

    private static object ScanUrlsAction(JsonObject p)
    {
        var links = UrlTools.ExtractLinks(Pdf(p), Password(p));
        var creds = new CloudflareCredentials(
            p["cfAccountId"]?.GetValue<string>() ?? "",
            p["cfApiToken"]?.GetValue<string>() ?? "");
        var verdicts = CloudflareUrlScanner
            .ScanAsync(links, creds.IsUsable ? creds : null).GetAwaiter().GetResult();
        return new
        {
            usedCloudflare = creds.IsUsable,
            verdicts = verdicts.Select(v => new
            {
                page = v.Page, url = v.Url, level = v.Level,
                category = v.Category, source = v.Source, detail = v.Detail
            })
        };
    }

    private static object GetRegionText(JsonObject p)
    {
        var text = TextTools.GetTextInRegion(Pdf(p), Region(p[RegionKey]!.AsObject()), Password(p));
        return new
        {
            text = text.Text,
            fontSize = text.FontSize,
            fontFamily = text.FontFamily,
            bold = text.Bold,
            italic = text.Italic,
            // The font as the document names it, so the viewer can report what it matched against
            // — 'helvetica' alone hides the fact that the original was, say, DejaVuSans.
            sourceFont = text.SourceFont
        };
    }

    private static object ReplaceRegionText(JsonObject p)
    {
        var result = TextTools.ReplaceTextInRegion(Pdf(p), Region(p[RegionKey]!.AsObject()),
            p["text"]?.GetValue<string>() ?? "", p["fontSize"]?.GetValue<float>(),
            p["fontFamily"]?.GetValue<string>(),
            // Left absent, family and style inherit the run being replaced rather than
            // resetting it to plain Helvetica.
            p["bold"]?.GetValue<bool>(),
            p["italic"]?.GetValue<bool>(),
            p[ColorKey]?.GetValue<string>(),
            Password(p));
        return new { pdf = Convert.ToBase64String(result.Pdf), warnings = result.Warnings };
    }

    private static object PageTextAction(JsonObject p)
    {
        var spans = TextTools.GetTextSpans(Pdf(p), p["page"]!.GetValue<int>(), Password(p));
        return new
        {
            spans = spans.Select(s => new { text = s.Text, x = s.X, y = s.Y, width = s.Width, height = s.Height })
        };
    }

    private static object FindTextAction(JsonObject p)
    {
        var options = SearchOptions.Parse(p["matchMode"]?.GetValue<string>(),
            p["caseSensitive"]?.GetValue<bool>() ?? false);
        var matches = TextTools.FindText(Pdf(p), p["phrase"]?.GetValue<string>() ?? "", Password(p), options);
        return new
        {
            matches = matches.Select(m => new
            {
                page = m.Page, text = m.Text, x = m.X, y = m.Y, width = m.Width, height = m.Height
            })
        };
    }

    private static object ReplaceAllAction(JsonObject p)
    {
        var (result, count) = TextTools.ReplaceAll(Pdf(p),
            p["phrase"]?.GetValue<string>() ?? "",
            p["replacement"]?.GetValue<string>() ?? "", Password(p));
        return new { pdf = Convert.ToBase64String(result.Pdf), count, warnings = result.Warnings };
    }

    private static object MergeAction(JsonObject p)
    {
        var pdfs = p["pdfs"]!.AsArray().Select(n => Convert.FromBase64String(n!.GetValue<string>())).ToList();
        var passwords = p["passwords"]?.AsArray().Select(n => n?.GetValue<string>()).ToList();
        return new { pdf = Convert.ToBase64String(Merger.Merge(pdfs, passwords)) };
    }

    private static object MergeFilesAction(JsonObject p)
    {
        // Each entry is { data: base64, kind: "pdf"|"image"|"docx", password?, base? }; non-PDFs
        // are converted first. "base" marks the document being edited, and "password" opens it.
        var files = p["files"]!.AsArray().Select(n => n!.AsObject()).ToList();
        var pdfs = files.Select(o => DocumentImport.ToPdf(
            Convert.FromBase64String(o["data"]!.GetValue<string>()), o["kind"]?.GetValue<string>() ?? "pdf")).ToList();
        var passwords = files.Select(o => o["password"]?.GetValue<string>()).ToList();
        byte[] merged = Merger.Merge(pdfs, passwords);
        // Merging into a protected document is an edit of it, so the result keeps its encryption.
        int edited = files.FindIndex(o => o["base"]?.GetValue<bool>() == true);
        if (edited >= 0) merged = Encryptor.EncryptLike(merged, pdfs[edited], passwords[edited]);
        return new { pdf = Convert.ToBase64String(merged) };
    }

    // Converts a single non-PDF input (image or Word doc) to a PDF so it can be opened and edited
    // directly, not just merged. { data: base64, kind: "image"|"docx" }.
    private static object ImportAction(JsonObject p)
    {
        byte[] data = Convert.FromBase64String(p["data"]!.GetValue<string>());
        return new { pdf = Convert.ToBase64String(DocumentImport.ToPdf(data, p["kind"]?.GetValue<string>() ?? "image")) };
    }

    private static object EncryptAction(JsonObject p) => new
    {
        pdf = Convert.ToBase64String(Encryptor.Encrypt(Pdf(p),
            p["userPassword"]!.GetValue<string>(),
            p["ownerPassword"]?.GetValue<string>(), Password(p)))
    };

    private static object DecryptAction(JsonObject p) => new
    {
        pdf = Convert.ToBase64String(Encryptor.Decrypt(Pdf(p), p["password"]!.GetValue<string>()))
    };

    private static object WatermarkAction(JsonObject p)
    {
        var options = new WatermarkOptions(
            FontFamily: p["fontFamily"]?.GetValue<string>(),
            Bold: p["bold"]?.GetValue<bool>() ?? false,
            Italic: p["italic"]?.GetValue<bool>() ?? false,
            ColorHex: p[ColorKey]?.GetValue<string>(),
            Opacity: p["opacity"]?.GetValue<float>() ?? 0.3f,
            RotationDegrees: p["rotation"]?.GetValue<float>() ?? 45f,
            FontSize: p["fontSize"]?.GetValue<float>(),
            Pages: p["pages"]?.AsArray().Select(n => n!.GetValue<int>()).ToList());
        var result = WatermarkTool.AddTextWatermark(Pdf(p), p["text"]!.GetValue<string>(), options, Password(p));
        return new { pdf = Convert.ToBase64String(result.Pdf), warnings = result.Warnings };
    }

    private static object FlattenAction(JsonObject p)
    {
        var mode = (p["mode"]?.GetValue<string>() ?? "everything").ToLowerInvariant() switch
        {
            "forms" => FlattenTool.Mode.Forms,
            "annotations" => FlattenTool.Mode.AnnotationsOnly,
            _ => FlattenTool.Mode.Everything,
        };
        var result = FlattenTool.Flatten(Pdf(p), mode, Password(p));
        return new
        {
            pdf = Convert.ToBase64String(result.Pdf),
            formFields = result.FormFieldsFlattened,
            annotations = result.AnnotationsFlattened,
        };
    }

    private static object BatesAction(JsonObject p)
    {
        var options = new BatesOptions(
            Prefix: p["prefix"]?.GetValue<string>(),
            Suffix: p["suffix"]?.GetValue<string>(),
            Start: p["start"]?.GetValue<int>() ?? 1,
            Digits: p["digits"]?.GetValue<int>() ?? 6,
            Position: BatesTool.ParseCorner(p["position"]?.GetValue<string>()),
            FontSize: p["fontSize"]?.GetValue<float>() ?? 10f,
            ColorHex: p[ColorKey]?.GetValue<string>(),
            Pages: p["pages"]?.AsArray().Select(n => n!.GetValue<int>()).ToList());
        var result = BatesTool.AddBatesNumbers(Pdf(p), options, Password(p));
        return new { pdf = Convert.ToBase64String(result.Pdf), first = result.FirstLabel, last = result.LastLabel };
    }

    private static object SignImage(JsonObject p) => new
    {
        pdf = Convert.ToBase64String(Signer.AddImageSignature(Pdf(p),
            Region(p[RegionKey]!.AsObject()),
            Convert.FromBase64String(p["png"]!.GetValue<string>()), Password(p)))
    };

    private static object SignDigital(JsonObject p) => new
    {
        pdf = Convert.ToBase64String(Signer.SignDigitally(Pdf(p),
            Convert.FromBase64String(p["pfx"]!.GetValue<string>()),
            p["pfxPassword"]!.GetValue<string>(),
            p["reason"]?.GetValue<string>(),
            p["location"]?.GetValue<string>(),
            p[RegionKey] is JsonObject r ? Region(r) : null,
            p["appearancePng"] is JsonNode img ? Convert.FromBase64String(img.GetValue<string>()) : null,
            Password(p)))
    };

    private static object Signatures(JsonObject p) => new
    {
        signatures = Signer.GetSignatures(Pdf(p), Password(p)).Select(s => new
        {
            name = s.Name, signer = s.SignerName,
            valid = s.IntegrityValid, coversWholeDocument = s.CoversWholeDocument
        })
    };

    private static object CreateCert(JsonObject p) => new
    {
        pfx = Convert.ToBase64String(CertificateFactory.CreateSelfSignedPkcs12(
            p["name"]?.GetValue<string>() ?? "PDF Editor User",
            p["password"]!.GetValue<string>()))
    };

    // ------------------------------------------------------------- helpers

    private static byte[] Pdf(JsonObject p) =>
        Convert.FromBase64String(p["pdf"]?.GetValue<string>()
            ?? throw new InvalidDataException("Missing 'pdf'."));

    private static string? Password(JsonObject p) => p["pdfPassword"]?.GetValue<string>();

    private static RectRegion Region(JsonObject r) => new(
        r["page"]!.GetValue<int>(),
        r["x"]!.GetValue<float>(), r["y"]!.GetValue<float>(),
        r["width"]!.GetValue<float>(), r["height"]!.GetValue<float>());

    private static List<RectRegion> Regions(JsonArray arr) =>
        arr.Select(n => Region(n!.AsObject())).ToList();

    /// <summary>Wraps a result in a single frame, or splits it into chunk frames when large.</summary>
    private static IReadOnlyList<string> Frame(string id, bool ok, JsonNode? body)
    {
        var envelope = new JsonObject
        {
            ["id"] = id,
            ["ok"] = ok,
            ["result"] = body
        };
        string json = envelope.ToJsonString(JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) <= NativeMessaging.MaxOutgoingFrame)
            return new[] { json };

        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        const int chunkSize = 600_000;
        int count = (encoded.Length + chunkSize - 1) / chunkSize;
        var frames = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            var chunk = new JsonObject
            {
                ["id"] = id,
                ["chunkIndex"] = i,
                ["chunkCount"] = count,
                ["data"] = encoded.Substring(i * chunkSize, Math.Min(chunkSize, encoded.Length - i * chunkSize))
            };
            frames.Add(chunk.ToJsonString(JsonOptions));
        }
        return frames;
    }
}
