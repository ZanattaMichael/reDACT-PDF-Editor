# GitHub Issues Action Plan

This document reviews all 40 currently open issues in this repository and groups them
into a sequenced action plan. Issues are organized by theme, with a recommended
priority tier per group. Priority favors: (1) correctness bugs affecting core
functionality, (2) quick, high-impact fixes, (3) foundational work that unblocks
other enhancements, and (4) larger feature/quality investments.

_Generated 2026-07-28. Source: 40 open issues (#17–#56) on `zanattamichael/chromium-pdf-editor`._

## Progress

> **Superseded for planning purposes as of 2026-09-03.** This document remains the
> record of the original 40-issue triage and the reasoning behind it, but it is no
> longer the plan being worked. The backlog has grown to 62 open issues, most of them
> filed after this was written. See **[`docs/VERSION_3.0.0_PLAN.md`](docs/VERSION_3.0.0_PLAN.md)**
> for what is scheduled, what is blocked, and what is deferred. Of the original 40,
> twenty are still open and are deferred to 3.1 — they are the editing and rendering
> tail in Tiers 3–5 and 8–9 below.

**Tier 0 is complete.** #18 + #22 (PR #59), #20 + #21 (PR #68), #24 (PR #67) are all merged.

**Tier 2 is complete, and Tier 1 all but one.** #23 landed in PR #83 and #29 is closed;
#17 (forms menu redesign) is the one still open, and is deferred to 3.1. In Tier 2, #52
landed in PR #75, #54 in PR #76, #53 in PR #78, #19 in PR #79, and #56 is closed. The
safety net those produced — the export validator, the fuzz corpus and the golden-file
suite — is what later work asserts against, and the 3.0.0 plan's release gates use it
directly.

Issues raised by this work, not in the original 40: **#72** (bare `catch {}` hides
render failures), **#74** (ban interpolated `innerHTML` in `extension/src`), **#146**
(batch redaction from a configuration file, filed into Tier 6). A client-side XSS
found by the new code scanning was fixed in #73.

Two corrections to the sequencing below, learned in practice:

- **#52 should come before #53 and #54, not alongside them.** It produces the
  structural validator both of the others want as their assertion oracle.
- **"A corpus of tricky real-world PDFs" (#53) has to mean synthetic ones.** There is
  no network access in the working environment, and real-world files carry licensing
  questions. `tests/PdfEditor.Core.Tests/CorruptPdfs.cs` (from #52) and
  `tests/PdfEditor.Core.Tests/Fuzz/RawPdf.cs` (from #54) already construct malformed
  and awkward-but-valid documents byte by byte; #53 should build on those.

## Tier 0 — Critical correctness bugs (fix first)

These break core, everyday functionality and should be fixed before new features are
layered on top.

- **#18 — JavaScript is not being executed within documents**: Buttons/JS in PDFs are
  inert. Blocks #22 (form JS notifications) and #43 (form event/timing correctness).
- **#20 — JPEG documents break during OCR parsing**: Image becomes unviewable after
  OCR. Likely an image-decoding/re-encoding bug in the OCR pipeline.
- **#21 — OCR results are zoomed in**: Coordinate/scale mismatch between the OCR
  render pass and the display/overlay pass.
- **#24 — Links side menu doesn't refresh on new document load**: Stale state bug;
  likely a missing reset in the document-load lifecycle.
- **#22 — Form JavaScript notification doesn't notify**: Related to #18; once JS
  execution works, wire up the notification path.

**Recommended order:** #18 → #22 → #20 → #21 → #24 (the first two are related JS-engine
work; #20/#21 are both in the OCR image pipeline and can be fixed together; #24 is
independent and small).

## Tier 1 — High-impact UX bugs

- **#23 — Highlighting is still box-drawing, not click-and-drag**: UX regression vs.
  expected annotation behavior.
- **#17 — Forms Menu Redesign**: Modernize to an overlay toolbar (mockups provided in
  issue). Tagged bug+enhancement — treat as UI bug since current menu is described as
  broken/dated.
- **#29 — Text edits don't match existing font size/type**: Core editing fidelity bug,
  directly related to Tier 3 font work (#28, #34, #35).

## Tier 2 — Foundational/infrastructure (unblocks later work)

- **#56 — Resolve all SonarCube issues flagged in build pipeline**: Pure code-quality
  debt; do incrementally, but don't let it block feature work. Recommend splitting
  into sub-issues by rule category (bugs vs. code smells vs. security hotspots) and
  burning down in small PRs so review stays tractable.
- **#19 — Async link parser + loading indicator**: Performance fix that also makes the
  UI usable on link-heavy PDFs; pairs naturally with the #24 stale-state fix (same
  subsystem).
- **#52 — Export pipeline validation (post-export checks)**: A validation/diagnostics
  step that will make every other export-affecting change (redaction, flattening,
  watermarking, font handling) easier to verify and safer to ship.
- **#53 — Golden-file regression suite** and **#54 — Fuzz/regression testing for
  parser robustness**: Testing infrastructure. Stand these up early since nearly every
  other issue below (rendering, redaction, forms, fonts) benefits from a regression
  safety net before it's touched.

**Recommended order:** #52 and #53/#54 first (safety net), then #56 (quality) and #19.

## Tier 3 — Text & font fidelity

- **#28 — Expand text editing fonts**
- **#34 — Embedded font preservation mode**
- **#35 — Font diagnostics UI** (why-substituted tooltip)
- **#32 — Improved semantic text model** (ligatures, multi-run spans, rotated text)
- **#33 — Reflow/line wrapping inside edit boxes**
- **#31 — True caret/cursor editing**
- **#29** (Tier 1, but logically part of this group)

**Recommended order:** #29 → #28 → #34 → #35 → #32 → #33 → #31. Font-size/type
matching and font expansion are quick wins; the semantic text model (#32) is a
prerequisite for reliable reflow (#33) and caret editing (#31), which are the largest
efforts in the whole backlog.

## Tier 4 — Rendering & geometry correctness

- **#36 — Deterministic preview/export parity**
- **#37 — Transparency & blend mode preservation**
- **#38 — Clipping paths + crop box/rotation handling**
- **#39 — Deeper Form XObject support for editing**
- **#40 — Robust hit-testing on transformed/zoomed pages**
- **#45 — Accessibility/structure preservation (tagged PDF tree)**

These are interrelated rendering-engine correctness issues. Recommend tackling
**#36 first** (establishes a single source of truth for render parity/testing, which
makes the rest verifiable), then #38 and #40 together (geometry/hit-testing share
transform math), then #37 and #39, with #45 last since it depends on edits/redaction
not breaking structure once the others are stable.

## Tier 5 — Forms

- **#42 — Form field appearance regeneration across viewers**
- **#43 — Radio groups + event/timing correctness** (depends on #18 JS fix)
- **#44 — Flattening modes: granular controls**
- **#17** (Tier 1, forms menu UI, listed here for subsystem grouping)

**Recommended order:** #42 → #43 (needs #18) → #44.

## Tier 6 — Redaction & compliance

- **#48 — Auditable redaction report**
- **#49 — Redaction recovery-resistance guarantees (tests/verification)**
- **#51 — Undo/redo cross-operation atomicity**
- **#146 — Batch redaction across many documents from a configuration file**

Do #49 before shipping #48's "redaction complete" messaging — an audit report is only
trustworthy once recovery-resistance is verified. #51 (undo/redo atomicity) is
higher-risk correctness work that benefits from the regression suite (Tier 2) being in
place first.

#146 comes last in this tier, and deliberately so: it applies the same redaction to a
whole directory unattended, which multiplies whatever the single-document path gets
wrong. It needs #49's recovery-resistance verification first, and it should emit #48's
report per document rather than inventing a second reporting format. Four pieces are
missing before it can be built — regex matching in `TextTools` (with a bounded
evaluation, since the patterns arrive from a file), a `--batch` entry point beside
`HostDiagnostics.TryRunCli`, config parsing that names the offending rule when it
rejects one, and the multi-document orchestration itself.

## Tier 7 — Diff / review tooling

- **#46 — Visual (pixel) diff mode**
- **#47 — Diff UX: click hotspots to navigate to changes**

**Recommended order:** #46 → #47 (navigation needs the diff surface to exist first).

## Tier 8 — Performance & platform

- **#50 — Cancellation + progressive processing for long operations** (OCR, signing,
  large merges)

## Tier 9 — New capabilities (larger features, schedule after above)

- **#25 — Remove encryption from a document**
- **#26 — Open image files natively / combine images into a PDF via OCR**
- **#27 — Bates numbering**
- **#30 — Watermarking (tamper-resistant)**
- **#41 — Annotation appearance stream controls** (color/opacity/stroke/border/stamp)
- **#55 — HEIC image support** (pairs naturally with #26)

These are self-contained feature additions with few cross-dependencies; sequence by
business priority once Tiers 0–8 are stable. #55 should be scheduled alongside #26
since both touch the same image-import pipeline.

## Suggested execution order (summary)

1. **Stabilize**: #18, #22, #20, #21, #24, #23
2. **Safety net**: #52, #53, #54, #56, #19
3. **Text/font fidelity**: #29, #28, #34, #35, #32, #33, #31
4. **Rendering/geometry**: #36, #38, #40, #37, #39, #45
5. **Forms**: #42, #43, #44, #17
6. **Redaction/undo**: #49, #48, #51, #146
7. **Diff tooling**: #46, #47
8. **Performance**: #50
9. **New features**: #25, #26, #55, #27, #30, #41

## Notes on process

- Several issues (#56 SonarCube, #53/#54 testing) are best split into smaller
  sub-issues/PRs rather than resolved in one large change, to keep review scope
  manageable and avoid regressions.
- Tiers 3–7 share underlying subsystems (text model, rendering transforms, forms
  pipeline); within each tier the issues should ideally be picked up by whoever last
  touched that subsystem to minimize context-switching.
- No issues are currently duplicates of each other, though #17 and #23 both touch
  annotation/menu UX and could be scoped into a single "annotation toolbar" effort if
  desired.
