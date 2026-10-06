'use strict';

/**
 * The features that lean on tools beside the host, end to end: making a scan searchable with
 * Tesseract, and opening or merging a Word document through LibreOffice. Both tools must be
 * installed where this runs (CI installs them; see CONTRIBUTING.md). Each test judges the saved
 * file: the words the OCR layer holds and where it put them, and the text a converted Word
 * document carries, decoded through the fonts' own ToUnicode maps by helpers/pdf-model.js.
 */

const { test, expect } = require('@playwright/test');
const { buildPdf } = require('../helpers/pdf');
const { SCAN_PNG } = require('../helpers/images');
const { WORD_DOCX } = require('../helpers/office');
const { pdfModel, plain } = require('../helpers/pdf-model');
const { ui, extensionSuite } = require('../helpers/viewer');

const { openCapturingViewerWith, saveExport, writeFixture } = extensionSuite(test, 'pdf-editor-import-');

const near = (value, expected, tolerance) => Math.abs(value - expected) < tolerance;

test.describe('OCR', () => {
  test('a scan made searchable gains an invisible layer of its words, over the unchanged picture', async () => {
    const page = await openCapturingViewerWith(writeFixture('invoice-scan.png', SCAN_PNG));
    const before = pdfModel((await saveExport(page, 'scan-before.pdf')).bytes);
    expect(before.textRuns(1)).toEqual([]);
    const [scan] = before.imagePlacements(1);

    await ui(page, '#btn-ocr');
    await expect(page.locator('#status')).toContainText('searchable', { timeout: 90000 });
    const exported = await saveExport(page, 'scan-searchable.pdf');
    await page.close();

    const model = pdfModel(exported.bytes);
    // The words are there, in reading order, and drawn invisibly (rendering mode 3) so the page
    // looks exactly as the scan did.
    expect(model.compactText(1)).toBe('INVOICE4471');
    const runs = model.textRuns(1);
    expect(runs.every((r) => r.render === 3)).toBe(true);
    // Each word sits on the scan's line of text: inside the picture, in its upper half, where the
    // words were drawn (baseline 190 of 300 pixels down).
    const [, , , height, , bottom] = scan.ctm;
    for (const run of runs) {
      expect(run.y).toBeGreaterThan(bottom + height * 0.2);
      expect(run.y).toBeLessThan(bottom + height * 0.6);
    }
    // The page is the same size, and still shows one picture covering it.
    const [x0, y0, x1, y1] = plain(model.inherited(1, 'MediaBox'));
    expect([x0, y0]).toEqual([0, 0]);
    expect(near(x1, 842, 0.5) && near(y1, 595, 0.5)).toBe(true);
    expect(model.drawnImages(1)).toHaveLength(1);
  });
});

test.describe('Word documents', () => {
  test('a .docx opens as a document carrying its paragraphs as text, in order', async () => {
    const page = await openCapturingViewerWith(writeFixture('report.docx', WORD_DOCX));
    await expect(page.locator('#page-total')).toHaveText('1');
    const exported = await saveExport(page, 'report-from-word.pdf');
    await page.close();

    const model = pdfModel(exported.bytes);
    const [first, second] = model.textRuns(1);
    expect(first.text).toBe('Quarterly Word Report');
    expect(second.text).toBe('Second paragraph here');
    expect(second.y).toBeLessThan(first.y);
  });

  test('a .docx merged into a PDF becomes the pages after it', async () => {
    const page = await openCapturingViewerWith(writeFixture('cover.pdf', buildPdf([[{ text: 'Cover page', x: 72, y: 700 }]])));
    const chooser = page.waitForEvent('filechooser');
    await ui(page, '#btn-merge');
    await (await chooser).setFiles(writeFixture('appendix.docx', WORD_DOCX));
    await page.locator('dialog#modal').getByRole('button', { name: 'Merge' }).click();
    await expect(page.locator('#status')).toContainText('Merged 1 file', { timeout: 60000 });
    const exported = await saveExport(page, 'cover-and-word.pdf');
    await page.close();

    const model = pdfModel(exported.bytes);
    expect(model.pageCount()).toBe(2);
    expect(model.compactText(1)).toBe('Coverpage');
    expect(model.compactText(2)).toBe('QuarterlyWordReportSecondparagraphhere');
  });
});
