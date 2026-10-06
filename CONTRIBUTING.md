# Contributing to reDACT PDF Editor

Thanks for looking at improving this project. This document covers getting a dev
environment running, the test suites, and what a PR should look like. For the
project's architecture (how redaction actually removes content, the extension ↔
native host protocol, etc.), see [README.md](README.md) instead.

## Getting set up

### Option A: Dev Container (recommended)

The repository ships a [dev container](https://containers.dev/) (`.devcontainer/`)
with the .NET 8 SDK, Node.js, Python, and PowerShell preinstalled. It works with
[GitHub Codespaces](https://github.com/features/codespaces) or locally with
[VS Code](https://code.visualstudio.com/) + the
[Dev Containers extension](https://marketplace.visualstudio.com/items?itemName=ms-vscode-remote.remote-containers)
(Docker Desktop or another Docker engine required locally).

1. Open the repository in VS Code.
2. **Reopen in Container** when prompted (or run *Dev Containers: Reopen in Container*
   from the command palette), or open it directly in a Codespace.
3. The container automatically runs the bootstrap script on creation — see below for
   what that does. Once it finishes, you're ready: run `dotnet test` or open a
   terminal and go.

### Option B: Bootstrap your own machine

If you'd rather work directly on your host machine, run one of the two equivalent
bootstrap scripts — they do the same thing, pick whichever fits your platform:

```bash
# PowerShell (Windows, or PowerShell 7+/pwsh on Linux/macOS)
./scripts/bootstrap.ps1

# Python 3 (any platform)
python3 scripts/bootstrap.py
```

Each restores the .NET solution's NuGet packages, restores the pinned `reportgenerator`
dotnet tool, generates the extension's toolbar icons, and installs the e2e test
dependencies (Node packages + Playwright's bundled Chromium). Pass `-SkipE2E` /
`--skip-e2e` to skip that last part if you only care about the C# side.

**Prerequisites for a manual bootstrap:**

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) — required.
- Python 3 — required (bootstrap.py needs it to run itself; bootstrap.ps1 shells out to
  it for icon generation either way).
- [Node.js 22+](https://nodejs.org/) — only needed for the Playwright end-to-end suite;
  everything else works without it.

Re-run either bootstrap script any time — both are idempotent.

## Project layout

```
extension/                         Chromium MV3 extension (UI)
src/PdfEditor.Core/                PDF engine (in-house, see src/PdfEditor.Core/Pdf/) + PDFium rendering
src/PdfEditor.NativeHost/          native messaging host executable
tests/PdfEditor.Core.Tests/        unit tests for the PDF engine
tests/PdfEditor.NativeHost.Tests/  unit tests for the JSON dispatcher (in-process)
tests/PdfEditor.IntegrationTests/  process-level protocol & workflow tests
e2e/                               Playwright browser end-to-end tests
scripts/                           bootstrap, packaging, coverage, host-install scripts
.devcontainer/                     dev container definition
.github/workflows/                 CI and release pipelines
```

## Making changes

1. Create a branch off `main`.
2. Make your change. Prefer small, focused commits/PRs over one large one.
3. Match the existing style: XML doc comments only where the *why* isn't obvious from
   the code (a non-obvious invariant, a workaround, a subtle constraint) — not
   restating what a well-named method already says. No unrelated refactors bundled
   into a bug-fix PR.
4. Run the relevant tests locally before opening a PR (see below) — CI runs all of
   them anyway, but catching a failure locally is faster than waiting on Actions.
5. Open the PR against `main`. Describe *why*, not just *what* — the diff already
   shows what changed.

## Running the tests

```bash
dotnet build                # build everything
dotnet test                 # run all 652 .NET tests (unit, integration and perf)
```

- `PdfEditor.Core.Tests` — unit tests for the PDF engine itself.
- `PdfEditor.NativeHost.Tests` — fast in-process tests for the JSON dispatcher and
  chunk reassembly, no process spawning.
- `PdfEditor.IntegrationTests` — spawns the real native host binary and drives it over
  the actual framed protocol end to end.

If you touch `PdfEditor.Core` or `PdfEditor.NativeHost`, add or update unit tests in
the matching project. If you touch the extension ↔ host wire protocol, add a case to
`PdfEditor.IntegrationTests` too.

The extension's own unit tests are plain `node --test`, and CI runs them:

```bash
node --test "extension/test/**/*.test.mjs"
node scripts/check-innerhtml.mjs   # also run as one of the tests above
```

If you touch how the extension reports a missing native host, `extension/src/host-install.js` is
deliberately DOM-free so the diagnosis and the instructions it produces can be asserted directly
(`extension/test/host-install.test.mjs`), and `extension/test/host-client.test.mjs` drives
`probeHost` against a stubbed `chrome.runtime` to cover the failure paths that are otherwise only
reproducible by breaking an install by hand.

### Never assign interpolated `innerHTML` in `extension/src/`

The viewer renders text that comes straight out of the document being edited — form
field names, file names, link URLs, host error strings — on an extension page with
`chrome.*` privileges. Build nodes with `createElement`/`textContent`. Clearing a
container with `innerHTML = ''` and assigning a static string literal are both fine;
anything interpolated is not, and `scripts/check-innerhtml.mjs` fails CI on it.

### Golden-file regression suite

`tests/PdfEditor.Core.Tests/Golden/` runs a corpus of deliberately awkward documents
(Type 3 and Identity-H fonts, `/ToUnicode` CMaps, soft masks, nested transparency
groups, malformed streams) through every export operation and compares the result
against a recording in `Golden/goldens/*.txt`. It runs as part of `dotnet test`; no
extra setup.

The recordings are **not** raw PDF bytes. The writer stamps a `/ModDate` (and, for a new
document, a random `/ID`) into everything it writes, so the same operation on the same input produces
different bytes every time; the goldens hold a *semantic projection* instead — page
geometry, extracted text, the font/XObject/graphics-state inventory, an operator census
per content stream, and the `ExportValidator` findings.

If you change the engine on purpose, the diff on those files is your review artefact.
Re-record them **explicitly**:

```bash
PDFEDITOR_UPDATE_GOLDENS=1 dotnet test tests/PdfEditor.Core.Tests \
    --filter FullyQualifiedName~Corpus_MatchesRecordedGolden
```

That rewrites the files **and still fails the run**, on purpose: a regeneration must
never look like a green build. Read the diff, convince yourself each change is one you
meant, commit it, then re-run without the variable. Nothing else — least of all a
failing run — ever rewrites a golden.

### Coverage

```bash
./scripts/coverage.sh       # run every .NET test project with coverage, print a summary
./scripts/coverage.sh 90    # same, but fail if line coverage drops below 90%
```

CI gates on 90% line coverage; see the README's Code Coverage section for the current
numbers and the (documented, deliberate) gaps.

### Browser end-to-end tests

If you touch the extension UI (`extension/src/`) or the PDF engine (`src/PdfEditor.Core/`), run
the Playwright suite — it loads the real extension into Chromium and drives it against the real
native host. `tests/engine-v3.spec.js` also reads back the files Save exports, so it catches an
engine change that leaves the page looking right and the file wrong:

```bash
cd e2e
npx playwright test
```

(`npm install` and `npx playwright install chromium` first if you skipped e2e during
bootstrap.)

## Packaging and releasing

`scripts/package-extension.sh` builds a Chrome Web Store-ready zip locally, useful for
manually testing an unpacked build or sanity-checking a release before tagging:

```bash
./scripts/package-extension.sh
```

### The Linux host packages

`package-deb.sh`, `package-rpm.sh` and `package-arch.sh` all install the same self-contained host
under `/opt/pdf-editor-host`, symlink it to `/usr/bin/pdf-editor-host`, and drop the
native-messaging manifest into every directory listed in `scripts/linux-manifest-dirs.sh`. The
manifest points at the `/usr/bin` symlink rather than at `/opt`, because a sandboxed browser is
far more likely to be allowed to execute it there.

Getting any of that wrong is invisible until someone installs the package: the browser reports
`Specified native messaging host not found.` whether the manifest is missing, in a directory that
browser does not read, or naming a path the package does not ship. Two guards keep that honest:

```bash
./scripts/test-package-scripts.sh                 # builds all three with a stub host, then verifies
./scripts/verify-linux-package.sh dist/*.deb      # verify a real package you already built
```

`test-package-scripts.sh` runs in CI on every push. It needs no .NET SDK — it puts a `dotnet` stub
on `PATH` so `dotnet publish` writes a few placeholder files — so it takes seconds rather than the
two minutes a self-contained publish costs. Install its tooling with
`sudo apt-get install -y rpm libarchive-tools zstd`.

**Adding a browser directory**: add it to `scripts/linux-manifest-dirs.sh` only. All three
packagers and the verifier read that list, so they cannot drift apart. Per-user directories
(including the snap and flatpak layouts, which `/etc` cannot reach) live in
`scripts/register-host.sh`, which the packages install as `/usr/bin/pdf-editor-host-register` and
which `install-host.sh` delegates to.

Every merge to `main` automatically builds a release candidate (a `vX.Y.Z-<build>` tag
and prerelease); promoting one to a real, Chrome Web Store-published release is a manual
step (create a Release targeting the RC's commit with a clean `vX.Y.Z` tag). See the
README's **Release cycle: release candidates → promotion** section for the full flow.

## Questions

Open an issue if something here is unclear or a step doesn't work for you — the goal
is for `bootstrap.ps1`/`bootstrap.py` (or the dev container) to be the *only* setup
step anyone needs.
