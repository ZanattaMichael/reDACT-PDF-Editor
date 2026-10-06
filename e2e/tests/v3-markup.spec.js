'use strict';

/**
 * Markup, end to end: highlights, freehand drawing, placed signatures, watermarks, Bates numbers
 * and flattening — each judged by what the saved file draws, read back with helpers/pdf-model.js:
 * the colour, blend, width and place of what was added, and that nothing else was disturbed.
 */

const { test, expect } = require('@playwright/test');
const { buildPdf, buildFlattenPdf } = require('../helpers/pdf');
const { JPEG } = require('../helpers/images');
const { pdfModel, plain } = require('../helpers/pdf-model');
const { ui, dragPdfRect, dragPdf, extensionSuite } = require('../helpers/viewer');

const { openCapturingViewerWith, saveExport, writeFixture } = extensionSuite(test, 'pdf-editor-markup-');

const near = (value, expected, tolerance = 1.5) => Math.abs(value - expected) < tolerance;
const nearAll = (values, expected, tolerance) => values.every((v, i) => near(v, expected[i], tolerance));

/** #rrggbb as the 0–1 components a PDF colour operator carries. */
const components = (hex) => [1, 3, 5].map((i) => Number.parseInt(hex.slice(i, i + 2), 16) / 255);

/** Sets a native colour input, which fill() cannot drive. */
async function setColour(locator, hex) {
  await locator.evaluate((el, value) => {
    el.value = value;
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
  }, hex);
}

/** The rectangles (`re` operands) of the filled paths painted under a Multiply blend. */
function highlightRects(model, pageNum = 1) {
  return model.paintedPaths(pageNum)
    .filter((p) => p.paint === 'f' && plain(model.get(p.extGState, 'BM')) === 'Multiply')
    .map((p) => ({ fill: p.fill, rects: p.segments.filter((s) => s.op === 're').map((s) => s.operands) }));
}

/** Opens a document and waits for its text layer, which word-snapping highlights depend on. */
async function openWithTextLayer(file) {
  const page = await openCapturingViewerWith(file);
  await page.locator('.page[data-page="1"] .text-layer span').first().waitFor({ timeout: 15000 });
  return page;
}

/** Fills the dialog a toolbar button opens: inputs by position, the select by value. */
async function fillToolDialog(page, button, inputs, select) {
  await ui(page, button);
  const dialog = page.locator('dialog#modal');
  await expect(dialog).toBeVisible();
  for (const [index, value] of Object.entries(inputs)) {
    const input = dialog.locator('input').nth(Number(index));
    if ((await input.getAttribute('type')) === 'color') await setColour(input, value);
    else await input.fill(value);
  }
  if (select) await dialog.locator('select').selectOption(select);
  return dialog;
}

test.describe('Highlights', () => {
  test('a sweep across a line marks the words under a multiply blend, and leaves them readable', async () => {
    const file = writeFixture('highlight.pdf', buildPdf([[
      { text: 'HIGHLIGHT THIS LINE', x: 72, y: 700 },
      { text: 'not this one', x: 72, y: 600 },
    ]]));
    const page = await openWithTextLayer(file);
    await ui(page, '#tool-highlight');
    await dragPdf(page, { x: 60, y: 705 }, { x: 300, y: 705 });
    await expect(page.locator('#status')).toContainText('Highlighted');
    const exported = await saveExport(page, 'highlight.pdf');
    await page.close();

    const model = pdfModel(exported.bytes);
    const highlights = highlightRects(model);
    expect(highlights).toHaveLength(1);
    expect(nearAll(highlights[0].fill, components('#ffeb3b'), 0.01)).toBe(true);
    // Snapped to the run: it starts at the line's left edge and spans its height, not the drag.
    const [[x, y, width, height]] = highlights[0].rects;
    expect(near(x, 72)).toBe(true);
    expect(y).toBeGreaterThan(694);
    expect(y + height).toBeLessThan(716);
    expect(width).toBeGreaterThan(100);
    // Nothing near the other line, and the text is all still there.
    expect(y).toBeGreaterThan(620);
    expect(model.compactText(1)).toContain('HIGHLIGHTTHISLINE');
    expect(model.compactText(1)).toContain('notthisone');
  });

  test('a box-mode highlight in a chosen colour marks exactly the rectangle drawn', async () => {
    const file = writeFixture('highlight-box.pdf', buildPdf([[{ text: 'AAAAA BBBBB CCCCC', x: 72, y: 700 }]]));
    const page = await openWithTextLayer(file);
    await ui(page, '#tool-highlight');
    await page.locator('input[name="highlight-mode"][value="box"]').check();
    await setColour(page.locator('#highlight-color'), '#00ffff');
    await dragPdfRect(page, { x: 66, y: 694, width: 160, height: 22 });
    await expect(page.locator('#status')).toContainText('Highlighted');
    const exported = await saveExport(page, 'highlight-box.pdf');
    await page.close();

    const [highlight] = highlightRects(pdfModel(exported.bytes));
    expect(highlight.fill).toEqual([0, 1, 1]);
    expect(highlight.rects).toHaveLength(1);
    expect(nearAll(highlight.rects[0], [66, 694, 160, 22], 2)).toBe(true);
  });
});

test.describe('Drawing and placed signatures', () => {
  test('a freehand stroke is saved as a round-capped path in the chosen colour and width', async () => {
    const file = writeFixture('draw.pdf', buildPdf([[{ text: 'canvas', x: 72, y: 100 }]]));
    const page = await openCapturingViewerWith(file);
    await ui(page, '#tool-draw');
    await setColour(page.locator('#draw-color'), '#00ff00');
    await page.fill('#draw-width', '8');
    await dragPdf(page, { x: 150, y: 421 }, { x: 400, y: 421 });
    await page.click('#draw-apply');
    await expect(page.locator('#status')).toContainText('stroke');
    const exported = await saveExport(page, 'draw.pdf');
    await page.close();

    const model = pdfModel(exported.bytes);
    const strokes = model.paintedPaths(1).filter((p) => p.paint === 'S');
    expect(strokes).toHaveLength(1);
    const [stroke] = strokes;
    expect(stroke.stroke).toEqual([0, 1, 0]);
    expect(stroke.lineWidth).toBe(8);
    expect([stroke.cap, stroke.join]).toEqual([1, 1]);
    // It runs from where the pen went down to where it came up, through the points between.
    const points = stroke.segments.map((s) => s.operands);
    expect(stroke.segments[0].op).toBe('m');
    expect(stroke.segments.slice(1).every((s) => s.op === 'l')).toBe(true);
    expect(points.length).toBeGreaterThan(2);
    expect(nearAll(points[0], [150, 421], 3)).toBe(true);
    expect(nearAll(points.at(-1), [400, 421], 3)).toBe(true);
    expect(model.compactText(1)).toContain('canvas');
  });

  test('a signature drawn on the pad is placed as an image filling the chosen box', async () => {
    const file = writeFixture('sign-drawn.pdf', buildPdf([[{ text: 'Sign here:', x: 72, y: 700 }]]));
    const page = await openCapturingViewerWith(file);
    await ui(page, '#tool-sign');
    await dragPdfRect(page, { x: 200, y: 640, width: 160, height: 50 });
    await expect(page.locator('#panel-sign')).toBeVisible();
    const pad = await page.locator('#sign-pad').boundingBox();
    await page.mouse.move(pad.x + 12, pad.y + pad.height - 20);
    await page.mouse.down();
    await page.mouse.move(pad.x + pad.width / 2, pad.y + 14, { steps: 8 });
    await page.mouse.move(pad.x + pad.width - 12, pad.y + pad.height - 20, { steps: 8 });
    await page.mouse.up();
    await page.click('#sign-apply');
    await expect(page.locator('#status')).toContainText('Signature placed');
    const exported = await saveExport(page, 'sign-drawn.pdf');
    await page.close();

    const model = pdfModel(exported.bytes);
    const placements = model.imagePlacements(1);
    expect(placements).toHaveLength(1);
    expect(nearAll(placements[0].ctm, [160, 0, 0, 50, 200, 640], 2)).toBe(true);
    // The pad is transparent around the ink, so the paper shows through it.
    const [image] = model.drawnImages(1);
    expect(model.get(image.dict, 'SMask')).toBeDefined();
    expect(model.compactText(1)).toContain('Signhere:');
  });

  test('an uploaded JPEG signature is placed with its bytes unchanged', async () => {
    const file = writeFixture('sign-upload.pdf', buildPdf([[{ text: 'Approved by:', x: 72, y: 700 }]]));
    const jpeg = writeFixture('signature.jpg', JPEG);
    const page = await openCapturingViewerWith(file);
    await ui(page, '#tool-sign');
    await dragPdfRect(page, { x: 220, y: 680, width: 120, height: 40 });
    await page.click('#sign-tab-upload');
    await page.setInputFiles('#sign-file', jpeg);
    await page.click('#sign-apply');
    await expect(page.locator('#status')).toContainText('Signature placed');
    const exported = await saveExport(page, 'sign-upload.pdf');
    await page.close();

    const model = pdfModel(exported.bytes);
    const [image] = model.drawnImages(1);
    expect(plain(model.get(image.dict, 'Filter'))).toBe('DCTDecode');
    expect(model.streamData(image.ref).equals(JPEG)).toBe(true);
    expect(nearAll(model.imagePlacements(1)[0].ctm, [120, 0, 0, 40, 220, 680], 2)).toBe(true);
  });
});

test.describe('Stamps across the document', () => {
  test('a watermark is drawn on every page in the chosen colour, opacity and angle', async () => {
    const file = writeFixture('watermark.pdf', buildPdf([1, 2, 3].map((n) => [{ text: `body ${n}`, x: 72, y: 80 }])));
    const page = await openCapturingViewerWith(file);
    await fillToolDialog(page, '#btn-watermark', { 0: 'CONFIDENTIAL', 1: '#ff0000', 2: '40', 3: '30' });
    await page.locator('dialog#modal').getByRole('button', { name: 'Apply' }).click();
    await expect(page.locator('#status')).toContainText('Watermark added');
    const exported = await saveExport(page, 'watermark.pdf');
    await page.close();

    const model = pdfModel(exported.bytes);
    const angle = (30 * Math.PI) / 180;
    for (const pageNum of [1, 2, 3]) {
      const marks = model.textRuns(pageNum).filter((r) => r.text === 'CONFIDENTIAL');
      expect(marks).toHaveLength(1);
      const [mark] = marks;
      expect(mark.fill).toEqual([1, 0, 0]);
      expect(plain(model.get(mark.extGState, 'ca'))).toBeCloseTo(0.4, 5);
      expect(nearAll(mark.matrix.slice(0, 4), [Math.cos(angle), Math.sin(angle), -Math.sin(angle), Math.cos(angle)], 0.001))
        .toBe(true);
      expect(model.compactText(pageNum)).toContain(`body${pageNum}`);
    }
  });

  test('Bates numbers run on from the start number, padded, with prefix and suffix, top left', async () => {
    const file = writeFixture('bates.pdf', buildPdf([1, 2, 3].map((n) => [{ text: `page ${n}`, x: 72, y: 400 }])));
    const page = await openCapturingViewerWith(file);
    await fillToolDialog(page, '#btn-bates', { 0: 'ACME', 1: '7', 2: '4', 3: '-X' }, 'top-left');
    await page.locator('dialog#modal').getByRole('button', { name: 'Apply' }).click();
    await expect(page.locator('#status')).toContainText('ACME0007-X – ACME0009-X');
    const exported = await saveExport(page, 'bates.pdf');
    await page.close();

    const model = pdfModel(exported.bytes);
    for (const [index, label] of ['ACME0007-X', 'ACME0008-X', 'ACME0009-X'].entries()) {
      const runs = model.textRuns(index + 1).filter((r) => r.text === label);
      expect(runs).toHaveLength(1);
      // A 24 pt margin in from the top-left corner.
      expect(near(runs[0].x, 24)).toBe(true);
      expect(runs[0].y).toBeGreaterThan(790);
      expect(runs[0].y).toBeLessThan(818);
    }
  });

  test('Bates numbers by default sit bottom right, right-aligned to the margin', async () => {
    const file = writeFixture('bates-default.pdf', buildPdf([[{ text: 'only page', x: 72, y: 400 }]]));
    const page = await openCapturingViewerWith(file);
    await fillToolDialog(page, '#btn-bates', { 0: 'DOC' });
    await page.locator('dialog#modal').getByRole('button', { name: 'Apply' }).click();
    await expect(page.locator('#status')).toContainText('DOC000001');
    const exported = await saveExport(page, 'bates-default.pdf');
    await page.close();

    const [label] = pdfModel(exported.bytes).textRuns(1).filter((r) => r.text === 'DOC000001');
    expect(label).toBeDefined();
    expect(near(label.y, 24)).toBe(true);
    expect(label.x).toBeGreaterThan(480);
    expect(label.x).toBeLessThan(571);
  });
});

test.describe('Flattening', () => {
  /** Flattens with the given mode and saves. */
  async function flatten(name, mode, status) {
    const file = writeFixture(`${name}.pdf`, buildFlattenPdf('Ada Lovelace'));
    const page = await openCapturingViewerWith(file);
    await fillToolDialog(page, '#btn-flatten', {}, mode);
    await page.locator('dialog#modal').getByRole('button', { name: 'Flatten' }).click();
    await expect(page.locator('#status')).toContainText(status);
    const exported = await saveExport(page, `${name}-flat.pdf`);
    await page.close();
    return pdfModel(exported.bytes);
  }

  /** Whether the page content (forms included) strokes in red, as the square's appearance does. */
  const drawsRedStroke = (model) => model.pageOperations(1)
    .some((o) => o.op === 'RG' && o.operands.join(' ') === '1 0 0');

  test('everything: no field or annotation is left, and both are drawn into the page', async () => {
    const model = await flatten('flatten-all', 'everything', 'Flattened 1 form field and 1 annotation');
    expect(model.fields()).toEqual([]);
    expect(model.annotations(1)).toEqual([]);
    expect(model.compactText(1)).toContain('AdaLovelace');
    expect(drawsRedStroke(model)).toBe(true);
  });

  test('forms only: the field becomes page content and the comment stays a comment', async () => {
    const model = await flatten('flatten-forms', 'forms', 'Flattened 1 form field');
    expect(model.fields()).toEqual([]);
    expect(model.compactText(1)).toContain('AdaLovelace');
    expect(model.annotations(1).map((a) => a.subtype)).toEqual(['Square']);
    expect(drawsRedStroke(model)).toBe(false);
  });

  test('annotations only: the comment becomes page content and the field stays fillable', async () => {
    const model = await flatten('flatten-annots', 'annotations', 'Flattened 1 annotation');
    expect(model.fields().map((f) => [f.name, f.value])).toEqual([['name', 'Ada Lovelace']]);
    expect(model.annotations(1).map((a) => a.subtype)).toEqual(['Widget']);
    expect(drawsRedStroke(model)).toBe(true);
  });
});
