# Issues to submit to EvotecIT/OfficeIMO

> **Status: 5 of 7 drafts are obsolete; two are live — Issue 5 and the new Issue 8.**
>
> Between the review that produced this list and now, OfficeIMO's `master` picked up
> ~33 commits that independently fix or address six of the seven items. Filing them
> would be reporting work that's already done. Only **Issue 5** below is still worth
> submitting.
>
> Nothing here was ever filed, and there's no evidence these fixes relate to this
> analysis — they're the maintainer's own work landing on a repo with very high commit
> velocity. Verified at `origin/master` (`819a0516`); the release these were written
> against, `OfficeIMO-v20260902190744` (= commit `bd9e881f` = NuGet **3.3.0**),
> contains none of them.

## Current state of each draft

| # | Original title | Status on `master` | Evidence |
|---|---|---|---|
| 1 | Redaction bounds computed without font width providers | ✅ **Fixed** | `SumWidth1000` now resolves `fontWidthProviders` from `ResourceResolver.GetFontWidthProviders(...)`, threaded through `CollectTextObjects` → `BuildRedactionTextObject` → `ParseTextSpans` |
| 2 | Sub-text-object redaction granularity | ✅ **Fixed for redaction only** | New `PdfContentStreamTextRewriter.cs` (610 lines), commit `6a384161` "Preserve unaffected PDF glyphs during redaction". Shipped in 3.4.0. **Text editing still does not use it** — see Issue 8 |
| 3 | Text editing rejects invisible text (OCR'd scans) | ✅ **Addressed** | `IsSafelyEditableSpan(span, allowTextRenderingMode3)` opt-in; commit `e5eee161` "Support opt-in PDF OCR text operations" |
| 4 | Blend mode on `Stamp.Content` canvas | ✅ **Addressed** | Commit `d88ae3b3` "Add scoped PDF canvas blend modes" — `PdfPageCanvas.cs` + `PdfDocumentCanvasTests.cs` |
| 5 | Per-field JavaScript on non-button form fields | ❌ **Not addressed** | No matching commit — **still worth filing** |
| 6 | Selectable outward-action kinds when sanitizing | ✅ **Addressed** | Commit `522dc06a` "Add typed PDF action sanitization" — new `PdfSanitizationActionCounts.cs` |
| 7 | Combined "hidden data" inspect + sanitize | ✅ **Addressed** | Commit `00e109ff` "Add combined PDF before-sharing sanitization" — new `PdfSanitizationCategoryCounts.cs` |

**Update 2026-09-16: they shipped.** `OfficeIMO.Pdf` **3.4.0** (release
`OfficeIMO-v20260906153413`) is the first published version carrying the glyph rewriter;
**3.4.3** is current. The "unreleased" caveat that used to sit here is spent. Still
validate against our own fixtures rather than the commit messages or this table.

Draft #2's *alternative* ask — making `TextContentParser` / `PdfContentStreamInterpreter`
public — was **not** done; they remain `internal`. That is moot for **redaction**, which
reaches the glyph rewriter through the public `PdfDocumentRedactions.Apply` facade. It is
**not** moot for text editing — see Issue 8 below, which is the new draft this update adds.

Draft #2 is therefore only *half* fixed, and the table above overstates it:
`PdfContentStreamTextRewriter.TryRemoveIntersectingGlyphs` has exactly one caller in the
library (`PdfRedactionApplier.TextScrubbing.cs:302`). `PdfTextEditor` never touches it.

---

## Issue 5 — Per-field JavaScript activation on non-button AcroForm fields

**One of two drafts still worth submitting.**

**Labels:** question, enhancement

**Title:** `Setting and reading per-field JavaScript activation on non-button form fields`

**Body:**

Evaluating `OfficeIMO.Pdf`'s form APIs as a replacement for a tool that lets a user
attach a JavaScript snippet to any inserted form field (text, checkbox, combo, radio
group, push button) so it runs when the field is activated in Acrobat/Chrome — and later
lists that script back for display and editing.

The convention we currently follow: a push button's script goes on its widget's `/A`
(activation) entry; every other field type's goes on the widget's `/AA` dictionary's
`/U` (mouse-up) entry. Radio groups get it on each option's widget.

The README shows `JavaScript` as a `PdfFormFieldCreateOptions` property, demonstrated
only on a `PushButton`:

```csharp
.Create(new PdfFormFieldCreateOptions {
    Name = "calculate",
    Kind = PdfFormFieldCreationKind.PushButton,
    ...
    JavaScript = "this.getField('total').value = 42;"
})
```

Two questions:

1. Does `JavaScript` write to `/AA /U` when `Kind` is
   `Text`/`CheckBox`/`Combo`/`List`/`RadioGroup`, rather than only to a button's `/A`?
2. Is there a way to **read back** an existing field's activation script — an equivalent
   of "the JS attached to this field's widget", checking `/A` and `/AA /U` as
   appropriate for the field type?

If neither exists today, would they be in scope? Happy to describe the exact
widget-type → action-slot mapping we're trying to match.

*(From reading source rather than running it — no .NET SDK in the environment this was
written in — so apologies if there's already a supported route.)*

---

## Issue 8 — Reuse the glyph rewriter for text editing, and stamp in embedded fonts

**New, and the highest-value ask on this list.** This is the one remaining item standing
between this project and dropping its AGPL dependency.

**Labels:** enhancement

**Title:** `Preserve embedded fonts when editing text, as redaction now does`

**Body:**

`PdfContentStreamTextRewriter.TryRemoveIntersectingGlyphs` (added in #2435, shipped in
3.4.0) solved partial-overlap text removal beautifully — surviving glyphs keep their
original encoded bytes and font resources, and removed ones become width-compensating `TJ`
displacements. It is exactly the right technique.

It appears to have exactly one caller: `PdfRedactionApplier.TextScrubbing.cs`.
`PdfTextEditor` still goes through `RemoveTextPreservingUnmatchedSpans`, which removes and
re-stamps, and `ResolveStandardFont` maps the re-stamped text onto one of the 14 standard
fonts by substring-matching the base font name. The practical effect is that editing a
word set in an embedded Calibri returns Helvetica, with a substitution warning.

Since `RemoveTextPreservingUnmatchedSpans` now calls `RemoveTextInAreas`, the *collateral*
case is largely handled already — untargeted neighbours survive in the stream instead of
being redrawn. What remains is the text the editor writes itself (`Text.Replace`,
`Text.Add`, `Text.Move`).

Two questions:

1. Is there appetite for routing `PdfTextEditor`'s removal side through
   `PdfContentStreamTextRewriter`, so an edit that only touches part of a text object
   does not have to reconstruct the rest?
2. Is there any supported way to stamp text in a font **already embedded in the
   document** — reusing the existing font resource and encoding rather than resolving to a
   standard font? `PdfStamper` doesn't appear to expose one. If not, would it be in scope?

Context: we're evaluating `OfficeIMO.Pdf` as a replacement for iText in an open-source PDF
editor, largely to escape AGPL. Redaction now maps cleanly onto 3.4.x. Font fidelity on
in-place text edits is the one capability we'd regress on, and we have a regression test
that would fail.

Failing (2), making `PdfContentStreamInterpreter` / `TextContentParser` public would let
callers build this themselves — but extending the existing rewriter seems much the better
outcome, for the same reason it was the better outcome for #2435.

*(From reading source rather than running it — no .NET SDK in this environment — so
apologies if there's already a supported route.)*

---

## Appendix: the superseded drafts

The full text of drafts 1, 2, 3, 4, 6 and 7 is preserved in this file's git history
(see the commit that added them, prior to this revision) should any of them need
reviving — for example if a shipped release turns out not to cover the case, or if
validation against our fixtures shows a fix is incomplete.
