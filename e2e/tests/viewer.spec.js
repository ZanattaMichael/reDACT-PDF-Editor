'use strict';

const { test, expect } = require('@playwright/test');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { launchExtension } = require('../helpers/harness');
const {
  buildPdf, buildLeftoverCtmPdf, buildImagePdf, buildFormPdf, buildFormWithButtonScriptPdf,
  buildJavaScriptPdf, buildLinkPdf, buildJsLinkPdf, buildLinkOnPage2Pdf, buildLinkOverTextPdf,
  buildMultiLinkPdf,
} = require('../helpers/pdf');
const { installHostGate } = require('../helpers/hostgate');
const {
  A4, pageImageSel, ui, dragPdfRect, fillDialog,
  textRuns, pageText, expectText, expectCompactText,
  viewerSession,
} = require('../helpers/viewer');

/** @type {Awaited<ReturnType<typeof launchExtension>>} */
let ext;
let fixtureDir;

test.beforeAll(async () => {
  ext = await launchExtension();
  fixtureDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pdf-editor-fixtures-'));
});

test.afterAll(async () => {
  await ext?.close();
  if (fixtureDir) fs.rmSync(fixtureDir, { recursive: true, force: true });
});

function fixture(name, pages, opts) {
  const file = path.join(fixtureDir, name);
  fs.writeFileSync(file, buildPdf(pages, opts));
  return file;
}

const { openViewerWith, openCapturingViewerWith, writeCapturedExport } =
  viewerSession(() => ext, () => fixtureDir);

/** Samples a rendered-page pixel at a PDF user-space point (returns [r,g,b,a]). */
async function pixelAt(page, pdfX, pdfY, mediaBox = A4) {
  return page.evaluate(async ([px, py, [llx, , urx, ury]]) => {
    const img = document.querySelector('.page[data-page="1"] .page-image');
    await img.decode();
    const canvas = document.createElement('canvas');
    canvas.width = img.naturalWidth;
    canvas.height = img.naturalHeight;
    const ctx = canvas.getContext('2d');
    ctx.drawImage(img, 0, 0);
    const scale = img.naturalWidth / (urx - llx);
    const data = ctx.getImageData(
      Math.round((px - llx) * scale), Math.round((ury - py) * scale), 1, 1).data;
    return [...data];
  }, [pdfX, pdfY, mediaBox]);
}

// ------------------------------------------------------- pixel measurement helpers
//
// Everything below measures a *band* of pixels, never a single point. A lone coordinate lands
// between the legs of an "A" and reports white on a page full of text, which is how several
// "fixed" bugs shipped behind green tests. Bands also make the assertions quantitative — an
// ink fraction of 0.0 vs 0.35 vs 1.0 tells you which of "nothing happened", "the text is
// there" and "a solid box covers it" is true, where `toBeVisible()` tells you nothing.

/**
 * Reduces the PDF-user-space band {x, y, width, height} of a rendered page to statistics.
 *
 *   n        pixels sampled
 *   ink      fraction darker than mid-grey — glyphs, black boxes, dark strokes
 *   paper    fraction that is (near-)white page background
 *   match    fraction within `tolerance` of `rgb`, when one is given
 *   yellow   fraction that reads as highlighter yellow
 *   mean     [r, g, b] average over the band
 *   dominant most common colour, quantised to 16 levels per channel
 *   inkBox   PDF-space bounding box {x, y, width, height} of the dark pixels, or null
 */
async function bandStats(page, band, { pageNum = 1, mediaBox = A4, rgb = null, tolerance = 12 } = {}) {
  // Accept either band schema: {x, y, width, height} or the {x0, x1, y0, y1} corner form the
  // highlight tests use. Normalising here means neither style has to know about the other.
  if (band && band.x0 !== undefined) {
    band = { x: band.x0, y: band.y0, width: band.x1 - band.x0, height: band.y1 - band.y0 };
  }
  return page.evaluate(async ([sel, b, box, target, tol]) => {
    const [llx, lly, urx, ury] = box;
    const img = document.querySelector(sel);
    await img.decode();
    const c = document.createElement('canvas');
    c.width = img.naturalWidth; c.height = img.naturalHeight;
    const ctx = c.getContext('2d');
    ctx.drawImage(img, 0, 0);
    const sx = img.naturalWidth / (urx - llx);
    const sy = img.naturalHeight / (ury - lly);
    const px0 = Math.max(0, Math.round((b.x - llx) * sx));
    const px1 = Math.min(c.width, Math.round((b.x + b.width - llx) * sx));
    // PDF y grows upwards, image y downwards, so the band's top edge is the smaller image row.
    const py0 = Math.max(0, Math.round((ury - (b.y + b.height)) * sy));
    const py1 = Math.min(c.height, Math.round((ury - b.y) * sy));
    if (px1 <= px0 || py1 <= py0) throw new Error('empty sampling band');
    const { data } = ctx.getImageData(px0, py0, px1 - px0, py1 - py0);
    const w = px1 - px0;

    let n = 0, ink = 0, paper = 0, match = 0, yellow = 0, sr = 0, sg = 0, sb = 0;
    let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity;
    const counts = new Map();
    for (let i = 0; i < data.length; i += 4) {
      const r = data[i], g = data[i + 1], bl = data[i + 2];
      n++; sr += r; sg += g; sb += bl;
      const lum = 0.299 * r + 0.587 * g + 0.114 * bl;
      if (lum < 128) {
        ink++;
        const ix = px0 + ((i / 4) % w), iy = py0 + Math.floor((i / 4) / w);
        if (ix < minX) minX = ix;
        if (ix > maxX) maxX = ix;
        if (iy < minY) minY = iy;
        if (iy > maxY) maxY = iy;
      }
      if (r > 245 && g > 245 && bl > 245) paper++;
      if (r > 200 && g > 170 && bl < 150) yellow++;
      if (target && Math.abs(r - target[0]) <= tol && Math.abs(g - target[1]) <= tol &&
          Math.abs(bl - target[2]) <= tol) match++;
      const key = ((r >> 4) << 8) | ((g >> 4) << 4) | (bl >> 4);
      counts.set(key, (counts.get(key) ?? 0) + 1);
    }
    let bestKey = 0, bestN = -1;
    for (const [k, v] of counts) if (v > bestN) { bestN = v; bestKey = k; }

    return {
      n,
      ink: ink / n, paper: paper / n, match: match / n, yellow: yellow / n,
      mean: [Math.round(sr / n), Math.round(sg / n), Math.round(sb / n)],
      dominant: [((bestKey >> 8) & 15) * 17, ((bestKey >> 4) & 15) * 17, (bestKey & 15) * 17],
      inkBox: ink === 0 ? null : {
        x: llx + minX / sx, y: ury - (maxY + 1) / sy,
        width: (maxX - minX + 1) / sx, height: (maxY - minY + 1) / sy,
      },
    };
  }, [pageImageSel(pageNum), band, mediaBox, rgb, tolerance]);
}

/** Fraction of the band that is dark ink. 0 = blank paper, ~0.2-0.4 = a line of text, 1 = solid. */
async function inkFraction(page, band, opts) { return (await bandStats(page, band, opts)).ink; }

/** Fraction of the band within `tolerance` of `rgb` — for "the image is still that blue". */
async function colorFraction(page, band, rgb, opts = {}) {
  return (await bandStats(page, band, { ...opts, rgb })).match;
}

/** Fraction of the band that reads as highlighter yellow. */
async function yellowFraction(page, band, opts) { return (await bandStats(page, band, opts)).yellow; }

/** Most common colour in the band, quantised — "what colour is this area, broadly". */
async function dominantColor(page, band, opts) { return (await bandStats(page, band, opts)).dominant; }

/** PDF-space bounding box of the dark pixels in the band, or null when it is blank. */
async function inkBounds(page, band, opts) { return (await bandStats(page, band, opts)).inkBox; }

/**
 * Presses at one PDF-space point, sweeps to another and releases — the way a reader
 * selects text with the mouse (as opposed to dragPdfRect's box drag).
 */
async function sweepPdf(page, from, to, { pageNum = 1, mediaBox = A4 } = {}) {
  const [llx, , urx, ury] = mediaBox;
  const box = await page.locator(pageImageSel(pageNum)).boundingBox();
  const scale = box.width / (urx - llx);
  const at = (p) => [box.x + (p.x - llx) * scale, box.y + (ury - p.y) * scale];
  await page.mouse.move(...at(from));
  await page.mouse.down();
  await page.mouse.move(...at(to), { steps: 10 });
  await page.mouse.up();
}


test.describe('PDF Editor end-to-end (extension + native host)', () => {
  test('options page reports the native host as connected', async () => {
    const page = await ext.context.newPage();
    await page.goto(ext.optionsUrl);
    await expect(page.locator('#host-status')).toContainText('✓ connected');
    await page.close();
  });

  test('opens a PDF and renders the first page', async () => {
    const file = fixture('render.pdf', [[{ text: 'Hello Playwright', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);
    await expect(page.locator('#page-input')).toHaveValue('1');
    await expect(page.locator('#page-total')).toHaveText('1');
    // The rendered page is white paper, not a blank/black failure.
    expect(await pixelAt(page, 300, 400)).toEqual([255, 255, 255, 255]);

    // "It rendered" has to mean the words are on the page, not that an image element exists.
    // A blank render, a render of the wrong page, and a render at the wrong scale all leave
    // an <img> with a data: URL; only ink in the line's own band distinguishes them.
    expect(await inkFraction(page, { x: 70, y: 696, width: 160, height: 16 })).toBeGreaterThan(0.05);
    // ...and only in that band: an all-over grey/black render would light this up too.
    expect(await inkFraction(page, { x: 70, y: 300, width: 160, height: 16 })).toBe(0);
    // The characters really are the ones in the file.
    await expectText(page).toBe('Hello Playwright');
    await page.close();
  });

  test('open image: a PNG opens as an editable one-page PDF (#26)', async () => {
    // A 16x16 solid-red opaque PNG, generated once (zlib-deflated RGB scanlines).
    const pngB64 = 'iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAIAAACQkWg2AAAAFklEQVR4nGO4IyJCEmIY1TCqYfhqAAACcQQQFwFJQgAAAABJRU5ErkJggg==';
    const imgPath = path.join(fixtureDir, 'photo.png');
    fs.writeFileSync(imgPath, Buffer.from(pngB64, 'base64'));

    // Opening the image converts it to a PDF page in the host, then loads it like any document.
    const page = await openViewerWith(imgPath);
    await expect(page.locator('#page-total')).toHaveText('1');
    await expect(page.locator('#status')).toContainText('photo.pdf');

    // The red image is drawn on the page — ink in the centred band it was fitted into.
    expect(await inkFraction(page, { x: 120, y: 350, width: 300, height: 150 })).toBeGreaterThan(0);
    await page.close();
  });

  test('drag-and-drop: dropping an image onto the window opens it (#26)', async () => {
    const pngB64 = 'iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAIAAACQkWg2AAAAFklEQVR4nGO4IyJCEmIY1TCqYfhqAAACcQQQFwFJQgAAAABJRU5ErkJggg==';
    const page = await ext.context.newPage();
    await page.goto(ext.viewerUrl);

    // Build a DataTransfer carrying a PNG File and dispatch a real drop on the body.
    const dt = await page.evaluateHandle((b64) => {
      const bytes = Uint8Array.from(atob(b64), (c) => c.charCodeAt(0));
      const data = new DataTransfer();
      data.items.add(new File([bytes], 'dropped.png', { type: 'image/png' }));
      return data;
    }, pngB64);
    await page.dispatchEvent('body', 'drop', { dataTransfer: dt });

    // It is converted to a one-page PDF and rendered, exactly like the file-picker path.
    await expect(page.locator(pageImageSel(1))).toHaveAttribute('src', /data:image\/png/);
    await expect(page.locator('#page-total')).toHaveText('1');
    await expect(page.locator('#status')).toContainText('dropped.pdf');
    await page.close();
  });

  test('redaction: the Privacy slider governs areas that were already marked (#privacy)', async () => {
    const file = fixture('privacy-all.pdf', [[
      { text: 'SECRET', x: 72, y: 700 },
      { text: 'PUBLIC', x: 320, y: 700 },
    ]]);
    const page = await openViewerWith(file);

    await ui(page, '#tool-redact');
    // Mark one word at the default (Exact) level — the chip shows no type suffix.
    await dragPdfRect(page, { x: 60, y: 690, width: 120, height: 34 });
    await expect(page.locator('#redact-list li')).toContainText('#1 p1');
    await expect(page.locator('#redact-list li')).not.toContainText('Full line');

    // Raise Privacy to Full line. Marking first and choosing the level afterwards is the ordinary
    // order of work, so the already-marked area has to follow the slider rather than keep the
    // weaker level it was drawn under — otherwise the box silently comes back exact.
    await page.locator('#redact-intensity').evaluate((el) => {
      el.value = '3';
      el.dispatchEvent(new Event('input'));
    });
    await expect(page.locator('#redact-list li')).toContainText('Full line');

    await page.click('#redact-apply');
    await expect(page.locator('#status')).toContainText('content removed');
    // Full-line took effect globally: the far end of the line is blacked out and its text is gone.
    const stats = await bandStats(page, { x: 300, y: 694, width: 240, height: 22 });
    expect(stats.ink).toBe(1);
    await expectText(page).not.toContain('PUBLIC');
    await page.close();
  });

  test('redaction: draw, preview window, apply — content removed and box painted', async () => {
    const file = fixture('redact.pdf', [[
      { text: 'TOP SECRET DATA', x: 72, y: 700 },
      { text: 'public information', x: 72, y: 600 },
    ]]);
    const page = await openViewerWith(file);

    // Both lines start out in the extracted text, and the surviving line's ink is measured now
    // so the assertions afterwards can prove it did not move, shrink or get scrubbed too.
    await expectText(page).toContain('TOP SECRET DATA');
    await expectText(page).toContain('public information');
    const survivorBefore = await inkBounds(page, { x: 60, y: 590, width: 300, height: 30 });
    expect(survivorBefore).not.toBeNull();

    await ui(page, '#tool-redact');
    await dragPdfRect(page, { x: 60, y: 690, width: 260, height: 34 });
    await expect(page.locator('#redact-list li')).toHaveCount(1);

    // Preview window renders the redacted copy without touching the document.
    await page.click('#redact-preview');
    const dialog = page.locator('dialog#modal');
    await expect(dialog).toBeVisible();
    await expect(dialog.locator('.preview-pages img')).toHaveCount(1);
    await expect(dialog).toContainText('permanently');

    // Apply from the preview.
    await dialog.getByRole('button', { name: 'Apply redaction' }).click();
    await expect(page.locator('#status')).toContainText('content removed');
    await expect(page.locator('#redact-list li')).toHaveCount(0);

    // The region renders as opaque black; untouched text area stays white.
    expect(await pixelAt(page, 180, 707)).toEqual([0, 0, 0, 255]);
    expect(await pixelAt(page, 400, 400)).toEqual([255, 255, 255, 255]);

    // Redaction has to satisfy three separate things, and painting a box only satisfies one.
    // 1. The characters are gone from the file — a black rectangle over live text is the
    //    classic redaction failure, and it is invisible to a pixel test. The surviving line is
    //    asserted first so the negative cannot be met by a text layer that has not rebuilt yet.
    await expectText(page).toContain('public information');
    await expectText(page).not.toContain('TOP SECRET');
    // 2. The box is solid, over the whole marked band, not a thin outline or a partial cover.
    const covered = await bandStats(page, { x: 62, y: 692, width: 256, height: 30 });
    expect(covered.ink).toBe(1);
    expect(covered.dominant).toEqual([0, 0, 0]);
    // 3. Nothing else was disturbed: the other line's ink occupies the same box, to the pixel.
    const survivorAfter = await inkBounds(page, { x: 60, y: 590, width: 300, height: 30 });
    expect(survivorAfter.x).toBeCloseTo(survivorBefore.x, 0);
    expect(survivorAfter.y).toBeCloseTo(survivorBefore.y, 0);
    expect(survivorAfter.width).toBeCloseTo(survivorBefore.width, 0);
    expect(survivorAfter.height).toBeCloseTo(survivorBefore.height, 0);
    await page.close();
  });

  test('redaction report: shows per-page counts after applying (#48)', async () => {
    const file = fixture('redact-report.pdf', [[
      { text: 'TOP SECRET DATA', x: 72, y: 700 },
      { text: 'public information', x: 72, y: 600 },
    ]]);
    const page = await openViewerWith(file);

    await ui(page, '#tool-redact');
    await dragPdfRect(page, { x: 60, y: 690, width: 260, height: 34 });
    await expect(page.locator('#redact-list li')).toHaveCount(1);

    await page.click('#redact-apply');

    // The audit modal appears with a per-page table and a totals row.
    const dialog = page.locator('dialog#modal');
    await expect(dialog).toBeVisible();
    await expect(dialog).toContainText('Redaction report');
    await expect(dialog.locator('.report-table')).toContainText('Text runs');
    const totals = dialog.locator('.report-total');
    await expect(totals).toContainText('Total');
    // One region was applied; at least one text run sat under it.
    await expect(totals).toContainText('1');

    // The compliance detail shows the actual text that was removed, and offers a download.
    await expect(dialog).toContainText('Removed content');
    await expect(dialog.locator('.report-removed')).toContainText('TOP SECRET');
    await expect(dialog.getByRole('button', { name: /Download compliance report/ })).toBeVisible();

    await dialog.getByRole('button', { name: 'Close' }).click();
    await expect(dialog).toBeHidden();
    await page.close();
  });

  test('redaction: full-line box hides the length and clears the whole line (#privacy)', async () => {
    // Two words on one line; we mark only the first, but choose the length-hiding "Full line" box.
    const file = fixture('rlen.pdf', [[
      { text: 'SECRET', x: 72, y: 700 },
      { text: 'PUBLIC', x: 320, y: 700 },
    ]]);
    const page = await openViewerWith(file);

    await ui(page, '#tool-redact');
    // Privacy intensity 3 = "Full line". The order does not matter — the slider governs areas
    // marked before it moves as well as after — but here it is chosen up front.
    await page.locator('#redact-intensity').evaluate((el) => {
      el.value = '3';
      el.dispatchEvent(new Event('input'));
    });
    await dragPdfRect(page, { x: 60, y: 690, width: 120, height: 34 });
    await page.click('#redact-apply');
    await expect(page.locator('#status')).toContainText('content removed');

    // The black box now spans the whole line, so its width leaks nothing about the marked word...
    const farRight = { x: 300, y: 694, width: 240, height: 22 };
    const stats = await bandStats(page, farRight);
    expect(stats.ink).toBe(1);
    expect(stats.dominant).toEqual([0, 0, 0]);
    // ...and every word on the line — not just the one marked — is gone from the file.
    await expectText(page).not.toContain('SECRET');
    await expectText(page).not.toContain('PUBLIC');
    await page.close();
  });

  test('search & mark: match mode and case sensitivity control what is marked (#search)', async () => {
    const file = fixture('search-modes.pdf', [[
      { text: 'Assignment assign ASSIGN', x: 72, y: 700 },
    ]]);
    const page = await openViewerWith(file);
    await ui(page, '#tool-redact');

    // Default: case-insensitive, contains → "assign" matches inside "Assignment", "assign", "ASSIGN".
    await page.fill('#redact-search-text', 'assign');
    await page.click('#redact-search-btn');
    await expect(page.locator('#redact-list li')).toHaveCount(3);
    await page.click('#redact-clear');
    await expect(page.locator('#redact-list li')).toHaveCount(0);

    // Whole word → only the standalone "assign"/"ASSIGN" (still case-insensitive), not "Assignment".
    await page.selectOption('#redact-match-mode', 'wholeWord');
    await page.fill('#redact-search-text', 'assign');
    await page.click('#redact-search-btn');
    await expect(page.locator('#redact-list li')).toHaveCount(2);
    await page.click('#redact-clear');

    // Case sensitive whole word → only the exact-case "ASSIGN".
    await page.check('#redact-case');
    await page.fill('#redact-search-text', 'ASSIGN');
    await page.click('#redact-search-btn');
    await expect(page.locator('#redact-list li')).toHaveCount(1);
    await page.close();
  });

  test('search & mark: finds every occurrence of a phrase, marks boxes, redacts them', async () => {
    // Two copies of the secret word plus an unrelated line. Searching marks both copies as
    // redaction boxes; applying blacks out both spots and leaves the other line untouched.
    const file = fixture('search-redact.pdf', [[
      { text: 'CONFIDENTIAL summary', x: 72, y: 700 },
      { text: 'again CONFIDENTIAL here', x: 72, y: 600 },
      { text: 'ordinary public line', x: 72, y: 500 },
    ]]);
    const page = await openViewerWith(file);
    const bystanderBefore = await inkBounds(page, { x: 60, y: 490, width: 320, height: 30 });
    expect(bystanderBefore).not.toBeNull();

    // The Redact panel is shown by the redact tool; the search box lives inside it.
    await ui(page, '#tool-redact');
    await page.fill('#redact-search-text', 'CONFIDENTIAL');
    await page.click('#redact-search-btn');

    // Both occurrences are marked as boxes (and the input is cleared for the next search).
    await expect(page.locator('#redact-list li')).toHaveCount(2);
    await expect(page.locator('#redact-search-text')).toHaveValue('');

    await page.click('#redact-apply');
    await expect(page.locator('#status')).toContainText('content removed');

    // Both words are blacked out; the ordinary line survives (its glyphs are not a black box)
    // and blank paper stays white.
    expect(await pixelAt(page, 90, 704)).toEqual([0, 0, 0, 255]);
    expect(await pixelAt(page, 120, 604)).toEqual([0, 0, 0, 255]);
    expect(await pixelAt(page, 90, 504)).not.toEqual([0, 0, 0, 255]);
    expect(await pixelAt(page, 400, 504)).toEqual([255, 255, 255, 255]);

    // Both copies of the word are gone from the file, not merely covered up — and the search
    // did not overreach: the substring lives nowhere in the page's text any more, while the
    // words that surrounded it on those same lines survive.
    // Positive first: it is what waits for the rebuilt text layer, so the negative below cannot
    // be satisfied by an empty page (see expectText).
    await expectCompactText(page).toContain('summary');
    await expectCompactText(page).toContain('here');
    await expectCompactText(page).not.toContain('CONFIDENTIAL');
    // The unrelated third line is untouched, character for character and pixel for pixel.
    await expectText(page).toContain('ordinary public line');
    const bystanderAfter = await inkBounds(page, { x: 60, y: 490, width: 320, height: 30 });
    expect(bystanderAfter.x).toBeCloseTo(bystanderBefore.x, 0);
    expect(bystanderAfter.y).toBeCloseTo(bystanderBefore.y, 0);
    expect(bystanderAfter.width).toBeCloseTo(bystanderBefore.width, 0);
    await page.close();
  });

  test('redaction lands on the text on a Chrome/Google-Docs PDF (leftover transform)', async () => {
    // Regression for the reported bug: Chrome / Google-Docs-exported PDFs leave a scale+flip
    // matrix active at the end of the page content. The black box used to inherit it and land
    // scaled/flipped away, while the text under it was correctly removed. Here we search for the
    // word (absolute coordinates, no screen mapping), redact it, and prove the black box covers
    // exactly the pixels the word occupied — end-to-end through the extension and native host.
    const file = path.join(fixtureDir, 'leftover-ctm.pdf');
    fs.writeFileSync(file, buildLeftoverCtmPdf('SECRET'));
    const page = await openViewerWith(file);

    // Find the centroid of the redacted word's dark pixels (natural-image pixels). The scan is
    // limited to the top 62% of the sheet: under the leftover matrix SECRET renders around
    // absolute y=300 (image row ~50%) and the KEEPME control around y=150 (image row ~75%), and
    // a centroid averaged over both words would land between them, on blank paper.
    const darkCentroid = async () => page.evaluate(async () => {
      const img = document.querySelector('.page[data-page="1"] .page-image');
      await img.decode();
      const c = document.createElement('canvas');
      c.width = img.naturalWidth; c.height = img.naturalHeight;
      const ctx = c.getContext('2d');
      ctx.drawImage(img, 0, 0);
      const { data, width, height } = ctx.getImageData(0, 0, c.width, c.height);
      let sx = 0, sy = 0, n = 0;
      const lastRow = Math.round(height * 0.62);
      for (let y = 0; y < lastRow; y++) {
        for (let x = 0; x < width; x++) {
          const i = (y * width + x) * 4;
          if (data[i] < 100 && data[i + 1] < 100 && data[i + 2] < 100) { sx += x; sy += y; n++; }
        }
      }
      return n ? { x: Math.round(sx / n), y: Math.round(sy / n), n, width, height } : null;
    });

    const before = await darkCentroid();
    expect(before).not.toBeNull();       // the word actually renders
    expect(before.n).toBeGreaterThan(50);
    await expectText(page).toContain('SECRET');
    await expectText(page).toContain('KEEPME');

    // Search + mark + apply through the real UI / native host.
    await ui(page, '#tool-redact');
    await page.fill('#redact-search-text', 'SECRET');
    await page.click('#redact-search-btn');
    await expect(page.locator('#redact-list li')).toHaveCount(1);
    await page.click('#redact-apply');
    await expect(page.locator('#status')).toContainText('content removed');

    // The pixel at the word's former centroid is now opaque black — the box landed on the word.
    const pixel = await page.evaluate(async (pt) => {
      const img = document.querySelector('.page[data-page="1"] .page-image');
      await img.decode();
      const c = document.createElement('canvas');
      c.width = img.naturalWidth; c.height = img.naturalHeight;
      const ctx = c.getContext('2d');
      ctx.drawImage(img, 0, 0);
      // Sample relative to the (possibly re-rendered at a different scale) image.
      const x = Math.round(pt.x / pt.width * c.width);
      const y = Math.round(pt.y / pt.height * c.height);
      return [...ctx.getImageData(x, y, 1, 1).data];
    }, before);
    expect(pixel).toEqual([0, 0, 0, 255]);

    // Where the box landed is only half of it. A build that removes no text at all and merely
    // paints a rectangle passes every geometry check above, so the word has to be gone from the
    // document too — with the control word asserted first, so an empty read cannot satisfy it.
    await expectText(page).toContain('KEEPME');
    await expectText(page).not.toContain('SECRET');
    await page.close();
  });

  test('search & mark: reports when a phrase is not found and marks nothing', async () => {
    const file = fixture('search-none.pdf', [[{ text: 'nothing to hide here', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);

    await ui(page, '#tool-redact');
    await page.fill('#redact-search-text', 'MISSING');
    await page.click('#redact-search-btn');

    await expect(page.locator('#status')).toContainText('No matches');
    await expect(page.locator('#redact-list li')).toHaveCount(0);
    await page.close();
  });

  test('continuous scroll: all pages stack and the counter tracks the visible page', async () => {
    const file = fixture('scroll.pdf', [
      [{ text: 'Page one', x: 72, y: 700 }],
      [{ text: 'Page two', x: 72, y: 700 }],
      [{ text: 'Page three', x: 72, y: 700 }],
    ]);
    const page = await openViewerWith(file);

    await expect(page.locator('.page')).toHaveCount(3); // every page laid out in the column
    await expect(page.locator('#page-input')).toHaveValue('1');
    await expect(page.locator('#page-total')).toHaveText('3');
    await expect(page.locator('#btn-prev')).toBeDisabled();

    // Paging forward scrolls the next page into view; the counter follows what's visible,
    // and that page renders lazily once it's near the viewport.
    await page.click('#btn-next');
    await expect(page.locator('#page-input')).toHaveValue('2');
    await expect(page.locator(pageImageSel(2))).toHaveAttribute('src', /data:image\/png/);

    await page.click('#btn-next');
    await expect(page.locator('#page-input')).toHaveValue('3');
    await expect(page.locator('#btn-next')).toBeDisabled();

    await page.click('#btn-prev');
    await expect(page.locator('#page-input')).toHaveValue('2');
    await page.close();
  });

  test('editable page counter: typing a number jumps to that page', async () => {
    const file = fixture('jump.pdf', [
      [{ text: 'One', x: 72, y: 700 }],
      [{ text: 'Two', x: 72, y: 700 }],
      [{ text: 'Three', x: 72, y: 700 }],
    ]);
    const page = await openViewerWith(file);

    await page.fill('#page-input', '3');
    await page.press('#page-input', 'Enter');
    await expect(page.locator('#page-input')).toHaveValue('3');
    await expect(page.locator('#btn-next')).toBeDisabled();

    // Out-of-range input is clamped/rejected rather than navigating off the end.
    await page.fill('#page-input', '99');
    await page.press('#page-input', 'Enter');
    await expect(page.locator('#page-input')).toHaveValue('3');
    await page.close();
  });

  test('thumbnail sidebar: toggles, lists every page, and navigates on click', async () => {
    const file = fixture('thumbs.pdf', [
      [{ text: 'Alpha', x: 72, y: 700 }],
      [{ text: 'Beta', x: 72, y: 700 }],
      [{ text: 'Gamma', x: 72, y: 700 }],
    ]);
    const page = await openViewerWith(file);

    await expect(page.locator('#thumbnails')).toBeHidden();
    await page.click('#btn-sidebar');
    await expect(page.locator('#thumbnails')).toBeVisible();
    await expect(page.locator('#thumbnails .thumb')).toHaveCount(3);
    // The first thumbnail renders an image, and page 1 is marked current.
    await expect(page.locator('#thumbnails .thumb[data-page="1"] img')).toHaveAttribute('src', /data:image\/png/);
    await expect(page.locator('#thumbnails .thumb[data-page="1"]')).toHaveClass(/current/);

    // Clicking a thumbnail navigates to that page.
    await page.click('#thumbnails .thumb[data-page="3"]');
    await expect(page.locator('#page-input')).toHaveValue('3');
    await expect(page.locator('#thumbnails .thumb[data-page="3"]')).toHaveClass(/current/);

    await page.click('#btn-sidebar');
    await expect(page.locator('#thumbnails')).toBeHidden();
    await page.close();
  });

  test('rotate: turns the current page a quarter turn (portrait -> landscape)', async () => {
    const file = fixture('rotate.pdf', [[{ text: 'Portrait', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);

    const portrait = await page.locator('.page[data-page="1"]').boundingBox();
    expect(portrait.height).toBeGreaterThan(portrait.width); // A4 starts portrait

    // How much of the page is ink before the turn. A rotation that drops or blanks the page
    // content is the failure that matters, and a landscape bounding box says nothing about it.
    const inkBefore = await inkFraction(page, { x: 0, y: 0, width: 595, height: 842 });
    expect(inkBefore).toBeGreaterThan(0);

    await ui(page, '#btn-rotate-right');
    await expect(page.locator('#status')).toContainText('Rotated page 1');

    // After a 90° turn the laid-out page is landscape (width/height swapped).
    await expect.poll(async () => {
      const b = await page.locator('.page[data-page="1"]').boundingBox();
      return b.width > b.height;
    }).toBe(true);

    // The rendered bitmap itself is landscape, not a portrait image in a landscape frame.
    const [w, h] = await page.evaluate(async (sel) => {
      const img = document.querySelector(sel);
      await img.decode();
      return [img.naturalWidth, img.naturalHeight];
    }, pageImageSel(1));
    expect(w).toBeGreaterThan(h);

    // And the content survived the turn: the same words are still extractable, and the same
    // amount of ink is on the page (rotating into a clipped or blank raster would lose it).
    await expectText(page).toBe('Portrait');
    const inkAfter = await inkFraction(page, { x: 0, y: 0, width: 842, height: 595 },
      { mediaBox: [0, 0, 842, 595] });
    expect(inkAfter).toBeGreaterThan(inkBefore * 0.8);
    expect(inkAfter).toBeLessThan(inkBefore * 1.2);
    await page.close();
  });

  test('add text: click to place a text box, type, and stamp it onto the page', async () => {
    const file = fixture('addtext.pdf', [[{ text: 'background', x: 72, y: 100 }]]);
    const page = await openViewerWith(file);

    await ui(page, '#tool-text');
    // Click (no drag) on the page to drop a default text box near the top.
    const box = await page.locator(pageImageSel(1)).boundingBox();
    const scale = box.width / 595;
    await page.mouse.click(box.x + 120 * scale, box.y + (842 - 700) * scale);

    await expect(page.locator('#panel-edit')).toBeVisible();
    await expect(page.locator('#edit-title')).toHaveText('Add text');
    await page.fill('#edit-text', 'STAMPED CAPTION');
    await page.click('#edit-apply');
    await expect(page.locator('#status')).toContainText('Text added');

    // The new text is really in the document (and the original still there).
    await ui(page, '#tool-edit');
    await dragPdfRect(page, { x: 60, y: 675, width: 320, height: 45 });
    await expect(page.locator('#edit-text')).toHaveValue(/STAMPED CAPTION/);
    await page.close();
  });

  test('draw: freehand strokes are baked onto the page in the chosen colour', async () => {
    const file = fixture('draw.pdf', [[{ text: 'canvas', x: 72, y: 100 }]]);
    const page = await openViewerWith(file);

    await ui(page, '#tool-draw');
    await expect(page.locator('#panel-draw')).toBeVisible();
    // Use a pure-green pen so we can detect it unambiguously.
    await page.fill('#draw-color', '#00ff00');
    await page.fill('#draw-width', '8');

    // Draw a stroke straight across the middle of the page (display coordinates).
    const box = await page.locator(pageImageSel(1)).boundingBox();
    const midY = box.y + box.height * 0.5;
    await page.mouse.move(box.x + box.width * 0.2, midY);
    await page.mouse.down();
    await page.mouse.move(box.x + box.width * 0.4, midY, { steps: 6 });
    await page.mouse.move(box.x + box.width * 0.7, midY, { steps: 6 });
    await page.mouse.up();

    await page.click('#draw-apply');
    await expect(page.locator('#status')).toContainText('stroke');

    // "A green pixel exists somewhere on the page" was the old assertion, and it holds however
    // wrongly the stroke is placed — wrong page origin, wrong scale, a single stray dot. What
    // has to be true is that the ink is *along the line that was drawn*: the band the pointer
    // swept is substantially green, and the paper a stroke-width above it is untouched.
    const GREEN = [0, 255, 0];
    // The pointer swept the vertical middle of the page at 20%..70% of its width.
    const swept = { x: 595 * 0.25, y: 842 * 0.5 - 5, width: 595 * 0.4, height: 10 };
    expect(await colorFraction(page, swept, GREEN, { tolerance: 40 })).toBeGreaterThan(0.5);
    // Clear of the 8pt nib, above the line: still blank paper.
    const above = { x: 595 * 0.25, y: 842 * 0.5 + 20, width: 595 * 0.4, height: 10 };
    expect(await colorFraction(page, above, GREEN, { tolerance: 40 })).toBe(0);
    expect(await bandStats(page, above).then((s) => s.paper)).toBe(1);
    // And before 20% of the width, where the pen was not yet down.
    const beforeStart = { x: 20, y: 842 * 0.5 - 5, width: 60, height: 10 };
    expect(await colorFraction(page, beforeStart, GREEN, { tolerance: 40 })).toBe(0);
    await page.close();
  });

  test('forms: lists AcroForm fields, fills a value, and reads it back', async () => {
    const file = path.join(fixtureDir, 'form.pdf');
    fs.writeFileSync(file, buildFormPdf('fullName', ''));
    const page = await openViewerWith(file);

    await ui(page, '#btn-forms');
    await expect(page.locator('#panel-forms')).toBeVisible();
    const field = page.locator('#forms-list [data-field="fullName"]');
    await expect(field).toHaveCount(1);
    // The field's rectangle ([100 700 300 724]) is empty paper before the fill.
    const fieldRect = { x: 102, y: 702, width: 196, height: 20 };
    expect(await inkFraction(page, fieldRect)).toBe(0);

    await field.fill('Alan Turing');
    await page.click('#forms-apply');
    await expect(page.locator('#status')).toContainText('Form filled');

    // Re-opening the forms panel shows the value persisted into the document.
    await ui(page, '#btn-forms');
    await expect(page.locator('#forms-list [data-field="fullName"]')).toHaveValue('Alan Turing');

    // But a value round-tripping through the panel is exactly the assertion that proves nothing:
    // the panel is fed from the viewer's own state. What matters is that the name is *drawn on
    // the page*, in the field's own rectangle — a filled field with no regenerated appearance
    // stream reads back fine in the panel and prints blank.
    await expect.poll(() => inkFraction(page, fieldRect), { timeout: 20000 }).toBeGreaterThan(0.01);
    // ...and it is drawn *inside* the widget. Sampling the whole line rather than just the
    // widget makes this a real containment check: value text that escapes its own rectangle
    // (wrong appearance box, wrong font size) shows up here and cannot hide inside the crop.
    const wholeLine = { x: 40, y: 696, width: 500, height: 32 };
    const drawn = await inkBounds(page, wholeLine);
    expect(drawn.x).toBeGreaterThanOrEqual(100);
    expect(drawn.x + drawn.width).toBeLessThanOrEqual(300);
    await page.close();
  });

  test('flatten: forms mode bakes fields into static content (#44)', async () => {
    const file = path.join(fixtureDir, 'flatten-forms.pdf');
    fs.writeFileSync(file, buildFormPdf('fullName', 'Ada Lovelace'));
    const page = await openViewerWith(file);

    // The field is interactive before flattening.
    await ui(page, '#btn-forms');
    await expect(page.locator('#forms-list [data-field="fullName"]')).toHaveCount(1);

    await ui(page, '#btn-flatten');
    const dialog = page.locator('dialog#modal');
    await dialog.locator('select').selectOption('forms');
    await dialog.getByRole('button', { name: 'Flatten' }).click();
    await expect(page.locator('#status')).toContainText('Flattened 1 form field');

    // No interactive field remains, but its value is still drawn in the field's rectangle.
    await ui(page, '#btn-forms');
    await expect(page.locator('#forms-list [data-field="fullName"]')).toHaveCount(0);
    expect(await inkFraction(page, { x: 102, y: 702, width: 196, height: 20 })).toBeGreaterThan(0.01);
    await page.close();
  });

  test('forms: insert a new text field by drawing a box', async () => {
    const file = fixture('insertfield.pdf', [[{ text: 'blank form', x: 72, y: 100 }]]);
    const page = await openViewerWith(file);

    await ui(page, '#btn-forms');
    await expect(page.locator('#panel-forms')).toBeVisible();
    await page.selectOption('#field-type', 'text');
    await page.fill('#field-name', 'signature_name');
    await page.click('#field-place');
    await expect(page.locator('#status')).toContainText('Drag a box');

    await dragPdfRect(page, { x: 100, y: 600, width: 220, height: 24 });

    // The forms panel reopens and lists the newly inserted field.
    await expect(page.locator('#forms-list [data-field="signature_name"]')).toHaveCount(1);
    await page.close();
  });

  test('forms: clicking a button simulates its calculation script (#18)', async () => {
    const file = path.join(fixtureDir, 'button-calc.pdf');
    fs.writeFileSync(file, buildFormWithButtonScriptPdf());
    const page = await openViewerWith(file);

    await ui(page, '#btn-forms');
    await expect(page.locator('#panel-forms')).toBeVisible();
    await expect(page.locator('#forms-list [data-field="a"]')).toHaveValue('2');
    await expect(page.locator('#forms-list [data-field="b"]')).toHaveValue('3');

    // Edit the inputs, then run the button's script — it should read the live panel values.
    await page.locator('#forms-list [data-field="a"]').fill('10');
    await page.locator('#forms-list [data-field="b"]').fill('5');
    await page.locator('.form-field[data-field-name="calc"] .form-field-run').click();

    await expect(page.locator('#forms-list [data-field="total"]')).toHaveValue('15');
    await expect(page.locator('#status')).toContainText('Ran "calc"');
    await page.close();
  });

  test('forms: fields are fillable directly on the page and stay in sync with the panel', async () => {
    const file = path.join(fixtureDir, 'onpage.pdf');
    fs.writeFileSync(file, buildFormWithButtonScriptPdf());
    const page = await openViewerWith(file);

    // Type into the field where it sits on the page, not in the side panel.
    const onPage = page.locator('.field-marker [data-page-field="a"]');
    await expect(onPage).toHaveCount(1);
    await onPage.fill('42');

    // The panel reflects it...
    await ui(page, '#btn-forms');
    await expect(page.locator('#forms-list [data-field="a"]')).toHaveValue('42');
    // ...and editing the panel flows back to the page.
    await page.locator('#forms-list [data-field="a"]').fill('7');
    await expect(onPage).toHaveValue('7');
    await page.close();
  });

  test('forms: clicking a scripted button on the page runs it', async () => {
    const file = path.join(fixtureDir, 'onpage-btn.pdf');
    fs.writeFileSync(file, buildFormWithButtonScriptPdf());
    const page = await openViewerWith(file);

    await page.locator('.field-marker [data-page-field="a"]').fill('10');
    await page.locator('.field-marker [data-page-field="b"]').fill('5');
    await page.locator('.field-marker .field-input-button').click();

    await expect(page.locator('.field-marker [data-page-field="total"]')).toHaveValue('15');
    await page.close();
  });

  test('forms: an app.alert button shows the message like a real reader', async () => {
    const file = path.join(fixtureDir, 'button-alert.pdf');
    fs.writeFileSync(file, buildFormWithButtonScriptPdf("app.alert('thanks!');"));
    const page = await openViewerWith(file);

    await ui(page, '#btn-forms');
    await page.locator('.form-field[data-field-name="calc"] .form-field-run').click();

    const dialog = page.locator('dialog#modal');
    await expect(dialog).toBeVisible();
    await expect(dialog).toContainText('thanks!');
    await dialog.getByRole('button', { name: 'OK' }).click();
    await expect(dialog).toBeHidden();
    await page.close();
  });

  test('forms: a button script outside the supported grammar is reported, not silently ignored (#18/#22)', async () => {
    const file = path.join(fixtureDir, 'button-unsupported.pdf');
    // Deliberately outside the grammar: a submit call, not something the viewer can stand in for.
    fs.writeFileSync(file, buildFormWithButtonScriptPdf("this.submitForm('https://example.com');"));
    const page = await openViewerWith(file);

    await ui(page, '#btn-forms');
    await page.locator('.form-field[data-field-name="calc"] .form-field-run').click();

    await expect(page.locator('#status')).toContainText("can't simulate");
    await expect(page.locator('#forms-list [data-field="total"]')).toHaveValue('');
    await page.close();
  });

  test('forms: adding a scripted button notifies that its JavaScript will be kept (#22)', async () => {
    const file = fixture('insertbutton.pdf', [[{ text: 'blank form', x: 72, y: 100 }]]);
    const page = await openViewerWith(file);

    await ui(page, '#btn-forms');
    await page.selectOption('#field-type', 'button');
    await page.fill('#field-name', 'go');
    await page.fill('#field-caption', 'Go');
    await page.fill('#field-script', "app.alert('hi');");
    await page.click('#field-place');
    await dragPdfRect(page, { x: 100, y: 600, width: 90, height: 24 });

    await expect(page.locator('#status')).toContainText('JavaScript will be kept');
    await page.close();
  });

  test('safety: opening a form whose field carries JavaScript raises the warning badge', async () => {
    // Field-level scripts live on a widget annotation's /A, not in the document-level name tree
    // or /OpenAction -- so this covers a path the document-script test above does not.
    const file = path.join(fixtureDir, 'formjs.pdf');
    fs.writeFileSync(file, buildFormWithButtonScriptPdf("app.alert('FORM_FIELD_MARKER_456');"));
    const page = await openViewerWith(file);

    const badge = page.locator('#badges .badge.warn', { hasText: 'JavaScript' });
    await expect(badge).toBeVisible();
    await expect(badge).toContainText('disabled');

    // ...and the details dialog names the field's actual script, not a generic notice.
    await badge.click();
    const dialog = page.locator('dialog#modal');
    await expect(dialog).toBeVisible();
    await expect(dialog.locator('.safety-samples')).toContainText('FORM_FIELD_MARKER_456');
    await page.close();
  });

  test('safety: JavaScript is detected, flagged with its source, and stripped by default', async () => {
    const file = path.join(fixtureDir, 'hasjs.pdf');
    fs.writeFileSync(file, buildJavaScriptPdf("app.alert('DISTINCTIVE_MARKER_123');"));
    const page = await openViewerWith(file);

    // The active-content badge appears and reads "disabled" by default.
    const badge = page.locator('#badges .badge.warn', { hasText: 'JavaScript' });
    await expect(badge).toBeVisible();
    await expect(badge).toContainText('disabled');

    // The details dialog points at the actual script source and lets the user opt in to keeping it.
    await badge.click();
    const dialog = page.locator('dialog#modal');
    await expect(dialog).toBeVisible();
    await expect(dialog).toContainText('JavaScript');
    await expect(dialog.locator('.safety-samples')).toContainText('DISTINCTIVE_MARKER_123');
    await dialog.getByRole('button', { name: /Enable \(keep\)/ }).click();
    await expect(badge).toContainText('kept');
    await page.close();
  });

  test('links: a document with URLs warns, disables them, and can list the source', async () => {
    // Links are detected and disabled by default (a warning badge), with the panel listing every
    // URL (the source) and an opt-in to keep them.
    const file = path.join(fixtureDir, 'links.pdf');
    fs.writeFileSync(file, buildLinkPdf('https://github.com/example/repo'));
    const page = await openViewerWith(file);

    // A warning badge appears reading "disabled".
    const badge = page.locator('#badges .badge.warn', { hasText: 'links' });
    await expect(badge).toBeVisible();
    await expect(badge).toContainText('disabled');

    // Clicking it opens the Links panel, which lists the URL and offers to enable it.
    await badge.click();
    await expect(page.locator('#panel-links')).toBeVisible();
    await expect(page.locator('#links-list')).toContainText('github.com/example/repo');
    await expect(page.locator('#links-enable')).not.toBeChecked();

    // Enabling flips the badge to "enabled".
    await page.locator('#links-enable').check();
    await expect(badge).toContainText('enabled');
    await page.close();
  });

  test('links: the panel does not keep the previous document\'s links (#24)', async () => {
    // Regression (#24): opening another document left the Links panel listing the OLD document's
    // URLs. loadDocument() cleared state.linkHotspots (the on-page overlay) but never state.links
    // (the panel's list), and nothing re-ran openLinks() for the panel that was already showing.
    const first = path.join(fixtureDir, 'links-first.pdf');
    fs.writeFileSync(first, buildLinkPdf('https://github.com/example/FIRST-DOC'));
    const second = path.join(fixtureDir, 'links-second.pdf');
    fs.writeFileSync(second, buildLinkPdf('https://example.com/SECOND-DOC'));

    const page = await openViewerWith(first);
    await ui(page, '#btn-links');
    await expect(page.locator('#links-list')).toContainText('FIRST-DOC');

    // Open a different document while the panel is still on screen.
    const chooser = page.waitForEvent('filechooser');
    await ui(page, '#btn-open');
    await (await chooser).setFiles(second);
    await expect(page.locator(pageImageSel(1))).toHaveAttribute('src', /data:image\/png/);

    // The first document's link must be gone — whether the panel refreshes or closes.
    await expect(page.locator('#links-list')).not.toContainText('FIRST-DOC');

    // And the panel must show the new document's link once it is on screen.
    if (await page.locator('#panel-links').isHidden()) await ui(page, '#btn-links');
    await expect(page.locator('#links-list')).toContainText('SECOND-DOC');
    await expect(page.locator('#links-list')).not.toContainText('FIRST-DOC');
    await page.close();
  });

  test('links: a hotspot is drawn over the link, coloured by risk, inert until enabled', async () => {
    const file = path.join(fixtureDir, 'linkspot.pdf');
    fs.writeFileSync(file, buildLinkPdf('https://github.com/example/repo'));
    const page = await openViewerWith(file);

    // A hotspot is laid over the link's rectangle; github rates yellow (code-hosting heuristic).
    const hotspot = page.locator('.page[data-page="1"] .link-hotspot');
    await expect(hotspot).toHaveCount(1, { timeout: 15000 });
    await expect(hotspot).toHaveClass(/risk-yellow/);
    await expect(hotspot.locator('.link-risk-dot.yellow')).toHaveCount(1);

    // Links are disabled by default, so the hotspot is shown but NOT navigable (a plain div, no href).
    await expect(hotspot).toHaveJSProperty('tagName', 'DIV');
    await expect(hotspot).not.toHaveAttribute('href', /.*/);
    await hotspot.hover();
    const popup = page.locator('#link-popup');
    await expect(popup).toBeVisible();
    await expect(popup.locator('.lp-url')).toHaveText('https://github.com/example/repo');
    await expect(popup.locator('.lp-risk.yellow')).toBeVisible();
    await expect(popup.locator('.lp-note')).toContainText('enable links');

    // Enabling links turns the hotspot into a real anchor that opens in a new tab.
    await ui(page, '#btn-links');
    await page.locator('#links-enable').check();
    await expect(hotspot).toHaveJSProperty('tagName', 'A');
    await expect(hotspot).toHaveAttribute('href', 'https://github.com/example/repo');
    await expect(hotspot).toHaveAttribute('target', '_blank');
    await page.close();
  });

  test('links: non-URL (JavaScript) link annotations are highlighted too', async () => {
    // Salesforce-style "Close Window" links are JavaScript actions, not web URLs.
    const file = path.join(fixtureDir, 'jslink.pdf');
    fs.writeFileSync(file, buildJsLinkPdf('window.close();'));
    const page = await openViewerWith(file);

    // A hotspot is still drawn over it (rendered as a non-navigable div, neutral colour).
    const hotspot = page.locator('.page[data-page="1"] .link-hotspot');
    await expect(hotspot).toHaveCount(1, { timeout: 15000 });
    await expect(hotspot).toHaveJSProperty('tagName', 'DIV');
    // Rollover explains the action instead of a URL.
    await hotspot.hover();
    await expect(page.locator('#link-popup .lp-url')).toHaveText('JavaScript action');
    await page.close();
  });

  test('links: the overlay is drawn on a later page after scrolling to it', async () => {
    // Regression: scrolling to a page whose image comes from the render cache must still draw
    // its link hotspots (the overlay used to be skipped on the cached-render path).
    const file = path.join(fixtureDir, 'link-p2.pdf');
    fs.writeFileSync(file, buildLinkOnPage2Pdf('https://github.com/example/repo'));
    const page = await openViewerWith(file);

    // Page 1 carries no links; jump to page 2, where the annotation lives.
    await expect(page.locator('.page[data-page="1"] .link-hotspot')).toHaveCount(0);
    await page.fill('#page-input', '2');
    await page.press('#page-input', 'Enter');
    await expect(page.locator(pageImageSel(2))).toHaveAttribute('src', /data:image\/png/);

    const hotspot = page.locator('.page[data-page="2"] .link-hotspot');
    await expect(hotspot).toHaveCount(1, { timeout: 15000 });
    await expect(hotspot).toHaveClass(/risk-yellow/);
    await page.close();
  });

  test('links: the hotspot sits above the text layer so hover/click reach it', async () => {
    // Regression: the selectable text layer used to stack above the link layer (its async build
    // could insert it last), swallowing link hover/clicks. Explicit z-index pins the order.
    const file = path.join(fixtureDir, 'link-over-text.pdf');
    fs.writeFileSync(file, buildLinkOverTextPdf('https://github.com/example/repo'));
    const page = await openViewerWith(file);

    const hotspot = page.locator('.page[data-page="1"] .link-hotspot');
    await expect(hotspot).toHaveCount(1, { timeout: 15000 });

    // Wait for the link work to go idle before probing geometry. Since #19 the overlay is drawn
    // in two phases — hotspots as soon as the annotations are listed, then rebuilt to apply risk
    // colours when the scan lands — so a hit test run between them lands mid-rebuild and finds
    // the text layer instead of the hotspot. The status element is hidden once nothing is
    // in flight, which is the observable "settled" signal.
    await expect(page.locator('#link-status')).toBeHidden({ timeout: 15000 });

    // The topmost element at the hotspot's centre must be the hotspot itself, not the text layer.
    const hitsHotspot = await hotspot.evaluate((el) => {
      const r = el.getBoundingClientRect();
      const top = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
      return !!(top && top.closest('.link-hotspot'));
    });
    expect(hitsHotspot).toBe(true);

    // And hovering actually shows the rollover popup.
    await hotspot.hover();
    await expect(page.locator('#link-popup')).toBeVisible();
    await expect(page.locator('#link-popup .lp-url')).toHaveText('https://github.com/example/repo');
    await page.close();
  });

  test('links: hotspots are drawn before the risk scan finishes, with a progress hint (#19)', async () => {
    // #19: the link pipeline used to draw nothing until the URL risk scan had come back, so a
    // link-heavy document showed no hotspots at all for as long as the scan took. The hotspots
    // must now appear as soon as the annotations are listed and be recoloured afterwards, with a
    // non-blocking indicator saying the rating is still running.
    const file = path.join(fixtureDir, 'link-async.pdf');
    fs.writeFileSync(file, buildLinkPdf('https://github.com/example/repo'));

    const page = await ext.context.newPage();
    await installHostGate(page, { hold: ['scan-urls'] });
    await page.goto(ext.viewerUrl);
    const chooser = page.waitForEvent('filechooser');
    await page.click('#btn-open-empty');
    await (await chooser).setFiles(file);
    await expect(page.locator(pageImageSel(1))).toHaveAttribute('src', /data:image\/png/);

    // The hotspot is on the page while the rating is still parked, shown as not-yet-rated.
    const hotspot = page.locator('.page[data-page="1"] .link-hotspot');
    await expect(hotspot).toHaveCount(1, { timeout: 15000 });
    await expect(hotspot).toHaveClass(/risk-unknown/);

    // The indicator says so — and does not clobber the active-content warning in the status bar.
    await expect(page.locator('#link-status')).toBeVisible();
    await expect(page.locator('#link-status')).toContainText(/link/i);
    await expect(page.locator('#status')).toContainText('disabled and removed when you save');

    // Releasing the scan recolours the hotspot and clears the indicator.
    await page.evaluate(() => window.__hostGate.release());
    await expect(hotspot).toHaveClass(/risk-yellow/, { timeout: 15000 });
    await expect(page.locator('#link-status')).toBeHidden();
    await page.close();
  });

  test('links: a failed link scan clears the progress indicator (#19)', async () => {
    // A spinner that never stops is worse than none: the indicator has to clear on failure too.
    const file = path.join(fixtureDir, 'link-fail.pdf');
    fs.writeFileSync(file, buildLinkPdf('https://github.com/example/repo'));

    const page = await ext.context.newPage();
    await installHostGate(page, { fail: ['list-link-hotspots'] });
    await page.goto(ext.viewerUrl);
    const chooser = page.waitForEvent('filechooser');
    await page.click('#btn-open-empty');
    await (await chooser).setFiles(file);
    await expect(page.locator(pageImageSel(1))).toHaveAttribute('src', /data:image\/png/);

    // No hotspots (the listing failed) and, crucially, no stuck spinner. The indicator element has
    // to exist for "hidden" to mean anything — otherwise this assertion would pass vacuously.
    await expect(page.locator('#link-status')).toHaveCount(1);
    await expect(page.locator('#link-status')).toBeHidden({ timeout: 15000 });
    await expect(page.locator('#link-status')).toHaveText('');
    await expect(page.locator('.page[data-page="1"] .link-hotspot')).toHaveCount(0);
    await page.close();
  });

  test('links: a scan that lands after another document is opened is discarded (#19)', async () => {
    // The race that making the link pipeline asynchronous invites, and the back door into #24:
    // results for document A must never populate the panel or the overlay of document B. The gate
    // parks A's link responses until B is fully on screen, so A's results land strictly last.
    const stale = path.join(fixtureDir, 'link-stale-a.pdf');
    fs.writeFileSync(stale, buildMultiLinkPdf([
      'https://example.com/STALE-DOC-A/one',
      'https://example.com/STALE-DOC-A/two',
      'https://example.com/STALE-DOC-A/three',
    ]));
    const fresh = path.join(fixtureDir, 'link-fresh-b.pdf');
    fs.writeFileSync(fresh, buildLinkPdf('https://example.com/FRESH-DOC-B'));

    const page = await ext.context.newPage();
    await installHostGate(page, { hold: ['list-link-hotspots', 'scan-urls', 'list-urls'] });
    await page.goto(ext.viewerUrl);
    const chooserA = page.waitForEvent('filechooser');
    await page.click('#btn-open-empty');
    await (await chooserA).setFiles(stale);
    await expect(page.locator(pageImageSel(1))).toHaveAttribute('src', /data:image\/png/);

    // Ask for A's Links panel too, so the panel fetch is in flight as well, then wait until every
    // one of A's link requests is parked.
    await ui(page, '#btn-links');
    await expect.poll(() => page.evaluate(() => window.__hostGate.heldCount()), { timeout: 20000 })
      .toBeGreaterThanOrEqual(2);

    // Let document B load unimpeded; A's responses stay parked.
    await page.evaluate(() => window.__hostGate.stopHolding());
    const chooserB = page.waitForEvent('filechooser');
    await ui(page, '#btn-open');
    await (await chooserB).setFiles(fresh);
    await expect(page.locator(pageImageSel(1))).toHaveAttribute('src', /data:image\/png/);
    const hotspots = page.locator('.page[data-page="1"] .link-hotspot');
    await expect(hotspots).toHaveCount(1, { timeout: 20000 });
    if (await page.locator('#panel-links').isHidden()) await ui(page, '#btn-links');
    await expect(page.locator('#links-list')).toContainText('FRESH-DOC-B');

    // Now deliver document A's results. They must be dropped on the floor.
    expect(await page.evaluate(() => window.__hostGate.release())).toBeGreaterThan(0);

    // Synchronise on something observable rather than sleeping: the gate reports every parked
    // response handed back to the page, and `settled()` resolves after the microtask queue has
    // drained and a frame has been painted — so each continuation those responses resumed has
    // run to the point where it would have written to the DOM.
    await expect.poll(() => page.evaluate(() => window.__hostGate.heldCount())).toBe(0);
    await page.evaluate(() => window.__hostGate.settled());

    await expect(hotspots).toHaveCount(1);
    await expect(page.locator('#links-list')).not.toContainText('STALE-DOC-A');
    await expect(page.locator('#links-list')).toContainText('FRESH-DOC-B');
    await page.close();
  });

  test('forms: fields are outlined on the page and the rollover shows their value', async () => {
    const file = path.join(fixtureDir, 'formoverlay.pdf');
    fs.writeFileSync(file, buildFormPdf('fullName', 'Ada'));
    const page = await openViewerWith(file);

    // The field's widget rectangle is outlined so an otherwise-invisible field is locatable.
    const marker = page.locator('.page[data-page="1"] .field-marker');
    await expect(marker).toHaveCount(1, { timeout: 15000 });
    await expect(marker.locator('.field-tag')).toHaveText('abc'); // text-field icon

    // Rolling over reveals the field's name, type, and current value.
    await marker.hover();
    const popup = page.locator('#link-popup');
    await expect(popup).toBeVisible();
    await expect(popup.locator('.lp-url')).toHaveText('fullName');
    await expect(popup.locator('.lp-risk')).toContainText('text');
    await expect(popup.locator('.lp-risk')).toContainText('Ada');
    await page.close();
  });

  test('undo / redo: a change can be undone and then redone', async () => {
    const file = fixture('undoredo.pdf', [[{ text: 'Keep me', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);

    await expect(page.locator('#btn-undo')).toBeDisabled();
    await expect(page.locator('#btn-redo')).toBeDisabled();

    // Make a change (find & replace), then undo and redo it. Each step is checked in the
    // document itself, not just on the status line and the button states: the old test asserted
    // only "Undid last change" / "Redid change" and the enabled/disabled buttons, so it passed
    // whether or not undo and redo actually moved any content — it survived a no-op of the very
    // edit it claims to reverse. In particular redo's effect on the document is covered nowhere
    // else. (Positive assertions lead each step so the async text-layer rebuild can't satisfy a
    // negative against an empty page — see expectText.)
    await ui(page, '#btn-find');
    await fillDialog(page, ['Keep me', 'Changed'], 'Replace all');
    await expect(page.locator('#status')).toContainText('Replaced 1 occurrence');
    await expect(page.locator('#btn-undo')).toBeEnabled();
    await expectText(page).toContain('Changed');
    await expectText(page).not.toContain('Keep me');

    await page.click('#btn-undo');
    await expect(page.locator('#status')).toContainText('Undid last change');
    await expect(page.locator('#btn-redo')).toBeEnabled();
    await expectText(page).toContain('Keep me');
    await expectText(page).not.toContain('Changed');

    await page.click('#btn-redo');
    await expect(page.locator('#status')).toContainText('Redid change');
    await expect(page.locator('#btn-redo')).toBeDisabled();
    await expectText(page).toContain('Changed');
    await expectText(page).not.toContain('Keep me');
    await page.close();
  });

  test('redaction works on a page other than the first (per-page overlays)', async () => {
    // Text near the top of page 2 so it stays on-screen once page 2 is scrolled to the top.
    const file = fixture('multi-redact.pdf', [
      [{ text: 'first page', x: 72, y: 700 }],
      [{ text: 'SECOND SECRET', x: 72, y: 800 }, { text: 'page two control', x: 72, y: 700 }],
    ]);
    const page = await openViewerWith(file);

    await ui(page, '#tool-redact');
    // Top-align page 2 *instantly* (no smooth-scroll animation, so boundingBox() below is
    // settled and the drag can't land on stale coordinates), then let it render.
    await page.evaluate(() =>
      document.querySelector('.page[data-page="2"]').scrollIntoView({ block: 'start', behavior: 'instant' }));
    await expect(page.locator(pageImageSel(2))).toHaveAttribute('src', /data:image\/png/);
    await expect(page.locator('#page-input')).toHaveValue('2');
    await expect(page.locator('#page-total')).toHaveText('2');

    // Draw on page 2's own overlay, in that page's A4 user-space (near its top).
    const box = await page.locator(pageImageSel(2)).boundingBox();
    const scale = box.width / 595;
    const cx = (px) => box.x + px * scale;
    const cy = (py) => box.y + (842 - py) * scale;
    await page.mouse.move(cx(60), cy(790));
    await page.mouse.down();
    await page.mouse.move(cx(320), cy(825), { steps: 5 });
    await page.mouse.up();

    await expect(page.locator('#redact-list li')).toContainText('p2');
    await page.click('#redact-apply');
    await expect(page.locator('#status')).toContainText('content removed');

    // Page 2's region renders black — proving the drag mapped to page 2, not page 1.
    const pixel = await page.evaluate(async () => {
      const img = document.querySelector('.page[data-page="2"] .page-image');
      await img.decode();
      const canvas = document.createElement('canvas');
      canvas.width = img.naturalWidth;
      canvas.height = img.naturalHeight;
      const ctx = canvas.getContext('2d');
      ctx.drawImage(img, 0, 0);
      const s = img.naturalWidth / 595;
      return [...ctx.getImageData(Math.round(180 * s), Math.round((842 - 807) * s), 1, 1).data];
    });
    expect(pixel).toEqual([0, 0, 0, 255]);

    // ...and page 2's words are actually gone, while page 2's other line and the whole of
    // page 1 are untouched. Placement alone would pass on a build that removes nothing.
    await expectText(page, 2).toContain('page two control');
    await expectText(page, 2).not.toContain('SECOND SECRET');
    await expectText(page, 1).toContain('first page');
    await page.close();
  });

  test('redaction lands correctly on a page whose box origin is not (0,0)', async () => {
    // Regression: the viewer used to assume the page's lower-left is (0,0). PDFium renders
    // the MediaBox at its true origin, so on a box like [100 200 695 1042] every redaction
    // landed offset by (100,200). Draw a box over the text and prove the *text's* location
    // (not the shifted one) is what gets blacked out.
    const box = [100, 200, 695, 1042];
    const file = fixture('offset-redact.pdf',
      [[{ text: 'OFFSET SECRET', x: 150, y: 900 }, { text: 'offset control line', x: 150, y: 700 }]],
      { mediaBox: box });
    const page = await openViewerWith(file);

    await ui(page, '#tool-redact');
    await dragPdfRect(page, { x: 140, y: 892, width: 240, height: 30 }, box);
    await expect(page.locator('#redact-list li')).toHaveCount(1);

    await page.click('#redact-preview');
    const dialog = page.locator('dialog#modal');
    await expect(dialog).toBeVisible();
    await dialog.getByRole('button', { name: 'Apply redaction' }).click();
    await expect(page.locator('#status')).toContainText('content removed');

    // The redaction box is painted exactly over the text (origin honoured), and a point
    // outside it stays white.
    expect(await pixelAt(page, 200, 905, box)).toEqual([0, 0, 0, 255]);
    expect(await pixelAt(page, 600, 400, box)).toEqual([255, 255, 255, 255]);

    // ...and the content behind it is gone, on a page whose origin is not (0,0) — the offset
    // has to be honoured by the *removal* as well as by the box, and only this half says so.
    await expectText(page).toContain('offset control line');
    await expectText(page).not.toContain('OFFSET SECRET');
    await page.close();
  });

  test('redaction lands correctly when the CropBox exceeds the MediaBox', async () => {
    // Real-world regression: some PDFs set a CropBox larger than the MediaBox. The renderer
    // clamps to the media box, so if the viewer trusted the oversized crop box the whole page
    // was scaled and the redaction landed above where it was drawn. The page here is a normal
    // A4 media box with a crop box 160pt taller; text sits inside the real (media) area.
    const file = fixture('oversized-crop.pdf',
      [[{ text: 'CLAMP ME', x: 72, y: 500 }, { text: 'crop control line', x: 72, y: 300 }]],
      { mediaBox: [0, 0, 595, 842], cropBox: [0, 0, 595, 1002] });
    const page = await openViewerWith(file);

    await ui(page, '#tool-redact');
    await dragPdfRect(page, { x: 60, y: 492, width: 220, height: 30 });
    await expect(page.locator('#redact-list li')).toHaveCount(1);
    await page.click('#redact-apply');
    await expect(page.locator('#status')).toContainText('content removed');

    // The drawn spot (not a shifted one) is what turns black.
    expect(await pixelAt(page, 150, 507)).toEqual([0, 0, 0, 255]);
    expect(await pixelAt(page, 450, 300)).toEqual([255, 255, 255, 255]);

    // ...and the words behind the box left the file. The crop-box bug moved *both* the box and
    // the removal, so a geometry-only test would go green on a build that removed nothing.
    await expectText(page).toContain('crop control line');
    await expectText(page).not.toContain('CLAMP ME');
    await page.close();
  });

  /**
   * A rotated-page redaction fixture and the drag that puts a box over its first line.
   *
   * On the landscape (/Rotate 90) image the first line renders as a vertical strip at display
   * fractions x 0.475..0.486, y 0.203..0.344, and the control line at x 0.119..0.13. The drag
   * is x 0.44..0.52, y 0.18..0.37 — over the first line, clear of the control.
   *
   * The old test dragged across the middle of the sheet at y 0.40..0.62, which the text does
   * not reach: it asserted a black box had appeared on an empty part of the page, and said
   * nothing whatever about redaction.
   */
  async function rotatedRedaction() {
    const file = fixture('rotated.pdf',
      [[{ text: 'rotated secret', x: 120, y: 400 }, { text: 'rotated control', x: 120, y: 100 }]],
      { rotate: 90 });
    const page = await openViewerWith(file);
    await ui(page, '#tool-redact');
    const box = await page.locator(pageImageSel(1)).boundingBox();
    await page.mouse.move(box.x + box.width * 0.44, box.y + box.height * 0.18);
    await page.mouse.down();
    await page.mouse.move(box.x + box.width * 0.52, box.y + box.height * 0.37, { steps: 5 });
    await page.mouse.up();
    await expect(page.locator('#redact-list li')).toHaveCount(1);
    await page.click('#redact-apply');
    await expect(page.locator('#status')).toContainText('content removed');
    return page;
  }

  /** Dark-pixel fraction over a rectangle given in display-image fractions. */
  async function displayDarkFraction(page, fx0, fy0, fx1, fy1) {
    return page.evaluate(async ([sel, a, b, c, d]) => {
      const img = document.querySelector(sel);
      await img.decode();
      const cv = document.createElement('canvas');
      cv.width = img.naturalWidth; cv.height = img.naturalHeight;
      const ctx = cv.getContext('2d');
      ctx.drawImage(img, 0, 0);
      const x0 = Math.round(a * cv.width), x1 = Math.round(c * cv.width);
      const y0 = Math.round(b * cv.height), y1 = Math.round(d * cv.height);
      const { data } = ctx.getImageData(x0, y0, x1 - x0, y1 - y0);
      let dark = 0, n = 0;
      for (let i = 0; i < data.length; i += 4) {
        n++;
        if (data[i] < 60 && data[i + 1] < 60 && data[i + 2] < 60) dark++;
      }
      return dark / n;
    }, [pageImageSel(1), fx0, fy0, fx1, fy1]);
  }

  test('text layer: real text can be selected/copied and right-clicked to edit', async () => {
    const file = fixture('selecttext.pdf', [[{ text: 'Selectable Sentence Here', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);

    // The invisible selectable text layer builds over the rendered page.
    const span = page.locator('.page[data-page="1"] .text-layer span', { hasText: 'Selectable' });
    await expect(span).toHaveCount(1);
    // The original run's baseline (its extracted PDF-space box), so the replacement can be held to
    // it. Comparing extracted run boxes rather than ink bounds keeps the baseline check independent
    // of which glyphs happen to have descenders.
    const original = (await textRuns(page)).find((r) => /Selectable/.test(r.text));
    expect(original).toBeTruthy();

    // Selecting it yields the real text (so Ctrl/Cmd+C copies actual characters, not an image).
    await span.click({ clickCount: 3 });
    const selected = await page.evaluate(() => window.getSelection().toString());
    expect(selected).toContain('Selectable');

    // Right-clicking opens the context menu; "Edit text" opens the edit panel pre-filled with
    // the selected run's text (edit in place).
    await span.click({ button: 'right' });
    await page.locator('#context-menu').getByRole('button', { name: /Edit text/ }).click();
    await expect(page.locator('#panel-edit')).toBeVisible();
    await expect(page.locator('#edit-title')).toHaveText('Edit text');
    await expect(page.locator('#edit-text')).toHaveValue(/Selectable Sentence Here/);

    // ...and pressing Apply actually edits the document. The old test stopped at "the panel
    // opens pre-filled", which is exactly where this feature was broken: the menu item set
    // state.pendingEditRegion and then called setTool('select'), whose hidePanels() nulled it
    // again, so applyTextEdit() returned on its first line. The panel looked right and Apply
    // did nothing, for months. Fixed in this branch; this is the assertion that holds it.
    await page.fill('#edit-text', 'Replaced Via Menu');
    await page.click('#edit-apply');
    await expect(page.locator('#status')).toContainText('Text replaced');
    await expectText(page).toContain('Replaced Via Menu');
    await expectText(page).not.toContain('Selectable');
    // The replacement is drawn where the original was — same baseline, same left edge, not a line
    // lower or higher. Before #96 the edit laid the text out top-down in a box, so the layout
    // engine's leading pushed the baseline off the original line; 2pt is well inside a line
    // (10-28pt here) but tight enough to catch that drift.
    const replaced = (await textRuns(page)).find((r) => /Replaced/.test(r.text));
    expect(replaced).toBeTruthy();
    expect(Math.abs(replaced.y - original.y)).toBeLessThan(2);
    expect(Math.abs(replaced.x - original.x)).toBeLessThan(3);
    // The replacement is shorter than the original, so the paper past its right edge is clear.
    expect(await inkFraction(page, { x: 320, y: 694, width: 120, height: 24 })).toBe(0);
    await page.close();
  });

  test('edit text: the panel pre-fills with the run\'s real type size, and the edit keeps it (#29)', async () => {
    // 28pt Helvetica. The detector used to report the ascender-to-descender box height instead of
    // the type size — 25.9pt here — and the replacement was then stamped at that, so every edit
    // shrank the text a little more.
    const file = fixture('fontmatch.pdf', [[{ text: 'Match My Size', x: 72, y: 700, size: 28 }]]);
    const page = await openViewerWith(file);
    await openConsole(page);

    // Drag an edit box over the line. (The right-click "Edit text" entry reaches the same panel,
    // but applying from there is a no-op on main — see the note in the PR; not this issue's bug.)
    await ui(page, '#tool-edit');
    await dragPdfRect(page, { x: 60, y: 690, width: 320, height: 45 });
    await expect(page.locator('#panel-edit')).toBeVisible();
    await expect(page.locator('#edit-text')).toHaveValue(/Match My Size/);

    // The size control is what the user sees and what gets sent back, so assert on its value.
    await expect(page.locator('#edit-size')).toHaveValue('28.0');
    await expect(page.locator('#edit-font')).toHaveValue('helvetica');

    // The console says what it matched against, so a substitution is never silent.
    const matched = page.locator('#console-log .console-entry', { hasText: 'matched the existing text style' });
    await expect(matched).toHaveCount(1);
    await expect(matched.locator('.console-detail')).toContainText('28.0pt');

    // Apply the edit without touching the size, then re-open the replaced run: it still measures
    // 28pt. Before the fix this came back as 25.9 and fell further on each pass.
    await page.fill('#edit-text', 'Replaced Words');
    await page.locator('#edit-apply').click();
    await expect(page.locator('#status')).toContainText('Text replaced.');

    await ui(page, '#tool-edit');
    await dragPdfRect(page, { x: 60, y: 690, width: 320, height: 45 });
    await expect(page.locator('#edit-text')).toHaveValue(/Replaced Words/);
    await expect(page.locator('#edit-size')).toHaveValue('28.0');

    // The console's open/closed state is persisted in extension storage, so leaving it open here
    // would dock a pane in every later test's viewer and move the page geometry under them.
    await page.locator('#console-close').click();
    await expect(page.locator('#console-pane')).toBeHidden();
    await page.close();
  });

  test('highlight: dragging across text marks it, keeping the text readable', async () => {
    const file = fixture('highlight.pdf', [[{ text: 'HIGHLIGHT THIS LINE', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);
    // Let the text layer (span cache) build so the highlight snaps to the text run.
    await page.locator('.page[data-page="1"] .text-layer span').first().waitFor({ timeout: 15000 });

    await ui(page, '#tool-highlight');
    // Swipe horizontally across the text line.
    const box = await page.locator(pageImageSel(1)).boundingBox();
    const scale = box.width / 595;
    const cy = box.y + (842 - 707) * scale;
    await page.mouse.move(box.x + 60 * scale, cy);
    await page.mouse.down();
    await page.mouse.move(box.x + 300 * scale, cy, { steps: 8 });
    await page.mouse.up();
    await expect(page.locator('#status')).toContainText('Highlighted');

    // Scan the highlighted line band for a yellow pixel (paper under the highlight) and a dark
    // one (the text is still legible through the multiply blend).
    const scan = await page.evaluate(async () => {
      const img = document.querySelector('.page[data-page="1"] .page-image');
      await img.decode();
      const c = document.createElement('canvas');
      c.width = img.naturalWidth; c.height = img.naturalHeight;
      const ctx = c.getContext('2d');
      ctx.drawImage(img, 0, 0);
      const scale = img.naturalWidth / 595;
      let yellow = false, dark = false;
      // The line sits around PDF y≈697..711 (text drawn at baseline 700).
      for (let py = 697; py <= 711; py++) {
        for (let px = 74; px <= 235; px++) {
          const d = ctx.getImageData(Math.round(px * scale), Math.round((842 - py) * scale), 1, 1).data;
          if (d[0] > 200 && d[1] > 180 && d[2] < 140) yellow = true;
          if (d[0] < 120 && d[1] < 120 && d[2] < 120) dark = true;
        }
      }
      return { yellow, dark };
    });
    expect(scan.yellow).toBe(true);
    expect(scan.dark).toBe(true);
    await page.close();
  });

  // #23: highlighting is a text selection, not a box. Sweeping across half a line must mark
  // exactly the characters swept — the old box drag marked whole runs, so the untouched word
  // on the same run came out yellow too.
  test('highlight: sweeping across part of a line marks only the swept words', async () => {
    // One show-text run holding two well-separated words, so run-snapping cannot pass this.
    // Helvetica 14: "ALPHA" spans x≈72..118, "OMEGA" x≈157..209.
    const file = fixture('sweep.pdf', [[{ text: 'ALPHA          OMEGA', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);
    await page.locator('.page[data-page="1"] .text-layer span').first().waitFor({ timeout: 15000 });

    await ui(page, '#tool-highlight');
    // Press on the first letter, sweep into the gap, release — no box drawn.
    await sweepPdf(page, { x: 73, y: 703 }, { x: 137, y: 703 });
    await expect(page.locator('#status')).toContainText('Highlighted');

    const band = { y0: 698, y1: 709 };
    expect(await yellowFraction(page, { x0: 74, x1: 116, ...band })).toBeGreaterThan(0.3);
    expect(await yellowFraction(page, { x0: 158, x1: 207, ...band })).toBeLessThan(0.02);
    // ...and nothing above the line either.
    expect(await yellowFraction(page, { x0: 74, x1: 207, y0: 715, y1: 730 })).toBeLessThan(0.02);
    await page.close();
  });

  test('highlight: a sweep across a line break marks the swept part of each line', async () => {
    // Helvetica 14 groups: line 1 A≈72..119, B≈123..169, C≈173..220;
    //                      line 2 D≈72..122, E≈126..173, F≈177..220.
    const file = fixture('sweeplines.pdf', [[
      { text: 'AAAAA BBBBB CCCCC', x: 72, y: 700 },
      { text: 'DDDDD EEEEE FFFFF', x: 72, y: 670 },
    ]]);
    const page = await openViewerWith(file);
    await page.locator('.page[data-page="1"] .text-layer span').first().waitFor({ timeout: 15000 });

    await ui(page, '#tool-highlight');
    // Start at the last word of line 1, end at the first word of line 2.
    await sweepPdf(page, { x: 174, y: 703 }, { x: 122, y: 673 });
    await expect(page.locator('#status')).toContainText('Highlighted');

    const l1 = { y0: 698, y1: 709 };
    const l2 = { y0: 668, y1: 679 };
    expect(await yellowFraction(page, { x0: 175, x1: 218, ...l1 })).toBeGreaterThan(0.3); // CCCCC
    expect(await yellowFraction(page, { x0: 74, x1: 117, ...l1 })).toBeLessThan(0.02);    // AAAAA
    expect(await yellowFraction(page, { x0: 74, x1: 120, ...l2 })).toBeGreaterThan(0.3);  // DDDDD
    expect(await yellowFraction(page, { x0: 179, x1: 218, ...l2 })).toBeLessThan(0.02);   // FFFFF
    // The gap between the two lines is never painted.
    expect(await yellowFraction(page, { x0: 74, x1: 218, y0: 683, y1: 694 })).toBeLessThan(0.02);
    await page.close();
  });

  test('highlight: a selection spanning two pages highlights both, each in its own place', async () => {
    const file = fixture('sweeppages.pdf', [
      [{ text: 'PAGE ONE TAIL', x: 72, y: 700 }],
      [{ text: 'PAGE TWO HEAD', x: 72, y: 700 }],
    ]);
    const page = await openViewerWith(file);
    await page.locator('.page[data-page="2"]').scrollIntoViewIfNeeded();
    await page.locator('.page[data-page="2"] .text-layer span').first().waitFor({ timeout: 15000 });
    await page.locator('.page[data-page="1"] .text-layer span').first().waitFor({ timeout: 15000 });

    await ui(page, '#tool-highlight');
    // A sweep that crosses a page boundary can't be driven by one mouse path without
    // scrolling mid-drag, so the selection is made directly and released with a real mouseup.
    await page.evaluate(() => {
      const runOn = (n) => document.querySelector(`.page[data-page="${n}"] .text-layer span`).firstChild;
      const a = runOn(1), b = runOn(2);
      window.getSelection().setBaseAndExtent(a, 0, b, b.length);
    });
    await page.mouse.up();
    await expect(page.locator('#status')).toContainText('Highlighted');

    const band = { y0: 698, y1: 709 };
    expect(await yellowFraction(page, { x0: 74, x1: 160, ...band }, { pageNum: 1 })).toBeGreaterThan(0.3);
    expect(await yellowFraction(page, { x0: 74, x1: 160, ...band }, { pageNum: 2 })).toBeGreaterThan(0.3);
    // Neither page is painted outside its text line.
    expect(await yellowFraction(page, { x0: 300, x1: 400, ...band }, { pageNum: 2 })).toBeLessThan(0.02);
    await page.close();
  });

  test('highlight: the right-click menu marks the selection, not a block around it', async () => {
    // The sweep tool was fixed for #23 but the context menu still went through applyHighlight(),
    // which paints selectionRegion()'s single bounding rectangle. Across a line break that is one
    // block covering both lines and the gap — the box behaviour #23 is about, reached by the other
    // route. Selecting and right-clicking must mark what a sweep would.
    const file = fixture('ctxsweep.pdf', [[
      { text: 'AAAAA BBBBB CCCCC', x: 72, y: 700 },
      { text: 'DDDDD EEEEE FFFFF', x: 72, y: 670 },
    ]]);
    const page = await openViewerWith(file);
    await page.locator('.page[data-page="1"] .text-layer span').first().waitFor({ timeout: 15000 });

    // Select from the last word of line 1 into the first word of line 2, then right-click on it.
    await page.evaluate(() => {
      const runs = [...document.querySelectorAll('.page[data-page="1"] .text-layer span')];
      window.getSelection().setBaseAndExtent(runs[0].firstChild, 12, runs[1].firstChild, 5);
    });
    // Right-click *inside* the selection (over "CCCCC"): clicking outside it makes Chrome collapse
    // the selection first, which is a different scenario from the one under test.
    const imgBox = await page.locator(pageImageSel(1)).boundingBox();
    const scale = imgBox.width / 595;
    await page.mouse.click(imgBox.x + 190 * scale, imgBox.y + (842 - 703) * scale, { button: 'right' });
    await page.locator('#context-menu').getByRole('button', { name: /Highlight/ }).click();
    await expect(page.locator('#status')).toContainText('Highlighted');

    const l1 = { y0: 698, y1: 709 };
    const l2 = { y0: 668, y1: 679 };
    expect(await yellowFraction(page, { x0: 175, x1: 218, ...l1 })).toBeGreaterThan(0.3); // CCCCC
    expect(await yellowFraction(page, { x0: 74, x1: 117, ...l1 })).toBeLessThan(0.02);    // AAAAA
    expect(await yellowFraction(page, { x0: 74, x1: 120, ...l2 })).toBeGreaterThan(0.3);  // DDDDD
    expect(await yellowFraction(page, { x0: 179, x1: 218, ...l2 })).toBeLessThan(0.02);   // FFFFF
    // The block a bounding rectangle would have painted: the gap between the two lines.
    expect(await yellowFraction(page, { x0: 74, x1: 218, y0: 683, y1: 694 })).toBeLessThan(0.02);
    await page.close();
  });

  test('highlight: choosing "Draw a box" marks the rectangle even over selectable text', async () => {
    // Sweeping is the default, but a box has to stay reachable: it is the only thing that works on
    // a scan, and it is what you want over a table or a figure. Over text the two compete for the
    // pointer, so the mode has to actually switch which one gets it.
    const file = fixture('modebox.pdf', [[
      { text: 'AAAAA BBBBB CCCCC', x: 72, y: 700 },
    ]]);
    const page = await openViewerWith(file);
    await page.locator('.page[data-page="1"] .text-layer span').first().waitFor({ timeout: 15000 });

    await ui(page, '#tool-highlight');
    // Sweep is the default the panel opens on.
    await expect(page.locator('input[name="highlight-mode"][value="sweep"]')).toBeChecked();

    await page.locator('input[name="highlight-mode"][value="box"]').check();
    await dragPdfRect(page, { x: 66, y: 694, width: 160, height: 22 });
    await expect(page.locator('#status')).toContainText('Highlighted');

    // The discriminating band: inside the rectangle dragged (y 694..716) but outside the text run
    // (y 697.1..710.1). Run-snapping paints the run and nothing else, so it leaves this blank —
    // only a real box fills it. Sampling over the words instead would prove nothing and would not
    // even reach 0.9, since their dark glyphs are not yellow.
    expect(await yellowFraction(page, { x0: 100, x1: 200, y0: 694.5, y1: 696.5 })).toBeGreaterThan(0.9);
    expect(await yellowFraction(page, { x0: 100, x1: 200, y0: 711.5, y1: 715 })).toBeGreaterThan(0.9);
    // ...and it stops at the rectangle: past its right edge nothing is marked.
    expect(await yellowFraction(page, { x0: 240, x1: 300, y0: 694, y1: 716 })).toBeLessThan(0.02);
    await page.close();
  });

  test('highlight: a page with no selectable text still falls back to a box drag', async () => {
    const file = fixture('highlightbox.pdf', [[]]); // no text runs: nothing to select
    const page = await openViewerWith(file);
    await expect(page.locator('.page[data-page="1"] .text-layer')).toHaveCount(0);

    await ui(page, '#tool-highlight');
    // Draw a rectangle: with no text layer the overlay still takes the pointer.
    await dragPdfRect(page, { x: 66, y: 694, width: 250, height: 22 });
    await expect(page.locator('#status')).toContainText('Highlighted');
    expect(await yellowFraction(page, { x0: 70, x1: 310, y0: 697, y1: 713 })).toBeGreaterThan(0.9);
    expect(await yellowFraction(page, { x0: 70, x1: 310, y0: 725, y1: 740 })).toBeLessThan(0.02);
    await page.close();
  });

  test('highlight: a loose diagonal drag over a line still marks that line, and only it', async () => {
    const file = fixture('highlightloose.pdf', [[
      { text: 'BOX HIGHLIGHT LINE', x: 72, y: 700 },
      { text: 'UNTOUCHED LINE BELOW', x: 72, y: 640 },
    ]]);
    const page = await openViewerWith(file);
    await page.locator('.page[data-page="1"] .text-layer span').first().waitFor({ timeout: 15000 });

    await ui(page, '#tool-highlight');
    // Sloppy diagonal drag across the first line only.
    await dragPdfRect(page, { x: 66, y: 694, width: 250, height: 22 });
    await expect(page.locator('#status')).toContainText('Highlighted');

    expect(await yellowFraction(page, { x0: 74, x1: 200, y0: 698, y1: 709 })).toBeGreaterThan(0.3);
    expect(await yellowFraction(page, { x0: 74, x1: 200, y0: 638, y1: 649 })).toBeLessThan(0.02);
    await page.close();
  });

  test('text edit: reads existing text, replaces it in place', async () => {
    const file = fixture('edit.pdf', [[{ text: 'Amount Due: $500', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);

    await ui(page, '#tool-edit');
    await dragPdfRect(page, { x: 60, y: 690, width: 250, height: 34 });
    await expect(page.locator('#panel-edit')).toBeVisible();
    await expect(page.locator('#edit-text')).toHaveValue('Amount Due: $500');

    await page.fill('#edit-text', 'Amount Due: $750 (revised)');
    await page.click('#edit-apply');
    await expect(page.locator('#status')).toContainText('Text replaced');

    // Re-selecting the same region proves the old text is gone from the file.
    await ui(page, '#tool-edit');
    await dragPdfRect(page, { x: 60, y: 685, width: 300, height: 40 });
    await expect(page.locator('#edit-text')).toHaveValue(/\$750 \(revised\)/);
    await expect(page.locator('#edit-text')).not.toHaveValue(/\$500/);
    await page.close();
  });

  test('text edit: the right-click route applies, not just opens the panel', async () => {
    // The context-menu route had its own bug: it opened a correctly pre-filled panel and then did
    // nothing at all on Apply, because setTool('select') hid the panels and cleared the pending
    // region before the edit could use it. The existing context-menu test stops at "the panel is
    // pre-filled", which is exactly why that shipped — so this one presses Apply and checks the
    // document actually changed.
    const file = fixture('ctxedit.pdf', [[{ text: 'Invoice Total 500', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);

    const span = page.locator('.page[data-page="1"] .text-layer span', { hasText: 'Invoice' });
    await span.waitFor({ timeout: 15000 });
    await span.click({ button: 'right' });
    await page.locator('#context-menu').getByRole('button', { name: /Edit text/ }).click();
    await expect(page.locator('#panel-edit')).toBeVisible();
    await expect(page.locator('#edit-text')).toHaveValue(/Invoice Total 500/);

    await page.fill('#edit-text', 'Invoice Total 750');
    await page.click('#edit-apply');
    await expect(page.locator('#status')).toContainText('Text replaced');

    // Re-reading the region proves the file changed, not just the panel.
    await ui(page, '#tool-edit');
    await dragPdfRect(page, { x: 60, y: 685, width: 300, height: 40 });
    await expect(page.locator('#edit-text')).toHaveValue(/750/);
    await expect(page.locator('#edit-text')).not.toHaveValue(/500/);
    await page.close();
  });

  test('move text: grab a run of text and drag it to a new position', async () => {
    const file = fixture('movetext.pdf', [[{ text: 'MOVE ME', x: 72, y: 700, size: 20 }]]);
    const page = await openViewerWith(file);
    // Let the selectable text layer (span cache) build so the grab snaps to the run.
    await page.locator('.page[data-page="1"] .text-layer span').first().waitFor({ timeout: 15000 });

    // The word's ink where it starts, so "it left" is a measured change and not an assumption.
    const origin = { x: 68, y: 694, width: 120, height: 24 };
    const destination = { x: 85, y: 544, width: 130, height: 26 };
    expect(await inkFraction(page, origin)).toBeGreaterThan(0.05);
    expect(await inkFraction(page, destination)).toBe(0);

    await ui(page, '#tool-move');
    const box = await page.locator(pageImageSel(1)).boundingBox();
    const scale = box.width / 595;
    const cx = (px) => box.x + px * scale;
    const cy = (py) => box.y + (842 - py) * scale;
    // Grab the word (~90, 705) and drop it ~150 pt lower.
    await page.mouse.move(cx(90), cy(705));
    await page.mouse.down();
    await page.mouse.move(cx(110), cy(555), { steps: 8 });
    await page.mouse.up();
    await expect(page.locator('#status')).toContainText('Text moved');

    // Both halves, every time. The old location is back to the page background colour...
    await expect.poll(() => inkFraction(page, origin), { timeout: 20000 }).toBe(0);
    expect(await bandStats(page, origin).then((s) => s.paper)).toBe(1);
    expect(await dominantColor(page, origin)).toEqual([255, 255, 255]);
    // ...and the new location contains the text. Ink alone would be satisfied by a smear, so
    // the run is also read back out of the document, at its new coordinates.
    expect(await inkFraction(page, destination)).toBeGreaterThan(0.05);
    const runs = await textRuns(page);
    expect(runs).toHaveLength(1);            // moved, not copied
    expect(runs[0].text).toBe('MOVE ME');    // and not mangled on the way
    expect(runs[0].y).toBeGreaterThan(535);
    expect(runs[0].y).toBeLessThan(565);
    await page.close();
  });

  test('context menu: right-clicking selected text offers Edit and Redact', async () => {
    // The second line is a control: it must survive, and asserting it first is what proves the
    // text layer has rebuilt before the "the run is gone" negative is checked.
    const file = fixture('ctxsel.pdf', [[
      { text: 'Right Click Me', x: 72, y: 700 },
      { text: 'leave this alone', x: 72, y: 640 },
    ]]);
    const page = await openViewerWith(file);
    const span = page.locator('.page[data-page="1"] .text-layer span', { hasText: 'Right' });
    await span.waitFor({ timeout: 15000 });
    await span.click({ clickCount: 3 }); // select the run
    // Right-click *inside* the selection. Chrome discards a selection when you right-click
    // outside it, and the menu then quietly offers the document-level actions instead — a test
    // that clicks anywhere else can pass while exercising a completely different code path.
    const b = await span.boundingBox();
    await page.mouse.click(b.x + b.width / 2, b.y + b.height / 2, { button: 'right' });

    const menu = page.locator('#context-menu');
    await expect(menu).toBeVisible();
    await expect(menu).toContainText('Edit text');
    await expect(menu).toContainText('Redact this');
    await expect(menu).toContainText('Highlight');

    // "Redact this" marks the selection as a redaction region — and the region it marks is the
    // selected run, not the whole page or a default box. Applying it has to remove those words
    // and nothing else, which is the only reason the menu item exists.
    await menu.getByRole('button', { name: /Redact this/ }).click();
    await expect(page.locator('#redact-list li')).toHaveCount(1);
    await page.click('#redact-apply');
    await expect(page.locator('#status')).toContainText('content removed');
    await expectText(page).toContain('leave this alone');
    await expectText(page).not.toContain('Right Click Me');
    expect(await inkFraction(page, { x: 74, y: 698, width: 90, height: 12 })).toBe(1);
    // The rest of the page is untouched paper — the region was the run, not the page.
    expect(await bandStats(page, { x: 60, y: 400, width: 400, height: 100 }).then((s) => s.paper))
      .toBe(1);
    await page.close();
  });

  test('text edit (#90): right-clicking a selection, Edit, then Apply changes the document', async () => {
    // #90 as reported: the user *highlights* text, right-clicks, chooses Edit, edits, presses Apply
    // — and nothing happens. The sibling "the right-click route applies" test drives the
    // no-selection span route; this one drives the selection route the issue actually describes, all
    // the way through Apply to a changed document.
    const file = fixture('ctxseledit.pdf', [[
      { text: 'Amount Due 500', x: 72, y: 700 },
      { text: 'keep me', x: 72, y: 640 },
    ]]);
    const page = await openViewerWith(file);
    const span = page.locator('.page[data-page="1"] .text-layer span', { hasText: 'Amount' });
    await span.waitFor({ timeout: 15000 });
    await span.click({ clickCount: 3 }); // highlight the run
    const b = await span.boundingBox();
    await page.mouse.click(b.x + b.width / 2, b.y + b.height / 2, { button: 'right' });

    await page.locator('#context-menu').getByRole('button', { name: /Edit text/ }).click();
    await expect(page.locator('#panel-edit')).toBeVisible();
    await expect(page.locator('#edit-text')).toHaveValue(/Amount Due 500/);

    await page.fill('#edit-text', 'Amount Due 750');
    await page.click('#edit-apply');
    await expect(page.locator('#status')).toContainText('Text replaced');

    // The file changed, not just the panel — and the control line is untouched.
    await expectText(page).toContain('Amount Due 750');
    await expectText(page).not.toContain('500');
    await expectText(page).toContain('keep me');
    await page.close();
  });

  test('context menu: right-clicking with no selection offers document actions', async () => {
    const file = fixture('ctxdoc.pdf', [[{ text: 'plain', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);

    // Right-click the visible centre of the scroll area (blank page area, no selection).
    const sa = await page.locator('#scroll-area').boundingBox();
    await page.mouse.click(sa.x + sa.width * 0.5, sa.y + sa.height * 0.5, { button: 'right' });

    const menu = page.locator('#context-menu');
    await expect(menu).toBeVisible();
    await expect(menu).toContainText('Make searchable');
    await expect(menu).toContainText('Show source code');
    await expect(menu).toContainText('Save');
    await expect(menu).toContainText('Print');
    await expect(menu).toContainText('Zoom in');
    await page.close();
  });

  test('text edit: change the font family, size, and style', async () => {
    const file = fixture('font-edit.pdf', [[{ text: 'Plain Heading', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);

    await ui(page, '#tool-edit');
    await dragPdfRect(page, { x: 60, y: 690, width: 260, height: 34 });
    await expect(page.locator('#panel-edit')).toBeVisible();
    await expect(page.locator('#edit-text')).toHaveValue('Plain Heading');
    // Plain Helvetica text pre-fills the controls with the sans-serif default.
    await expect(page.locator('#edit-font')).toHaveValue('helvetica');
    await expect(page.locator('#edit-bold')).not.toHaveClass(/active/);

    // Change the text, switch to Times, bump the size, and turn on bold.
    await page.fill('#edit-text', 'Styled Heading');
    await page.selectOption('#edit-font', 'times');
    await page.fill('#edit-size', '20');
    await page.click('#edit-bold');
    await expect(page.locator('#edit-bold')).toHaveClass(/active/);
    await page.click('#edit-apply');
    await expect(page.locator('#status')).toContainText('Text replaced');

    // The new text really is in the document.
    await expectText(page).toContain('Styled Heading');

    // Re-selecting the region has to show the *detected* font and style — but nothing clears
    // the edit controls after an apply, so they still hold "times", "bold" and the typed string
    // from a moment ago. Re-reading them straight away compares the controls with themselves
    // and passes even when replace-region-text does nothing at all.
    //
    // So: open the panel over a blank part of the page first. That is a real host round trip
    // (get-region-text on an empty region), and it resets the controls to the defaults —
    // asserted here, because that reset is what makes the next three assertions mean anything.
    await ui(page, '#tool-edit');
    await dragPdfRect(page, { x: 60, y: 380, width: 260, height: 34 });
    await expect(page.locator('#panel-edit')).toBeVisible();
    await expect(page.locator('#edit-text')).toHaveValue('');
    await expect(page.locator('#edit-font')).toHaveValue('helvetica');
    await expect(page.locator('#edit-bold')).not.toHaveClass(/active/);

    // Now the styled line, with the controls known to be showing something else beforehand.
    await ui(page, '#tool-edit');
    await dragPdfRect(page, { x: 55, y: 685, width: 300, height: 45 });
    await expect(page.locator('#edit-text')).toHaveValue(/Styled Heading/);
    await expect(page.locator('#edit-font')).toHaveValue('times');
    await expect(page.locator('#edit-bold')).toHaveClass(/active/);
    await page.close();
  });

  test('find & replace across the document', async () => {
    const file = fixture('replace.pdf', [[
      { text: 'Contract with OldCorp', x: 72, y: 700 },
      { text: 'OldCorp shall deliver', x: 72, y: 650 },
    ]]);
    const page = await openViewerWith(file);

    await ui(page, '#btn-find');
    await fillDialog(page, ['OldCorp', 'NewCorp'], 'Replace all');
    await expect(page.locator('#status')).toContainText('Replaced 2 occurrences');

    // "Replaced 2 occurrences" is a count the viewer computed, not evidence about the document.
    // Both occurrences have to actually be gone, both replacements actually present, and the
    // words around them left alone.
    await expectCompactText(page).toContain('ContractwithNewCorp');
    await expectCompactText(page).toContain('NewCorpshalldeliver');
    await expectCompactText(page).not.toContain('OldCorp');
    await expect
      .poll(() => pageText(page).then((t) => t.replace(/\s+/g, '').match(/NewCorp/g)?.length ?? 0),
        { timeout: 20000 })
      .toBe(2);
    // Each replacement sits on the line it replaced. Find & replace removes the original text
    // operators and appends the new ones at the end of the content stream, so "the word is in
    // the document somewhere" is genuinely a weaker claim than "the line still reads correctly".
    const replaced = (await textRuns(page)).filter((r) => r.text.includes('NewCorp'));
    expect(replaced).toHaveLength(2);
    expect(replaced.map((r) => Math.round(r.y)).sort((a, b) => a - b)).toEqual([647, 697]);
    await page.close();
  });

  test('print: defers to the browser by loading the real PDF for printing', async () => {
    const file = fixture('print.pdf', [[{ text: 'Print me', x: 72, y: 700 }]]);
    const page = await ext.context.newPage();
    // Record any new-tab fallback so the test never actually spawns a tab.
    await page.addInitScript(() => { window.__opened = []; window.open = (u) => { window.__opened.push(u); return null; }; });
    await page.goto(ext.viewerUrl);
    const chooser = page.waitForEvent('filechooser');
    await page.click('#btn-open-empty');
    await (await chooser).setFiles(file);
    await expect(page.locator(pageImageSel(1))).toHaveAttribute('src', /data:image\/png/);

    // Printing hands the actual PDF to the browser via an off-screen blob iframe (vector print,
    // with "Save as PDF" available in the browser's dialog).
    await page.click('#btn-print');
    await expect(page.locator('iframe[src^="blob:"]')).toHaveCount(1);
    await page.close();
  });

  test('merge appends an image as a new page', async () => {
    const base = fixture('merge-img-base.pdf', [[{ text: 'Base page', x: 72, y: 700 }]]);
    // A minimal 1x1 PNG written to disk for the merge picker.
    const png = Buffer.from(
      'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==',
      'base64');
    const imgFile = path.join(fixtureDir, 'stamp.png');
    fs.writeFileSync(imgFile, png);
    const page = await openViewerWith(base);

    const chooser = page.waitForEvent('filechooser');
    await ui(page, '#btn-merge');
    await (await chooser).setFiles(imgFile);
    // The merge dialog lets you arrange the files; confirm to combine them.
    await page.locator('dialog#modal').getByRole('button', { name: 'Merge' }).click();
    await expect(page.locator('#status')).toContainText('Merged 1 file');
    await expect(page.locator('#page-total')).toHaveText('2'); // image became a second page
    await page.close();
  });

  test('merge appends another document', async () => {
    const one = fixture('merge-base.pdf', [[{ text: 'Base page', x: 72, y: 700 }]]);
    const two = fixture('merge-extra.pdf', [
      [{ text: 'Extra page 1', x: 72, y: 700 }],
      [{ text: 'Extra page 2', x: 72, y: 700 }],
    ]);
    const page = await openViewerWith(one);

    const chooser = page.waitForEvent('filechooser');
    await ui(page, '#btn-merge');
    await (await chooser).setFiles(two);
    await page.locator('dialog#modal').getByRole('button', { name: 'Merge' }).click();
    await expect(page.locator('#status')).toContainText('Merged 1 file');
    await expect(page.locator('#page-input')).toHaveValue('1');
    await expect(page.locator('#page-total')).toHaveText('3');
    await page.close();
  });

  test('merge & arrange: dropping the current document keeps only the appended file', async () => {
    const one = fixture('merge-drop-base.pdf', [[{ text: 'Base only', x: 72, y: 700 }]]);
    const two = fixture('merge-drop-extra.pdf', [
      [{ text: 'Extra A', x: 72, y: 700 }],
      [{ text: 'Extra B', x: 72, y: 700 }],
    ]);
    const page = await openViewerWith(one);

    const chooser = page.waitForEvent('filechooser');
    await ui(page, '#btn-merge');
    await (await chooser).setFiles(two);

    // The dialog lists "This document" first; remove it so only the two appended pages remain.
    const dialog = page.locator('dialog#modal');
    await expect(dialog.locator('.organize-item')).toHaveCount(2);
    await dialog.locator('.organize-item').first().getByRole('button', { name: 'Remove' }).click();
    await expect(dialog.locator('.organize-item')).toHaveCount(1);
    await dialog.getByRole('button', { name: 'Merge' }).click();

    await expect(page.locator('#status')).toContainText('Merged 1 file');
    await expect(page.locator('#page-total')).toHaveText('2'); // base dropped, 2 extra pages kept
    await page.close();
  });

  test('organize pages: remove a page from the document', async () => {
    const file = fixture('organize.pdf', [
      [{ text: 'Keep one', x: 72, y: 700 }],
      [{ text: 'Delete two', x: 72, y: 700 }],
      [{ text: 'Keep three', x: 72, y: 700 }],
    ]);
    const page = await openViewerWith(file);
    await expect(page.locator('#page-total')).toHaveText('3');

    await ui(page, '#btn-organize');
    await expect(page.locator('#panel-organize')).toBeVisible();
    await expect(page.locator('#organize-list .organize-item')).toHaveCount(3);

    // Remove the middle page, then apply.
    await page.locator('#organize-list .organize-item').nth(1)
      .getByRole('button', { name: 'Remove page' }).click();
    await expect(page.locator('#organize-list .organize-item')).toHaveCount(2);
    await page.click('#organize-apply');

    await expect(page.locator('#status')).toContainText('reorganized');
    await expect(page.locator('#page-total')).toHaveText('2');

    // A page count of 2 is equally true of removing the wrong page. Which page went is the
    // whole point, so read the surviving pages' text back out of the document.
    await expectText(page, 1).toContain('Keep one');
    await page.evaluate(() => document.querySelector('.page[data-page="2"]')
      .scrollIntoView({ block: 'start', behavior: 'instant' }));
    await expect(page.locator(pageImageSel(2))).toHaveAttribute('src', /data:image\/png/);
    await expectText(page, 2).toContain('Keep three');
    await expectText(page, 2).not.toContain('Delete two');
    await page.close();
  });

  test('forms: insert a dropdown (choice) field with options', async () => {
    const file = fixture('dropdown.pdf', [[{ text: 'pick one', x: 72, y: 100 }]]);
    const page = await openViewerWith(file);

    await ui(page, '#btn-forms');
    await expect(page.locator('#panel-forms')).toBeVisible();
    await page.selectOption('#field-type', 'dropdown');
    await expect(page.locator('#field-options-row')).toBeVisible();
    await page.fill('#field-name', 'country');
    await page.fill('#field-options', 'Australia\nCanada\nDenmark');
    await page.click('#field-place');
    await expect(page.locator('#status')).toContainText('Drag a box');

    await dragPdfRect(page, { x: 100, y: 600, width: 220, height: 24 });

    // The forms panel reopens and lists the new choice field as a <select>.
    await expect(page.locator('#forms-list [data-field="country"]')).toHaveCount(1);
    await page.close();
  });

  test('forms: an inserted field is visible on the page (not blank space)', async () => {
    const file = fixture('visfield.pdf', [[{ text: 'form', x: 72, y: 100 }]]);
    const page = await openViewerWith(file);

    await ui(page, '#btn-forms');
    await page.selectOption('#field-type', 'text');
    await page.fill('#field-name', 'visible_field');
    await page.click('#field-place');
    await dragPdfRect(page, { x: 100, y: 600, width: 220, height: 26 });
    await expect(page.locator('#forms-list [data-field="visible_field"]')).toHaveCount(1);

    // Scan the field's rectangle on the rendered page for non-white pixels (its border/background).
    const drawn = await page.evaluate(async () => {
      const img = document.querySelector('.page[data-page="1"] .page-image');
      await img.decode();
      const c = document.createElement('canvas');
      c.width = img.naturalWidth; c.height = img.naturalHeight;
      const ctx = c.getContext('2d'); ctx.drawImage(img, 0, 0);
      const s = img.naturalWidth / 595;
      let nonWhite = 0;
      for (let py = 600; py <= 626; py++)
        for (let px = 100; px <= 320; px++) {
          const d = ctx.getImageData(Math.round(px * s), Math.round((842 - py) * s), 1, 1).data;
          if (!(d[0] > 248 && d[1] > 248 && d[2] > 248)) nonWhite++;
        }
      return nonWhite;
    });
    expect(drawn).toBeGreaterThan(0);
    await page.close();
  });

  test('forms: insert an option (radio) group', async () => {
    const file = fixture('radio.pdf', [[{ text: 'choose', x: 72, y: 100 }]]);
    const page = await openViewerWith(file);

    await ui(page, '#btn-forms');
    await page.selectOption('#field-type', 'radio');
    await expect(page.locator('#field-options-row')).toBeVisible();
    await page.fill('#field-name', 'plan');
    await page.fill('#field-options', 'Basic\nPro\nEnterprise');
    await page.click('#field-place');
    await dragPdfRect(page, { x: 100, y: 560, width: 200, height: 90 });

    // Listed as a single option field, rendered as a <select> of the choices (minus the Off state).
    const select = page.locator('#forms-list [data-field="plan"]');
    await expect(select).toHaveCount(1);
    await expect(select.locator('option', { hasText: 'Enterprise' })).toHaveCount(1);
    await expect(select.locator('option', { hasText: 'Off' })).toHaveCount(0);
    await page.close();
  });

  test('javascript: author a document script in the code editor, kept on save', async () => {
    const file = fixture('addjs.pdf', [[{ text: 'form doc', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);

    await ui(page, '#btn-js');
    await expect(page.locator('#js-dialog')).toBeVisible();
    await page.fill('#js-name', 'greet');
    await page.fill('#js-source', "app.alert('hello from the PDF');");
    await page.click('#js-add');
    await expect(page.locator('#status')).toContainText('added');

    // The script is now listed in the panel...
    await expect(page.locator('#js-list .organize-label', { hasText: 'greet' })).toHaveCount(1);
    // ...and the active-content badge shows it is being kept (not stripped) on save.
    const badge = page.locator('#badges .badge.warn');
    await expect(badge).toBeVisible();
    await expect(badge).toContainText('kept');
    await page.close();
  });

  test('javascript: a document script can be removed again', async () => {
    const file = fixture('rmjs.pdf', [[{ text: 'doc', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);

    await ui(page, '#btn-js');
    await page.fill('#js-name', 'temp');
    await page.fill('#js-source', 'console.println("x");');
    await page.click('#js-add');
    await expect(page.locator('#js-list .organize-label', { hasText: 'temp' })).toHaveCount(1);

    await page.locator('#js-list .organize-item').first()
      .getByRole('button', { name: 'Remove script' }).click();
    await expect(page.locator('#status')).toContainText('removed');
    await expect(page.locator('#js-list .organize-item')).toHaveCount(0);
    await page.close();
  });

  test('forms: insert a JavaScript push-button', async () => {
    const file = fixture('jsbutton.pdf', [[{ text: 'form', x: 72, y: 100 }]]);
    const page = await openViewerWith(file);

    await ui(page, '#btn-forms');
    // Type-specific rows must actually respond to the selected type -- these assertions only mean
    // something because [hidden] is now authoritative in CSS; a `label { display: block }` rule
    // used to render every one of them regardless, which is how a checkbox ended up offering a
    // script box whose contents were then silently discarded.
    await page.selectOption('#field-type', 'checkbox');
    await expect(page.locator('#field-caption-row')).toBeHidden(); // only a button has a label
    await expect(page.locator('#field-options-row')).toBeHidden();
    await expect(page.locator('#field-script-row')).toBeVisible(); // any field can carry a script

    await page.selectOption('#field-type', 'button');
    await expect(page.locator('#field-caption-row')).toBeVisible();
    await expect(page.locator('#field-script-row')).toBeVisible();
    await expect(page.locator('#field-options-row')).toBeHidden();
    await page.fill('#field-name', 'submitBtn');
    await page.fill('#field-caption', 'Submit');
    await page.fill('#field-script', "app.alert('submitted');");
    await page.click('#field-place');
    await expect(page.locator('#status')).toContainText('Drag a box');

    await dragPdfRect(page, { x: 100, y: 600, width: 120, height: 28 });

    // The button is listed as a form field, and the script it carries is kept on save.
    await expect(page.locator('.form-field[data-field-name="submitBtn"]')).toHaveCount(1);
    await expect(page.locator('#badges .badge.warn')).toContainText('kept');

    // ...and it is actually drawn on the page. A widget with no /AP appearance stream renders
    // as blank space in PDFium (the preview) and in most readers, so the button would be
    // invisible and unclickable even though it exists in the document.
    const drawn = await page.evaluate(async () => {
      const img = document.querySelector('.page[data-page="1"] .page-image');
      await img.decode();
      const c = document.createElement('canvas');
      c.width = img.naturalWidth; c.height = img.naturalHeight;
      const ctx = c.getContext('2d'); ctx.drawImage(img, 0, 0);
      const s = img.naturalWidth / 595;
      let nonWhite = 0;
      for (let py = 600; py <= 628; py++)
        for (let px = 100; px <= 220; px++) {
          const d = ctx.getImageData(Math.round(px * s), Math.round((842 - py) * s), 1, 1).data;
          if (!(d[0] > 248 && d[1] > 248 && d[2] > 248)) nonWhite++;
        }
      return nonWhite;
    });
    expect(drawn).toBeGreaterThan(0);
    await page.close();
  });

  test('forms: a checkbox can carry JavaScript too, and it survives the save', async () => {
    // Regression for the reported bug: the script box was offered on every field type but
    // beginPlaceField() only forwarded it for buttons, so a checkbox's script was silently
    // dropped -- the saved PDF had no /A and no /AA at all.
    const file = fixture('checkboxjs.pdf', [[{ text: 'form', x: 72, y: 100 }]]);
    const page = await openViewerWith(file);

    await ui(page, '#btn-forms');
    await page.selectOption('#field-type', 'checkbox');
    await expect(page.locator('#field-script-row')).toBeVisible();
    await page.fill('#field-name', 'agree');
    await page.fill('#field-script', "this.getField('agree').value = 'Yes';");
    await page.click('#field-place');
    await dragPdfRect(page, { x: 100, y: 600, width: 20, height: 20 });

    // It is listed, it kept its script (so it gets a Run control), and the document is now
    // flagged as carrying active content.
    const row = page.locator('.form-field[data-field-name="agree"]');
    await expect(row).toHaveCount(1);
    await expect(row.locator('.form-field-run')).toHaveCount(1);
    await expect(page.locator('#badges .badge.warn')).toContainText('kept');
    await page.close();
  });

  test('ocr: make searchable runs, or reports Tesseract is required', async () => {
    const file = fixture('ocr.pdf', [[{ text: 'Scanned document', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);

    const before = await page.locator('.page').first().boundingBox();
    const inkBefore = await inkFraction(page, { x: 60, y: 690, width: 300, height: 30 });
    expect(inkBefore).toBeGreaterThan(0);

    await ui(page, '#btn-ocr');
    // Deterministic across environments: OCR either succeeds (status confirms "searchable") or,
    // when Tesseract is not installed, an in-app note naming it appears — never a silent failure.
    await expect.poll(async () => {
      const dialog = page.locator('dialog#modal');
      if (await dialog.isVisible() && /Tesseract/i.test((await dialog.textContent()) || '')) return 'note';
      if (/searchable/i.test((await page.locator('#status').textContent()) || '')) return 'done';
      return 'pending';
    }, { timeout: 60000 }).not.toBe('pending');

    // Issue #21: where OCR did run, the searchable copy must occupy the same page geometry, so
    // the page is laid out at the same on-screen size — not blown up several times over.
    if (/searchable/i.test((await page.locator('#status').textContent()) || '')) {
      const after = await page.locator('.page').first().boundingBox();
      expect(Math.abs(after.width - before.width)).toBeLessThan(2);
      expect(Math.abs(after.height - before.height)).toBeLessThan(2);
      // Issue #20: and it is still viewable — the page image renders rather than falling back
      // to a blank placeholder.
      await expect.poll(
        async () => page.locator('.page').first().locator('img.page-image').evaluate((i) => i.naturalWidth),
        { timeout: 30000 },
      ).toBeGreaterThan(0);

      // "naturalWidth > 0" is also true of an all-white raster, which is exactly what an OCR
      // layer laid over a lost page looks like. The page's own ink has to still be there, in
      // the same quantity, in the same place...
      const inkAfter = await inkFraction(page, { x: 60, y: 690, width: 300, height: 30 });
      expect(inkAfter).toBeGreaterThan(inkBefore * 0.7);
      expect(inkAfter).toBeLessThan(inkBefore * 1.3);
      // ...and the point of the operation is that the words are now selectable text, so they
      // have to come back out of the document.
      await expectText(page).toContain('Scanned');
      await expectText(page).toContain('document');
    }
    await page.close();
  });

  test('compare versions: summarises added and removed words', async () => {
    const current = fixture('compare-new.pdf', [[{ text: 'Amount Due 750 dollars', x: 72, y: 700 }]]);
    const older = fixture('compare-old.pdf', [[{ text: 'Amount Due 500 dollars', x: 72, y: 700 }]]);
    const page = await openViewerWith(current);

    const chooser = page.waitForEvent('filechooser');
    await ui(page, '#btn-compare');
    await (await chooser).setFiles(older);

    await expect(page.locator('#panel-compare')).toBeVisible();
    await expect(page.locator('#compare-summary')).toContainText('1 page');
    // The changed page lists 750 as added and 500 as removed.
    await expect(page.locator('#compare-list .w-add', { hasText: '750' })).toHaveCount(1);
    await expect(page.locator('#compare-list .w-del', { hasText: '500' })).toHaveCount(1);
    await page.close();
  });

  test('compare versions: visual diff renders a pixel-diff image for a page (#46/#47)', async () => {
    const current = fixture('vdiff-new.pdf', [[{ text: 'Amount Due 750 dollars', x: 72, y: 700 }]]);
    const older = fixture('vdiff-old.pdf', [[{ text: 'Amount Due 500 dollars', x: 72, y: 700 }]]);
    const page = await openViewerWith(current);

    const chooser = page.waitForEvent('filechooser');
    await ui(page, '#btn-compare');
    await (await chooser).setFiles(older);
    await expect(page.locator('#panel-compare')).toBeVisible();

    // The changed page carries a "Visual diff" button; clicking it renders the pixel diff modal.
    await page.locator('#compare-list .compare-visual').first().click();
    const dialog = page.locator('dialog#modal');
    await expect(dialog).toBeVisible();
    await expect(dialog).toContainText('Visual diff');
    await expect(dialog).toContainText('% of pixels');
    await expect(dialog.locator('img.visual-diff-img')).toHaveAttribute('src', /^data:image\/png;base64,/);

    await dialog.getByRole('button', { name: 'Close' }).click();
    await expect(dialog).toBeHidden();

    // The standalone page picker works too (for pages the text diff didn't flag).
    await page.locator('.compare-visual-row .compare-visual-page').fill('1');
    await page.locator('.compare-visual-row button', { hasText: 'Show' }).click();
    await expect(dialog.locator('img.visual-diff-img')).toHaveAttribute('src', /^data:image\/png;base64,/);
    await page.close();
  });

  test('compare versions: identical documents report no differences', async () => {
    const same = fixture('compare-same.pdf', [[{ text: 'Unchanged content here', x: 72, y: 700 }]]);
    const page = await openViewerWith(same);

    const chooser = page.waitForEvent('filechooser');
    await ui(page, '#btn-compare');
    await (await chooser).setFiles(same);

    await expect(page.locator('#compare-summary')).toContainText('no text differences');
    await page.close();
  });

  test('remove hidden info: detects and strips a document script', async () => {
    const file = fixture('sanitize.pdf', [[{ text: 'shareable', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);

    // Seed some hidden data: add a document-level script.
    await ui(page, '#btn-js');
    await page.fill('#js-name', 'tracker');
    await page.fill('#js-source', "app.alert('phone home');");
    await page.click('#js-add');
    await expect(page.locator('#js-list .organize-label', { hasText: 'tracker' })).toHaveCount(1);
    await page.click('#js-close'); // the editor is a modal window — close it before the menu

    // Open the sanitiser: it should report the script and pre-check that category.
    await ui(page, '#btn-sanitize');
    await expect(page.locator('#panel-sanitize')).toBeVisible();
    const scriptRow = page.locator('#sanitize-items [data-opt="scriptsAndActions"]');
    await expect(scriptRow).toBeChecked();
    await expect(page.locator('#sanitize-items')).toContainText('JavaScript & actions — 1 found');

    await page.click('#sanitize-apply');
    await expect(page.locator('#status')).toContainText('Hidden information removed');

    // Re-opening the JavaScript panel shows the script is gone.
    await ui(page, '#btn-js');
    await expect(page.locator('#js-list .organize-item')).toHaveCount(0);
    await page.close();
  });

  test('remove hidden info: reports a clean document', async () => {
    const file = fixture('clean.pdf', [[{ text: 'nothing hidden', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);

    await ui(page, '#btn-sanitize');
    await expect(page.locator('#sanitize-clean')).toBeVisible();
    await expect(page.locator('#sanitize-apply')).toBeDisabled();
    await page.close();
  });

  test('password protection encrypts the document', async () => {
    const file = fixture('protect.pdf', [[{ text: 'classified', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);

    await ui(page, '#btn-protect');
    await fillDialog(page, ['s3cret', null], 'Encrypt');
    await expect(page.locator('#status')).toContainText('encrypted');
    await expect(page.locator('#badges .badge.locked')).toBeVisible();

    // The document stays editable with the retained password (re-render works).
    await page.click('#btn-zoom-in');
    await expect(page.locator('#zoom-label')).toHaveText('125%');
    await expect(page.locator(pageImageSel(1))).toHaveAttribute('src', /data:image\/png/);
    await page.close();
  });

  test('remove encryption: strips the password from a protected document (#25)', async () => {
    const file = fixture('decrypt.pdf', [[{ text: 'classified', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);

    // "Remove encryption" is disabled until the document is actually encrypted.
    await page.click('#menu-edit-trigger');
    await expect(page.locator('#btn-decrypt')).toBeDisabled();
    await page.click('#menu-edit-trigger'); // close the menu again

    // Encrypt it, then strip the encryption back off.
    await ui(page, '#btn-protect');
    await fillDialog(page, ['s3cret', null], 'Encrypt');
    await expect(page.locator('#badges .badge.locked')).toBeVisible();

    await ui(page, '#btn-decrypt');
    await expect(page.locator('#status')).toContainText('Encryption removed');
    await expect(page.locator('#badges .badge.locked')).toHaveCount(0);

    // Still a working document, and the control is disabled again now that it is not encrypted.
    await expect(page.locator(pageImageSel(1))).toHaveAttribute('src', /data:image\/png/);
    await page.click('#menu-edit-trigger');
    await expect(page.locator('#btn-decrypt')).toBeDisabled();
    await page.close();
  });

  test('watermark: stamps text across the page and bakes it in (#30)', async () => {
    // Body text sits low on the page; the centred watermark paints the middle, previously-blank band.
    const file = fixture('watermark.pdf', [[{ text: 'body copy', x: 72, y: 80 }]]);
    const page = await openViewerWith(file);

    const band = { x: 150, y: 360, width: 300, height: 120 };
    expect(await inkFraction(page, band)).toBe(0);

    // Opaque black so the stamped glyphs clear the ink/darkness threshold. The colour input is a
    // native picker (fill() doesn't apply to type=color), so set its value directly.
    await ui(page, '#btn-watermark');
    const dialog = page.locator('dialog#modal');
    await dialog.locator('input').nth(0).fill('CONFIDENTIAL');
    await dialog.locator('input[type=color]').evaluate((el) => { el.value = '#000000'; });
    await dialog.locator('input').nth(2).fill('100'); // opacity %
    await dialog.locator('input').nth(3).fill('45');  // rotation °
    await dialog.getByRole('button', { name: 'Apply' }).click();
    await expect(page.locator('#status')).toContainText('Watermark added');

    expect(await inkFraction(page, band)).toBeGreaterThan(0);
    await page.close();
  });

  test('bates numbering: stamps a sequential number in the bottom-right corner (#27)', async () => {
    const file = fixture('bates.pdf', [[{ text: 'body copy', x: 72, y: 400 }]]);
    const page = await openViewerWith(file);

    // Bottom-right corner band is blank before numbering.
    const corner = { x: 470, y: 20, width: 110, height: 22 };
    expect(await inkFraction(page, corner)).toBe(0);

    await ui(page, '#btn-bates');
    const dialog = page.locator('dialog#modal');
    await dialog.locator('input').nth(0).fill('ACME');   // prefix
    await dialog.locator('input').nth(1).fill('1');       // start
    await dialog.locator('input').nth(2).fill('6');       // digits
    await dialog.locator('select').selectOption('bottom-right');
    await dialog.getByRole('button', { name: 'Apply' }).click();

    await expect(page.locator('#status')).toContainText('ACME000001');
    expect(await inkFraction(page, corner)).toBeGreaterThan(0);
    await page.close();
  });

  test('drawn signature is placed on the page', async () => {
    // Keep the signature area in the upper part of the page: the drag helper works in
    // viewport coordinates, and A4 at 100% zoom extends below the fold.
    const file = fixture('sign-image.pdf', [[{ text: 'Sign here:', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);

    await ui(page, '#tool-sign');
    await dragPdfRect(page, { x: 200, y: 640, width: 160, height: 50 });
    await expect(page.locator('#panel-sign')).toBeVisible();

    // Scribble on the signature pad.
    const pad = await page.locator('#sign-pad').boundingBox();
    await page.mouse.move(pad.x + 12, pad.y + pad.height - 20);
    await page.mouse.down();
    await page.mouse.move(pad.x + pad.width / 2, pad.y + 14, { steps: 8 });
    await page.mouse.move(pad.x + pad.width - 12, pad.y + pad.height - 20, { steps: 8 });
    await page.mouse.up();

    await page.click('#sign-apply');
    await expect(page.locator('#status')).toContainText('Signature placed');
    await page.close();
  });

  test('digital signature with a generated self-signed certificate', async () => {
    const file = fixture('sign-digital.pdf', [[{ text: 'Agreement', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);

    await ui(page, '#btn-digital');
    await fillDialog(page, ['Approval', '', 'certpw'], 'Continue');

    const dialog = page.locator('dialog#modal');
    await dialog.getByRole('button', { name: 'Create self-signed' }).click();
    await fillDialog(page, ['E2E Signer', 'certpw'], 'Create & sign');

    await expect(page.locator('#status')).toContainText('digitally signed');
    await expect(page.locator('#badges .badge.signed')).toContainText('E2E Signer');
    await expect(page.locator('#badges .badge.signed')).toContainText('✓');
    await page.close();
  });

  test('save falls back to the downloads API, undo restores the previous state', async () => {
    const file = fixture('save.pdf', [[{ text: 'Original', x: 72, y: 700 }]]);
    const page = await ext.context.newPage();
    // Force the chrome.downloads path (the file-picker dialog can't be driven headlessly).
    await page.addInitScript(() => { delete window.showSaveFilePicker; });
    await page.goto(ext.viewerUrl);
    const chooser = page.waitForEvent('filechooser');
    await page.click('#btn-open-empty');
    await (await chooser).setFiles(file);
    await expect(page.locator(pageImageSel(1))).toHaveAttribute('src', /data:image\/png/);

    // Make one change so there is something to save/undo.
    await ui(page, '#btn-find');
    await fillDialog(page, ['Original', 'Changed'], 'Replace all');
    await expect(page.locator('#status')).toContainText('Replaced 1 occurrence');

    await page.click('#btn-save');
    await expect(page.locator('#status')).toContainText('Saving via downloads');

    await expect(page.locator('#btn-undo')).toBeEnabled();
    await page.click('#btn-undo');
    await expect(page.locator('#status')).toContainText('Undid last change');
    await page.close();
  });

  // ------------------------------------------------------- activity console (#72)

  /** Opens a viewer with the host gate installed, loads `file`, and returns the page. */
  async function openGatedViewerWith(file, gateOptions) {
    const page = await ext.context.newPage();
    await installHostGate(page, gateOptions);
    await page.goto(ext.viewerUrl);
    const chooser = page.waitForEvent('filechooser');
    await page.click('#btn-open-empty');
    await (await chooser).setFiles(file);
    await expect(page.locator(pageImageSel(1))).toHaveAttribute('src', /data:image\/png/);
    return page;
  }

  /**
   * Opens the activity console from the Help menu. The pane's open/closed state is remembered in
   * chrome.storage.local, which is shared by every page in this suite's one persistent profile —
   * so a plain toggle would close it for whichever test ran after one that left it open.
   */
  async function openConsole(page) {
    if (await page.locator('#console-pane').isHidden()) await ui(page, '#btn-console');
    await expect(page.locator('#console-pane')).toBeVisible();
  }

  test('activity console: the Help menu opens a docked log of what the viewer did', async () => {
    const page = await openViewerWith(fixture('console.pdf', [[{ text: 'Console test', x: 72, y: 700 }]]));

    // Closed by default. Help is reachable straight away — unlike Read/Edit it is never disabled,
    // because the console matters most before a document loads and while an open is failing.
    await expect(page.locator('#console-pane')).toBeHidden();
    await expect(page.locator('#menu-help-trigger')).toBeEnabled();
    await openConsole(page);
    await expect(page.locator('#btn-console')).toHaveAttribute('aria-pressed', 'true');

    // The open we just performed is in it: the document open itself, and the host round-trips.
    const log = page.locator('#console-log');
    await expect(log.locator('.console-entry', { hasText: 'document opened' })).toHaveCount(1);
    const renders = log.locator('.console-entry', { hasText: 'render' });
    expect(await renders.count()).toBeGreaterThan(0);

    // Round-trip time is the single most useful thing here, so every host entry carries one.
    await expect(renders.first().locator('.console-detail')).toContainText(/\d+ ms/);
    // Timestamped and levelled.
    await expect(log.locator('.console-entry').first().locator('.console-time'))
      .toContainText(/^\d\d:\d\d:\d\d\.\d\d\d$/);
    await expect(log.locator('.console-entry').first().locator('.console-level'))
      .toContainText(/^(info|warn|error)$/);

    // A tool change is an action too...
    await ui(page, '#tool-highlight');
    const toolEntry = log.locator('.console-entry', { hasText: 'tool changed' });
    await expect(toolEntry).toHaveCount(1);
    await expect(toolEntry).toContainText('select → highlight');
    // ...and logging it did not touch the shared status line.
    await expect(page.locator('#status')).not.toContainText('tool changed');

    // Clear discards what was there.
    await page.locator('#console-clear').click();
    await expect(log.locator('.console-entry', { hasText: 'document opened' })).toHaveCount(0);

    // The pane's state survives a reload; the log itself deliberately does not.
    await page.reload();
    await expect(page.locator('#console-pane')).toBeVisible();
    await expect(log.locator('.console-entry', { hasText: 'document opened' })).toHaveCount(0);

    await page.locator('#console-close').click();
    await expect(page.locator('#console-pane')).toBeHidden();
    await page.close();
  });

  test('help: About shows the version, build and browser (#103)', async () => {
    // Help is reachable with no document open, so About works from the empty state.
    const page = await ext.context.newPage();
    await page.goto(ext.viewerUrl);
    const version = await page.evaluate(() => chrome.runtime.getManifest().version);

    await ui(page, '#btn-about');
    const dialog = page.locator('dialog#modal');
    await expect(dialog).toBeVisible();
    await expect(dialog).toContainText('About');
    // The manifest version, and rows for the build stamp and the browser.
    await expect(dialog.locator('.about-list')).toContainText(version);
    await expect(dialog.locator('.about-list')).toContainText('Build');
    await expect(dialog.locator('.about-list')).toContainText('Browser');

    await dialog.getByRole('button', { name: 'Close' }).click();
    await expect(dialog).toBeHidden();
    await page.close();
  });

  test('activity console: diagnostics mode records environment and transfer sizes (#97)', async () => {
    const page = await openViewerWith(fixture('diag.pdf', [[{ text: 'Diag', x: 72, y: 700 }]]));
    await openConsole(page);
    const log = page.locator('#console-log');

    await page.check('#console-diagnostics');
    // Enabling it records an environment snapshot as tagged diagnostic entries.
    await expect(log.locator('.console-entry.diag', { hasText: 'extension version' })).toHaveCount(1);
    await expect(log.locator('.console-entry.diag', { hasText: 'browser' })).toHaveCount(1);
    // #114: the host now answers the `diagnostics` action, so its version is reported as a
    // "native host — Host version: <v>" row (richer than the legacy ping-only fallback).
    await expect(log.locator('.console-entry.diag', { hasText: 'Host version' })).toHaveCount(1);

    // A subsequent host round-trip now carries transfer sizes and is tagged diagnostic.
    await page.click('#btn-zoom-in');
    await expect(log.locator('.console-entry.diag', { hasText: 'recv' }).first()).toBeVisible();
    await page.close();
  });

  test('activity console: a forced host failure is logged instead of swallowed (#72)', async () => {
    // `form-fields` is the case #72 names by hand: its bare `catch { state.formFields = []; }`
    // turned any failure into the claim "this document has no fillable form fields".
    const page = await openGatedViewerWith(
      fixture('console-fail.pdf', [[{ text: 'Console failure test', x: 72, y: 700 }]]), { fail: ['form-fields'] });

    await openConsole(page);
    const log = page.locator('#console-log');
    // Both halves: the raw round-trip failure, and what it cost the user.
    const raw = log.locator('.console-entry', { hasText: 'form-fields failed' });
    await expect(raw).toHaveCount(1);
    await expect(raw).toContainText('injected host failure');
    await expect(log.locator('.console-entry', { hasText: 'form fields could not be listed' }))
      .toHaveCount(1);
    await expect(log.locator('.console-entry.error').first()).toBeVisible();

    // Additive, not a replacement: the failure never overwrote the shared status line.
    await expect(page.locator('#status')).not.toContainText('form-fields');
    await page.locator('#console-close').click();
    await page.close();
  });

  test('activity console: untrusted entry text is rendered as text, never as HTML', async () => {
    // Host error strings reach the console verbatim, and a hostile document can influence them.
    // A console built with innerHTML would be a worse sink than the one #73 fixed.
    const payload = '<img src=x onerror="window.__pwned = 1">';
    const page = await openGatedViewerWith(fixture('console-xss.pdf', [[{ text: 'XSS test', x: 72, y: 700 }]]),
      { fail: ['form-fields'], failMessage: payload });

    await openConsole(page);
    const log = page.locator('#console-log');
    await expect(log.locator('.console-entry.error').first()).toBeVisible();
    // The payload is shown, as text...
    await expect(log).toContainText(payload);
    // ...and produced no element and ran no script.
    await expect(log.locator('img')).toHaveCount(0);
    expect(await page.evaluate(() => window.__pwned)).toBeUndefined();
    await page.locator('#console-close').click();
    await page.close();
  });

  test('a page that keeps failing to render says so instead of staying blank (#72/#20)', async () => {
    const page = await openGatedViewerWith(fixture('render-fail.pdf', [[{ text: 'Page one', x: 72, y: 700 }], [{ text: 'Page two', x: 72, y: 700 }]]), {});
    // No placeholder on a healthy document.
    await expect(page.locator('.page-error')).toHaveCount(0);

    // Now every render fails. Zooming invalidates the render cache, so the page re-renders.
    // Retrying on scroll stays deliberate: only repeated failures for a page get a placeholder,
    // so a single hiccup during fast scrolling still just retries, silently.
    await page.evaluate(() => window.__hostGate.fail(['render']));
    await page.click('#btn-zoom-in');
    await page.click('#btn-zoom-in');
    await expect(page.locator('.page-error').first()).toBeVisible({ timeout: 30000 });
    await expect(page.locator('.page-error').first())
      .toContainText('This page could not be rendered');

    await openConsole(page);
    await expect(page.locator('#console-log')
      .locator('.console-entry', { hasText: 'did not render' }).first()).toBeVisible();

    // Recovering clears the placeholder again — it is a state, not a permanent tombstone.
    await page.evaluate(() => window.__hostGate.fail([]));
    await page.click('#btn-zoom-out');
    await expect(page.locator('.page-error')).toHaveCount(0, { timeout: 30000 });
    await page.locator('#console-close').click();
    await page.close();
  });

  test('open-from-url: refuses a src param pointing at a private/internal host', async () => {
    const page = await ext.context.newPage();
    await page.goto(`${ext.viewerUrl}?src=${encodeURIComponent('http://169.254.169.254/latest/meta-data/doc.pdf')}`);
    await expect(page.locator('#status')).toContainText('Refusing to open');
    await page.close();
  });

  test('open-from-url: refuses a src param that is not a PDF URL', async () => {
    const page = await ext.context.newPage();
    await page.goto(`${ext.viewerUrl}?src=${encodeURIComponent('https://example.com/not-a-pdf')}`);
    await expect(page.locator('#status')).toContainText('Refusing to open');
    await page.close();
  });
  test('redaction over a picture removes the words without scrubbing the image (#87)', async () => {
    // A black box that also eats the photograph under it is a data-loss bug, and one that no
    // status line or "is the region black?" check can see. The fixture is a flat green picture
    // with a line of text on top: after redacting just the words, the pixels *inside* the box
    // must be black, the pixels *around* it must still be exactly that green, and the words
    // must be gone from the extracted text.
    const file = path.join(fixtureDir, 'redact-image.pdf');
    const GREEN = [0, 153, 68];
    // The second line is a control that must survive: without a word left on the page, the
    // "the secret is gone" assertion would be satisfied by a text layer that simply had not
    // rebuilt yet, and would pass against a build that removed nothing.
    fs.writeFileSync(file, buildImagePdf({
      rect: [72, 500, 500, 750], rgb: GREEN,
      text: [
        { text: 'SECRET OVER PICTURE', x: 90, y: 700, size: 18 },
        { text: 'caption stays put', x: 90, y: 460, size: 14 },
      ],
    }));
    const page = await openViewerWith(file);
    await expectText(page).toContain('SECRET OVER PICTURE');
    // The picture really is one flat colour to begin with, so "still green" means something.
    expect(await colorFraction(page, { x: 100, y: 540, width: 300, height: 100 }, GREEN)).toBe(1);

    await ui(page, '#tool-redact');
    await dragPdfRect(page, { x: 85, y: 694, width: 210, height: 26 });
    await expect(page.locator('#redact-list li')).toHaveCount(1);
    await page.click('#redact-apply');
    await expect(page.locator('#status')).toContainText('content removed');

    // Words gone from the file, while the control line below the picture is still there...
    await expectText(page).toContain('caption stays put');
    await expectText(page).not.toContain('SECRET');
    // ...box solid black where it was drawn...
    // Note this is a colour test, not an ink test: the picture's green is dark enough to count
    // as "ink" on its own, so an ink fraction of 1.0 here would be true with no box at all.
    const box = { x: 95, y: 698, width: 180, height: 16 };
    expect(await colorFraction(page, box, [0, 0, 0], { tolerance: 4 })).toBe(1);
    expect(await dominantColor(page, box)).toEqual([0, 0, 0]);
    // ...and the picture intact everywhere else, including the strip to the right of the box
    // on the very same scanline (where a "scrub the whole image" bug shows up first).
    expect(await colorFraction(page, { x: 100, y: 540, width: 300, height: 100 }, GREEN)).toBe(1);
    expect(await colorFraction(page, { x: 320, y: 694, width: 160, height: 26 }, GREEN)).toBe(1);
    await page.close();
  });

  test('add text: drag a box, type, and stamp it onto the page', async () => {
    const file = fixture('addtext.pdf', [[{ text: 'background', x: 72, y: 100 }]]);
    const page = await openViewerWith(file);

    // Nothing is here yet — measured, so "the text appeared" is a change and not a coincidence.
    expect(await inkFraction(page, { x: 105, y: 675, width: 310, height: 26 })).toBe(0);

    await ui(page, '#tool-text');
    await dragPdfRect(page, { x: 110, y: 680, width: 300, height: 16 });

    await expect(page.locator('#panel-edit')).toBeVisible();
    await expect(page.locator('#edit-title')).toHaveText('Add text');
    await page.fill('#edit-text', 'STAMPED CAPTION');
    await page.click('#edit-apply');
    await expect(page.locator('#status')).toContainText('Text added');

    // The characters are in the document — read back out of it, not out of the form control
    // that still holds what was typed. (The old assertion re-read the region with the edit tool
    // and matched `#edit-text` before the host had answered, so it was matching the typed value
    // against itself; it passed even when the stamped text was truncated.)
    await expectText(page).toContain('STAMPED CAPTION');
    // ...and it lands in the box that was dragged. "Some ink appeared in a generous band" is
    // satisfied by text stamped a whole line out of place, so the assertion is on where the ink
    // actually is: the drag was x=110..410, y=680..696, and the glyphs must sit inside it.
    const stamped = await inkBounds(page, { x: 100, y: 620, width: 340, height: 100 });
    expect(stamped).not.toBeNull();
    expect(stamped.x).toBeGreaterThan(105);
    expect(stamped.x).toBeLessThan(120);
    expect(stamped.y).toBeGreaterThan(674);
    expect(stamped.y).toBeLessThan(686);
    expect(stamped.y + stamped.height).toBeLessThan(700);
    // ...and nowhere else: text stamped at the wrong scale or origin still satisfies "it exists".
    expect(await inkFraction(page, { x: 105, y: 600, width: 310, height: 26 })).toBe(0);
    // The page's existing content is still there and still where it was.
    await expectText(page).toContain('background');
    expect(await inkFraction(page, { x: 70, y: 96, width: 80, height: 16 })).toBeGreaterThan(0);
    await page.close();
  });

  // eslint-disable-next-line playwright/no-skipped-test
  test('add text: a caption longer than its box is stamped in full, not clipped', async () => {
    // LIVE BUG. Placing text with a plain click gives a 240x26pt box and defaults the type size
    // to the box height (26pt). "STAMPED CAPTION" does not fit 240pt at 26pt, and TextTools.
    // StampText lays the string into a fixed-size iText Canvas, so the overflow is *clipped and
    // discarded*: the document ends up holding "STAMPED CAPTIO". The same clipping is what turns
    // a replacement of "HELLO" with "WORLDWIDE GREETINGS" into "WORLDWIDE" (see the text-edit
    // fixme below) — one defect, two doors.
    //
    // This test asserts the whole caption survives, from the file's own extracted text:
    //   await expectText(page).toContain('STAMPED CAPTION');
    // Observed on this branch: the extracted run is "STAMPED CAPTIO" and the assertion reads
    //   Expected string: "STAMPED CAPTION" / Received string: "background STAMPED CAPTIO"
    // Fixing it means deciding what "too long" should do — shrink to fit, grow the box, or
    // wrap and grow downwards — which is a product decision, not a test fix.
    const file = fixture('addtext-long.pdf', [[{ text: 'background', x: 72, y: 100 }]]);
    const page = await openViewerWith(file);
    await ui(page, '#tool-text');
    const box = await page.locator(pageImageSel(1)).boundingBox();
    const scale = box.width / 595;
    await page.mouse.click(box.x + 120 * scale, box.y + (842 - 700) * scale);
    await expect(page.locator('#panel-edit')).toBeVisible();
    await page.fill('#edit-text', 'STAMPED CAPTION');
    await page.click('#edit-apply');
    await expect(page.locator('#status')).toContainText('Text added');
    await expectText(page).toContain('STAMPED CAPTION');
    await page.close();
  });

  test('redaction on a rotated (/Rotate 90) page: the box lands where it is drawn', async () => {
    // Placement only — deliberately. The content half of this behaviour is broken and lives in
    // the fixme below; this test's title now claims exactly what it proves and no more.
    const page = await rotatedRedaction();

    // Sample inside the drawn rectangle but clear of the glyph strip (x 0.44..0.465). The text
    // is dark, so sampling over it reads black whether a box was painted or not — that band is
    // 0.03 dark before the redaction and would not discriminate.
    expect(await displayDarkFraction(page, 0.44, 0.18, 0.465, 0.37)).toBe(1);
    // ...and the box is a box: the rest of the sheet is untouched paper.
    expect(await displayDarkFraction(page, 0.60, 0.45, 0.90, 0.75)).toBe(0);
    await page.close();
  });

  // eslint-disable-next-line playwright/no-skipped-test
  test('redaction on a rotated (/Rotate 90) page removes the text under the box', async () => {
    // LIVE BUG — and the most serious one in this branch. On a /Rotate 90 page, dragging a
    // redaction box paints the black rectangle exactly where it was drawn and removes *no text
    // at all*. The words stay in the file: selectable, copyable and searchable underneath an
    // opaque black box. That is the failure mode redaction exists to prevent.
    //
    // Measured on this branch, dragging over the first line of a rotated page:
    //   dark fraction inside the box, clear of glyphs   0.00 -> 1.00   (the box is painted)
    //   extracted text                "rotated secret rotated control" -> unchanged
    //
    // It is the viewer's drag mapping, not the host: search-and-mark redaction on the *same*
    // rotated fixture, which uses absolute PDF coordinates and no screen mapping at all,
    // removes the word correctly ("ROTSECRET","ROTCONTROL" -> "ROTCONTROL"). So the region a
    // drag produces on a rotated page paints correctly but does not match the glyphs for
    // removal — the two disagree about which space the rectangle is in.
    //
    // Not fixed here: I was asked not to touch extension/src/viewer.js further, and the fix is
    // a coordinate-space change in cssToPdf/displayToPage that wants its own tests.
    const page = await rotatedRedaction();
    await expectText(page).toContain('rotated control');
    await expectText(page).not.toContain('rotated secret');
    await page.close();
  });

  test('text edit: reads existing text, replaces it in place, leaves its neighbour alone', async () => {
    const file = fixture('edit.pdf', [
      [{ text: 'Amount Due: $500', x: 72, y: 700 }, { text: 'Do not touch this', x: 72, y: 640 }],
    ]);
    const page = await openViewerWith(file);
    const editedBefore = await inkBounds(page, { x: 60, y: 690, width: 300, height: 30 });
    const neighbourBefore = await inkBounds(page, { x: 60, y: 630, width: 300, height: 30 });
    expect(neighbourBefore).not.toBeNull();

    await ui(page, '#tool-edit');
    // Drag a box that hugs the line. The replacement is laid out from the top-left of whatever
    // box you draw, so a box noticeably bigger than the text legitimately moves it — that is the
    // behaviour ("lay the new text inside the same rectangle"), not a bug, and it would make the
    // position assertions below meaningless.
    await dragPdfRect(page, { x: 71, y: 694, width: 240, height: 20 });
    await expect(page.locator('#panel-edit')).toBeVisible();
    await expect(page.locator('#edit-text')).toHaveValue('Amount Due: $500');

    await page.fill('#edit-text', 'Amount Due: $750');
    await page.click('#edit-apply');
    await expect(page.locator('#status')).toContainText('Text replaced');

    // Read the result out of the *document*. The old test re-selected the region and asserted on
    // `#edit-text`, but nothing clears that control after an apply, so it still held the string
    // that had just been typed into it — the assertion matched the typed value against itself
    // and would have passed even if replace-region-text had done nothing at all.
    await expectText(page).toContain('$750');
    await expectText(page).not.toContain('$500');
    // The old amount is gone from the paper too, not merely overprinted.
    const line = await bandStats(page, { x: 60, y: 690, width: 260, height: 30 });
    expect(line.ink).toBeGreaterThan(0.02);   // the new text is drawn there
    expect(line.ink).toBeLessThan(0.5);       // ...and it is text, not a filled patch
    // ...on the line it replaced, not shunted up or down it. The tolerance is 4pt on 14pt text:
    // tight enough that a displacement of a whole line fails, loose enough to absorb the ~2.5pt
    // baseline creep the type-size bug in the fixme below currently causes.
    expect(Math.abs(line.inkBox.y - editedBefore.y)).toBeLessThan(4);
    expect(Math.abs(line.inkBox.x - editedBefore.x)).toBeLessThan(4);
    // And the line below it did not shift, resize, or vanish while we were editing above it.
    await expectText(page).toContain('Do not touch this');
    const neighbourAfter = await inkBounds(page, { x: 60, y: 630, width: 300, height: 30 });
    expect(neighbourAfter.x).toBeCloseTo(neighbourBefore.x, 0);
    expect(neighbourAfter.y).toBeCloseTo(neighbourBefore.y, 0);
    expect(neighbourAfter.width).toBeCloseTo(neighbourBefore.width, 0);
    expect(neighbourAfter.height).toBeCloseTo(neighbourBefore.height, 0);
    await page.close();
  });

  // eslint-disable-next-line playwright/no-skipped-test
  test('text edit: the type size survives repeated edits (#29/#84)', async () => {
    // LIVE BUG. TextTools.GetTextInRegion reports the type size as the *glyph* box height —
    // chunks.Max(c => c.FontHeight), the ascent-to-descent span of the rendered characters —
    // rather than the em size the text was set in. For Helvetica that is 0.925 em, so every
    // edit that keeps "the size that was already there" sets it 7.5% smaller than it was, and
    // the loss compounds. Measured on this branch, editing 24pt text three times over:
    //
    //   detected size   24 -> 22.2 -> 20.5 -> 19.0
    //   ink box height  18 -> 17   -> 15.5 -> 14.5
    //
    // The same detection feeds move-text, so dragging a run also shrinks it (18.5 -> 17.1).
    //
    // The assertion below is the maintainer's rule — after an edit the type size must still
    // match what was there before — measured off the rendered glyphs rather than off the
    // control that is itself wrong. It currently reads, on the first round:
    //   Expected: close to 18 (2 digits precision) / Received: 17
    // and drifts further every round. The fix belongs in the native host's text metrics
    // (derive the em from the font's ascender/descender, or carry the Tf size through), which
    // is core-library work with the golden corpus attached to it, not an e2e change.
    const file = fixture('edit-size.pdf', [[{ text: 'SIZE TEST', x: 72, y: 700, size: 24 }]]);
    const page = await openViewerWith(file);
    const band = { x: 55, y: 680, width: 320, height: 50 };
    const original = await inkBounds(page, band);

    for (let round = 1; round <= 3; round++) {
      await ui(page, '#tool-edit');
      await dragPdfRect(page, { x: 60, y: 685, width: 300, height: 40 });
      await expect(page.locator('#panel-edit')).toBeVisible();
      // Change nothing at all — same text, same font, same size. This must be a no-op.
      await expect(page.locator('#edit-font')).toHaveValue('helvetica');
      await page.click('#edit-apply');
      await expect(page.locator('#status')).toContainText('Text replaced');
      await expect.poll(() => inkBounds(page, band).then((b) => b?.height), { timeout: 20000 })
        .toBeCloseTo(original.height, 0);
    }
    await page.close();
  });

  // eslint-disable-next-line playwright/no-skipped-test
  test('text edit: a replacement longer than the original is not truncated', async () => {
    // LIVE BUG, and the one the maintainer reported as "HELLO" becoming "WORL". TextTools.
    // StampText lays the replacement into an iText Canvas sized to the *original* region, so
    // anything that does not fit is clipped away and lost. Replacing "HELLO" (24pt, in a
    // 200x40pt region) with "WORLDWIDE GREETINGS" wraps to two lines, the second of which does
    // not fit, and the document is left holding just "WORLDWIDE".
    //
    // Observed on this branch:
    //   Expected string: "WORLDWIDE GREETINGS" / Received string: "WORLDWIDE"
    //
    // Same root cause as the add-text fixme above. Not fixed here because the right behaviour
    // (shrink to fit / grow the box / wrap downwards) is a product decision.
    const file = fixture('edit-longer.pdf', [[{ text: 'HELLO', x: 72, y: 700, size: 24 }]]);
    const page = await openViewerWith(file);
    await ui(page, '#tool-edit');
    await dragPdfRect(page, { x: 60, y: 690, width: 200, height: 40 });
    await expect(page.locator('#panel-edit')).toBeVisible();
    await expect(page.locator('#edit-text')).toHaveValue('HELLO');
    await page.fill('#edit-text', 'WORLDWIDE GREETINGS');
    await page.click('#edit-apply');
    await expect(page.locator('#status')).toContainText('Text replaced');
    await expectText(page).toContain('WORLDWIDE GREETINGS');
    await page.close();
  });

  test('text edit: replacing a caption over a picture does not punch through the picture', async () => {
    // Regression (#89): editing text drawn on top of an image used to paint the "background"
    // the editor assumed was under it, knocking a black rectangle through the picture. Nothing
    // in the old suite looked at a single pixel of an image, so it shipped.
    const file = path.join(fixtureDir, 'edit-over-image.pdf');
    const GREEN = [0, 153, 68];
    fs.writeFileSync(file, buildImagePdf({
      rect: [72, 500, 500, 750], rgb: GREEN,
      text: [{ text: 'CAPTION', x: 100, y: 700, size: 18 }],
    }));
    const page = await openViewerWith(file);

    await ui(page, '#tool-edit');
    await dragPdfRect(page, { x: 95, y: 694, width: 150, height: 28 });
    await expect(page.locator('#panel-edit')).toBeVisible();
    await expect(page.locator('#edit-text')).toHaveValue('CAPTION');
    await page.fill('#edit-text', 'REVISED');
    await page.click('#edit-apply');
    await expect(page.locator('#status')).toContainText('Text replaced');

    // The edit took...
    await expectText(page).toContain('REVISED');
    await expectText(page).not.toContain('CAPTION');
    // ...and the picture is still the picture where the old caption used to be. A punched-out
    // rectangle drops this to roughly zero; a fully intact patch would be 1.0, and the ~0.9
    // floor leaves room for the new glyphs themselves.
    const edited = { x: 95, y: 694, width: 150, height: 28 };
    expect(await colorFraction(page, edited, GREEN)).toBeGreaterThan(0.85);
    // Nothing black was introduced anywhere in the patch.
    expect(await dominantColor(page, edited)).toEqual(GREEN);
    await page.close();
  });

  test('move image: grab a picture and drag it to a new position', async () => {
    // The old test merged a 1x1 PNG and asserted the status line said "Image moved" — which it
    // says whether the picture went where it was dropped, went somewhere else, or vanished. A
    // flat-colour picture at a known rectangle turns that into two measurements: the colour has
    // to leave the old rectangle entirely and arrive, whole, at the new one.
    const file = path.join(fixtureDir, 'move-image.pdf');
    const RED = [220, 20, 20];
    fs.writeFileSync(file, buildImagePdf({ rect: [200, 500, 300, 600], rgb: RED }));
    const page = await openViewerWith(file);

    const origin = { x: 210, y: 510, width: 80, height: 80 };
    const destination = { x: 110, y: 360, width: 80, height: 80 };
    expect(await colorFraction(page, origin, RED)).toBe(1);
    expect(await colorFraction(page, destination, RED)).toBe(0);

    await ui(page, '#tool-move');
    const box = await page.locator(pageImageSel(1)).boundingBox();
    const scale = box.width / 595;
    const cx = (px) => box.x + px * scale;
    const cy = (py) => box.y + (842 - py) * scale;
    // Grab the middle of the picture and drop it 100pt left and 150pt down.
    await page.mouse.move(cx(250), cy(550));
    await page.mouse.down();
    await page.mouse.move(cx(150), cy(400), { steps: 8 });
    await page.mouse.up();
    await expect(page.locator('#status')).toContainText('Image moved');

    // Gone from where it was — back to blank paper, not a red smear or a torn edge.
    await expect.poll(() => colorFraction(page, origin, RED), { timeout: 20000 }).toBe(0);
    expect(await bandStats(page, origin).then((s) => s.paper)).toBe(1);
    // ...and arrived intact at the drop point: every pixel of the sampled interior is the
    // picture's colour, so it was translated rather than clipped, scaled or recoloured.
    expect(await colorFraction(page, destination, RED)).toBe(1);
    await page.close();
  });

  test('organize pages: reorder moves the page, and its content moves with it', async () => {
    const file = fixture('reorder.pdf', [
      [{ text: 'AAAONE', x: 72, y: 700 }],
      [{ text: 'BBBTWO', x: 72, y: 700 }],
    ]);
    const page = await openViewerWith(file);
    await expectText(page, 1).toContain('AAAONE');

    await ui(page, '#btn-organize');
    await expect(page.locator('#organize-list .organize-item')).toHaveCount(2);
    await page.locator('#organize-list .organize-item').nth(1)
      .getByRole('button', { name: 'Move up' }).click();
    await page.click('#organize-apply');
    await expect(page.locator('#status')).toContainText('reorganized');

    // Reordering is the operation where "the UI list changed" and "the file changed" come apart
    // most easily, so both pages are read back: the second page's content is now first, and the
    // first page's content is now second — not duplicated, not dropped, not left in place.
    await expect(page.locator('#page-total')).toHaveText('2');
    await expectText(page, 1).toContain('BBBTWO');
    await expectText(page, 1).not.toContain('AAAONE');
    await page.evaluate(() => document.querySelector('.page[data-page="2"]')
      .scrollIntoView({ block: 'start', behavior: 'instant' }));
    await expect(page.locator(pageImageSel(2))).toHaveAttribute('src', /data:image\/png/);
    await expectText(page, 2).toContain('AAAONE');
    await page.close();
  });

  test('save: the exported file is a real PDF that carries the edit', async () => {
    // "Saving via downloads…" is a status line the viewer prints before it knows anything about
    // what came out. The bytes handed to chrome.downloads are intercepted here, written to disk,
    // and *reopened through the whole extension + native host pipeline* — which is the only way
    // to prove the export is a valid document and that the user's edit is in it.
    const file = fixture('save.pdf', [[{ text: 'ORIGINALWORD', x: 72, y: 700 }]]);
    const page = await openCapturingViewerWith(file);

    await ui(page, '#btn-find');
    await fillDialog(page, ['ORIGINALWORD', 'REPLACEDWORD'], 'Replace all');
    await expect(page.locator('#status')).toContainText('Replaced 1 occurrence');

    await page.click('#btn-save');
    await expect(page.locator('#status')).toContainText('Saving via downloads');
    const exported = await writeCapturedExport(page, 'save-exported.pdf');
    await page.close();

    // It is named after the source, and it is a PDF, not an empty or truncated blob.
    expect(exported.name).toBe('save-edited.pdf');
    expect(exported.bytes.subarray(0, 5).toString('latin1')).toBe('%PDF-');
    expect(exported.bytes.length).toBeGreaterThan(500);

    // Reopened, it renders and holds the edited text — not the original.
    const reopened = await openViewerWith(exported.file);
    await expectText(reopened).toContain('REPLACEDWORD');
    await expectText(reopened).not.toContain('ORIGINALWORD');
    expect(await inkFraction(reopened, { x: 70, y: 696, width: 120, height: 16 }))
      .toBeGreaterThan(0.05);
    await reopened.close();
  });

  test('undo restores the previous state, in the document and on the page', async () => {
    const file = fixture('undo-content.pdf', [[{ text: 'Original', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);
    const before = await inkBounds(page, { x: 60, y: 690, width: 300, height: 30 });

    await ui(page, '#btn-find');
    await fillDialog(page, ['Original', 'Changed'], 'Replace all');
    await expect(page.locator('#status')).toContainText('Replaced 1 occurrence');
    await expectText(page).toContain('Changed');

    await expect(page.locator('#btn-undo')).toBeEnabled();
    await page.click('#btn-undo');
    await expect(page.locator('#status')).toContainText('Undid last change');

    // Undo has to put the document back, not just re-enable a button and print a message.
    await expectText(page).toContain('Original');
    await expectText(page).not.toContain('Changed');
    const after = await inkBounds(page, { x: 60, y: 690, width: 300, height: 30 });
    expect(after.x).toBeCloseTo(before.x, 0);
    expect(after.y).toBeCloseTo(before.y, 0);
    expect(after.width).toBeCloseTo(before.width, 0);
    await page.close();
  });

  // ------------------------------------------------------- activity console (#72)

  /** Opens a viewer with the host gate installed, loads `file`, and returns the page. */
  async function openGatedViewerWith(file, gateOptions) {
    const page = await ext.context.newPage();
    await installHostGate(page, gateOptions);
    await page.goto(ext.viewerUrl);
    const chooser = page.waitForEvent('filechooser');
    await page.click('#btn-open-empty');
    await (await chooser).setFiles(file);
    await expect(page.locator(pageImageSel(1))).toHaveAttribute('src', /data:image\/png/);
    return page;
  }

  // ---------------------------------------------------------------------------------------------
  // Control coverage: every action button, when clicked, actually runs its function — not just
  // that the control is present. Each of these was uncovered before; the assertion is the effect
  // in the document or the panel, and each was watched failing with its handler stubbed.
  // ---------------------------------------------------------------------------------------------

  test('control: rotate-left turns the page the other way and keeps its content', async () => {
    const file = fixture('rotleft.pdf', [[{ text: 'Portrait', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);
    const before = await page.locator('.page[data-page="1"]').boundingBox();
    expect(before.height).toBeGreaterThan(before.width);
    const inkBefore = await inkFraction(page, { x: 0, y: 0, width: 595, height: 842 });

    await ui(page, '#btn-rotate-left');
    await expect(page.locator('#status')).toContainText('Rotated page 1');

    await expect.poll(async () => {
      const b = await page.locator('.page[data-page="1"]').boundingBox();
      return b.width > b.height;
    }).toBe(true);
    // Content survived the turn — a rotate that blanks or clips the page is the failure that counts.
    await expectText(page).toBe('Portrait');
    const inkAfter = await inkFraction(page, { x: 0, y: 0, width: 842, height: 595 },
      { mediaBox: [0, 0, 842, 595] });
    expect(inkAfter).toBeGreaterThan(inkBefore * 0.8);
    await page.close();
  });

  test('control: redact-clear drops the pending boxes so Apply removes nothing', async () => {
    const file = fixture('redactclear.pdf', [[{ text: 'KEEP THIS LINE', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);

    await ui(page, '#tool-redact');
    await dragPdfRect(page, { x: 66, y: 694, width: 250, height: 22 });
    await expect(page.locator('#redact-list li')).toHaveCount(1);
    expect(await page.locator('#redact-clear').isDisabled()).toBe(false);

    await page.click('#redact-clear');
    // The queue is emptied and the action buttons go back to disabled.
    await expect(page.locator('#redact-list li')).toHaveCount(0);
    expect(await page.locator('#redact-clear').isDisabled()).toBe(true);
    expect(await page.locator('#redact-preview').isDisabled()).toBe(true);

    // And with nothing queued the text is untouched — the boxes really were dropped, not hidden.
    await expectText(page).toContain('KEEP THIS LINE');
    await page.close();
  });

  test('control: draw-clear discards the stroke so Apply stamps nothing', async () => {
    const file = fixture('drawclear.pdf', [[{ text: 'canvas', x: 72, y: 100 }]]);
    const page = await openViewerWith(file);

    await ui(page, '#tool-draw');
    await expect(page.locator('#panel-draw')).toBeVisible();
    await page.fill('#draw-color', '#00ff00');
    await page.fill('#draw-width', '8');
    const box = await page.locator(pageImageSel(1)).boundingBox();
    const midY = box.y + box.height * 0.5;
    await page.mouse.move(box.x + box.width * 0.2, midY);
    await page.mouse.down();
    await page.mouse.move(box.x + box.width * 0.4, midY, { steps: 6 });
    await page.mouse.move(box.x + box.width * 0.7, midY, { steps: 6 });
    await page.mouse.up();

    await page.click('#draw-clear');
    await page.click('#draw-apply');

    // applyDrawing() bails with this toast only when no strokes remain to apply, so it is the
    // proof draw-clear emptied them: with the handler stubbed the stroke survives and the status
    // reads "Added 1 stroke" instead. (Sampling pixels here races the apply render — this status
    // is the deterministic signal.)
    await expect(page.locator('#status')).toHaveText('Draw something first.');
    await page.close();
  });

  test('control: the italic toggle sets the run in italic, and it round-trips', async () => {
    const file = fixture('italic.pdf', [[{ text: 'Plain Words', x: 72, y: 700, size: 20 }]]);
    const page = await openViewerWith(file);

    await ui(page, '#tool-edit');
    await dragPdfRect(page, { x: 60, y: 690, width: 320, height: 34 });
    await expect(page.locator('#edit-italic')).not.toHaveClass(/active/);
    await page.click('#edit-italic');
    await expect(page.locator('#edit-italic')).toHaveClass(/active/);
    await page.click('#edit-apply');
    await expect(page.locator('#status')).toContainText('Text replaced');

    // Re-open a blank region to reset the controls, then re-read the edited run: the detector
    // reports italic because the replacement really was stamped in an italic face.
    await ui(page, '#tool-edit');
    await dragPdfRect(page, { x: 60, y: 380, width: 260, height: 34 });
    await expect(page.locator('#edit-italic')).not.toHaveClass(/active/);
    await ui(page, '#tool-edit');
    await dragPdfRect(page, { x: 60, y: 690, width: 320, height: 34 });
    await expect(page.locator('#edit-italic')).toHaveClass(/active/);
    await page.close();
  });

  test('control: js-clear empties the script editor', async () => {
    const file = fixture('jsclear.pdf', [[{ text: 'doc', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);

    await ui(page, '#btn-js');
    await expect(page.locator('#js-dialog')).toBeVisible();
    await page.fill('#js-name', 'greet');
    await page.fill('#js-source', "app.alert('hi');");

    await page.click('#js-clear');
    await expect(page.locator('#js-name')).toHaveValue('');
    await expect(page.locator('#js-source')).toHaveValue('');
    await page.close();
  });

  test('control: organize-reset restores pages removed in the panel', async () => {
    const file = fixture('orgreset.pdf', [
      [{ text: 'Page one', x: 72, y: 700 }],
      [{ text: 'Page two', x: 72, y: 700 }],
      [{ text: 'Page three', x: 72, y: 700 }],
    ]);
    const page = await openViewerWith(file);

    await ui(page, '#btn-organize');
    await expect(page.locator('#organize-list .organize-item')).toHaveCount(3);
    await page.locator('#organize-list .organize-item').nth(1)
      .getByRole('button', { name: 'Remove page' }).click();
    await expect(page.locator('#organize-list .organize-item')).toHaveCount(2);

    // Reset rebuilds the original arrangement without touching the document.
    await page.click('#organize-reset');
    await expect(page.locator('#organize-list .organize-item')).toHaveCount(3);
    // Applying the reset arrangement leaves all three pages in place.
    await page.click('#organize-apply');
    await expect(page.locator('#page-total')).toHaveText('3');
    await page.close();
  });


  // Dismiss buttons: clicking cancel/done closes the panel. Parameterised over the panels that
  // open with a single click; each was watched staying open with its handler stubbed.
  for (const c of [
    { name: 'forms-cancel', open: (p) => ui(p, '#btn-forms'), panel: '#panel-forms', btn: '#forms-cancel' },
    { name: 'organize-cancel', open: (p) => ui(p, '#btn-organize'), panel: '#panel-organize', btn: '#organize-cancel' },
    { name: 'sanitize-cancel', open: (p) => ui(p, '#btn-sanitize'), panel: '#panel-sanitize', btn: '#sanitize-cancel' },
    { name: 'draw-cancel', open: (p) => ui(p, '#tool-draw'), panel: '#panel-draw', btn: '#draw-cancel' },
    { name: 'highlight-done', open: (p) => ui(p, '#tool-highlight'), panel: '#panel-highlight', btn: '#highlight-done' },
  ]) {
    test(`control: ${c.name} closes its panel`, async () => {
      const page = await openViewerWith(fixture(`${c.name}.pdf`, [[{ text: 'doc', x: 72, y: 700 }]]));
      await c.open(page);
      await expect(page.locator(c.panel)).toBeVisible();
      await page.click(c.btn);
      await expect(page.locator(c.panel)).toBeHidden();
      await page.close();
    });
  }

  test('control: edit-cancel closes the panel and discards the staged edit', async () => {
    const file = fixture('editcancel.pdf', [[{ text: 'Original Text', x: 72, y: 700, size: 18 }]]);
    const page = await openViewerWith(file);

    await ui(page, '#tool-edit');
    await dragPdfRect(page, { x: 60, y: 690, width: 320, height: 34 });
    await expect(page.locator('#panel-edit')).toBeVisible();
    await page.fill('#edit-text', 'Changed Text');
    await page.click('#edit-cancel');
    await expect(page.locator('#panel-edit')).toBeHidden();

    // Nothing was applied: the document still holds the original words.
    await expectText(page).toContain('Original Text');
    await expectText(page).not.toContain('Changed Text');
    await page.close();
  });

  test('control: sign-cancel closes the signature panel', async () => {
    const file = fixture('signcancel.pdf', [[{ text: 'Sign here:', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);
    await ui(page, '#tool-sign');
    await dragPdfRect(page, { x: 200, y: 640, width: 160, height: 50 });
    await expect(page.locator('#panel-sign')).toBeVisible();
    await page.click('#sign-cancel');
    await expect(page.locator('#panel-sign')).toBeHidden();
    await page.close();
  });

  test('control: the signature tabs switch between the pad and the upload field', async () => {
    const file = fixture('signtabs.pdf', [[{ text: 'Sign here:', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);
    await ui(page, '#tool-sign');
    await dragPdfRect(page, { x: 200, y: 640, width: 160, height: 50 });
    // Opens on the draw pad.
    await expect(page.locator('#sign-draw')).toBeVisible();
    await expect(page.locator('#sign-upload')).toBeHidden();
    // Upload tab reveals the file field and hides the pad...
    await page.click('#sign-tab-upload');
    await expect(page.locator('#sign-upload')).toBeVisible();
    await expect(page.locator('#sign-draw')).toBeHidden();
    // ...and the draw tab switches back.
    await page.click('#sign-tab-draw');
    await expect(page.locator('#sign-draw')).toBeVisible();
    await expect(page.locator('#sign-upload')).toBeHidden();
    await page.close();
  });

  test('control: sign-pad-clear wipes the pad so Apply has nothing to place', async () => {
    const file = fixture('signpadclear.pdf', [[{ text: 'Sign here:', x: 72, y: 700 }]]);
    const page = await openViewerWith(file);
    await ui(page, '#tool-sign');
    await dragPdfRect(page, { x: 200, y: 640, width: 160, height: 50 });

    const pad = await page.locator('#sign-pad').boundingBox();
    await page.mouse.move(pad.x + 12, pad.y + pad.height - 20);
    await page.mouse.down();
    await page.mouse.move(pad.x + pad.width / 2, pad.y + 14, { steps: 8 });
    await page.mouse.up();

    await page.click('#sign-pad-clear');
    await page.click('#sign-apply');
    // Apply refuses because the pad is empty again — the proof clear wiped it. A stubbed clear
    // leaves the scribble and Apply proceeds to "Placing signature…" / "Signature placed."
    await expect(page.locator('#status')).toHaveText('Draw a signature first.');
    await page.close();
  });

  test('control: links-close closes the links panel', async () => {
    const file = path.join(fixtureDir, 'linksclose.pdf');
    fs.writeFileSync(file, buildLinkPdf('https://example.com/x'));
    const page = await openViewerWith(file);
    await ui(page, '#btn-links');
    await expect(page.locator('#panel-links')).toBeVisible();
    await page.click('#links-close');
    await expect(page.locator('#panel-links')).toBeHidden();
    await page.close();
  });

  test('control: links-rescan re-runs the risk scan', async () => {
    const file = path.join(fixtureDir, 'linksrescan.pdf');
    fs.writeFileSync(file, buildLinkPdf('https://example.com/y'));

    // Hold the URL risk scan so its running state is observable — a single-link scan otherwise
    // lands faster than the progress indicator can be seen (the #19 test uses the same gate).
    const page = await ext.context.newPage();
    await installHostGate(page, { hold: ['scan-urls'] });
    await page.goto(ext.viewerUrl);
    const chooser = page.waitForEvent('filechooser');
    await page.click('#btn-open-empty');
    await (await chooser).setFiles(file);
    await expect(page.locator(pageImageSel(1))).toHaveAttribute('src', /data:image\/png/);
    // Let the document's own initial scan through and settle. stopHolding() first, because
    // release() only delivers what is *already* parked: the initial scan-urls is issued behind a
    // list-link-hotspots round trip and a chrome.storage read, so it can still be in flight when
    // page 1's image lands. Releasing alone loses that race on a slow runner — the scan is parked
    // *after* the release, nothing ever delivers it, and #link-status stays on "Checking 1 link…"
    // until this assertion times out. Clearing the held set first makes both orderings pass.
    await page.evaluate(() => { window.__hostGate.stopHolding(); window.__hostGate.release(); });
    await expect(page.locator('#link-status')).toBeHidden({ timeout: 15000 });

    await ui(page, '#btn-links');
    await page.locator('#links-enable').check();           // rescan appears once links are kept
    await expect(page.locator('#links-rescan')).toBeVisible();

    // The handler was wired as addEventListener('click', scanLinks), so the click Event was passed
    // as scanLinks's `link` argument and `link.stale()` threw "link.stale is not a function" — the
    // rescan never ran, the button did nothing. Now wired as () => scanLinks(). Catch any recurrence.
    const errors = [];
    page.on('pageerror', (e) => errors.push(String(e)));

    await page.evaluate(() => window.__hostGate.hold(['scan-urls'])); // park the next scan
    await page.click('#links-rescan');
    // Rescan genuinely issued a scan: a scan-urls round trip reached the host and is now parked.
    // (heldCount is the deterministic signal; the #link-status indicator flickers too fast to
    // catch on a one-link document.) With the button broken, no scan is issued and this stays 0.
    await expect.poll(() => page.evaluate(() => window.__hostGate.heldCount()),
      { timeout: 15000 }).toBeGreaterThan(0);
    expect(errors).toEqual([]);
    await page.evaluate(() => window.__hostGate.release());
    await expect(page.locator('#link-status')).toBeHidden({ timeout: 15000 });
    await page.close();
  });

  test('control: compare-close closes the compare panel', async () => {
    const current = fixture('cmpclose-a.pdf', [[{ text: 'Alpha', x: 72, y: 700 }]]);
    const older = fixture('cmpclose-b.pdf', [[{ text: 'Beta', x: 72, y: 700 }]]);
    const page = await openViewerWith(current);
    const chooser = page.waitForEvent('filechooser');
    await ui(page, '#btn-compare');
    await (await chooser).setFiles(older);
    await expect(page.locator('#panel-compare')).toBeVisible();
    await page.click('#compare-close');
    await expect(page.locator('#panel-compare')).toBeHidden();
    await page.close();
  });

  test('control: compare-pick compares against a freshly chosen file', async () => {
    const current = fixture('cmppick-cur.pdf', [[{ text: 'Amount 100', x: 72, y: 700 }]]);
    const first = fixture('cmppick-1.pdf', [[{ text: 'Amount 200', x: 72, y: 700 }]]);
    const second = fixture('cmppick-2.pdf', [[{ text: 'Amount 999', x: 72, y: 700 }]]);
    const page = await openViewerWith(current);

    const chooserA = page.waitForEvent('filechooser');
    await ui(page, '#btn-compare');
    await (await chooserA).setFiles(first);
    await expect(page.locator('#compare-list .w-del', { hasText: '200' })).toHaveCount(1);

    // Pick a different file from inside the panel: the comparison is redone against it.
    const chooserB = page.waitForEvent('filechooser');
    await page.click('#compare-pick');
    await (await chooserB).setFiles(second);
    await expect(page.locator('#compare-list .w-del', { hasText: '999' })).toHaveCount(1);
    await expect(page.locator('#compare-list .w-del', { hasText: '200' })).toHaveCount(0);
    await page.close();
  });

  test('control: console-copy copies the activity log to the clipboard', async () => {
    const page = await openViewerWith(fixture('consolecopy.pdf', [[{ text: 'doc', x: 72, y: 700 }]]));
    await openConsole(page);
    await page.click('#console-copy');
    // copyConsole() shows this only after clipboard.writeText resolves — the proof it ran.
    await expect(page.locator('#status')).toHaveText('Activity log copied to the clipboard.');
    await page.close();
  });

  test('control: tool-select leaves the active tool and re-arms text selection', async () => {
    const page = await openViewerWith(fixture('toolselect.pdf', [[{ text: 'pick me', x: 72, y: 700 }]]));
    await ui(page, '#tool-redact');
    await expect(page.locator('#tool-redact')).toHaveClass(/active/);
    await expect(page.locator('#pages')).not.toHaveClass(/select-mode/);

    await page.click('#tool-select');
    // The redact tool is no longer active, select is, and the page is back in select mode so the
    // text layer takes the pointer again.
    await expect(page.locator('#tool-redact')).not.toHaveClass(/active/);
    await expect(page.locator('#tool-select')).toHaveClass(/active/);
    await expect(page.locator('#pages')).toHaveClass(/select-mode/);
    await page.close();
  });


  test('text edit: editing highlighted (selected) text applies (#90)', async () => {
    // #90 reported that right-clicking highlighted text, choosing Edit and pressing Apply did
    // nothing. That is the selection path (selectionRegion), distinct from the run-under-cursor
    // path #85/#89 fixed. This holds the whole selection route end to end so it cannot regress.
    const file = fixture('sel-edit.pdf', [[{ text: 'Original Sentence Here', x: 72, y: 700, size: 18 }]]);
    const page = await openViewerWith(file);
    await page.locator('.page[data-page="1"] .text-layer span').first().waitFor({ timeout: 15000 });

    // Highlight the run, then right-click the selection and choose Edit text.
    await page.evaluate(() => {
      const span = document.querySelector('.page[data-page="1"] .text-layer span');
      window.getSelection().selectAllChildren(span);
    });
    await page.locator('.page[data-page="1"] .text-layer span').first().click({ button: 'right' });
    await page.locator('#context-menu').getByRole('button', { name: /Edit text/ }).click();
    await expect(page.locator('#panel-edit')).toBeVisible();
    await expect(page.locator('#edit-text')).toHaveValue(/Original Sentence Here/);

    await page.fill('#edit-text', 'Replaced Sentence');
    await page.click('#edit-apply');
    await expect(page.locator('#status')).toContainText('Text replaced');

    // The document really changed — Apply ran, it was not a no-op.
    await expectText(page).toContain('Replaced Sentence');
    await expectText(page).not.toContain('Original Sentence');
    await page.close();
  });


});
