using System.Text;
using PdfEditor.Core.Pdf.Fonts;

namespace PdfEditor.Core.Pdf;

/// <summary>A form field as the field tree defines it (§12.7.4): a terminal node and its widgets.</summary>
internal sealed class FormFieldNode
{
    public required PdfDictionary Dictionary { get; init; }

    /// <summary>The fully qualified name (parent.child).</summary>
    public required string Name { get; init; }

    /// <summary>The widget annotations that show this field.</summary>
    public required List<PdfDictionary> Widgets { get; init; }

    /// <summary>/FT, inherited; null for a node that is not a field (a pure container).</summary>
    public string? FieldType => (AcroForm.Inherited(Dictionary, PdfName.FT) as PdfName)?.Value;

    public int Flags => (AcroForm.Inherited(Dictionary, PdfName.Ff) as PdfNumber)?.IntValue() ?? 0;

    public PdfObject? Value => AcroForm.Inherited(Dictionary, PdfName.V);
}

/// <summary>
/// Reads and writes interactive forms: enumerating fields, setting values (and regenerating the
/// appearance every viewer draws), building new fields, and flattening fields into page content.
/// </summary>
internal static class AcroForm
{
    public const int FlagReadOnly = 1;
    public const int FlagMultiline = 1 << 12;
    public const int FlagNoToggleToOff = 1 << 14;
    public const int FlagRadio = 1 << 15;
    public const int FlagPushButton = 1 << 16;
    public const int FlagCombo = 1 << 17;

    /// <summary>A light fill so an empty field is a visible box on the page.</summary>
    public static readonly PdfColor FieldFill = PdfColor.Rgb(240, 244, 250);

    /// <summary>The border colour new fields are drawn with.</summary>
    public static readonly PdfColor FieldBorder = PdfColor.Rgb(128, 128, 128);

    private const string DefaultFontName = "Helv";

    /// <summary>A field attribute, looked up through /Parent as §12.7.3.1 specifies.</summary>
    public static PdfObject? Inherited(PdfDictionary field, PdfName key)
    {
        var node = field;
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        for (int depth = 0; node != null && depth < 64 && seen.Add(node); depth++)
        {
            if (node.Get(key) is { } value) return value;
            node = node.GetAsDictionary(PdfName.Parent);
        }
        return null;
    }

    public static PdfDictionary? Get(PdfDocument doc) => doc.Catalog?.GetAsDictionary(PdfName.AcroForm);

    /// <summary>The document's form, created (empty) when it has none.</summary>
    public static PdfDictionary GetOrCreate(PdfDocument doc)
    {
        var catalog = doc.Catalog!;
        if (catalog.GetAsDictionary(PdfName.AcroForm) is { } existing)
        {
            if (existing.GetAsArray(PdfName.Fields) == null) existing.Put(PdfName.Fields, new PdfArray());
            return existing;
        }
        var form = doc.MakeIndirect(new PdfDictionary());
        form.Put(PdfName.Fields, new PdfArray());
        catalog.Put(PdfName.AcroForm, form);
        return form;
    }

    /// <summary>Every node of the field tree (terminal or not) with its qualified name.</summary>
    public static List<(string Name, PdfDictionary Node)> AllNodes(PdfDocument doc)
    {
        var result = new List<(string, PdfDictionary)>();
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var fields = Get(doc)?.GetAsArray(PdfName.Fields);
        if (fields == null) return result;
        foreach (var f in fields)
            if (f is PdfDictionary d) Walk(d, null, 0);
        return result;

        void Walk(PdfDictionary node, string? parentName, int depth)
        {
            if (depth > 64 || !seen.Add(node)) return;
            string? partial = node.GetText(PdfName.T);
            if (partial == null && parentName != null) return; // a widget, not a field
            string name = parentName == null ? partial ?? "" : partial == null ? parentName : parentName + "." + partial;
            result.Add((name, node));
            if (node.GetAsArray(PdfName.Kids) is { } kids)
                foreach (var kid in kids)
                    if (kid is PdfDictionary k && k.ContainsKey(PdfName.T)) Walk(k, name, depth + 1);
        }
    }

    /// <summary>The terminal fields (those that own widgets), in tree order.</summary>
    public static List<FormFieldNode> TerminalFields(PdfDocument doc)
    {
        var result = new List<FormFieldNode>();
        foreach (var (name, node) in AllNodes(doc))
        {
            var kids = node.GetAsArray(PdfName.Kids);
            bool hasFieldKids = kids?.OfType<PdfDictionary>().Any(k => k.ContainsKey(PdfName.T)) == true;
            if (hasFieldKids) continue;
            var widgets = kids?.OfType<PdfDictionary>().Where(k => !k.ContainsKey(PdfName.T)).ToList() ?? new List<PdfDictionary>();
            if (widgets.Count == 0 && (node.Is(PdfName.Widget, PdfName.Subtype) || node.ContainsKey(PdfName.Rect)))
                widgets.Add(node);
            result.Add(new FormFieldNode { Dictionary = node, Name = name, Widgets = widgets });
        }
        return result;
    }

    /// <summary>The 1-based page a widget sits on (by /P, else by searching /Annots); 0 when on none.</summary>
    public static int PageOf(PdfDocument doc, PdfDictionary widget)
    {
        if (widget.GetAsDictionary(PdfName.P) is { } p)
            foreach (var page in doc.Pages)
                if (ReferenceEquals(page.Dictionary, p)) return page.Number;
        foreach (var page in doc.Pages)
            if (page.Dictionary.GetAsArray(PdfName.Annots)?.Any(a => ReferenceEquals(a, widget)) == true)
                return page.Number;
        return 0;
    }

    /// <summary>The value as text: a string's contents, a name's value, a choice list's first entry.</summary>
    public static string ValueAsString(FormFieldNode field) => field.Value switch
    {
        PdfString s => s.ToUnicodeString(),
        PdfName n => n.Value,
        PdfArray a => a.Get(0) switch { PdfString s => s.ToUnicodeString(), PdfName n => n.Value, _ => "" },
        PdfStream st => Encoding.UTF8.GetString(st.GetDecodedBytes()),
        _ => "",
    };

    /// <summary>The names of a widget's appearance states (e.g. "Yes" and "Off").</summary>
    public static List<string> AppearanceStates(PdfDictionary widget)
    {
        var states = new List<string>();
        var ap = widget.GetAsDictionary(PdfName.AP);
        foreach (var key in new[] { PdfName.N, PdfName.Of("D") })
            if (ap?.Get(key) is PdfDictionary dict && dict is not PdfStream)
                foreach (var state in dict.Keys)
                    if (!states.Contains(state.Value)) states.Add(state.Value);
        return states;
    }

    // ------------------------------------------------------------------ values

    /// <summary>Sets a field's value and regenerates what its widgets show.</summary>
    public static void SetValue(PdfDocument doc, FormFieldNode field, string value)
    {
        var form = GetOrCreate(doc);
        switch (field.FieldType)
        {
            case "Btn" when (field.Flags & FlagPushButton) != 0:
                return; // a push button has no value
            case "Btn":
            {
                // A check box or radio button: the value is the name of the "on" appearance state.
                var state = PdfName.Of(value);
                field.Dictionary.Put(PdfName.V, state);
                foreach (var widget in field.Widgets)
                {
                    bool has = AppearanceStates(widget).Contains(value);
                    widget.Put(PdfName.AS, has && value != "Off" ? state : PdfName.Off);
                }
                return;
            }
            default:
                field.Dictionary.Put(PdfName.V, PdfString.FromText(value));
                foreach (var widget in field.Widgets) RegenerateText(doc, form, field, widget);
                return;
        }
    }

    /// <summary>Builds the normal appearance of a text or choice field's widget from its value.</summary>
    public static void RegenerateText(PdfDocument doc, PdfDictionary form, FormFieldNode field, PdfDictionary widget)
    {
        var rect = PdfRect.FromArray(widget.GetAsArray(PdfName.Rect)) ?? new PdfRect(0, 0, 0, 0);
        float w = rect.Width, h = rect.Height;
        string text = ValueAsString(field);
        bool multiline = field.FieldType == "Tx" && (field.Flags & FlagMultiline) != 0;

        var (fontName, fontSize, font) = AppearanceFont(doc, form, field);
        var canvas = new ContentBuilder();
        DrawBackgroundAndBorder(canvas, widget, w, h);
        canvas.Raw("/Tx BMC\n").SaveState();
        canvas.Rectangle(1, 1, Math.Max(0, w - 2), Math.Max(0, h - 2)).Clip().EndPath();
        canvas.BeginText().FillGray(0);
        if (multiline)
        {
            float size = fontSize > 0 ? fontSize : 12;
            canvas.Font(fontName, size);
            double y = h - 2 - font.Ascent / 1000.0 * size;
            canvas.MoveText(2, y);
            bool first = true;
            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            {
                if (!first) canvas.MoveText(0, -size * 1.15);
                canvas.ShowText(font.Encode(line));
                first = false;
            }
        }
        else
        {
            float size = fontSize > 0 ? fontSize : AutoSize(font, text, w, h);
            canvas.Font(fontName, size);
            // Centre the line vertically, as viewers do for single-line fields.
            double y = (h - (font.Ascent - font.Descent) / 1000.0 * size) / 2 - font.Descent / 1000.0 * size;
            canvas.MoveText(2, y).ShowText(font.Encode(text));
        }
        canvas.EndText().RestoreState().Raw("EMC\n");

        var resources = new PdfDictionary();
        var fonts = new PdfDictionary();
        fonts.Put(fontName, form.GetAsDictionary(PdfName.DR)?.GetAsDictionary(PdfName.Font)?.Get(fontName) ?? font.Dictionary!);
        resources.Put(PdfName.Font, fonts);
        SetNormalAppearance(doc, widget, Appearance(doc, w, h, canvas.ToArray(), resources));
    }

    private static float AutoSize(PdfFont font, string text, float w, float h)
    {
        float size = Math.Clamp((h - 4) / ((font.Ascent - font.Descent) / 1000f), 4f, 12f);
        float width = (float)font.MeasureText(text, size);
        if (width > w - 4 && width > 0) size = Math.Max(4f, size * (w - 4) / width);
        return size;
    }

    /// <summary>The font a field's /DA names (resolved through the form's /DR), falling back to Helvetica.</summary>
    private static (PdfName Name, float Size, PdfFont Font) AppearanceFont(PdfDocument doc, PdfDictionary form, FormFieldNode field)
    {
        string da = (Inherited(field.Dictionary, PdfName.DA) as PdfString)?.ToUnicodeString()
            ?? form.GetText(PdfName.DA) ?? $"/{DefaultFontName} 0 Tf 0 g";
        PdfName name = PdfName.Of(DefaultFontName);
        float size = 0;
        var ops = ContentParser.Parse(Encoding.Latin1.GetBytes(da));
        foreach (var op in ops)
            if (op.Operator == "Tf" && op.Operands.Count >= 2 && op.Operands[^2] is PdfName n && op.Operands[^1] is PdfNumber s)
            {
                name = n;
                size = s.FloatValue();
            }
        var dict = EnsureDefaultResources(doc, form).GetAsDictionary(PdfName.Font)?.GetAsDictionary(name);
        if (dict == null)
        {
            name = PdfName.Of(DefaultFontName);
            dict = EnsureDefaultResources(doc, form).GetAsDictionary(PdfName.Font)!.GetAsDictionary(name)!;
        }
        return (name, size, PdfFont.Load(dict));
    }

    /// <summary>The form's default resources, with Helvetica as /Helv (what /DA strings conventionally name).</summary>
    public static PdfDictionary EnsureDefaultResources(PdfDocument doc, PdfDictionary form)
    {
        var dr = form.GetAsDictionary(PdfName.DR);
        if (dr == null)
        {
            dr = new PdfDictionary();
            form.Put(PdfName.DR, dr);
        }
        var fonts = dr.GetAsDictionary(PdfName.Font);
        if (fonts == null)
        {
            fonts = new PdfDictionary();
            dr.Put(PdfName.Font, fonts);
        }
        if (fonts.Get(PdfName.Of(DefaultFontName)) is not PdfDictionary)
            fonts.Put(PdfName.Of(DefaultFontName), doc.MakeIndirect(PdfFont.Standard(StandardFonts.Helvetica).Dictionary!));
        return dr;
    }

    private static void DrawBackgroundAndBorder(ContentBuilder canvas, PdfDictionary widget, float w, float h)
    {
        var mk = widget.GetAsDictionary(PdfName.MK);
        if (ColorOf(mk?.GetAsArray(PdfName.BG)) is { } bg)
            canvas.FillColor(bg).Rectangle(0, 0, w, h).Fill();
        if (ColorOf(mk?.GetAsArray(PdfName.BC)) is { } bc)
        {
            double bw = widget.GetAsDictionary(PdfName.BS)?.GetAsDouble(PdfName.W) ?? 1;
            if (bw > 0)
                canvas.StrokeColor(bc).LineWidth(bw).Rectangle(bw / 2, bw / 2, Math.Max(0, w - bw), Math.Max(0, h - bw)).Stroke();
        }
    }

    private static PdfColor? ColorOf(PdfArray? array) => array?.Count switch
    {
        1 => PdfColor.Gray(array.GetNumber(0)),
        3 => new PdfColor(array.GetNumber(0), array.GetNumber(1), array.GetNumber(2)),
        4 => new PdfColor((1 - array.GetNumber(0)) * (1 - array.GetNumber(3)), (1 - array.GetNumber(1)) * (1 - array.GetNumber(3)),
            (1 - array.GetNumber(2)) * (1 - array.GetNumber(3))),
        _ => null,
    };

    private static PdfStream Appearance(PdfDocument doc, float w, float h, byte[] content, PdfDictionary? resources)
    {
        var stream = new PdfStream(content);
        stream.Put(PdfName.Type, PdfName.XObject);
        stream.Put(PdfName.Subtype, PdfName.Form);
        stream.Put(PdfName.BBox, new PdfArray(0, 0, w, h));
        if (resources != null) stream.Put(PdfName.Resources, resources);
        return doc.MakeIndirect(stream);
    }

    private static void SetNormalAppearance(PdfDocument doc, PdfDictionary widget, PdfObject normal)
    {
        var ap = new PdfDictionary();
        ap.Put(PdfName.N, normal);
        widget.Put(PdfName.AP, ap);
    }

    // ------------------------------------------------------------------ building fields

    /// <summary>A new widget annotation dictionary at <paramref name="rect"/> on <paramref name="page"/>.</summary>
    public static PdfDictionary NewWidget(PdfRect rect, bool styled)
    {
        var widget = new PdfDictionary();
        widget.Put(PdfName.Type, PdfName.Annot);
        widget.Put(PdfName.Subtype, PdfName.Widget);
        widget.Put(PdfName.Rect, rect.ToArray());
        widget.Put(PdfName.F, new PdfNumber(4)); // print
        if (styled)
        {
            var mk = new PdfDictionary();
            mk.Put(PdfName.BG, new PdfArray(FieldFill.R, FieldFill.G, FieldFill.B));
            mk.Put(PdfName.BC, new PdfArray(FieldBorder.R, FieldBorder.G, FieldBorder.B));
            widget.Put(PdfName.MK, mk);
            var bs = new PdfDictionary();
            bs.Put(PdfName.W, new PdfNumber(1));
            bs.Put(PdfName.S, PdfName.Of("S"));
            widget.Put(PdfName.BS, bs);
        }
        return widget;
    }

    /// <summary>Registers a field (with its widget merged into it) on the page and in the form.</summary>
    public static FormFieldNode AddMergedField(PdfDocument doc, PdfPage page, PdfDictionary fieldAndWidget, string name)
    {
        var form = GetOrCreate(doc);
        EnsureDefaultResources(doc, form);
        fieldAndWidget.Put(PdfName.T, PdfString.FromText(name));
        page.AddAnnotation(fieldAndWidget);
        form.GetAsArray(PdfName.Fields)!.Add(fieldAndWidget);
        return new FormFieldNode { Dictionary = fieldAndWidget, Name = name, Widgets = new List<PdfDictionary> { fieldAndWidget } };
    }

    /// <summary>Check-box appearances: a check mark in the "Yes" state, the empty box in "Off".</summary>
    public static void BuildCheckBoxAppearances(PdfDocument doc, PdfDictionary widget, string onState)
    {
        var rect = PdfRect.FromArray(widget.GetAsArray(PdfName.Rect))!.Value;
        float w = rect.Width, h = rect.Height;
        var off = new ContentBuilder();
        DrawBackgroundAndBorder(off, widget, w, h);
        var on = new ContentBuilder();
        DrawBackgroundAndBorder(on, widget, w, h);
        // A check mark drawn as a path, so no symbol font is needed to show it.
        on.SaveState().StrokeGray(0).LineWidth(Math.Max(1, Math.Min(w, h) * 0.12)).LineCap(1).LineJoin(1)
            .MoveTo(w * 0.2, h * 0.52).LineTo(w * 0.42, h * 0.25).LineTo(w * 0.8, h * 0.78).Stroke().RestoreState();
        var states = new PdfDictionary();
        states.Put(PdfName.Of(onState), Appearance(doc, w, h, on.ToArray(), null));
        states.Put(PdfName.Off, Appearance(doc, w, h, off.ToArray(), null));
        var ap = new PdfDictionary();
        ap.Put(PdfName.N, states);
        widget.Put(PdfName.AP, ap);
    }

    /// <summary>Radio-button appearances: a filled dot in the option's own state, an empty circle in "Off".</summary>
    public static void BuildRadioAppearances(PdfDocument doc, PdfDictionary widget, string onState)
    {
        var rect = PdfRect.FromArray(widget.GetAsArray(PdfName.Rect))!.Value;
        float w = rect.Width, h = rect.Height;
        double r = Math.Min(w, h) / 2;
        ContentBuilder Ring()
        {
            var c = new ContentBuilder();
            var mk = widget.GetAsDictionary(PdfName.MK);
            if (ColorOf(mk?.GetAsArray(PdfName.BG)) is { } bg) c.FillColor(bg).Circle(w / 2, h / 2, r - 0.5).Fill();
            if (ColorOf(mk?.GetAsArray(PdfName.BC)) is { } bc) c.StrokeColor(bc).LineWidth(1).Circle(w / 2, h / 2, r - 0.5).Stroke();
            return c;
        }
        var off = Ring();
        var on = Ring();
        on.FillGray(0).Circle(w / 2, h / 2, r * 0.45).Fill();
        var states = new PdfDictionary();
        states.Put(PdfName.Of(onState), Appearance(doc, w, h, on.ToArray(), null));
        states.Put(PdfName.Off, Appearance(doc, w, h, off.ToArray(), null));
        var ap = new PdfDictionary();
        ap.Put(PdfName.N, states);
        widget.Put(PdfName.AP, ap);
    }

    /// <summary>A push button's appearance: its box with the caption centred in it.</summary>
    public static void BuildButtonAppearance(PdfDocument doc, PdfDictionary form, PdfDictionary widget, string caption)
    {
        var rect = PdfRect.FromArray(widget.GetAsArray(PdfName.Rect))!.Value;
        float w = rect.Width, h = rect.Height;
        var dr = EnsureDefaultResources(doc, form);
        var fontDict = dr.GetAsDictionary(PdfName.Font)!.GetAsDictionary(PdfName.Of(DefaultFontName))!;
        var font = PdfFont.Load(fontDict);
        float size = AutoSize(font, caption, w, h);
        double textWidth = font.MeasureText(caption, size);
        var canvas = new ContentBuilder();
        DrawBackgroundAndBorder(canvas, widget, w, h);
        double y = (h - (font.Ascent - font.Descent) / 1000.0 * size) / 2 - font.Descent / 1000.0 * size;
        canvas.BeginText().FillGray(0).Font(PdfName.Of(DefaultFontName), size)
            .MoveText(Math.Max(1, (w - textWidth) / 2), y).ShowText(font.Encode(caption)).EndText();
        var resources = new PdfDictionary();
        var fonts = new PdfDictionary();
        fonts.Put(PdfName.Of(DefaultFontName), fontDict);
        resources.Put(PdfName.Font, fonts);
        SetNormalAppearance(doc, widget, Appearance(doc, w, h, canvas.ToArray(), resources));
    }

    // ------------------------------------------------------------------ flattening

    /// <summary>
    /// Draws every field's current appearance into its page and removes the form: the widgets
    /// disappear from /Annots and the AcroForm from the catalog. Returns the number of fields.
    /// </summary>
    public static int Flatten(PdfDocument doc)
    {
        var form = Get(doc);
        if (form == null) return 0;
        var fields = TerminalFields(doc);
        var drawings = new Dictionary<PdfPage, ContentBuilder>();
        foreach (var field in fields)
        {
            foreach (var widget in field.Widgets)
            {
                int pageNumber = PageOf(doc, widget);
                if (pageNumber == 0) continue;
                var page = doc.GetPage(pageNumber);
                bool hidden = ((widget.GetAsInt(PdfName.F) ?? 0) & 2) != 0;
                if (!hidden && NormalAppearance(widget) is { } appearance
                    && PdfRect.FromArray(widget.GetAsArray(PdfName.Rect)) is { } rect)
                {
                    if (!drawings.TryGetValue(page, out var canvas)) drawings[page] = canvas = new ContentBuilder();
                    DrawFitted(canvas, page, appearance, rect);
                }
                page.RemoveAnnotation(widget);
            }
        }
        foreach (var (page, canvas) in drawings)
            PdfContentGuard.DrawInDefaultUserSpace(page, canvas.ToArray());
        doc.Catalog!.Remove(PdfName.AcroForm);
        return fields.Count;
    }

    /// <summary>A widget's normal appearance stream, choosing the /AS state when there are several.</summary>
    public static PdfStream? NormalAppearance(PdfDictionary annotation)
    {
        var normal = annotation.GetAsDictionary(PdfName.AP)?.Get(PdfName.N);
        if (normal is PdfStream stream) return stream;
        if (normal is PdfDictionary states && annotation.GetAsName(PdfName.AS) is { } state)
            return states.GetAsStream(state);
        return null;
    }

    /// <summary>
    /// Draws an appearance stream so that its bounding box (as transformed by its own /Matrix)
    /// fills <paramref name="rect"/> — the placement rule of §12.5.5.
    /// </summary>
    public static void DrawFitted(ContentBuilder canvas, PdfPage page, PdfStream appearance, PdfRect rect)
    {
        var bbox = PdfRect.FromArray(appearance.GetAsArray(PdfName.BBox)) ?? new PdfRect(0, 0, rect.Width, rect.Height);
        var box = Matrix.FromArray(appearance.GetAsArray(PdfName.Matrix)).TransformRect(bbox);
        if (box.Width <= 0 || box.Height <= 0) return;
        double sx = rect.Width / box.Width, sy = rect.Height / box.Height;
        if (appearance.Get(PdfName.Subtype) == null) appearance.Put(PdfName.Subtype, PdfName.Form);
        if (appearance.Get(PdfName.Type) == null) appearance.Put(PdfName.Type, PdfName.XObject);
        var name = PdfResources.Add(page.GetOrCreateResources(), PdfName.XObject, "Fm", appearance);
        canvas.SaveState()
            .Transform(sx, 0, 0, sy, rect.Left - box.Left * sx, rect.Bottom - box.Bottom * sy)
            .DrawXObject(name)
            .RestoreState();
    }
}
