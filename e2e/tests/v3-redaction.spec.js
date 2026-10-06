'use strict';

/**
 * Redaction, end to end: every way a user removes content in the viewer, judged by what the saved
 * file holds rather than by what the page looks like. Each test drives the real extension against
 * the real host, saves, and reads the export with helpers/pdf-model.js, which shares no code with
 * the engine.
 */

const { test, expect } = require('@playwright/test');
const crypto = require('node:crypto');
const fs = require('node:fs');
const { buildPdf, buildImagePdf, buildMultiLinkPdf, buildInlineImagePdf } = require('../helpers/pdf');
const { appearsAnywhere } = require('../helpers/pdf-inspect');
const { pdfModel, plain } = require('../helpers/pdf-model');
const {
  ui, dragPdfRect, expectText, applyRedaction, searchAndRedact, extensionSuite,
} = require('../helpers/viewer');

const { openCapturingViewerWith, saveExport, writeFixture, writeCapturedExport } =
  extensionSuite(test, 'pdf-editor-redaction-');

const sha256 = (bytes) => crypto.createHash('sha256').update(bytes).digest('hex');

/** Draws one redaction box with the Redact tool and checks it was marked. */
async function markBox(page, rect) {
  await ui(page, '#tool-redact');
  await dragPdfRect(page, rect);
  await expect(page.locator('#redact-list li')).toHaveCount(1);
}

/** The operators drawn after the first opaque black fill colour: the redaction box, if any. */
function blackBoxOperations(model, pageNum = 1) {
  const ops = model.pageOperations(pageNum);
  const black = ops.findIndex((o) => o.op === 'rg' && o.operands.every((v) => v === 0));
  return black < 0 ? [] : ops.slice(black);
}

/** The RGB value of every pixel of an 8-bit RGB image, as { col, row, rgb } with its width. */
function rgbPixels(model, image) {
  const width = plain(model.get(image.dict, 'Width'));
  const height = plain(model.get(image.dict, 'Height'));
  const data = model.streamData(image.ref);
  const pixels = [];
  for (let row = 0; row < height; row++) {
    for (let col = 0; col < width; col++) {
      const i = (row * width + col) * 3;
      pixels.push({ col, row, rgb: [data[i], data[i + 1], data[i + 2]] });
    }
  }
  return { width, pixels };
}

test.describe('Redaction leaves only what it should in the saved file', () => {
  test('a drawn box removes the text under it from the file and paints an opaque box there', async () => {
    const file = writeFixture('drawn.pdf', buildPdf([[
      { text: 'TOP SECRET DATA', x: 72, y: 700 },
      { text: 'public information', x: 72, y: 600 },
    ]]));
    const page = await openCapturingViewerWith(file);
    await markBox(page, { x: 60, y: 690, width: 260, height: 34 });
    await applyRedaction(page);
    const exported = await saveExport(page, 'drawn-redacted.pdf');
    await page.close();

    expect(appearsAnywhere(exported.bytes, 'TOP SECRET')).toBe(false);
    const model = pdfModel(exported.bytes);
    expect(model.compactText(1)).toContain('publicinformation');

    // The box is a black rectangle over the marked area, filled.
    const box = blackBoxOperations(model);
    const rect = box.find((o) => o.op === 're');
    expect(rect).toBeDefined();
    const [x, y, width, height] = rect.operands;
    expect(Math.abs(x - 60)).toBeLessThan(2);
    expect(Math.abs(y - 690)).toBeLessThan(2);
    expect(Math.abs(width - 260)).toBeLessThan(2);
    expect(Math.abs(height - 34)).toBeLessThan(2);
    expect(box.some((o) => o.op === 'f')).toBe(true);
  });

  test('search marks every occurrence on every page, and none of them survive in the file', async () => {
    const file = writeFixture('search-pages.pdf', buildPdf([
      [{ text: 'Client ACME CORP signed', x: 72, y: 700 }, { text: 'ordinary words', x: 72, y: 600 }],
      [{ text: 'ACME CORP again here', x: 72, y: 700 }, { text: 'more ordinary text', x: 72, y: 600 }],
    ]));
    const page = await openCapturingViewerWith(file);
    await searchAndRedact(page, 'ACME CORP', 2);
    const exported = await saveExport(page, 'search-pages-redacted.pdf');
    await page.close();

    expect(appearsAnywhere(exported.bytes, 'ACME')).toBe(false);
    const model = pdfModel(exported.bytes);
    expect(model.compactText(1)).toContain('Client');
    expect(model.compactText(1)).toContain('signed');
    expect(model.compactText(1)).toContain('ordinarywords');
    expect(model.compactText(2)).toContain('againhere');
    expect(model.compactText(2)).toContain('moreordinarytext');
  });

  test('redacting one page leaves every other page exactly as it was', async () => {
    const original = buildPdf([
      [{ text: 'Page one stays as it is', x: 72, y: 700 }],
      [{ text: 'PAGE TWO SECRET', x: 72, y: 700 }, { text: 'page two keeps this', x: 72, y: 600 }],
      [{ text: 'Page three stays too', x: 72, y: 700 }],
    ]);
    const page = await openCapturingViewerWith(writeFixture('one-page.pdf', original));
    await searchAndRedact(page, 'PAGE TWO SECRET', 1);
    const exported = await saveExport(page, 'one-page-redacted.pdf');
    await page.close();

    const before = pdfModel(original);
    const after = pdfModel(exported.bytes);
    expect(after.pageCount()).toBe(3);
    for (const pageNum of [1, 3]) {
      expect(JSON.stringify(after.pageOperations(pageNum))).toBe(JSON.stringify(before.pageOperations(pageNum)));
    }
    expect(after.compactText(2)).toContain('pagetwokeepsthis');
    expect(appearsAnywhere(exported.bytes, 'TWO SECRET')).toBe(false);
  });

  test('a link inside the region is removed with its target; a link outside it stays', async () => {
    const file = writeFixture('links.pdf', buildMultiLinkPdf(['https://one.example/', 'https://two.example/']));
    const page = await openCapturingViewerWith(file);
    // The first link spans y 760–780 and the second 730–750; the box covers only the first.
    await markBox(page, { x: 60, y: 755, width: 330, height: 30 });
    await applyRedaction(page);
    const exported = await saveExport(page, 'links-redacted.pdf');
    await page.close();

    const model = pdfModel(exported.bytes);
    const links = model.annotations(1).filter((a) => a.subtype === 'Link');
    expect(links).toHaveLength(1);
    expect(plain(model.get(links[0].dict, 'Rect'))).toEqual([72, 730, 372, 750]);
    expect(appearsAnywhere(exported.bytes, 'one.example')).toBe(false);
  });

  test('a partly covered image keeps its uncovered pixels and loses the covered ones', async () => {
    const BLUE = [0, 102, 204];
    const file = writeFixture('partial-image.pdf', buildImagePdf({ rect: [72, 500, 400, 700], rgb: BLUE }));
    const page = await openCapturingViewerWith(file);
    // The image spans x 72–400; the box covers 60–220, under half of it.
    await markBox(page, { x: 60, y: 490, width: 160, height: 220 });
    await applyRedaction(page);
    const exported = await saveExport(page, 'partial-image-redacted.pdf');
    await page.close();

    const model = pdfModel(exported.bytes);
    const images = model.drawnImages(1);
    expect(images).toHaveLength(1);
    const { width, pixels } = rgbPixels(model, images[0]);
    const across = (p) => (p.col + 0.5) / width;
    const covered = pixels.filter((p) => across(p) < 0.4);
    const clear = pixels.filter((p) => across(p) > 0.5);
    expect(covered.length).toBeGreaterThan(0);
    expect(clear.length).toBeGreaterThan(0);
    for (const p of covered) expect(p.rgb).toEqual([0, 0, 0]);
    for (const p of clear) expect(p.rgb).toEqual(BLUE);
  });

  test('an inline image touching the region is dropped from the content stream', async () => {
    const file = writeFixture('inline.pdf', buildInlineImagePdf({
      rect: [72, 500, 172, 600],
      lines: [{ text: 'kept caption', x: 72, y: 400 }],
    }));
    expect(pdfModel(fs.readFileSync(file)).pageOperations(1).some((o) => o.op === 'BI')).toBe(true);
    const page = await openCapturingViewerWith(file);
    await markBox(page, { x: 60, y: 490, width: 130, height: 120 });
    await applyRedaction(page);
    const exported = await saveExport(page, 'inline-redacted.pdf');
    await page.close();

    const model = pdfModel(exported.bytes);
    expect(model.pageOperations(1).some((o) => o.op === 'BI')).toBe(false);
    expect(model.compactText(1)).toContain('keptcaption');
  });

  test('closing the preview window changes nothing in the document', async () => {
    const file = writeFixture('preview.pdf', buildPdf([[{ text: 'STILL HERE', x: 72, y: 700 }]]));
    const page = await openCapturingViewerWith(file);
    await markBox(page, { x: 60, y: 690, width: 200, height: 30 });
    await page.click('#redact-preview');
    const dialog = page.locator('dialog#modal');
    await expect(dialog.locator('.preview-pages img')).toHaveCount(1);
    await dialog.getByRole('button', { name: 'Close preview' }).click();
    await expect(dialog).toBeHidden();
    const exported = await saveExport(page, 'preview-closed.pdf');
    await page.close();

    const model = pdfModel(exported.bytes);
    expect(model.compactText(1)).toContain('STILLHERE');
    expect(blackBoxOperations(model)).toEqual([]);
  });

  test('clearing the marks before applying leaves the text in the file', async () => {
    const file = writeFixture('cleared.pdf', buildPdf([[{ text: 'NOT REDACTED', x: 72, y: 700 }]]));
    const page = await openCapturingViewerWith(file);
    await markBox(page, { x: 60, y: 690, width: 200, height: 30 });
    await page.click('#redact-clear');
    await expect(page.locator('#redact-list li')).toHaveCount(0);
    const exported = await saveExport(page, 'cleared.pdf');
    await page.close();

    expect(pdfModel(exported.bytes).compactText(1)).toContain('NOTREDACTED');
  });

  test('redaction on a page turned with /Rotate removes the text and keeps the turn', async () => {
    const file = writeFixture('rotated.pdf', buildPdf(
      [[{ text: 'ROTATED SECRET', x: 72, y: 700 }, { text: 'rotated keeper', x: 72, y: 600 }]],
      { rotate: 90 }));
    const page = await openCapturingViewerWith(file);
    await searchAndRedact(page, 'ROTATED SECRET', 1);
    const exported = await saveExport(page, 'rotated-redacted.pdf');
    await page.close();

    const model = pdfModel(exported.bytes);
    expect(model.inherited(1, 'Rotate')).toBe(90);
    expect(model.compactText(1)).toContain('rotatedkeeper');
    expect(appearsAnywhere(exported.bytes, 'ROTATED SECRET')).toBe(false);
  });

  test('the compliance report lists what was removed and the hashes of both files', async () => {
    const original = buildPdf([[{ text: 'ACCOUNT 4111-1111', x: 72, y: 700 }, { text: 'remains', x: 72, y: 600 }]]);
    const page = await openCapturingViewerWith(writeFixture('report.pdf', original));
    await ui(page, '#tool-redact');
    await page.fill('#redact-search-text', 'ACCOUNT 4111-1111');
    await page.click('#redact-search-btn');
    await expect(page.locator('#redact-list li')).toHaveCount(1);
    await page.click('#redact-apply');
    const dialog = page.locator('dialog#modal');
    await expect(dialog).toContainText('Redaction report');
    await dialog.getByRole('button', { name: /Download compliance report/ }).click();
    const report = await writeCapturedExport(page, 'report.html');
    await dialog.getByRole('button', { name: 'Close' }).click();
    await expectText(page).toContain('remains');
    const exported = await saveExport(page, 'report-redacted.pdf');
    await page.close();

    expect(report.name).toBe('report-redaction-report.html');
    const html = report.bytes.toString('utf8');
    expect(html).toContain('Redaction Compliance Report');
    expect(html).toContain('ACCOUNT 4111-1111');
    expect(html).toContain(sha256(original));
    expect(html).toContain(sha256(exported.bytes));
    expect(appearsAnywhere(exported.bytes, '4111')).toBe(false);
  });
});
