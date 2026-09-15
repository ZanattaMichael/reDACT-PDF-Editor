# reDACT 3.0.0 — Release Plan

_Drafted 2026-09-03 against the 62 open issues (#17–#154) and `main` at `e0ad96d`._

## The one-sentence version

**3.0.0 is the release where redaction stops being one document at a time, and where the
report stops describing intent and starts proving removal.**

Everything else in the release is either what that requires (a rule format, a headless
entry point, an upgrade path for the host) or what it makes unsafe to leave undone
(binary trust, documentation for a surface with no UI).

## Where we are starting from

- **Shipped:** 2.0.6 (2026-09-02). `main` is at RC `v2.0.7-63`. Published to the Chrome
  Web Store; Edge Add-ons listing in certification.
- **Open issues: 62.** They partition almost cleanly:

  | Cluster | Count | Issues | In 3.0.0? |
  | --- | --- | --- | --- |
  | PDF-engine migration | 24 | #115, #116, #125–#145, #151 | **No** — two issues only (see A) |
  | Batch redaction | 6 | #106, #146–#150 | **Yes** — the headline |
  | Redaction evidence & UX | 3 | #49, #107, #108 | **Yes** |
  | Trust & delivery | 4 | #105, #110, #111, #152 | **Yes** — forced by the major bump |
  | Docs & process | 4 | #119, #120, #153, #154 | #119/#120 yes; #153/#154 close now |
  | Extension architecture | 1 | #81 | **Yes**, conditionally (see D) |
  | Editing/rendering long tail | 20 | #17, #28–#45, #50, #51, #55, #66 | **No** — 3.1 |

- **In flight:** PR #165 (draft) — the #115 investigation, documentation only.

## Why this is a major, and not 2.1

Four contract changes, any one of which is a minor bump; together they are a major:

1. **The redaction report changes what it claims.** Today it reports what was *under* the
   boxes, computed from the original. #108 makes it report what is *verifiably absent from
   the output*, per region, with a pass/fail status. Anyone parsing the report JSON sees a
   new shape and a stronger claim.
2. **A new public surface with its own versioned format.** `--batch` (#148) and the rule
   configuration file (#149, `"version": 1`) are an interface the project has to keep
   working, invoked by scripts and schedulers rather than by a person.
3. **`TextMatchMode` gains `Regex`** (#147) — a host-action contract change that reaches
   `find-text` and the redaction path.
4. **Every 2.x host becomes incompatible, visibly.** `checkHostVersion` compares
   `major.minor` only (`extension/src/host-version.js`), so a 3.0 extension reports
   `HOST_OLDER` against every host a user currently has installed. That banner is correct —
   the batch actions genuinely are not there — but it means **the major bump strands every
   existing installation until the host is upgraded.** This is the reason workstream E is
   release-blocking rather than nice-to-have.

Point 4 is the one that is easy to miss and expensive to discover at release time. The
extension auto-updates from the store; the host is an OS package a person installs by hand.
On the day 3.0.0 publishes, every user gets a warning and has to go and do something. If
there is no low-friction way to do that thing, the release looks broken.

## What 3.0.0 is deliberately *not*

**It is not the licensing release** — but the reason has changed, and the decision is now
genuinely open. 24 of the 62 open issues are the iText → OfficeIMO migration.

PR #165 parked that cluster on a measured finding: OfficeIMO's `PdfRedactionApplier`
excised an entire text object's byte range on any intersection with the redaction
rectangle, where this project's `ContentStreamEditor` splits at the glyph boundary. Since
most producers emit one text object per line, redacting one word would have removed the
line.

**That finding is stale as of OfficeIMO `v20260906153413` (2026-09-06).** Upstream commit
`6a38416`, "Preserve unaffected PDF glyphs during redaction" (#2435, 2026-09-03, +3,339
lines across 30 files), added `PdfContentStreamTextRewriter.TryRemoveIntersectingGlyphs`:
per-glyph excision that re-emits surviving glyphs with width-compensating `TJ`
displacements and retains the original encoded bytes, font resources, text state and
text-object structure. Whole-object removal is now only the fail-closed fallback for
mappings it cannot prove safe. The planner intersects at character level through the same
shared `PdfTextSpanGeometry`, and both paths resolve widths through
`ResourceResolver.GetFontWidthProviders`. Each of PR #165's four technical objections is
answered; see the audit recorded on #115.

So the blocking *technical* reason is gone. What remains is a scheduling judgement, and it
is the one this plan makes: **3.0.0 is already carrying four contract changes, and a PDF
backend swap is not a fifth.** The migration is a 3.1-or-later programme with its own
release, its own golden-file baseline and its own risk budget — not a passenger on a
release whose thesis is batch redaction and provable removal.

**Consequence for the release notes: 3.0.0 stays GPL-3.0 with an AGPL dependency.** Do not
imply otherwise, and do not describe the engine cluster as blocked — it is deferred, which
is a different word with a different meaning to anyone reading the tracker.

Two pieces of it are still worth doing now, on their own merits — see workstream A.

---

## Workstreams

### A — Settle the engine question and stop paying interest on it

Non-negotiable first, because it is what stops 24 open issues from reading like committed
work, and because #116 is a refactor that every later workstream benefits from.

| Issue | Work | Notes |
| --- | --- | --- |
| #115 | **Re-open and re-measure before merging PR #165.** Its granularity finding no longer holds against OfficeIMO `v20260906153413`+. Amend the PR to record the upstream change, then decide the cluster on scheduling grounds rather than capability grounds. | The upstream ask is now partly moot: `PdfContentStreamInterpreter`/`TextContentParser` are still `internal`, but glyph-granular redaction no longer requires them — it is reachable through the public `PdfDocumentRedactions.Apply`/`ApplyWithEvidence`. Do not file the ask as written. |
| #127 | Add an explicit `BouncyCastle.Cryptography` `PackageReference` inside `[2.7.0, 3.0.0)` | Safe no-op today; today it arrives only as an `itext.bouncy-castle-adapter` transitive, so `CertificateFactory` breaks the moment iText is dropped. Do it while it costs nothing. |
| #116 | The capability seam — 9 interfaces, one PR each | Worth doing without any migration: it isolates the 28 iText-using files, makes the tools testable against fakes, and documents what PDF capabilities the app actually relies on. Enforce with a `check-innerhtml.mjs`-style guard: no `using iText.*` outside the implementation folder. |
| #125 | **Run the trip-rate measurement in 3.0.0.** The upstream change it was waiting on has landed. | Measuring now against `OfficeIMO.Pdf` 3.4.3 is what turns the 3.1 migration decision from an argument into a number. Cheap, and it uses the #53 golden-file corpus that already exists. |
| #126, #128–#145, #151 | Label `deferred:3.1` (not `blocked:upstream`) and add one comment each pointing at the #115 decision | They are good analysis and should not be closed. They should also not sit in a 3.0.0 milestone. Nothing upstream blocks them any more, so the old label would misreport the tracker. |

**Acceptance:** #115 has a recorded decision with a named unblocking condition; #116 and
#127 merged; the other 21 carry a blocking label and no milestone.

### B — Redaction evidence: make the report proof

The batch feature in C is only worth having if a person can walk away from it. That trust
comes from here, so B lands before C is finished.

| Issue | Work |
| --- | --- |
| #49 | Recovery-resistance verification: tests that assert removed content cannot be recovered from the output by text extraction, content-stream inspection, image re-decode, or object-graph traversal. |
| #108 | The report gains a verification section: per region, a **✓ verified removed / ✗ still present** status derived from re-reading the *saved output*, plus a plain-language record of the actions the engine took. The run **fails loudly** if anything survives. |
| #147 | `Regex` on `TextMatchMode`, evaluated against the same extracted text runs the literal modes walk, producing `RedactionBox` geometry through the existing path. |

**#147's bound is a security requirement, not a nicety.** The patterns arrive from a file
and run unattended over a directory. Use `RegexOptions.NonBacktracking` where the pattern
permits and a hard `matchTimeout` regardless; reject at parse time (#149) any pattern that
cannot be constructed under those options, naming it. A catastrophic-backtracking pattern in
a batch run is a hang with no operator watching.

**Borrow the shape of this from upstream, even though we are not adopting the engine.**
`OfficeIMO.Pdf` 3.4.3 ships a worked version of exactly this contract, and it is a better
starting point than a blank page:

- `PdfRedactionEvidenceReport` binds a reviewed plan to a `SourceSha256` and an
  `OutputSha256`, so the evidence names the exact bytes in and the exact bytes out. #108's
  report should do the same — a verification section that does not identify the artifact it
  verified is not evidence.
- Per item it records `VerifiedAbsent` / `Residual` / **`Inconclusive`**. That third state
  is the one worth stealing: "we could not prove it is gone" is not "it is gone", and a
  report with only a two-state ✓/✗ will quietly round the former into the latter. This is
  the same failure mode the risk table already flags for batch runs.
- `PdfRedactionVerificationOptions` enumerates the checks as separate switches — raw bytes,
  encoded and hex strings, decoded streams, complete-stream inspection, managed rendering,
  external validators — and the report echoes back which ones actually ran. That echo is
  what makes #49's suite hard to stub out: a verification that reports *which* checks it
  performed cannot be silently downgraded.
- `CreateShareableSummary()` strips removed text, search criteria and labels from the
  evidence. #48/#108 reports will be handed to the people a redaction was performed *for*;
  a report that quotes the redacted strings back is a leak.

None of this requires taking the dependency. It is a contract to copy, not code to import.

**Ordering:** #49 → #108 (the verifier #49 builds is what #108 reports). #147 is
independent and can run in parallel.

**Acceptance:** #108's report distinguishes "we removed it" from "we confirmed it is gone";
#49's suite fails if the verification is stubbed out; #147 has a regression test for a known
catastrophic pattern that must be refused rather than run.

### C — Batch redaction: the headline

#146 is the umbrella; #147–#150 are its decomposition. #106 is the extension-side sibling
and needs a scoping decision before work starts (below).

| Order | Issue | Work |
| --- | --- | --- |
| 1 | #149 | Parse and validate the rule configuration **completely before the first document is opened**. Reject with the offending rule *named* — a misspelled key that silently defaults, or an intensity that falls back to the weakest level, produces a run that reports success and leaves data in the documents. |
| 2 | #148 | `--batch` beside the existing `HostDiagnostics.TryRunCli` seam in `Program.cs`. Keep the nullable-exit-code contract: a value means "handled, exit", null means "fall through to the native-messaging stdio loop". |
| 3 | #150 | Multi-document orchestration: walk the input set, apply the rule set, decide what happens when a document fails, and emit a run report a person can act on without opening the documents. Per document, emit the existing #48 audit report — same shape, same code. |
| 4 | #146 | Closes when 1–3 land. |
| 5 | #106 | **Rescope** (see below). |

**Design rules that are not negotiable, and should be written into the issues before work
starts:**

- **Never write in place.** Inputs are read-only; outputs go to a distinct `--out`. A batch
  tool that overwrites originals turns one bad regex into an unrecoverable incident.
- **`--dry-run` reports matches per document and writes nothing.** It should be the thing a
  person runs first, and the docs should say so.
- **Refuse to start on any invalid rule.** Partial application across a set is worse than a
  refusal, because the run reports success.
- **Exit non-zero if any document fails verification** (#108). A batch run that silently
  skipped three documents is the failure mode this whole workstream exists to prevent.
- **One run report + one per-document #48 report.** No second reporting format.

**#106 vs #146 — the decision to make first.** They overlap: #106 is "word list + files and
directories + OCR fallback + one report", #146 is "config file + rules + report". They are
the same engine with two front ends. Recommendation: **#146/#148–#150 is the engine and the
headless surface; rescope #106 to the extension-side UI over it** (paste or upload a term
list, pick files, show progress, download the run report) and schedule it after the engine,
gated on D. The OCR-fallback half of #106 is genuinely additional and should be split out
rather than absorbed — a scanned page that silently produces no matches is a batch run that
reports success over documents it could not read.

**#50 gets partially pulled in.** A 200-document run needs progress reporting and
cancellation. Take the batch-run slice of #50 here; leave the OCR/signing/large-merge slice
in 3.1 and say so in the issue.

**Acceptance:** a batch run over a mixed directory produces per-document #48 reports and one
run report; an invalid rule stops the run before any document is opened, naming the rule; a
document whose verification fails is reported and makes the run exit non-zero; `--dry-run`
writes nothing.

### D — Manual redaction UX, and the file it has to live in

| Issue | Work |
| --- | --- |
| #107 | Freehand (pen/tablet) redaction: strike a line to redact along it, circle a region to redact inside it. |
| #81 | Split `viewer.js` along the seams that already exist. |

**#81 is conditional-but-likely.** The issue was filed at 3,531 lines; `viewer.js` is now
**4,570**. Both #107 and the rescoped #106 add panels to it. The precedent is established and
works — `formScript.js`, `host-client.js`, `activity-log.js`, `geometry.js` — and the last of
those is DOM-free and unit-tested under `node --test` rather than needing a full Playwright
run. **If either #107 or #106's UI is in the release, do the extraction first**, at least for
the redaction panel and the batch panel. Adding two more panels to a 4,570-line file is how
the next #73-class bug gets written.

### E — Trust and delivery (release-blocking, per "Why this is a major", point 4)

| Issue | Work | Why now |
| --- | --- | --- |
| #152 | **Decide the drift policy before the version is bumped.** | 3.0.0 makes every installed host `HOST_OLDER`. The issue's own inclination — compare fully but *report* rather than warn — no longer covers this case: this drift is real and the user must act. Recommendation: keep `major.minor` as the *warn* threshold (it fires correctly here), and add the full version to the diagnostics surface. Revisit option 4 (a minimum-recommended host version) only if a later release needs it. |
| #110 | Signed installers in the release + winget/Homebrew/apt + in-extension onboarding | This is the answer to "every user has to upgrade their host on release day". `detect → guide → verify`, not silent install. |
| #105 | Tamper self-validation | A tool that now runs unattended over directories of sensitive documents, from a binary users download outside a store, needs an answer to "is this the build we shipped". |
| #111 | VirusTotal scan of release binaries, failing the build on detections | Cheapest of the four and complements #105/#110. Runs on the RC, not every push. |

**Acceptance:** a user on 2.x who updates to the 3.0 extension is shown what to do, can do it
with one command or one installer, and `ping` confirms it. That flow is exercised end to end
before the release is promoted.

### F — Documentation

| Issue | Work |
| --- | --- |
| #119 | The wiki. **Must** cover the batch CLI, the config file format, and the `--dry-run`-first workflow — a surface with no UI is a surface that only exists if it is documented. |
| #120 | Help menu: extension options, GitHub repo, wiki, report an issue. Small; pairs with #119. Note the URLs in the issue body still point at `Chromium-PDF-Editor` and need the reDACT rename applied. |

---

## Issues to close now, without doing any work

Three of the 62 are already satisfied and are inflating the backlog:

- **#153** (release notes must state the privacy impact) — **done.** The v2.0.4 GitHub
  release notes do exactly what the issue asks: they separate the *Merge words* / *Rounded*
  case from the *Full line* case, state that the marked text was always removed, say what a
  user should do, and cover the "Apply to all" removal and the *Rounded* width change. The
  only gap is that `docs/` has no `RELEASE_NOTES_2.0.4.md` to match the 2.0.1–2.0.3 series.
  Close by committing that file from the release body.
- **#154** (split PR #124) — **done.** Piece 1 landed as #155, piece 2 as #156, PR #124 is
  closed. The portable-backend piece is deferred with the rest of the engine cluster — note
  that, rather than citing PR #165's now-stale finding, and close.
- **#66** ("form fields have properties that can be edited") — **needs triage, not
  scheduling.** One line of body, no acceptance criteria, unlabelled. Ask for specifics or
  close as insufficiently specified; do not carry it into a milestone.

## Deferred to 3.1 — say so explicitly

The 20-issue editing and rendering tail: **#17** (forms menu redesign), **#28, #31–#35**
(text and font fidelity), **#36–#41, #45** (rendering, geometry, accessibility), **#42, #43**
(forms), **#50** (the non-batch slice), **#51** (undo/redo atomicity), **#55** (HEIC).

These are real and mostly well-specified. They are deferred because they share subsystems
with each other and not with 3.0.0's theme, so interleaving them buys nothing and costs
merge conflicts in `ContentStreamEditor` and the text model. **#51 is the one to watch**:
freehand redaction (#107) creates many regions in one gesture, and undo across them is
exactly the atomicity #51 describes. If #107's implementation trips over it, pull #51 in
rather than shipping a half-undoable gesture.

## Sequencing

Four waves. Within a wave, items on separate rows can run in parallel; across waves, the
arrows are real.

| Wave | Parallel tracks | Gate to leave the wave |
| --- | --- | --- |
| **1 — Clear the decks** | A: #115 decision + #127 · A: #116 seam (9 PRs) · Close #153, #154, triage #66 · E: #152 decision | Engine question recorded and labelled; seam merged; drift policy decided. **#152's decision must precede any version bump.** |
| **2 — Evidence** | B: #49 → #108 · B: #147 · D: #81 extraction (if D ships) | The report proves removal, and a stubbed verifier turns the suite red. |
| **3 — Batch** | C: #149 → #148 → #150 → #146 · E: #110, #111 · F: #119 (drafted alongside the CLI, not after) | A batch run over a mixed directory behaves per C's acceptance criteria. |
| **4 — Surface and ship** | C: #106 rescoped UI · D: #107 · E: #105 · F: #120 | Release gates below. |

Wave 1 is mostly decisions and a refactor and can move fast. Wave 3 is the bulk of the
engineering. E's items are spread across 3 and 4 because #110 is long-lead (signing
certificates, package-manager submissions) and needs starting early even though it lands late.

## Release gates for 3.0.0

Nothing is promoted to a final tag until all of these hold:

- [ ] Full .NET suite green with the 90% coverage gate; e2e (Playwright) green.
- [ ] A batch run over the synthetic corpus (`CorruptPdfs.cs`, `Fuzz/RawPdf.cs`, `docs/sample`)
      completes, and every per-document report's verification section reads ✓.
- [ ] An intentionally-surviving redaction (verifier stubbed) makes the run exit non-zero.
      **Verify this by breaking it, not by reading the code.**
- [ ] A catastrophic-backtracking regex in a config file is refused at parse time, named.
- [ ] `--dry-run` over a directory writes zero bytes outside the report path.
- [ ] The 2.x → 3.0 host upgrade path is walked end to end on Windows, macOS and Linux:
      warning shown → installer or package-manager command → `ping` confirms → warning clears.
- [ ] Release binaries pass the VirusTotal gate (#111).
- [ ] The wiki documents the batch CLI and config format, and the Help menu links resolve.
- [ ] Release notes state plainly: what batch redaction does and does not guarantee, that
      the host must be upgraded, and that the licence is unchanged.

## Risks, and what to do about each

| Risk | Why it is the dangerous one | Mitigation |
| --- | --- | --- |
| **A batch run reports success over documents it silently mishandled** | It is the entire premise of the feature. One scanned page with no text layer, or one document the engine refused, and the operator has a report saying it went fine. | #108's verification is per region and per document; #150 exits non-zero on any failure; a document that produced zero matches is reported as *zero matches*, distinguishable from *not processed*. |
| **Regex from a file hangs the host** | Unattended, no operator, no timeout by default in .NET. | Non-backtracking + hard timeout + parse-time rejection (#147/#149). |
| **The major bump strands every installed host** | The version check fires correctly and the user has nothing easy to do. | Workstream E is release-blocking, and the upgrade walk is a release gate. |
| **Batch writes over the user's originals** | Unrecoverable, and exactly the kind of thing a "just make it work" implementation does. | Designed out in #150: read-only inputs, distinct `--out`, `--dry-run` first. |
| **The engine cluster reasserts itself mid-release** | 24 open issues exert gravity, and #116 is genuinely satisfying work that can absorb a whole release. | #116 is time-boxed to the seam. Anything touching a second implementation is out of scope for 3.0.0 by definition. |
| **`viewer.js` absorbs two more panels** | 4,570 lines, and every past bug in that file has this shape. | #81 extraction precedes the new panels, or the panels do not ship in 3.0.0. |

## Decisions needed from the owner before wave 1 closes

1. **#152 drift policy** — recommendation above; the version bump depends on it.
2. **#106 scope** — engine + headless (#146) versus a second front end; recommendation above.
3. **Does the extension-side batch UI ship in 3.0.0 at all**, or is 3.0.0 headless-only with
   the UI in 3.1? Headless-only is a defensible, materially smaller release and removes #81
   and #106 from the critical path. This is the biggest single lever on the release's size.
4. **Whether to open the milestone and apply `blocked:upstream` labels** — this plan proposes
   both but changes nothing in the tracker.

## Suggested tracker hygiene

- Create a **3.0.0 milestone** and add only: #49, #81, #105, #106, #107, #108, #110, #111,
  #115, #116, #119, #120, #127, #146, #147, #148, #149, #150, #152.
- Label #126, #128–#145, #151 `blocked:upstream`, with one comment each linking the #115
  decision.
- Close #153 and #154; triage #66.
- That leaves the tracker reading as: 19 issues committed, 21 blocked on an upstream
  decision, 20 deferred to 3.1, 2 closed — instead of 62 undifferentiated open items.

---

_Companion documents: `ACTION_PLAN.md` (the original 40-issue triage, largely delivered),
`AGENT_PLAN.md` (how to run these as Claude Code sessions — its per-issue branch/PR
conventions and review gates apply unchanged to the workstreams above)._
