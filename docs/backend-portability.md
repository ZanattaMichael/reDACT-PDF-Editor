# PDF backend portability: iText → OfficeIMO

Tracks **#115** (should the engine change) and **#116** (the abstraction seam).

This document has been through two investigation passes:

1. A README/NuGet-only pass (recorded in the [#115 comment
   thread](https://github.com/ZanattaMichael/reDACT-PDF-Editor/issues/115#issuecomment-5233486742)),
   which could not reach OfficeIMO's source and flagged content-stream redaction as an
   unresolved 🔴 risk.
2. A source-level pass (this document), which cloned
   [`EvotecIT/OfficeIMO`](https://github.com/EvotecIT/OfficeIMO) at `master`
   (commit `aba60b7b`, 2026‑09‑03) and read the redaction and text-editing
   implementations line by line, verified the published NuGet dependency graph, and
   measured PDF text-object granularity empirically against real Microsoft Word output.

**The second pass reversed the first pass's own initial optimism.** An earlier draft of
this file rated the content-stream work "🟡→🟢 by evidence of scale" — inferring
capability from the size and naming of OfficeIMO's redaction files rather than from
reading the algorithm. Reading the algorithm gives a different answer, recorded below.

## Update 2 (2026-09-16) — it shipped, and the picture splits in two

The previous update said the fixes were real but unpublished, so the verdict was
*"blocked pending a release."* **That release exists.** `OfficeIMO.Pdf` **3.4.0** shipped
on NuGet as release `OfficeIMO-v20260906153413` and is the first published version
containing the glyph rewriter; three more have followed (**3.4.1**, **3.4.2**, **3.4.3** =
`OfficeIMO-v20260912151212`, 2026-09-12). Confirmed against `api.nuget.org`'s flat
container index, and by mapping each release tag to its `VersionPrefix`.

For the record, since the owner asked specifically about
`OfficeIMO-v20260902190744`: **that release does not contain any of this.** It is
3.3.0, tagged at `bd9e881f`, and `PdfContentStreamTextRewriter.cs` does not exist in
that tree — `git cat-file -e` reports the path "exists on disk, but not in" the tag. The
glyph commit `6a384161` landed 2026-09-03, one day *after* it. What that release did
carry, and which is separately interesting for #106, is `#2415` — easy local OCR and
searchable-PDF support.

So the timing question is answered. What replaces it is a **scope** question, because
re-reading the shipped code at 3.4.3 shows the fix is narrower than "OfficeIMO now does
glyph-level text surgery":

> `PdfContentStreamTextRewriter.TryRemoveIntersectingGlyphs` has **exactly one caller**
> in the entire library — `PdfRedactionApplier.TextScrubbing.cs:302`.

**Redaction got the glyph rewriter. Text editing did not.** `PdfTextEditor` still routes
through `RemoveTextPreservingUnmatchedSpans` → delete-and-re-stamp, and still resolves the
re-stamped font through `ResolveStandardFont`, which maps every input to one of the 14
standard fonts by substring-matching the base font name (`"bold"`, `"times"`, `"courier"`
… else Helvetica). There is no embedded-font stamping path in `PdfStamper`. Anything
OfficeIMO *writes* is Helvetica, Times or Courier.

Two things soften that, and one does not:

- **Softens it:** `RemoveTextPreservingUnmatchedSpans` calls
  `PdfRedactionApplier.RemoveTextInAreas`, which *does* go through the rewriter. So
  surviving neighbours now mostly survive in the content stream rather than being deleted
  and redrawn. The collateral re-stamp set should be near-empty in the common case, firing
  only where the rewriter fails closed (`ActualText` spans, vertical writing, encodings it
  cannot split). Font substitution went from the default path to the fallback path.
- **Softens it:** invisible-text handling is now a first-class targeting property
  (`PdfRedactionArea.WithTextRenderingMode`), and there is a concealed-content API
  (`OfficeContentConcealmentKind.InvisibleRenderingMode` / `TransparentText` /
  `ClippedContent`) that did not exist before.
- **Does not soften it:** text the editor *adds* — `Text.Replace`, `Text.Add`,
  `Text.Move` — is always newly stamped, and always through `ResolveStandardFont`. Editing
  a word set in embedded Calibri returns Helvetica. That is #29 exactly, and
  `tests/PdfEditor.Core.Tests/TextFontFidelityTests.cs` guards against it.

### What this means for the licence goal

A partial migration does not achieve it — that has been the through-line of this document
from the start, and it still holds. The question is now much narrower than it was:

| Surface | Shipped state at 3.4.3 | Migratable? |
|---|---|---|
| Redaction content removal | Glyph-granular, width-compensated `TJ`, original encoding and font resources retained, whole-object removal as fail-closed fallback | **Yes** |
| Redaction evidence | `ApplyWithEvidence` → source/output SHA-256, three-state per-item outcome, per-check switches | **Yes** — an upgrade |
| Everything else (merge, pages, forms, encryption, signing, JS, Bates, watermark, Word import) | Mapped 🟢 below, unchanged by this update | **Yes** |
| **Text editing fidelity** | Standard-font substitution on every stamp | **No** — regresses #29 |

One feature now stands between this project and dropping iText. That is a materially
different position from "blocked at the core."

### The sharpened upstream ask

The old ask — make `PdfContentStreamInterpreter` / `TextContentParser` public — is moot
*for redaction*, which is now reachable through the public `PdfDocumentRedactions.Apply` /
`ApplyWithEvidence` facade despite both types remaining `internal`. It is **not** moot for
text editing; it is precisely what a caller would need to build glyph-preserving editing
outside the library.

But there is a better-shaped request, and it should be filed instead: **route
`PdfTextEditor` through `PdfContentStreamTextRewriter` as well, and give `PdfStamper` a way
to stamp in an existing embedded font.** The machinery already exists, has one caller, and
was written by the maintainer in the last fortnight. That is a "please extend this to the
adjacent path" request rather than an architecture argument — the kind most likely to be
accepted, and the only remaining thing standing between this project and an AGPL-free
dependency graph.

---

## Update 1 — the blocking finding was fixed in then-unreleased `master`

_Superseded by Update 2 above on the release-status question; retained because the
code-reading is still accurate._

Everything below analyses `OfficeIMO.Pdf` **3.3.0** (release `OfficeIMO-v20260902190744`,
commit `bd9e881f`). That analysis stands for that package.

At the time this was written, `master` had picked up ~33 commits that fix the core blocker:

- **Glyph-level redaction** — new `PdfContentStreamTextRewriter.cs` (commit `6a384161`,
  "Preserve unaffected PDF glyphs during redaction"). `TrySplitGlyphs` splits encoded
  strings per glyph, tests each against the rectangle, keeps survivors **in their
  original encoding** (`kept.AddRange(glyph.Bytes)` — so no standard-font substitution)
  and flushes removed glyphs as numeric `TJ` displacements. That is the same technique
  as our `ContentStreamEditor`, and it removes both the whole-line blast radius and the
  font-fidelity regression described below. Backed by ~1,050 lines of new tests.
- **Font width providers** — the P0 bug is fixed: `SumWidth1000` now resolves real
  per-font providers via `ResourceResolver.GetFontWidthProviders(...)`, threaded through
  the whole call chain, so applier geometry matches the read model.
- **Invisible-text opt-in** (`allowTextRenderingMode3`), **scoped canvas blend modes**,
  **typed action sanitization**, and **combined before-sharing sanitization** also
  landed — see [`officeimo-issues-to-file.md`](officeimo-issues-to-file.md).

**None of this was published at the time of writing** — latest NuGet was then 3.3.0 — so
the verdict moved from *"blocked at the core"* to *"blocked pending a release."*
**Update 2 supersedes this:** the release shipped as 3.4.0 on 2026-09-06, and the
remaining gap is text-editing font fidelity rather than redaction granularity.

One caveat cuts the other way: redaction internals were substantially rewritten inside
24 hours. That responsiveness is a real asset, but it reinforces the churn note at the
bottom of this document — pin an exact version and re-run our own assertions on every
bump.

## Headline finding (as shipped in 3.3.0)

**The migration is clean for roughly 80% of the surface and blocked at the core.**

The licensing motivation is real and OfficeIMO delivers on it: verified from the
published `.nuspec` files, `OfficeIMO.Pdf` 3.3.0 depends on exactly one package
(`OfficeIMO.Core` 3.3.0), which itself declares **zero** dependencies on every target
framework. No transitive AGPL, no ImageSharp-style split license, nothing. Page
operations, merge/split, forms, encryption, signatures, JavaScript/action handling,
Bates numbering, watermarks, and Word→PDF import all map cleanly and in several cases
are an upgrade on what this project does today.

But `OfficeIMO.Pdf` redacts and edits text at **whole-text-object (`BT`…`ET`)
granularity**, where this project's `ContentStreamEditor` operates at **glyph**
granularity. That is an architectural difference, not a maturity gap, and it lands
squarely on this application's two differentiating features. Because a hybrid that
keeps iText for redaction still ships iText, a partial migration does **not** achieve
the AGPL goal — which is exactly the trap the original #115 comment warned about.

## Method

- Catalogued every `using iText.*` in `src/PdfEditor.Core` (28 of 40 files).
- Read OfficeIMO's actual redaction/editing implementation, not its documentation:
  `PdfRedactionApplier.TextScrubbing.cs`, `PdfRedactionApplier.cs`,
  `PdfRedactionPlan.cs`, `PdfRedactionVerification.Residue.cs`, `PdfTextEditor.cs`,
  `PdfDocumentRedactions.cs`, `PdfRedactionApplyOptions.cs`.
- **Verified licensing from the published artifacts**, not the README: downloaded
  `OfficeIMO.Pdf.3.3.0.nupkg` and `OfficeIMO.Core.3.3.0.nupkg` from nuget.org and read
  their `.nuspec` dependency groups directly.
- **Measured text-object granularity empirically.** Wrote a content-stream analyzer
  (decompress streams → enumerate `BT`…`ET` blocks → count characters, show-operators,
  and distinct baselines per block) and ran it against this repo's
  `docs/sample/PDF-Editor-Sample.pdf` and against real Microsoft Word–produced PDFs
  shipped as reference baselines in OfficeIMO's own test corpus.
- **Did not** compile or execute anything against OfficeIMO. There is no `dotnet` CLI
  in this container and the egress proxy rejects `builds.dotnet.microsoft.com`, so the
  SDK could not be installed. Findings below are from source reading plus static
  analysis of PDF bytes — strong evidence, but not a passing test.

## Licensing — verified clean ✅

```
OfficeIMO.Pdf 3.3.0  ──depends on──>  OfficeIMO.Core 3.3.0  ──depends on──>  (nothing)
```

Read from the packages' own `.nuspec` files. MIT license expression on both. The
optional `OfficeIMO.Security` package (CMS/RFC 3161/X.509 for certificate signing) is
**not** a transitive dependency — a build that doesn't sign never pulls it in. This is
a genuine, verified escape from iText's AGPL terms, and it is the strongest argument
in favour of the migration.

## The blocking finding: redaction granularity

### What this project does today

`ContentStreamEditor` subclasses iText's `PdfCanvasProcessor` and intercepts the
show-text operators (`Tj`/`TJ`/`'`/`"`). For a text run that only *partially* overlaps
a redaction rectangle, it splits at the **glyph** boundary: glyphs outside the region
are re-emitted unchanged, glyphs inside are replaced by an equivalent-width `TJ`
displacement so the surviving text keeps its original spacing and does not reflow
(`ContentStreamEditor.cs`, `HandleShowText`/`DisplacementFor`).

### What OfficeIMO does

`PdfRedactionApplier.TextScrubbing.cs` works one level up, on whole text objects:

- `BuildRedactionTextObject` computes **a single union bounding box for the entire
  `BT`…`ET` block** (`AddSpanBounds` takes `Math.Min`/`Math.Max` across every span in
  the object).
- `MarkMatchingTextObjects` → `IntersectsTarget` tests the redaction rectangle against
  that union box.
- On any intersection, `RemoveTextObjectSpans` **excises the entire `BT`→`ET` byte
  range** from the content stream. There is no re-emission of surviving glyphs and no
  spacing compensation.
- Nothing in `PdfRedactionApplyOptions` controls this granularity (its knobs are fill
  color, unmatched-area painting, image policy, path removal, and document-level
  cleanup scope).

### How much text that actually destroys

Measured, not assumed. One `BT`…`ET` block per line is the dominant pattern in real
output:

| Document | Text objects | Largest object |
|---|---|---|
| `docs/sample/PDF-Editor-Sample.pdf` (this repo's own sample) | 93 | 98 chars, ~1 baseline |
| `microsoft-word-16.109-native-word-report.pdf` | 73 | 89 chars, ~1 baseline |
| `microsoft-word-windows-word-business-delivery-summary.pdf` | ~136 per stream | 105 chars, ~1 baseline |

So the practical blast radius is **one whole line of text per redaction box**. A user
who draws a box over `John Smith` in the line

> Prepared by John Smith on 14 March for internal review

loses the entire line from the content stream, while the painted black box still covers
only the rectangle they drew.

### The mitigation, and why it doesn't fully rescue it

The text-editing path is smarter about this. `PdfTextEditor.RemoveTextPreservingUnmatchedSpans`
snapshots spans before, removes the whole text objects, re-reads the result, diffs which
*non-targeted* spans disappeared as collateral, and **re-stamps them back onto the
page**. That is how the README's "preserves unmatched source-span text" claim is
honoured — by delete-and-redraw, not by surgical retention.

Three consequences for this project:

1. **Font fidelity regresses.** The re-stamp resolves style through
   `ResolveStandardFont(...)` — the closest **standard PDF font** — and emits
   `BuildSubstitutionWarnings`. Text originally set in an embedded Calibri comes back
   as Helvetica. This is precisely the defect issue #29 was filed for and that
   `tests/PdfEditor.Core.Tests/TextFontFidelityTests.cs` now guards against.
2. **OCR'd scans fail closed.** `IsSafelyEditableSpan` requires
   `span.IsVisible && !span.ClipPath.HasValue && span.TextRenderingMode == 0 && ...`,
   and the editor throws `NotSupportedException` otherwise. A searchable scan's OCR
   layer is invisible text (`Tr 3`) by definition — so the text-edit path rejects the
   exact documents `OcrTool.MakeSearchable` produces and that
   `ContentKinds.TextAndPixelsBeneath` was built to handle. (The pure-redaction path
   does not throw; it removes the objects.)
3. **`Plan()` and `Apply()` use different geometry engines.** The planner
   (`PdfRedactionPlanner.cs:79`) walks `document.TextBlocks` from the logical read
   model, which uses real font metrics. The applier parses the raw content stream and
   approximates every glyph advance as a flat half-em —
   `SumWidth1000 = bytes.Length * 500D` (`PdfRedactionApplier.TextScrubbing.cs:525`).
   Preview and effect are therefore computed two different ways, which matters a great
   deal for a tool whose compliance story is "here is exactly what was removed" (#48).
   Under-estimated widths are also the one path by which this design could *under*-redact
   rather than over-redact; worth explicit testing if the migration proceeds.

### On the verification API

`Redactions.Verify`/`AssertVerified` is real and useful, but it is **marker-based**:
`ContainsEncodedPdfMarker`/`ContainsDecodedStreamMarker` search the output bytes for
strings the caller names. It proves "the string I told you about is gone." It is not a
geometric proof that a rectangle is free of extractable content, so it complements
rather than replaces this project's own assertions.

## Capability mapping

Organized by the seams #116 proposes. 🟢 direct equivalent verified in source ·
🟡 exists, needs a fidelity check · 🔴 architectural gap.

### `IPdfDocumentStore` — 🟢 clean port

| Today | OfficeIMO.Pdf |
|---|---|
| `PdfIo.Open`/`OpenReadOnly` | `PdfDocument.Load(...)`, `Save`/`SaveAsync` |
| `Merger.Merge` | `MergeWith`/`MergeWithReport`, per-source passwords and permission policy |
| `PageTools.Arrange` | `Pages.Extract`/`Split`/`Delete`/`Duplicate`/`Move` with range selectors |
| `PageTools.Rotate` | `Pages.Rotate(degrees, ranges)` |
| `DocumentImport.ImageToPdf` | `PdfDocument.Create` + image content block |
| `DocumentImport.DocxToPdf` (spawns LibreOffice) | `WordDocument.Load(...).SaveAsPdf(...)` in-process — **removes the external `soffice` dependency entirely** |

### `IInspector` — 🟢/🟡

| Today | OfficeIMO.Pdf | Status |
|---|---|---|
| `PdfInspector.GetInfo` | `Inspect()` page geometry/count | 🟢 |
| `Encryptor.IsEncrypted`/`CanOpen` | `PdfLoadOptions.Password`, `PdfDocumentSecurityInfo` | 🟢 |
| `ExportValidator.Validate` | `Analyze(...)` + `PdfRepairReport` | 🟡 different finding shape; needs an adapter |
| `PdfStructureGuard`/`PdfContentGuard` | Parser-level bounded limits (`PdfReadLimits`: max operations, nesting depth, operands, decoded bytes) | 🟢 likely **deletable** — becomes the library's job |
| `Sanitizer.Inspect` | No single equivalent; composable from `Inspect()`/`JavaScript.List()`/`Attachments`/`Annotations`/`Bookmarks` | 🟡 |

### `ITextEditor` / `IContentStreamEditor` — 🔴 the blocker

| Today | OfficeIMO.Pdf | Status |
|---|---|---|
| `TextTools.GetTextInRegion`/`GetTextSpans` | `Text.Inspect(region)`, public `PdfReadPage.GetTextSpans()` → `PdfTextSpan` with position/font/size/transform | 🟢 |
| `TextTools.FindText` | `Text.Find(...)` with case/whole-word filters | 🟢 |
| `TextTools.AddText` | `Text.Add(region, text, PdfTextEditOptions)` | 🟢 |
| `TextTools.ReplaceTextInRegion`, `MoveText` | `Text.Replace`/`Add` — but via whole-text-object removal + re-stamp in a **substituted standard font**; throws on invisible/clipped text | 🔴 regresses #29 font fidelity; rejects OCR'd scans |
| **`ContentStreamEditor`** (per-glyph split, width-compensated `TJ`, form-XObject recursion, inline images) | Whole-`BT`…`ET` excision against a union bbox with flat half-em width estimates | 🔴 **architectural gap** |
| `ImageTools.MoveImage` | `Images.Move(placement, dx, dy)` | 🟢 |
| `ImageScrubber.TryScrubPixels` | `PdfRedactionApplier.Images.PixelRewrite.cs`; JPEG/masked/indexed cases need a caller-supplied `IPdfRedactionImageDecoder` or fail closed | 🟡 |

### `IRedactionEngine` — 🔴 core, 🟢 periphery

| Today | OfficeIMO.Pdf | Status |
|---|---|---|
| `Redactor.Redact` content removal | `Redactions.Apply(areas, options)` — correct security posture (errs toward removing more), wrong granularity for an interactive editor | 🔴 |
| `RedactionBox.Prepare`/`Expand` | `Redactions.Plan(areas)` non-mutating preview | 🟢 — better than today; no dry-run exists now |
| `RedactionReporter.Analyze` | `PdfRedactionMatch` carries `Text`, `Kind`, `ImagePlacement`, page/rect | 🟡 — rebuildable, but plan/apply geometry mismatch (above) undermines "exactly what was removed" |
| *(nothing today)* | `Redactions.Verify`/`AssertVerified` marker-based residue proof | 🟢 net-new, worth adopting regardless |

### `IAnnotationEngine` / `IFormEngine` / `ISecurityEngine` / `ISigningEngine` — 🟢/🟡 clean

| Today | OfficeIMO.Pdf | Status |
|---|---|---|
| `HighlightTool` (`Multiply` blend) | `Stamp.Content` canvas; blend mode used internally, not clearly public | 🟡 upstream draft #1 |
| `InkTools`, `WatermarkTool` | `Stamp.Content` shapes/drawings; `Stamp.TextWatermark`, `PdfOptions.TextWatermark` | 🟢 |
| `BatesTool` | `PdfBatesNumberer` with prefix/digits/position — a first-class feature | 🟢 |
| `FormTools` add/list/fill | `Forms.Edit(...)`/`Forms.FillAndFlatten(...)`; per-field JS on non-button fields unconfirmed | 🟢/🟡 upstream draft #2 |
| `FlattenTool` 3 modes | `Forms.FillAndFlatten` + `Annotations.Flatten` | 🟢 |
| `Encryptor` | `PdfStandardEncryptionOptions` (AES-256/128, RC4, permissions) | 🟢 |
| `JavaScriptTool` | `JavaScript.List()`/`.Edit(...)` — near 1:1 | 🟢 |
| `PdfSafety`, `UrlTools` | Sanitizer + `Links`; action-subtype granularity unconfirmed | 🟡 upstream drafts #3/#4 |
| `Signer.SignDigitally`/`GetSignatures` | `Security.SignExternal(...)`, `ValidateSignatures(...)` → `PdfSignatureValidationReport` | 🟢 — resolves the earlier 🔴 |
| `CertificateFactory` | No iText dependency today; unaffected | 🟢 no work |

### Unaffected

`PageRenderer`/`VisualDiff` (PDFium + SkiaSharp), `DocComparer` (LCS over extracted
text), `OcrTool` (Tesseract; but see the invisible-text finding), `UrlClassifier`.

## Revised recommendation

1. **Phase 0 — build the seam (#116). Unchanged, and still worth doing on its own.**
   It isolates the iText surface, makes the tools testable against fakes, and is the
   only way to migrate the 🟢 majority without a big-bang rewrite.
2. ~~**Don't migrate against 3.3.0 — and don't build against `master` either.**~~
   **Superseded.** The fixes are published: pin `OfficeIMO.Pdf` **3.4.3** (or later) for
   any spike. 3.3.0 does still have the whole-line blast radius, the width-provider bug
   and the OCR rejection — do not evaluate against it.
3. **Run the spike, now, against 3.4.3.** It is no longer hypothetical and it is the
   thing that turns this argument into a number. Redact a word mid-line and assert the
   rest of the line survives *in its original font*; redact a partially-overlapping run
   and assert no reflow; redact on an OCR'd searchable scan; confirm `Plan()` and
   `Apply()` agree on geometry. Add one case this document did not previously call for:
   **edit** a word set in an embedded font and assert what comes back — that is the case
   expected to fail, and the size of that failure is now the deciding number.
4. **File the sharpened ask** — route `PdfTextEditor` through
   `PdfContentStreamTextRewriter`, and support stamping in an existing embedded font —
   alongside the per-field form JavaScript question, which is still outstanding
   ([`docs/officeimo-issues-to-file.md`](officeimo-issues-to-file.md)).
5. **Adopt one idea regardless of the outcome:** a post-redaction residue assertion in
   the spirit of `Redactions.Verify`. This project makes a removal guarantee (#48) and
   currently has no independent post-hoc check that the bytes are actually gone.

## Caveats

- Source reading and PDF byte analysis, not execution. The granularity finding is
  structural and I consider it solid (it follows from the shape of the algorithm, not
  from a judgement call about quality), but the *severity* estimates — how often a
  producer emits multi-line text objects, whether the half-em width approximation ever
  causes under-redaction — deserve a compiled test before anyone acts on them.
- The body of this document describes `OfficeIMO.Pdf` 3.3.0 / `master` at `aba60b7b`;
  Update 2 describes the shipped 3.4.3 (`OfficeIMO-v20260912151212`). This is a
  fast-moving library (3.2.x → 3.3.0 moved several public engine classes to `internal`
  per its `MIGRATION.md`); re-check before relying on any specific API shape.
- The flip side of that churn is a maintenance consideration this project should weigh
  independently of features: a security-critical removal guarantee resting on a young,
  rapidly-changing library with a small maintainer team is a different risk profile
  from iText's two decades of hardening — even though iText's licence is the reason
  this exercise exists at all.
