'use strict';

/**
 * Pages and documents, end to end: rotating, removing and reordering pages, merging documents and
 * images, and opening every image format the viewer accepts — each judged by the saved file's page
 * tree and what each page draws, read back with helpers/pdf-model.js.
 */

const { test, expect } = require('@playwright/test');
const { buildPdf } = require('../helpers/pdf');
const { appearsAnywhere } = require('../helpers/pdf-inspect');
const { pdfModel, plain } = require('../helpers/pdf-model');
const images = require('../helpers/images');
const { ui, extensionSuite } = require('../helpers/viewer');

const { openCapturingViewerWith, saveExport, writeFixture } = extensionSuite(test, 'pdf-editor-pages-');

/** A document whose pages each show one word, so each page can be told apart in the file. */
const wordPages = (...words) => buildPdf(words.map((text) => [{ text, x: 72, y: 700 }]));

/** What each page of a saved file shows, whitespace removed, in page order. */
function pageWords(model) {
  return model.pages().map((_, i) => model.compactText(i + 1));
}

/** The page's effective rotation, 0 when it has none. */
const rotation = (model, pageNum) => model.inherited(pageNum, 'Rotate') ?? 0;

/** Turns the page shown in the counter, after jumping to `pageNum`. */
async function rotatePage(page, pageNum, button) {
  await page.fill('#page-input', String(pageNum));
  await page.press('#page-input', 'Enter');
  await expect(page.locator('#page-input')).toHaveValue(String(pageNum));
  await ui(page, button);
  await expect(page.locator('#status')).toContainText(`Rotated page ${pageNum}`);
}

/** Opens the Organize panel, runs `arrange` on its rows, and applies. */
async function organize(page, arrange) {
  await ui(page, '#btn-organize');
  const rows = page.locator('#organize-list .organize-item');
  await arrange(rows);
  await page.click('#organize-apply');
  await expect(page.locator('#status')).toContainText('reorganized');
}

/** Picks `files` with Merge, runs `arrange` on the dialog's rows, and merges. */
async function merge(page, files, arrange = async () => {}) {
  const chooser = page.waitForEvent('filechooser');
  await ui(page, '#btn-merge');
  await (await chooser).setFiles(files);
  const dialog = page.locator('dialog#modal');
  const rows = dialog.locator('.organize-item');
  await expect(rows).toHaveCount(files.length + 1);
  await arrange(rows);
  await dialog.getByRole('button', { name: 'Merge' }).click();
}

/** The pixels of an image a page draws, as the decoded stream and its dimensions. */
function drawnImage(model, pageNum = 1) {
  const drawn = model.drawnImages(pageNum);
  expect(drawn).toHaveLength(1);
  const [image] = drawn;
  return {
    filter: plain(model.get(image.dict, 'Filter')),
    width: plain(model.get(image.dict, 'Width')),
    height: plain(model.get(image.dict, 'Height')),
    data: model.streamData(image.ref),
    softMask: model.get(image.dict, 'SMask') ? model.streamData(image.dict.raw('SMask')) : null,
    ctm: model.imagePlacements(pageNum)[0]?.ctm,
  };
}

/** Whether every pixel of 8-bit RGB `data` is within `tolerance` of `rgb`. */
function allPixels(data, rgb, tolerance = 0) {
  for (let i = 0; i < data.length; i += 3) {
    if (rgb.some((v, c) => Math.abs(data[i + c] - v) > tolerance)) return false;
  }
  return data.length > 0;
}

test.describe('Rotating pages', () => {
  test('rotate right sets /Rotate 90, rotate left twice from there 270, and the content is untouched', async () => {
    const original = wordPages('Portrait');
    const page = await openCapturingViewerWith(writeFixture('rotate.pdf', original));
    await rotatePage(page, 1, '#btn-rotate-right');
    const right = await saveExport(page, 'rotate-right.pdf');
    await rotatePage(page, 1, '#btn-rotate-left');
    await rotatePage(page, 1, '#btn-rotate-left');
    const left = await saveExport(page, 'rotate-left.pdf');
    await page.close();

    const before = JSON.stringify(pdfModel(original).pageOperations(1));
    for (const [exported, turn] of [[right, 90], [left, 270]]) {
      const model = pdfModel(exported.bytes);
      expect(rotation(model, 1)).toBe(turn);
      expect(plain(model.inherited(1, 'MediaBox'))).toEqual([0, 0, 595, 842]);
      expect(JSON.stringify(model.pageOperations(1))).toBe(before);
    }
  });

  test('rotating page 2 turns page 2 only', async () => {
    const page = await openCapturingViewerWith(writeFixture('rotate-two.pdf', wordPages('one', 'two', 'three')));
    await rotatePage(page, 2, '#btn-rotate-right');
    const exported = await saveExport(page, 'rotate-two.pdf');
    await page.close();

    const model = pdfModel(exported.bytes);
    expect([1, 2, 3].map((n) => rotation(model, n))).toEqual([0, 90, 0]);
    expect(pageWords(model)).toEqual(['one', 'two', 'three']);
  });
});

test.describe('Organizing pages', () => {
  test('a removed page is gone from the file, not just from the page tree', async () => {
    const page = await openCapturingViewerWith(writeFixture('remove.pdf', wordPages('Keepone', 'DeleteTWO', 'Keepthree')));
    await organize(page, (rows) => rows.nth(1).getByRole('button', { name: 'Remove page' }).click());
    const exported = await saveExport(page, 'remove.pdf');
    await page.close();

    const model = pdfModel(exported.bytes);
    expect(pageWords(model)).toEqual(['Keepone', 'Keepthree']);
    expect(appearsAnywhere(exported.bytes, 'DeleteTWO')).toBe(false);
  });

  test('moving a page up puts its content first, once', async () => {
    const page = await openCapturingViewerWith(writeFixture('reorder.pdf', wordPages('AAAONE', 'BBBTWO')));
    await organize(page, (rows) => rows.nth(1).getByRole('button', { name: 'Move up' }).click());
    const exported = await saveExport(page, 'reorder.pdf');
    await page.close();

    expect(pageWords(pdfModel(exported.bytes))).toEqual(['BBBTWO', 'AAAONE']);
  });

  test('several changes in one go are applied together, in the order shown', async () => {
    const page = await openCapturingViewerWith(writeFixture('arrange.pdf', wordPages('p1', 'p2', 'p3', 'p4')));
    await organize(page, async (rows) => {
      await rows.nth(0).getByRole('button', { name: 'Remove page' }).click(); // p2 p3 p4
      await rows.nth(2).getByRole('button', { name: 'Move up' }).click();     // p2 p4 p3
      await rows.nth(0).getByRole('button', { name: 'Move down' }).click();   // p4 p2 p3
    });
    const exported = await saveExport(page, 'arrange.pdf');
    await page.close();

    expect(pageWords(pdfModel(exported.bytes))).toEqual(['p4', 'p2', 'p3']);
    expect(appearsAnywhere(exported.bytes, 'p1')).toBe(false);
  });
});

test.describe('Merging', () => {
  test('a merged document\'s pages follow the current one, in order', async () => {
    const page = await openCapturingViewerWith(writeFixture('base.pdf', wordPages('Basepage')));
    await merge(page, [writeFixture('extra.pdf', wordPages('Extra1', 'Extra2'))]);
    await expect(page.locator('#status')).toContainText('Merged 1 file');
    const exported = await saveExport(page, 'merged.pdf');
    await page.close();

    expect(pageWords(pdfModel(exported.bytes))).toEqual(['Basepage', 'Extra1', 'Extra2']);
  });

  test('two files merged at once, arranged ahead of the current document, land in that order', async () => {
    const page = await openCapturingViewerWith(writeFixture('base2.pdf', wordPages('Current')));
    const first = writeFixture('first.pdf', wordPages('FirstFile'));
    const second = writeFixture('second.pdf', wordPages('SecondFile'));
    await merge(page, [first, second], async (rows) => {
      await rows.nth(0).getByRole('button', { name: 'Move down' }).click(); // first, current, second
      await rows.nth(1).getByRole('button', { name: 'Move down' }).click(); // first, second, current
    });
    await expect(page.locator('#status')).toContainText('Merged 2 files');
    const exported = await saveExport(page, 'arranged.pdf');
    await page.close();

    expect(pageWords(pdfModel(exported.bytes))).toEqual(['FirstFile', 'SecondFile', 'Current']);
  });

  test('dropping the current document in the merge dialog leaves none of it in the file', async () => {
    const page = await openCapturingViewerWith(writeFixture('drop.pdf', wordPages('BaseONLY')));
    await merge(page, [writeFixture('kept.pdf', wordPages('ExtraA', 'ExtraB'))],
      (rows) => rows.first().getByRole('button', { name: 'Remove' }).click());
    await expect(page.locator('#status')).toContainText('Merged 1 file');
    const exported = await saveExport(page, 'dropped.pdf');
    await page.close();

    expect(pageWords(pdfModel(exported.bytes))).toEqual(['ExtraA', 'ExtraB']);
    expect(appearsAnywhere(exported.bytes, 'BaseONLY')).toBe(false);
  });

  test('a merged JPEG becomes a page that carries the original JPEG bytes', async () => {
    const page = await openCapturingViewerWith(writeFixture('photo-base.pdf', wordPages('Report')));
    await merge(page, [writeFixture('photo.jpg', images.JPEG)]);
    await expect(page.locator('#status')).toContainText('Merged 1 file');
    const exported = await saveExport(page, 'with-photo.pdf');
    await page.close();

    const model = pdfModel(exported.bytes);
    expect(model.pageCount()).toBe(2);
    expect(model.compactText(1)).toBe('Report');
    const photo = drawnImage(model, 2);
    expect(photo.filter).toBe('DCTDecode');
    expect(photo.data.equals(images.JPEG)).toBe(true);
  });
});

test.describe('Opening images as documents', () => {
  /** Opens an image file and saves the document it became. */
  async function openImage(name, bytes) {
    const page = await openCapturingViewerWith(writeFixture(name, bytes));
    await expect(page.locator('#page-total')).toHaveText('1');
    const exported = await saveExport(page, `${name}.pdf`);
    await page.close();
    return pdfModel(exported.bytes);
  }

  test('a JPEG keeps its own bytes and is fitted, centred, on an A4 page', async () => {
    const model = await openImage('photo.jpg', images.JPEG);
    expect(model.pageCount()).toBe(1);
    expect(plain(model.inherited(1, 'MediaBox'))).toEqual([0, 0, 595, 842]);
    const image = drawnImage(model);
    expect(image.filter).toBe('DCTDecode');
    expect(image.data.equals(images.JPEG)).toBe(true);
    // 8x8 pixels scaled to the 559 pt the 18 pt margins leave, centred vertically.
    expect(image.ctm).toEqual([559, 0, 0, 559, 18, 141.5]);
  });

  test('a PNG with transparency keeps it as a soft mask', async () => {
    const image = drawnImage(await openImage('alpha.png', images.PNG_ALPHA));
    expect(allPixels(image.data, [255, 0, 0])).toBe(true);
    expect(image.softMask).not.toBeNull();
    expect(image.softMask.length).toBe(64);
    expect([...image.softMask].every((alpha) => Math.abs(alpha - 128) <= 1)).toBe(true);
  });

  test('an opaque PNG keeps its exact colours and needs no soft mask', async () => {
    const image = drawnImage(await openImage('opaque.png', images.PNG_OPAQUE));
    expect([image.width, image.height]).toEqual([8, 8]);
    expect(allPixels(image.data, [0, 128, 255])).toBe(true);
    expect(image.softMask).toBeNull();
  });

  for (const [name, bytes] of [['still.gif', images.GIF], ['still.bmp', images.BMP]]) {
    test(`a ${name.split('.')[1].toUpperCase()} opens with its exact colours`, async () => {
      const image = drawnImage(await openImage(name, bytes));
      expect([image.width, image.height]).toEqual([8, 8]);
      expect(allPixels(image.data, [0, 128, 255])).toBe(true);
    });
  }

  test('a lossy WebP opens with its colours within a step of the original', async () => {
    const image = drawnImage(await openImage('still.webp', images.WEBP));
    expect([image.width, image.height]).toEqual([8, 8]);
    expect(allPixels(image.data, [0, 128, 255], 3)).toBe(true);
  });

  test('an LZW-compressed TIFF is decoded to its exact colours', async () => {
    const image = drawnImage(await openImage('scan.tif', images.TIFF_LZW));
    expect(allPixels(image.data, [0, 160, 0])).toBe(true);
  });

  test('a multi-page TIFF opens on its first page', async () => {
    const model = await openImage('pages.tiff', images.TIFF_TWO_PAGES);
    expect(allPixels(drawnImage(model, 1).data, [255, 0, 0])).toBe(true);
  });
});
