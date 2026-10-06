'use strict';

/**
 * Viewer-driving helpers shared by the specs that run the extension against the native host:
 * opening documents, clicking toolbar controls, drawing on the page, filling dialogs, reading the
 * extracted text back, and capturing what Save exports.
 */

const { expect } = require('@playwright/test');
const fs = require('node:fs');
const path = require('node:path');

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

/** Drags a rectangle on the overlay, in PDF user-space coordinates. */
async function dragPdfRect(page, { x, y, width, height }, mediaBox = A4) {
  const [llx, , urx, ury] = mediaBox;
  const box = await page.locator(pageImageSel(1)).boundingBox();
  const scale = box.width / (urx - llx);
  const cssX = (pdfX) => box.x + (pdfX - llx) * scale;
  const cssY = (pdfY) => box.y + (ury - pdfY) * scale;
  await page.mouse.move(cssX(x), cssY(y + height));
  await page.mouse.down();
  await page.mouse.move(cssX(x + width), cssY(y), { steps: 5 });
  await page.mouse.up();
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

// ------------------------------------------------------- opening documents and capturing exports

/** Loads `file` through the viewer's Open button and waits for its first page to render. */
async function openFile(page, file) {
  const chooser = page.waitForEvent('filechooser');
  await page.click('#btn-open-empty');
  await (await chooser).setFiles(file);
  await expect(page.locator(pageImageSel(1))).toHaveAttribute('src', /data:image\/png/);
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
   */
  async function openCapturingViewerWith(file) {
    const ext = getExt();
    const page = await ext.context.newPage();
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
    await openFile(page, file);
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

  return { openViewerWith, openCapturingViewerWith, writeCapturedExport };
}

module.exports = {
  A4, pageImageSel, ui, dragPdfRect, fillDialog,
  textRuns, pageText, expectText, expectCompactText,
  viewerSession,
};
