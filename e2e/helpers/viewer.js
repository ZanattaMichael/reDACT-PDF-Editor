'use strict';

/**
 * Viewer-driving helpers shared by the specs that run the extension against the native host:
 * opening documents, clicking toolbar controls, drawing on the page, filling dialogs, reading the
 * extracted text back, and capturing what Save exports.
 */

const { expect } = require('@playwright/test');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { launchExtension } = require('./harness');

// In the continuous-scroll layout each page is `.page[data-page="N"]` with its own image.
const pageImageSel = (n = 1) => `.page[data-page="${n}"] .page-image`;

// PDF user-space coordinate helpers. The page box defaults to A4 at the origin; pass a
// [llx, lly, urx, ury] box to work with pages whose MediaBox does not start at (0,0) — the
// rendered image's bottom-left is (llx, lly), so mappings must subtract that origin.
const A4 = [0, 0, 595, 842];

/** Clicks a toolbar control, first opening its Reading/Editing dropdown when it lives in one. */
async function ui(page, sel) {
  const triggerId = await page.evaluate((s) => {
    const el = document.querySelector(s);
    const menu = el?.closest('.menu-group');
    return menu ? menu.querySelector('.menu-trigger').id : null;
  }, sel);
  if (triggerId) await page.click('#' + triggerId);
  await page.click(sel);
}

/**
 * Maps PDF user-space points on page `pageNum` to screen coordinates on its rendered image.
 * Returns the mapping function; the page must be on screen.
 */
async function pdfToScreen(page, pageNum = 1, mediaBox = A4) {
  const [llx, , urx, ury] = mediaBox;
  const box = await page.locator(pageImageSel(pageNum)).boundingBox();
  const scale = box.width / (urx - llx);
  return ({ x, y }) => ({ x: box.x + (x - llx) * scale, y: box.y + (ury - y) * scale });
}

/** Drags a rectangle on the overlay, in PDF user-space coordinates. */
async function dragPdfRect(page, { x, y, width, height }, mediaBox = A4) {
  await dragPdf(page, { x, y: y + height }, { x: x + width, y }, { mediaBox });
}

/** Presses at `from`, moves to `to` in steps and releases, both PDF user-space points. */
async function dragPdf(page, from, to, { pageNum = 1, mediaBox = A4 } = {}) {
  const screen = await pdfToScreen(page, pageNum, mediaBox);
  const start = screen(from);
  const end = screen(to);
  await page.mouse.move(start.x, start.y);
  await page.mouse.down();
  await page.mouse.move(end.x, end.y, { steps: 8 });
  await page.mouse.up();
}

/** Clicks a PDF user-space point on page `pageNum`. */
async function clickPdf(page, point, { pageNum = 1, mediaBox = A4 } = {}) {
  const at = (await pdfToScreen(page, pageNum, mediaBox))(point);
  await page.mouse.click(at.x, at.y);
}

/** Fills the promptDialog() form (inputs in creation order) and confirms. */
async function fillDialog(page, values, confirmText) {
  const dialog = page.locator('dialog#modal');
  await expect(dialog).toBeVisible();
  const inputs = dialog.locator('input');
  for (let i = 0; i < values.length; i++) {
    if (values[i] !== null) await inputs.nth(i).fill(values[i]);
  }
  await dialog.getByRole('button', { name: confirmText }).click();
}

// ------------------------------------------------------- document-content helpers

/**
 * The text runs the viewer's selectable text layer holds for a page: the real characters the
 * native host extracted from the *current* document, with their PDF-space boxes. This is the
 * "did the file actually change?" oracle — unlike a form field or a status string it cannot
 * round-trip a value the document never received.
 */
async function textRuns(page, pageNum = 1) {
  await page.locator(`.page[data-page="${pageNum}"] .page-image`).waitFor();
  return page.evaluate((n) => {
    const layer = document.querySelector(`.page[data-page="${n}"] .text-layer`);
    if (!layer) return [];
    return [...layer.querySelectorAll('span')].map((el) => ({
      text: el.textContent,
      ...JSON.parse(el.dataset.region),
    }));
  }, pageNum);
}

/**
 * The whole extracted text of a page, in reading order: runs bucketed into lines by baseline,
 * each line left to right, joined by single spaces.
 *
 * Reading order matters rather than being tidy. Operations that rewrite text — find & replace,
 * replace-region-text — remove the original operators and append the replacement at the end of
 * the content stream, so the runs arrive in an order that has nothing to do with the layout.
 * Sorting by position is what makes "the line now reads X" a meaningful assertion.
 */
async function pageText(page, pageNum = 1) {
  const runs = await textRuns(page, pageNum);
  const lines = [];
  for (const run of runs.toSorted((a, b) => b.y - a.y)) {
    const line = lines.find((l) => Math.abs(l.y - run.y) < Math.max(run.height, 1) * 0.6);
    if (line) line.runs.push(run);
    else lines.push({ y: run.y, runs: [run] });
  }
  return lines
    .map((l) => l.runs.toSorted((a, b) => a.x - b.x).map((r) => r.text).join(' '))
    .join(' ');
}

/**
 * The extracted text of a page as a Playwright poll, so an assertion can wait for the text
 * layer to be rebuilt after an edit instead of racing the status line that announced it.
 * Use as `await expectText(page).toContain('WORLD')`.
 *
 * ORDER MATTERS. The text layer is torn down and rebuilt asynchronously after every edit, so
 * for a moment the page reports no text at all — and `.not.toContain(...)` is satisfied by the
 * empty string on its very first poll. A negative assertion must therefore always come *after*
 * a positive one on the same page, which is what waits for the rebuilt layer. Getting this
 * backwards produced a test that passed against a build where redaction removed nothing.
 */
function expectText(page, pageNum = 1) {
  return expect.poll(() => pageText(page, pageNum), { timeout: 20000 });
}

/**
 * The same, with all whitespace removed. Needed after any operation that rewrites a content
 * stream in place (redaction, find & replace): the rewrite emits one text-showing operator per
 * surviving glyph, so extraction reads the leftovers back as "s u m m a r y" rather than
 * "summary". That is a real defect in its own right — it is what a reader's copy/paste produces
 * too — but it is not what these tests are about, so they assert on the letters, not the gaps.
 */
function expectCompactText(page, pageNum = 1) {
  return expect.poll(() => pageText(page, pageNum).then((t) => t.replace(/\s+/g, '')),
    { timeout: 20000 });
}

// ------------------------------------------------------- redaction

/** Applies the marked redactions and closes the report the viewer shows afterwards. */
async function applyRedaction(page) {
  await page.click('#redact-apply');
  await expect(page.locator('#status')).toContainText('content removed');
  const report = page.locator('dialog#modal');
  await expect(report).toContainText('Redaction report');
  await report.getByRole('button', { name: 'Close' }).click();
  await expect(report).toBeHidden();
}

/** Marks every occurrence of `phrase` with the Redact panel's search, then applies. */
async function searchAndRedact(page, phrase, expectedMarks) {
  await ui(page, '#tool-redact');
  await page.fill('#redact-search-text', phrase);
  await page.click('#redact-search-btn');
  await expect(page.locator('#redact-list li')).toHaveCount(expectedMarks);
  await applyRedaction(page);
}

// ------------------------------------------------------- opening documents and capturing exports

/**
 * Loads `file` through the viewer's Open button and waits for its first page to render, answering
 * the password prompt with `password` when one is given. An array answers it once per entry, in
 * order, which is how a wrong password followed by the right one is entered.
 */
async function openFile(page, file, password) {
  const chooser = page.waitForEvent('filechooser');
  await page.click('#btn-open-empty');
  await (await chooser).setFiles(file);
  for (const answer of password === undefined ? [] : [password].flat()) {
    await expect(page.locator('dialog#modal')).toContainText('password-protected');
    await fillDialog(page, [answer], 'OK');
  }
  await expect(page.locator(pageImageSel(1))).toHaveAttribute('src', /data:image\/png/);
}

/**
 * Runs in the page (as an init script): wraps chrome.runtime.connectNative so the replies to the
 * actions named in `delays` reach the page that many milliseconds late. Everything else, and the
 * host itself, is untouched.
 */
function holdHostReplies(delays) {
  const connect = chrome.runtime.connectNative.bind(chrome.runtime);
  chrome.runtime.connectNative = (name) => {
    const port = connect(name);
    const held = new Map();
    return {
      postMessage(message) {
        if (message.action in delays) held.set(message.id, delays[message.action]);
        port.postMessage(message);
      },
      disconnect: () => port.disconnect(),
      onDisconnect: port.onDisconnect,
      onMessage: {
        addListener: (listener) => port.onMessage.addListener((reply) => {
          const delay = held.get(reply.id);
          if (delay === undefined) listener(reply);
          else setTimeout(() => listener(reply), delay);
        }),
      },
    };
  };
}

/**
 * The helpers that open viewer pages need the suite's browser and fixture directory, which each
 * spec creates in its own beforeAll. They are passed as getters and read when a helper runs.
 *
 * @param {() => Awaited<ReturnType<import('./harness').launchExtension>>} getExt
 * @param {() => string} getFixtureDir
 */
function viewerSession(getExt, getFixtureDir) {
  /** Opens a fresh viewer page and loads the given fixture through the Open button. */
  async function openViewerWith(file) {
    const ext = getExt();
    const page = await ext.context.newPage();
    await page.goto(ext.viewerUrl);
    await openFile(page, file);
    return page;
  }

  /**
   * Opens a viewer page that captures whatever the Save button hands to chrome.downloads instead
   * of writing it out, so a test can inspect the actual exported bytes. The file-picker path is
   * removed first — it cannot be driven headlessly, and the downloads path is the fallback anyway.
   *
   * `slowHost` maps host actions to a delay in milliseconds: the host's reply to each is held that
   * long before the page sees it, so a test can act while that request is still outstanding.
   */
  async function openCapturingViewerWith(file, { password, slowHost } = {}) {
    const ext = getExt();
    const page = await ext.context.newPage();
    if (slowHost) await page.addInitScript(holdHostReplies, slowHost);
    await page.addInitScript(() => {
      delete window.showSaveFilePicker;
      window.__saved = null;
      chrome.downloads.download = async (opts) => {
        const buf = await (await fetch(opts.url)).arrayBuffer();
        window.__saved = { name: opts.filename, bytes: [...new Uint8Array(buf)] };
        return 1;
      };
    });
    await page.goto(ext.viewerUrl);
    await openFile(page, file, password);
    return page;
  }

  /** Waits for a captured export and writes it to `name` in the fixture directory. */
  async function writeCapturedExport(page, name) {
    await expect.poll(() => page.evaluate(() => window.__saved?.bytes.length ?? 0), { timeout: 20000 })
      .toBeGreaterThan(0);
    const saved = await page.evaluate(() => window.__saved);
    const file = path.join(getFixtureDir(), name);
    fs.writeFileSync(file, Buffer.from(saved.bytes));
    return { file, name: saved.name, bytes: Buffer.from(saved.bytes) };
  }

  /**
   * Presses Save and returns what it exported (see openCapturingViewerWith). Anything captured
   * earlier is dropped first: Save is not the only thing that goes through chrome.downloads
   * (creating a certificate saves the .p12 there), and the capture keeps whichever came last.
   */
  async function saveExport(page, name) {
    await page.evaluate(() => { window.__saved = null; });
    await page.click('#btn-save');
    await expect(page.locator('#status')).toContainText('Saving via downloads');
    const exported = await writeCapturedExport(page, name);
    expect(exported.name).toMatch(/\.pdf$/);
    return exported;
  }

  /** Writes `bytes` to `name` in the fixture directory and returns the path. */
  function writeFixture(name, bytes) {
    const file = path.join(getFixtureDir(), name);
    fs.writeFileSync(file, bytes);
    return file;
  }

  return {
    get ext() { return getExt(); },
    get fixtureDir() { return getFixtureDir(); },
    openViewerWith,
    openCapturingViewerWith,
    writeCapturedExport,
    saveExport,
    writeFixture,
  };
}

/**
 * Gives a spec its browser session: Chromium with the extension loaded and the freshly built host
 * registered, plus a fixture directory, created before the spec's tests and removed after them.
 * Returns the viewerSession helpers bound to that session.
 *
 * @param {import('@playwright/test').test} test
 */
function extensionSuite(test, prefix = 'pdf-editor-e2e-') {
  let ext;
  let fixtureDir;
  test.beforeAll(async () => {
    ext = await launchExtension();
    fixtureDir = fs.mkdtempSync(path.join(os.tmpdir(), prefix));
  });
  test.afterAll(async () => {
    await ext?.close();
    if (fixtureDir) fs.rmSync(fixtureDir, { recursive: true, force: true });
  });
  return viewerSession(() => ext, () => fixtureDir);
}

module.exports = {
  A4, pageImageSel, ui, pdfToScreen, dragPdfRect, dragPdf, clickPdf, fillDialog,
  textRuns, pageText, expectText, expectCompactText,
  applyRedaction, searchAndRedact,
  viewerSession, extensionSuite,
};
