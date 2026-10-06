'use strict';

/**
 * Text and object editing, end to end: replacing text in a region (with its font, size, style and
 * colour), find & replace, adding text, moving text and images, and undo/redo — each judged by the
 * saved file, read back with helpers/pdf-model.js rather than by what the page looks like.
 */

const { test, expect } = require('@playwright/test');
const { buildPdf, buildImagePdf } = require('../helpers/pdf');
const { appearsAnywhere } = require('../helpers/pdf-inspect');
const { pdfModel } = require('../helpers/pdf-model');
const {
  ui, dragPdfRect, dragPdf, clickPdf, fillDialog, expectText, extensionSuite,
} = require('../helpers/viewer');

const { openCapturingViewerWith, saveExport, writeFixture } = extensionSuite(test, 'pdf-editor-text-');

/** Selects `rect` with the Edit tool and waits for the panel to show the text found there. */
async function selectForEdit(page, rect, found) {
  await ui(page, '#tool-edit');
  await dragPdfRect(page, rect);
  await expect(page.locator('#panel-edit')).toBeVisible();
  await expect(page.locator('#edit-text')).toHaveValue(found);
}

/** Types `text` into the edit panel and applies it, waiting for the host to answer. */
async function applyEdit(page, text, status = 'Text replaced') {
  await page.fill('#edit-text', text);
  await page.click('#edit-apply');
  await expect(page.locator('#status')).toContainText(status);
}

/** Runs the Find & replace dialog and waits for its count. */
async function replaceAll(page, find, replacement, count) {
  await ui(page, '#btn-find');
  await fillDialog(page, [find, replacement], 'Replace all');
  await expect(page.locator('#status')).toContainText(`Replaced ${count} occurrence`);
}

/** The text runs of a page that contain `text` once whitespace is ignored. */
function runsWith(model, text, pageNum = 1) {
  const wanted = text.replace(/\s+/g, '');
  return model.textRuns(pageNum).filter((r) => r.text.replace(/\s+/g, '').includes(wanted));
}

const near = (value, expected, tolerance = 3) => Math.abs(value - expected) < tolerance;

test.describe('Text edits change what the saved file shows', () => {
  test('replacing the text in a region leaves the old words nowhere and the neighbours intact', async () => {
    const file = writeFixture('replace-region.pdf', buildPdf([[
      { text: 'Draft wording here', x: 72, y: 700 },
      { text: 'Customer copy', x: 72, y: 600 },
    ]]));
    const page = await openCapturingViewerWith(file);
    await selectForEdit(page, { x: 60, y: 690, width: 260, height: 34 }, /Draft wording here/);
    await applyEdit(page, 'Final wording here');
    const exported = await saveExport(page, 'replace-region.pdf');
    await page.close();

    const model = pdfModel(exported.bytes);
    expect(model.compactText(1)).toContain('Finalwordinghere');
    expect(model.compactText(1)).toContain('Customercopy');
    expect(appearsAnywhere(exported.bytes, 'Draft')).toBe(false);
    // The replacement is set where the original line was.
    const [run] = runsWith(model, 'Final');
    expect(near(run.x, 72)).toBe(true);
    expect(near(run.y, 700)).toBe(true);
  });

  test('a replacement in Times, 20 pt, bold is set in Times-Bold at 20 pt', async () => {
    const file = writeFixture('style.pdf', buildPdf([[{ text: 'Plain Heading', x: 72, y: 700 }]]));
    const page = await openCapturingViewerWith(file);
    await selectForEdit(page, { x: 60, y: 690, width: 260, height: 34 }, 'Plain Heading');
    await page.selectOption('#edit-font', 'times');
    await page.fill('#edit-size', '20');
    await page.click('#edit-bold');
    await applyEdit(page, 'Styled Heading');
    const exported = await saveExport(page, 'style.pdf');
    await page.close();

    const runs = runsWith(pdfModel(exported.bytes), 'StyledHeading');
    expect(runs).toHaveLength(1);
    expect(runs[0].baseFont).toBe('Times-Bold');
    expect(runs[0].size).toBe(20);
  });

  test('a replacement in Courier italic, in red, is set in Courier-Oblique with a red fill', async () => {
    const file = writeFixture('italic.pdf', buildPdf([[{ text: 'Plain words', x: 72, y: 700 }]]));
    const page = await openCapturingViewerWith(file);
    await selectForEdit(page, { x: 60, y: 690, width: 260, height: 34 }, 'Plain words');
    await page.selectOption('#edit-font', 'courier');
    await page.click('#edit-italic');
    await page.fill('#edit-color', '#ff0000');
    await applyEdit(page, 'Slanted words');
    const exported = await saveExport(page, 'italic.pdf');
    await page.close();

    const runs = runsWith(pdfModel(exported.bytes), 'Slantedwords');
    expect(runs).toHaveLength(1);
    expect(runs[0].baseFont).toBe('Courier-Oblique');
    expect(runs[0].fill).toEqual([1, 0, 0]);
  });

  test('a replacement much longer than the original is kept whole', async () => {
    const file = writeFixture('longer.pdf', buildPdf([[{ text: 'Short', x: 72, y: 700 }]]));
    const page = await openCapturingViewerWith(file);
    await selectForEdit(page, { x: 60, y: 690, width: 120, height: 34 }, 'Short');
    const longer = 'A considerably longer replacement sentence';
    await applyEdit(page, longer);
    const exported = await saveExport(page, 'longer.pdf');
    await page.close();

    expect(pdfModel(exported.bytes).compactText(1)).toContain(longer.replace(/\s+/g, ''));
  });

  test('a two-line replacement is set as two lines, one below the other', async () => {
    const file = writeFixture('two-lines.pdf', buildPdf([[{ text: 'One line only', x: 72, y: 700 }]]));
    const page = await openCapturingViewerWith(file);
    await selectForEdit(page, { x: 60, y: 660, width: 260, height: 64 }, 'One line only');
    await applyEdit(page, 'First new line\nSecond new line');
    const exported = await saveExport(page, 'two-lines.pdf');
    await page.close();

    const model = pdfModel(exported.bytes);
    const [first] = runsWith(model, 'First new line');
    const [second] = runsWith(model, 'Second new line');
    expect(first).toBeDefined();
    expect(second).toBeDefined();
    expect(second.y).toBeLessThan(first.y);
    expect(appearsAnywhere(exported.bytes, 'One line only')).toBe(false);
  });

  test('accented letters in a replacement are kept', async () => {
    const file = writeFixture('accents.pdf', buildPdf([[{ text: 'Cafe menu', x: 72, y: 700 }]]));
    const page = await openCapturingViewerWith(file);
    await selectForEdit(page, { x: 60, y: 690, width: 260, height: 34 }, 'Cafe menu');
    await applyEdit(page, 'Café menu');
    await expectText(page).toContain('Café');
    const exported = await saveExport(page, 'accents.pdf');
    await page.close();

    // Standard 14 fonts show WinAnsi bytes, where é is 0xE9 — the same byte as in Latin-1.
    expect(pdfModel(exported.bytes).compactText(1)).toContain('Café');
  });

  test('editing a caption drawn over a picture leaves the picture exactly as it was', async () => {
    const BLUE = [0, 102, 204];
    const original = buildImagePdf({
      rect: [72, 500, 400, 700], rgb: BLUE, text: [{ text: 'Old caption', x: 100, y: 600 }],
    });
    const page = await openCapturingViewerWith(writeFixture('caption.pdf', original));
    await selectForEdit(page, { x: 90, y: 590, width: 200, height: 30 }, /Old caption/);
    await applyEdit(page, 'New caption');
    const exported = await saveExport(page, 'caption.pdf');
    await page.close();

    const before = pdfModel(original);
    const after = pdfModel(exported.bytes);
    expect(after.compactText(1)).toContain('Newcaption');
    expect(appearsAnywhere(exported.bytes, 'Old caption')).toBe(false);
    const [image] = after.drawnImages(1);
    expect(after.streamData(image.ref)).toEqual(before.streamData(before.drawnImages(1)[0].ref));
    expect(after.imagePlacements(1).map((p) => p.ctm)).toEqual([[328, 0, 0, 200, 72, 500]]);
  });
});

test.describe('Find & replace', () => {
  test('replaces every occurrence on every page, in the file', async () => {
    const file = writeFixture('find-pages.pdf', buildPdf([
      [{ text: 'Contract with OldCorp', x: 72, y: 700 }, { text: 'OldCorp shall deliver', x: 72, y: 650 }],
      [{ text: 'Signed for OldCorp', x: 72, y: 700 }],
    ]));
    const page = await openCapturingViewerWith(file);
    await replaceAll(page, 'OldCorp', 'NewCorp', 3);
    const exported = await saveExport(page, 'find-pages.pdf');
    await page.close();

    const model = pdfModel(exported.bytes);
    expect(appearsAnywhere(exported.bytes, 'OldCorp')).toBe(false);
    // The words around each match stay where they were...
    expect(model.compactText(1)).toContain('Contractwith');
    expect(model.compactText(1)).toContain('shalldeliver');
    expect(model.compactText(2)).toContain('Signedfor');
    // ...and each replacement is set on its match's line, where the match started. (The engine
    // appends replacements to the end of the content stream, so stream order says nothing.)
    const placed = (pageNum) => runsWith(model, 'NewCorp', pageNum)
      .map((r) => ({ x: Math.round(r.x), y: Math.round(r.y) }))
      .toSorted((a, b) => b.y - a.y);
    const [first, second] = placed(1);
    expect(placed(1)).toHaveLength(2);
    expect(first.y).toBe(700);
    expect(first.x).toBeGreaterThan(140); // after "Contract with "
    expect(second).toEqual({ x: 72, y: 650 });
    const [signed] = placed(2);
    expect(placed(2)).toHaveLength(1);
    expect(signed.y).toBe(700);
    expect(signed.x).toBeGreaterThan(130); // after "Signed for "
  });

  test('a phrase that is not there changes nothing', async () => {
    const original = buildPdf([[{ text: 'Nothing to find here', x: 72, y: 700 }]]);
    const page = await openCapturingViewerWith(writeFixture('find-none.pdf', original));
    await ui(page, '#btn-find');
    await fillDialog(page, ['Absent phrase', 'Whatever'], 'Replace all');
    await expect(page.locator('#status')).toContainText('No matches');
    const exported = await saveExport(page, 'find-none.pdf');
    await page.close();

    const model = pdfModel(exported.bytes);
    expect(JSON.stringify(model.pageOperations(1))).toBe(JSON.stringify(pdfModel(original).pageOperations(1)));
  });

  test('undo restores the original in the saved file, and redo brings the change back', async () => {
    const file = writeFixture('undo.pdf', buildPdf([[{ text: 'Keep me', x: 72, y: 700 }]]));
    const page = await openCapturingViewerWith(file);
    await replaceAll(page, 'Keep me', 'Changed', 1);

    await page.click('#btn-undo');
    await expect(page.locator('#status')).toContainText('Undid last change');
    await expectText(page).toContain('Keep me');
    const undone = await saveExport(page, 'undone.pdf');
    expect(pdfModel(undone.bytes).compactText(1)).toBe('Keepme');
    expect(appearsAnywhere(undone.bytes, 'Changed')).toBe(false);

    await page.click('#btn-redo');
    await expect(page.locator('#status')).toContainText('Redid change');
    await expectText(page).toContain('Changed');
    const redone = await saveExport(page, 'redone.pdf');
    await page.close();
    expect(pdfModel(redone.bytes).compactText(1)).toBe('Changed');
    expect(appearsAnywhere(redone.bytes, 'Keep me')).toBe(false);
  });
});

test.describe('Adding and moving', () => {
  test('added text is in the file where it was placed, in Helvetica, with the page kept', async () => {
    const file = writeFixture('add.pdf', buildPdf([[{ text: 'background', x: 72, y: 100 }]]));
    const page = await openCapturingViewerWith(file);
    await ui(page, '#tool-text');
    await clickPdf(page, { x: 120, y: 700 });
    await expect(page.locator('#edit-title')).toHaveText('Add text');
    await applyEdit(page, 'STAMPED CAPTION', 'Text added');
    const exported = await saveExport(page, 'add.pdf');
    await page.close();

    const model = pdfModel(exported.bytes);
    expect(model.compactText(1)).toContain('background');
    const runs = runsWith(model, 'STAMPED CAPTION');
    expect(runs).toHaveLength(1);
    expect(runs[0].baseFont).toBe('Helvetica');
    // Placed by the click: the box opens at the click point, so the line starts near it.
    expect(near(runs[0].x, 120, 20)).toBe(true);
    expect(near(runs[0].y, 700, 40)).toBe(true);
  });

  test('moved text is in the file once, at its new position', async () => {
    const file = writeFixture('move-text.pdf', buildPdf([[
      { text: 'MOVE ME', x: 72, y: 700, size: 20 },
      { text: 'stays put', x: 72, y: 300 },
    ]]));
    const page = await openCapturingViewerWith(file);
    // The grab snaps to the run under the pointer, so the text layer has to be built first.
    await page.locator('.page[data-page="1"] .text-layer span').first().waitFor({ timeout: 15000 });
    await ui(page, '#tool-move');
    await dragPdf(page, { x: 90, y: 705 }, { x: 110, y: 555 });
    await expect(page.locator('#status')).toContainText('Text moved');
    const exported = await saveExport(page, 'move-text.pdf');
    await page.close();

    const model = pdfModel(exported.bytes);
    expect(model.compactText(1).split('MOVEME')).toHaveLength(2);
    const [run] = runsWith(model, 'MOVE ME');
    expect(near(run.x, 92)).toBe(true);
    expect(near(run.y, 550)).toBe(true);
    const [kept] = runsWith(model, 'stays put');
    expect([kept.x, kept.y]).toEqual([72, 300]);
  });

  test('a moved image is drawn once, at its new position and the same size', async () => {
    const original = buildImagePdf({ rect: [200, 500, 300, 600], text: [{ text: 'caption', x: 72, y: 300 }] });
    const page = await openCapturingViewerWith(writeFixture('move-image.pdf', original));
    await ui(page, '#tool-move');
    await dragPdf(page, { x: 250, y: 550 }, { x: 150, y: 400 });
    await expect(page.locator('#status')).toContainText('Image moved');
    const exported = await saveExport(page, 'move-image.pdf');
    await page.close();

    const model = pdfModel(exported.bytes);
    const placements = model.imagePlacements(1);
    expect(placements).toHaveLength(1);
    const [a, b, c, d, e, f] = placements[0].ctm;
    expect([a, b, c, d]).toEqual([100, 0, 0, 100]);
    expect(near(e, 100)).toBe(true);
    expect(near(f, 350)).toBe(true);
    expect(model.compactText(1)).toContain('caption');
  });
});
