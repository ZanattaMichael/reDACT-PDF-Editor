using PdfEditor.Core.Pdf;
using PdfEditor.Core.Pdf.Fonts;

namespace PdfEditor.Core;

/// <summary>Reads, fills, and inserts AcroForm (Adobe form) fields.</summary>
public static class FormTools
{
    /// <summary>
    /// Inserts a fillable text field on the given page at the given rectangle. When
    /// <paramref name="multiline"/> is true the field accepts multiple lines (a comment/notes box).
    /// </summary>
    public static EditResult AddTextField(byte[] pdf, int page, RectRegion rect, string? name = null,
        string? value = null, string? password = null, bool multiline = false, string? script = null)
    {
        var doc = PdfIo.Open(pdf, password);
        var pdfPage = PageOrThrow(doc, page);
        var field = AcroForm.NewWidget(ToRect(rect), styled: true);
        field.Put(PdfName.FT, PdfName.Tx);
        field.Put(PdfName.DA, PdfString.FromText("/Helv 0 Tf 0 g"));
        if (multiline) field.Put(PdfName.Ff, new PdfNumber(AcroForm.FlagMultiline));
        var node = AcroForm.AddMergedField(doc, pdfPage, field, UniqueName(doc, name, "text"));
        AcroForm.SetValue(doc, node, value ?? "");
        AttachScript(node, script);
        return EditResult.Of(PdfIo.Save(doc));
    }

    /// <summary>
    /// Inserts a dropdown (combo box) choice field with the given selectable options. The first
    /// option is preselected; the user picks one when filling the form.
    /// </summary>
    public static EditResult AddDropdown(byte[] pdf, int page, RectRegion rect, string? name,
        IReadOnlyList<string> options, string? password = null, string? script = null)
    {
        var choices = options.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.Trim()).ToArray();
        if (choices.Length == 0)
            throw new ArgumentException("A dropdown needs at least one option.", nameof(options));

        var doc = PdfIo.Open(pdf, password);
        var pdfPage = PageOrThrow(doc, page);
        var field = AcroForm.NewWidget(ToRect(rect), styled: true);
        field.Put(PdfName.FT, PdfName.Ch);
        field.Put(PdfName.Ff, new PdfNumber(AcroForm.FlagCombo));
        field.Put(PdfName.DA, PdfString.FromText("/Helv 0 Tf 0 g"));
        field.Put(PdfName.Opt, new PdfArray(choices.Select(c => (PdfObject)PdfString.FromText(c))));
        var node = AcroForm.AddMergedField(doc, pdfPage, field, UniqueName(doc, name, "choice"));
        AcroForm.SetValue(doc, node, choices[0]);
        AttachScript(node, script);
        return EditResult.Of(PdfIo.Save(doc));
    }

    /// <summary>
    /// Inserts a radio-button ("option") group: one field with a button per option, stacked
    /// vertically inside the rectangle with each option's label drawn beside it. Exactly one option
    /// can be selected; the first is selected by default. Needs at least two options.
    /// </summary>
    public static EditResult AddRadioGroup(byte[] pdf, int page, RectRegion rect, string? name,
        IReadOnlyList<string> options, string? password = null, string? script = null)
    {
        var choices = options.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.Trim())
            .Distinct().ToArray();
        if (choices.Length < 2)
            throw new ArgumentException("A radio/option group needs at least two options.", nameof(options));

        var doc = PdfIo.Open(pdf, password);
        var pdfPage = PageOrThrow(doc, page);
        var form = AcroForm.GetOrCreate(doc);
        AcroForm.EnsureDefaultResources(doc, form);
        string groupName = UniqueName(doc, name, "radio");

        var group = doc.MakeIndirect(new PdfDictionary());
        group.Put(PdfName.FT, PdfName.Btn);
        group.Put(PdfName.Ff, new PdfNumber(AcroForm.FlagRadio | AcroForm.FlagNoToggleToOff));
        group.Put(PdfName.T, PdfString.FromText(groupName));
        var kids = new PdfArray();
        group.Put(PdfName.Kids, kids);

        const float box = 14f;
        float rowHeight = Math.Max(box + 4f, Math.Min(28f, rect.Height / choices.Length));
        float top = rect.Y + rect.Height;
        var font = PdfFont.Standard(StandardFonts.Helvetica);
        var fontName = PdfResources.Add(pdfPage.GetOrCreateResources(), PdfName.Font, "F", doc.MakeIndirect(font.Dictionary!));
        var labels = new ContentBuilder();
        var widgets = new List<PdfDictionary>();
        for (int i = 0; i < choices.Length; i++)
        {
            float y = top - (i + 1) * rowHeight + (rowHeight - box) / 2f;
            var widget = AcroForm.NewWidget(new PdfRect(rect.X, y, box, box), styled: true);
            widget.Put(PdfName.Parent, group);
            AcroForm.BuildRadioAppearances(doc, widget, choices[i]);
            pdfPage.AddAnnotation(widget);
            kids.Add(widget);
            widgets.Add(widget);
            labels.BeginText().Font(fontName, 11).MoveText(rect.X + box + 6, y + 2)
                .ShowText(font.Encode(choices[i])).EndText();
        }
        form.GetAsArray(PdfName.Fields)!.Add(group);
        var node = new FormFieldNode { Dictionary = group, Name = groupName, Widgets = widgets };
        AcroForm.SetValue(doc, node, choices[0]); // first option selected by default
        AttachScript(node, script);
        PdfContentGuard.DrawInDefaultUserSpace(pdfPage, labels.ToArray());
        return EditResult.Of(PdfIo.Save(doc));
    }

    /// <summary>Inserts a checkbox on the given page at the given rectangle.</summary>
    public static EditResult AddCheckbox(byte[] pdf, int page, RectRegion rect, string? name = null,
        bool isChecked = false, string? password = null, string? script = null)
    {
        var doc = PdfIo.Open(pdf, password);
        var pdfPage = PageOrThrow(doc, page);
        var field = AcroForm.NewWidget(ToRect(rect), styled: true);
        field.Put(PdfName.FT, PdfName.Btn);
        AcroForm.BuildCheckBoxAppearances(doc, field, "Yes");
        var node = AcroForm.AddMergedField(doc, pdfPage, field, UniqueName(doc, name, "check"));
        AcroForm.SetValue(doc, node, isChecked ? "Yes" : "Off");
        AttachScript(node, script);
        return EditResult.Of(PdfIo.Save(doc));
    }

    /// <summary>
    /// Inserts a clickable push button. When <paramref name="script"/> is set the button runs that
    /// JavaScript on activation (mouse-up) in Acrobat/Chrome — e.g. a "Submit" or "Calculate"
    /// button on a fillable form. The caption is the visible label.
    /// </summary>
    public static EditResult AddButton(byte[] pdf, int page, RectRegion rect, string? name = null,
        string? caption = null, string? script = null, string? password = null)
    {
        var doc = PdfIo.Open(pdf, password);
        var pdfPage = PageOrThrow(doc, page);
        var form = AcroForm.GetOrCreate(doc);
        string label = string.IsNullOrWhiteSpace(caption) ? "Button" : caption;
        var field = AcroForm.NewWidget(ToRect(rect), styled: true);
        field.Put(PdfName.FT, PdfName.Btn);
        field.Put(PdfName.Ff, new PdfNumber(AcroForm.FlagPushButton));
        field.GetAsDictionary(PdfName.MK)!.Put(PdfName.Of("CA"), PdfString.FromText(label));
        AcroForm.EnsureDefaultResources(doc, form);
        AcroForm.BuildButtonAppearance(doc, form, field, label);
        var node = AcroForm.AddMergedField(doc, pdfPage, field, UniqueName(doc, name, "button"));
        AttachScript(node, script, asActivation: true);
        return EditResult.Of(PdfIo.Save(doc));
    }

    private static PdfPage PageOrThrow(PdfDocument doc, int page)
    {
        if (page < 1 || page > doc.PageCount)
            throw new ArgumentOutOfRangeException(nameof(page), $"Page {page} does not exist.");
        return doc.GetPage(page);
    }

    private static PdfRect ToRect(RectRegion r) => new(r.X, r.Y, r.Width, r.Height);

    /// <summary>
    /// Attaches <paramref name="script"/> to every one of a field's widgets so it runs when the
    /// user activates that field in Acrobat/Chrome. A push button uses the widget's /A (activation)
    /// entry — the conventional place for a button's click script — while every other field type
    /// uses /AA /U (the annotation's mouse-up additional action), which is where readers look for
    /// "run this when the field is clicked/toggled". Radio groups get it on each option's widget.
    /// No-op for a null/empty script.
    /// </summary>
    private static void AttachScript(FormFieldNode field, string? script, bool asActivation = false)
    {
        if (string.IsNullOrEmpty(script)) return;
        foreach (var widget in field.Widgets)
        {
            if (asActivation)
            {
                widget.Put(PdfName.A, JavaScriptTool.JavaScriptAction(script));
                continue;
            }
            var additional = widget.GetAsDictionary(PdfName.AA) ?? new PdfDictionary();
            additional.Put(PdfName.U, JavaScriptTool.JavaScriptAction(script));
            widget.Put(PdfName.AA, additional);
        }
    }

    /// <summary>The page number (1-based) and rectangle of a field's first on-page widget.</summary>
    private static (int Page, PdfRect? Rect) WidgetLocation(PdfDocument doc, FormFieldNode field)
    {
        var widget = field.Widgets.FirstOrDefault();
        if (widget == null) return (0, null);
        return (AcroForm.PageOf(doc, widget), PdfRect.FromArray(widget.GetAsArray(PdfName.Rect)));
    }

    /// <summary>A field name that doesn't collide with an existing one.</summary>
    private static string UniqueName(PdfDocument doc, string? requested, string prefix)
    {
        var existing = AcroForm.AllNodes(doc).Select(n => n.Name).ToHashSet(StringComparer.Ordinal);
        string baseName = string.IsNullOrWhiteSpace(requested) ? prefix : requested.Trim();
        if (!existing.Contains(baseName)) return baseName;
        int i = 2;
        while (existing.Contains($"{baseName}_{i}")) i++;
        return $"{baseName}_{i}";
    }

    /// <summary>Lists every fillable field with its type, current value, and allowed options.</summary>
    public static IReadOnlyList<FormField> ListFields(byte[] pdf, string? password = null)
    {
        var doc = PdfIo.OpenReadOnly(pdf, password);
        var fields = new List<FormField>();
        foreach (var field in AcroForm.TerminalFields(doc))
        {
            string type = FieldType(field);
            if (type == "container") continue; // non-terminal parent — not directly fillable
            bool readOnly = (field.Flags & AcroForm.FlagReadOnly) != 0;
            var (page, rect) = WidgetLocation(doc, field);
            string? script = FieldScript(field);
            fields.Add(new FormField(field.Name, type, AcroForm.ValueAsString(field), Options(field), readOnly,
                page,
                rect?.X ?? 0, rect?.Y ?? 0, rect?.Width ?? 0, rect?.Height ?? 0,
                script));
        }
        return fields;
    }

    /// <summary>
    /// Sets the given field values (keyed by fully-qualified field name). Unknown names are
    /// ignored. When <paramref name="flatten"/> is true the form is flattened afterwards so the
    /// values become static page content that can no longer be edited.
    /// </summary>
    public static EditResult FillFields(byte[] pdf, IReadOnlyDictionary<string, string> values,
        bool flatten = false, string? password = null)
    {
        var doc = PdfIo.Open(pdf, password);
        if (AcroForm.Get(doc) != null)
        {
            var all = AcroForm.TerminalFields(doc).GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.First());
            foreach (var (name, value) in values)
                if (all.TryGetValue(name, out var field)) AcroForm.SetValue(doc, field, value);
            if (flatten) AcroForm.Flatten(doc);
        }
        return EditResult.Of(PdfIo.Save(doc));
    }

    private static string FieldType(FormFieldNode field)
    {
        switch (field.FieldType)
        {
            case null: return "container";
            case "Tx": return "text";
            case "Ch": return "choice";
            case "Sig": return "signature";
            case "Btn":
                int flags = field.Flags;
                if ((flags & AcroForm.FlagPushButton) != 0) return "button";
                return (flags & AcroForm.FlagRadio) != 0 ? "radio" : "checkbox";
            default: return "text";
        }
    }

    /// <summary>
    /// The JavaScript source attached to a field's widget activation — its /A entry (how push
    /// buttons carry a click script), falling back to the widget's /AA /U mouse-up entry (how every
    /// other field type carries one). The viewer surfaces this so an activation can, for the common
    /// calculation/visibility patterns, be simulated locally; see extension/src/formScript.js.
    /// Returns null for a field with no script.
    /// </summary>
    private static string? FieldScript(FormFieldNode field)
    {
        foreach (var widget in field.Widgets)
        {
            string? script = JsActionText(widget.Get(PdfName.A))
                ?? JsActionText(widget.GetAsDictionary(PdfName.AA)?.Get(PdfName.U));
            if (script != null) return script;
        }
        return null;
    }

    /// <summary>Reads the /JS text out of an action dictionary if it's a JavaScript action.</summary>
    private static string? JsActionText(PdfObject? action)
    {
        if (action is not PdfDictionary a) return null;
        if (a.GetAsName(PdfName.S)?.Equals(PdfName.JavaScript) != true) return null;
        return a.Get(PdfName.JS) switch
        {
            PdfString s => s.ToUnicodeString(),
            PdfStream st => System.Text.Encoding.UTF8.GetString(st.GetDecodedBytes()),
            _ => null
        };
    }

    /// <summary>Allowed values: choice /Opt entries, or a checkbox/radio's appearance states.</summary>
    private static IReadOnlyList<string> Options(FormFieldNode field)
    {
        if (AcroForm.Inherited(field.Dictionary, PdfName.Opt) is PdfArray opts)
        {
            var list = new List<string>();
            foreach (var entry in opts)
            {
                if (entry is PdfString s) list.Add(s.ToUnicodeString());
                else if (entry is PdfArray pair && pair.Count > 1 && pair.Get(1) is PdfString disp)
                    list.Add(disp.ToUnicodeString());
            }
            return list;
        }

        if (field.FieldType == "Btn")
        {
            var states = field.Widgets.SelectMany(AcroForm.AppearanceStates).Distinct().ToList();
            if (states.Count > 0) return states;
        }
        return Array.Empty<string>();
    }
}
