'use strict';

/**
 * Active and hidden content, end to end: scripts and outward links are stripped on save unless the
 * user keeps them, document scripts can be authored and removed, and "Remove hidden information"
 * takes out each category it lists — judged by the saved file, which must hold no trace of what
 * was removed (helpers/pdf-inspect.js searches every byte and every decoded stream for it).
 */

const { test, expect } = require('@playwright/test');
const {
  buildPdf, buildJavaScriptPdf, buildLinkPdf, buildJsLinkPdf, buildFormWithButtonScriptPdf,
  buildHiddenInfoPdf,
} = require('../helpers/pdf');
const { appearsAnywhere } = require('../helpers/pdf-inspect');
const { pdfModel, plain } = require('../helpers/pdf-model');
const { ui, extensionSuite } = require('../helpers/viewer');

const { openCapturingViewerWith, saveExport, writeFixture } = extensionSuite(test, 'pdf-editor-active-');

/** The action an annotation runs when clicked, as { type, target }, or null. */
function linkAction(model, annotation) {
  const action = model.get(annotation.dict, 'A');
  if (!action) return null;
  return { type: plain(model.get(action, 'S')), target: plain(model.get(action, 'URI') ?? model.get(action, 'JS')) };
}

/** Opens the active-content badge's details and chooses to keep the scripts. */
async function keepScripts(page) {
  const badge = page.locator('#badges .badge.warn', { hasText: 'JavaScript' });
  await expect(badge).toContainText('disabled');
  await badge.click();
  await page.locator('dialog#modal').getByRole('button', { name: /Enable \(keep\)/ }).click();
  await expect(badge).toContainText('kept');
}

/** Adds a document-level script through the JavaScript editor, opening it unless it is open. */
async function addScript(page, name, source) {
  if (!(await page.locator('#js-dialog').isVisible())) await ui(page, '#btn-js');
  await expect(page.locator('#js-dialog')).toBeVisible();
  await page.fill('#js-name', name);
  await page.fill('#js-source', source);
  await page.click('#js-add');
  await expect(page.locator('#js-list .organize-label', { hasText: name })).toHaveCount(1);
}

test.describe('Scripts on save', () => {
  test('a document\'s open-action script is stripped from the saved file by default', async () => {
    const page = await openCapturingViewerWith(writeFixture('js.pdf', buildJavaScriptPdf("app.alert('MARKER_STRIP_1');")));
    await expect(page.locator('#badges .badge.warn', { hasText: 'JavaScript' })).toContainText('disabled');
    const exported = await saveExport(page, 'js-stripped.pdf');
    await page.close();

    const model = pdfModel(exported.bytes);
    expect(model.get(model.catalog, 'OpenAction')).toBeUndefined();
    expect(appearsAnywhere(exported.bytes, 'MARKER_STRIP_1')).toBe(false);
    expect(model.compactText(1)).toBe('Hasscript');
  });

  test('Save pressed while the active-content scan is still running waits for it, and strips', async () => {
    // The scan runs after the document is painted. Holding its reply back keeps it running while
    // Save is pressed: Save used to read "no scan result" as "nothing to strip".
    const page = await openCapturingViewerWith(writeFixture('js-race.pdf', buildJavaScriptPdf("app.alert('MARKER_RACE_1');")),
      { slowHost: { 'scan-safety': 4000 } });
    await expect(page.locator('#badges .badge.warn', { hasText: 'JavaScript' })).toHaveCount(0);
    const exported = await saveExport(page, 'js-race.pdf');
    await page.close();

    expect(appearsAnywhere(exported.bytes, 'MARKER_RACE_1')).toBe(false);
  });

  test('choosing "Enable (keep)" saves the script unchanged', async () => {
    const page = await openCapturingViewerWith(writeFixture('js-keep.pdf', buildJavaScriptPdf("app.alert('MARKER_KEEP_1');")));
    await keepScripts(page);
    const exported = await saveExport(page, 'js-kept.pdf');
    await page.close();

    const model = pdfModel(exported.bytes);
    const action = model.get(model.catalog, 'OpenAction');
    expect(plain(model.get(action, 'S'))).toBe('JavaScript');
    expect(plain(model.get(action, 'JS'))).toBe("app.alert('MARKER_KEEP_1');");
  });

  test('a form button\'s script is stripped by default, and the button itself kept', async () => {
    const page = await openCapturingViewerWith(writeFixture('button-js.pdf', buildFormWithButtonScriptPdf()));
    const exported = await saveExport(page, 'button-js.pdf');
    await page.close();

    const model = pdfModel(exported.bytes);
    expect(model.fields().map((f) => f.name)).toEqual(['a', 'b', 'total', 'calc']);
    const calc = model.fields().find((f) => f.name === 'calc');
    expect(model.get(calc.dict, 'A')).toBeUndefined();
    expect(appearsAnywhere(exported.bytes, 'getField')).toBe(false);
  });

  test('a link that runs JavaScript loses its script and keeps its place on the page', async () => {
    const page = await openCapturingViewerWith(writeFixture('js-link.pdf', buildJsLinkPdf('window.close();')));
    const exported = await saveExport(page, 'js-link.pdf');
    await page.close();

    const model = pdfModel(exported.bytes);
    const links = model.annotations(1).filter((a) => a.subtype === 'Link');
    expect(links).toHaveLength(1);
    expect(linkAction(model, links[0])).toBeNull();
    expect(appearsAnywhere(exported.bytes, 'window.close')).toBe(false);
  });
});

test.describe('Document scripts', () => {
  test('a script authored in the editor is saved in the document\'s JavaScript name tree', async () => {
    const page = await openCapturingViewerWith(writeFixture('author.pdf', buildPdf([[{ text: 'form doc', x: 72, y: 700 }]])));
    const source = "app.alert('hello from the PDF');";
    await addScript(page, 'greet', source);
    await page.click('#js-close');
    await expect(page.locator('#badges .badge.warn')).toContainText('kept');
    const exported = await saveExport(page, 'authored.pdf');
    await page.close();

    expect(pdfModel(exported.bytes).documentScripts()).toEqual([{ name: 'greet', source }]);
  });

  test('a removed script leaves nothing behind in the file', async () => {
    const page = await openCapturingViewerWith(writeFixture('unauthor.pdf', buildPdf([[{ text: 'doc', x: 72, y: 700 }]])));
    await addScript(page, 'keepme', "console.println('KEEP_MARKER');");
    await addScript(page, 'dropme', "console.println('DROP_MARKER');");
    const rows = page.locator('#js-list .organize-item');
    await rows.filter({ hasText: 'dropme' }).getByRole('button', { name: 'Remove script' }).click();
    await expect(page.locator('#status')).toContainText('removed');
    await expect(rows).toHaveCount(1);
    await page.click('#js-close');
    const exported = await saveExport(page, 'unauthored.pdf');
    await page.close();

    expect(pdfModel(exported.bytes).documentScripts().map((s) => s.name)).toEqual(['keepme']);
    expect(appearsAnywhere(exported.bytes, 'DROP_MARKER')).toBe(false);
  });
});

test.describe('Links on save', () => {
  test('a web link\'s address is stripped by default; the link area stays', async () => {
    const page = await openCapturingViewerWith(writeFixture('url.pdf', buildLinkPdf('https://tracker.example/abc')));
    await expect(page.locator('#badges .badge.warn', { hasText: 'links' })).toContainText('disabled');
    const exported = await saveExport(page, 'url-stripped.pdf');
    await page.close();

    const model = pdfModel(exported.bytes);
    const [link] = model.annotations(1).filter((a) => a.subtype === 'Link');
    expect(link).toBeDefined();
    expect(linkAction(model, link)).toBeNull();
    expect(appearsAnywhere(exported.bytes, 'tracker.example')).toBe(false);
  });

  test('enabling links keeps the address in the saved file', async () => {
    const page = await openCapturingViewerWith(writeFixture('url-keep.pdf', buildLinkPdf('https://github.com/example/repo')));
    const badge = page.locator('#badges .badge.warn', { hasText: 'links' });
    await badge.click();
    await expect(page.locator('#links-list')).toContainText('github.com/example/repo');
    await page.locator('#links-enable').check();
    await expect(badge).toContainText('enabled');
    const exported = await saveExport(page, 'url-kept.pdf');
    await page.close();

    const model = pdfModel(exported.bytes);
    const [link] = model.annotations(1).filter((a) => a.subtype === 'Link');
    expect(linkAction(model, link)).toEqual({ type: 'URI', target: 'https://github.com/example/repo' });
  });
});

test.describe('Remove hidden information', () => {
  // Everything buildHiddenInfoPdf hides, as text that would survive in the file if it were kept.
  const TRACES = {
    metadata: ['Internal draft', 'Secret subject', 'Draft tool', 'Department', 'xmpmeta'],
    attachments: ['attached secret notes', 'notes.txt'],
    scriptsAndActions: ['app.alert', 'https://example.com/'],
    annotations: ['reviewer comment'],
    bookmarks: ['Hidden chapter'],
    hiddenLayers: ['Hidden layer'],
  };

  /** Opens the sanitiser on the hidden-info fixture, unticks `keep`, applies, and saves. */
  async function sanitize(name, keep = []) {
    const page = await openCapturingViewerWith(writeFixture(`${name}.pdf`, buildHiddenInfoPdf()));
    await ui(page, '#btn-sanitize');
    await expect(page.locator('#panel-sanitize')).toBeVisible();
    for (const category of Object.keys(TRACES)) {
      await expect(page.locator(`#sanitize-items [data-opt="${category}"]`)).toBeChecked();
    }
    for (const category of keep) await page.locator(`#sanitize-items [data-opt="${category}"]`).uncheck();
    await page.click('#sanitize-apply');
    await expect(page.locator('#status')).toContainText('Hidden information removed');
    const exported = await saveExport(page, `${name}-clean.pdf`);
    await page.close();
    return exported;
  }

  test('every category found is reported, and once removed none of it is left in the file', async () => {
    const exported = await sanitize('hidden-all');
    const model = pdfModel(exported.bytes);

    for (const trace of Object.values(TRACES).flat()) {
      expect(appearsAnywhere(exported.bytes, trace), trace).toBe(false);
    }
    expect(appearsAnywhere(exported.bytes, 'Hidden Author')).toBe(false);
    for (const key of ['Metadata', 'Outlines', 'OCProperties', 'OpenAction']) {
      expect(model.catalog.has(key), key).toBe(false);
    }
    expect(model.embeddedFiles()).toEqual([]);
    // Comments go; the link stays as a place on the page, but without its address.
    expect(model.annotations(1).map((a) => a.subtype)).toEqual(['Link']);
    expect(model.compactText(1)).toBe('Shareablebodytext');
  });

  test('an unticked category is left exactly where it was', async () => {
    const exported = await sanitize('hidden-some', ['bookmarks', 'attachments']);
    const model = pdfModel(exported.bytes);

    expect(model.embeddedFiles()).toEqual(['notes.txt']);
    expect(appearsAnywhere(exported.bytes, 'attached secret notes')).toBe(true);
    const outline = model.get(model.catalog, 'Outlines');
    expect(plain(model.get(model.get(outline, 'First'), 'Title'))).toBe('Hidden chapter');
    for (const category of ['metadata', 'scriptsAndActions', 'annotations', 'hiddenLayers']) {
      for (const trace of TRACES[category]) expect(appearsAnywhere(exported.bytes, trace), trace).toBe(false);
    }
  });

  test('a document with nothing hidden is reported clean, with nothing to apply', async () => {
    const page = await openCapturingViewerWith(writeFixture('clean.pdf', buildPdf([[{ text: 'nothing hidden', x: 72, y: 700 }]])));
    await ui(page, '#btn-sanitize');
    await expect(page.locator('#sanitize-clean')).toBeVisible();
    await expect(page.locator('#sanitize-apply')).toBeDisabled();
    await page.close();
  });
});
