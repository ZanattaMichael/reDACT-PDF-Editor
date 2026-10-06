'use strict';

/**
 * Forms, end to end: inserting every kind of field the viewer offers, filling them, and flattening
 * on fill — each judged by the saved file's AcroForm, read back with helpers/pdf-model.js: the
 * field's type and flags, its value and widget states, its options, its script, and the appearance
 * stream a reader will actually draw.
 */

const { test, expect } = require('@playwright/test');
const { buildPdf, buildFormPdf } = require('../helpers/pdf');
const { pdfModel, contentOperations, shownText, plain } = require('../helpers/pdf-model');
const { ui, dragPdfRect, extensionSuite } = require('../helpers/viewer');

const { openCapturingViewerWith, saveExport, writeFixture } = extensionSuite(test, 'pdf-editor-forms-');

// Field flags (PDF 32000-1, tables 226–228).
const MULTILINE = 1 << 12;
const NO_TOGGLE_TO_OFF = 1 << 14;
const RADIO = 1 << 15;
const PUSHBUTTON = 1 << 16;
const COMBO = 1 << 17;

const blankPage = () => buildPdf([[{ text: 'form page', x: 72, y: 100 }]]);
const nearAll = (values, expected, tolerance = 2) => values.every((v, i) => Math.abs(v - expected[i]) < tolerance);

/**
 * Inserts a field through the Forms panel: picks the type, names it, fills the type's extra rows
 * (options, caption, script), then drags its box. Waits for the panel to list it.
 */
async function insertField(page, { type, name, rect, options, caption, script }) {
  await ui(page, '#btn-forms');
  await expect(page.locator('#panel-forms')).toBeVisible();
  await page.selectOption('#field-type', type);
  await page.fill('#field-name', name);
  if (options) await page.fill('#field-options', options.join('\n'));
  if (caption) await page.fill('#field-caption', caption);
  if (script) await page.fill('#field-script', script);
  await page.click('#field-place');
  await expect(page.locator('#status')).toContainText('Drag a box');
  await dragPdfRect(page, rect);
  await expect(page.locator(`.form-field[data-field-name="${name}"]`)).toHaveCount(1);
}

/** The single field named `name` in a saved file. */
function fieldNamed(model, name) {
  const matches = model.fields().filter((f) => f.name === name);
  expect(matches).toHaveLength(1);
  return matches[0];
}

/** A field's widget annotations: its kids, or the field itself when field and widget are merged. */
function widgetsOf(model, field) {
  const kids = model.get(field.dict, 'Kids');
  return kids ? kids.map(model.resolve) : [field.dict];
}

/** The text an appearance stream (a form XObject reference) shows. */
function appearanceText(model, ref) {
  return contentOperations(model.streamData(ref)).map((op) => shownText(op)).filter((s) => s !== null).join('');
}

/** The JavaScript a widget runs, from its /A action and every /AA trigger. */
function widgetScripts(model, widget) {
  const actions = [model.get(widget, 'A')];
  const additional = model.get(widget, 'AA');
  if (additional) actions.push(...additional.keys().map((k) => model.get(additional, k)));
  return actions.filter((a) => plain(model.get(a, 'S')) === 'JavaScript').map((a) => plain(model.get(a, 'JS')));
}

test.describe('Inserting fields', () => {
  test('a text field is an empty /Tx field whose widget sits in the box drawn', async () => {
    const page = await openCapturingViewerWith(writeFixture('text-field.pdf', blankPage()));
    await insertField(page, { type: 'text', name: 'fullName', rect: { x: 100, y: 600, width: 220, height: 24 } });
    const exported = await saveExport(page, 'text-field.pdf');
    await page.close();

    const model = pdfModel(exported.bytes);
    const field = fieldNamed(model, 'fullName');
    expect(field.type).toBe('Tx');
    expect(field.flags & MULTILINE).toBe(0);
    const [widget] = widgetsOf(model, field);
    expect(plain(model.get(widget, 'Subtype'))).toBe('Widget');
    expect(nearAll(plain(model.get(widget, 'Rect')), [100, 600, 320, 624])).toBe(true);
    // The widget is on the page it was drawn on.
    expect(model.annotations(1).some((a) => a.dict === widget)).toBe(true);
  });

  test('a text area is a /Tx field with the multiline flag', async () => {
    const page = await openCapturingViewerWith(writeFixture('textarea.pdf', blankPage()));
    await insertField(page, { type: 'multiline', name: 'notes', rect: { x: 100, y: 500, width: 300, height: 120 } });
    const exported = await saveExport(page, 'textarea.pdf');
    await page.close();

    const field = fieldNamed(pdfModel(exported.bytes), 'notes');
    expect(field.type).toBe('Tx');
    expect(field.flags & MULTILINE).toBe(MULTILINE);
  });

  test('a checkbox is a /Btn field, neither radio nor push button, unchecked, with an on and an off look', async () => {
    const page = await openCapturingViewerWith(writeFixture('checkbox.pdf', blankPage()));
    await insertField(page, { type: 'checkbox', name: 'agree', rect: { x: 100, y: 600, width: 20, height: 20 } });
    const exported = await saveExport(page, 'checkbox.pdf');
    await page.close();

    const model = pdfModel(exported.bytes);
    const field = fieldNamed(model, 'agree');
    expect(field.type).toBe('Btn');
    expect(field.flags & (RADIO | PUSHBUTTON)).toBe(0);
    expect(field.value).toBe('Off');
    const [widget] = widgetsOf(model, field);
    expect(plain(model.get(widget, 'AS'))).toBe('Off');
    expect(model.get(model.get(widget, 'AP'), 'N').keys().toSorted()).toEqual(['Off', 'Yes']);
  });

  test('a dropdown is a combo /Ch field listing the options typed, in order', async () => {
    const page = await openCapturingViewerWith(writeFixture('dropdown.pdf', blankPage()));
    await insertField(page, {
      type: 'dropdown', name: 'country', options: ['Australia', 'Canada', 'Denmark'],
      rect: { x: 100, y: 600, width: 220, height: 24 },
    });
    const exported = await saveExport(page, 'dropdown.pdf');
    await page.close();

    const field = fieldNamed(pdfModel(exported.bytes), 'country');
    expect(field.type).toBe('Ch');
    expect(field.flags & COMBO).toBe(COMBO);
    expect(field.options).toEqual(['Australia', 'Canada', 'Denmark']);
  });

  test('an option group is one radio field with a widget per option, the first chosen', async () => {
    const page = await openCapturingViewerWith(writeFixture('radio.pdf', blankPage()));
    await insertField(page, {
      type: 'radio', name: 'plan', options: ['Basic', 'Pro', 'Enterprise'],
      rect: { x: 100, y: 560, width: 200, height: 90 },
    });
    const exported = await saveExport(page, 'radio.pdf');
    await page.close();

    const model = pdfModel(exported.bytes);
    const field = fieldNamed(model, 'plan');
    expect(field.type).toBe('Btn');
    expect(field.flags & RADIO).toBe(RADIO);
    expect(field.flags & NO_TOGGLE_TO_OFF).toBe(NO_TOGGLE_TO_OFF);
    expect(field.value).toBe('Basic');
    const widgets = widgetsOf(model, field);
    expect(widgets).toHaveLength(3);
    // Each widget is one option's button: on in its own state, off otherwise, and only Basic on.
    const states = widgets.map((w) => model.get(model.get(w, 'AP'), 'N').keys().find((k) => k !== 'Off'));
    expect(states).toEqual(['Basic', 'Pro', 'Enterprise']);
    expect(widgets.map((w) => plain(model.get(w, 'AS')))).toEqual(['Basic', 'Off', 'Off']);
    // The options' labels are drawn beside them.
    for (const label of ['Basic', 'Pro', 'Enterprise']) expect(model.compactText(1)).toContain(label);
  });

  test('a button with a script is a push button that keeps its caption and its click script', async () => {
    const page = await openCapturingViewerWith(writeFixture('button.pdf', blankPage()));
    const script = "app.alert('submitted');";
    await insertField(page, {
      type: 'button', name: 'submitBtn', caption: 'Submit', script,
      rect: { x: 100, y: 600, width: 120, height: 28 },
    });
    const exported = await saveExport(page, 'button.pdf');
    await page.close();

    const model = pdfModel(exported.bytes);
    const field = fieldNamed(model, 'submitBtn');
    expect(field.type).toBe('Btn');
    expect(field.flags & PUSHBUTTON).toBe(PUSHBUTTON);
    const [widget] = widgetsOf(model, field);
    expect(plain(model.get(model.get(widget, 'MK'), 'CA'))).toBe('Submit');
    expect(plain(model.get(model.get(widget, 'A'), 'S'))).toBe('JavaScript');
    expect(widgetScripts(model, widget)).toEqual([script]);
    // Its face shows the caption, so a reader draws a labelled button, not blank space.
    expect(appearanceText(model, model.get(widget, 'AP').raw('N'))).toContain('Submit');
  });

  test('a checkbox given a script runs it on click (/AA /U), and the script is saved', async () => {
    const page = await openCapturingViewerWith(writeFixture('checkbox-js.pdf', blankPage()));
    const script = "this.getField('agree').value = 'Yes';";
    await insertField(page, { type: 'checkbox', name: 'agree', script, rect: { x: 100, y: 600, width: 20, height: 20 } });
    const exported = await saveExport(page, 'checkbox-js.pdf');
    await page.close();

    const model = pdfModel(exported.bytes);
    const [widget] = widgetsOf(model, fieldNamed(model, 'agree'));
    const mouseUp = model.get(model.get(widget, 'AA'), 'U');
    expect(plain(model.get(mouseUp, 'S'))).toBe('JavaScript');
    expect(plain(model.get(mouseUp, 'JS'))).toBe(script);
  });
});

test.describe('Filling fields', () => {
  test('a filled text field holds the value and an appearance that draws it', async () => {
    const page = await openCapturingViewerWith(writeFixture('fill.pdf', buildFormPdf('fullName', '')));
    await ui(page, '#btn-forms');
    await page.locator('#forms-list [data-field="fullName"]').fill('Alan Turing');
    await page.click('#forms-apply');
    await expect(page.locator('#status')).toContainText('Form filled');
    const exported = await saveExport(page, 'filled.pdf');
    await page.close();

    const model = pdfModel(exported.bytes);
    const field = fieldNamed(model, 'fullName');
    expect(field.value).toBe('Alan Turing');
    // A value with no appearance reads back in a form panel but prints blank.
    const [widget] = widgetsOf(model, field);
    expect(appearanceText(model, model.get(widget, 'AP').raw('N'))).toContain('Alan Turing');
  });

  test('choosing a radio option and ticking a checkbox set both the values and the widget states', async () => {
    const page = await openCapturingViewerWith(writeFixture('choices.pdf', blankPage()));
    await insertField(page, {
      type: 'radio', name: 'plan', options: ['Basic', 'Pro', 'Enterprise'],
      rect: { x: 100, y: 560, width: 200, height: 90 },
    });
    await insertField(page, { type: 'checkbox', name: 'agree', rect: { x: 100, y: 480, width: 20, height: 20 } });
    await insertField(page, {
      type: 'dropdown', name: 'country', options: ['Australia', 'Canada', 'Denmark'],
      rect: { x: 100, y: 400, width: 220, height: 24 },
    });
    await page.locator('#forms-list [data-field="plan"]').selectOption('Pro');
    await page.locator('#forms-list [data-field="agree"]').check();
    await page.locator('#forms-list [data-field="country"]').selectOption('Denmark');
    await page.click('#forms-apply');
    await expect(page.locator('#status')).toContainText('Form filled');
    const exported = await saveExport(page, 'choices.pdf');
    await page.close();

    const model = pdfModel(exported.bytes);
    const plan = fieldNamed(model, 'plan');
    expect(plan.value).toBe('Pro');
    expect(widgetsOf(model, plan).map((w) => plain(model.get(w, 'AS')))).toEqual(['Off', 'Pro', 'Off']);
    const agree = fieldNamed(model, 'agree');
    expect(agree.value).toBe('Yes');
    expect(plain(model.get(widgetsOf(model, agree)[0], 'AS'))).toBe('Yes');
    expect(fieldNamed(model, 'country').value).toBe('Denmark');
  });

  test('filling with "flatten" leaves no field, and the value drawn into the page', async () => {
    const page = await openCapturingViewerWith(writeFixture('fill-flat.pdf', buildFormPdf('fullName', '')));
    await ui(page, '#btn-forms');
    await page.locator('#forms-list [data-field="fullName"]').fill('Grace Hopper');
    await page.check('#forms-flatten');
    await page.click('#forms-apply');
    await expect(page.locator('#status')).toContainText('Form filled and flattened');
    const exported = await saveExport(page, 'fill-flat.pdf');
    await page.close();

    const model = pdfModel(exported.bytes);
    expect(model.fields()).toEqual([]);
    expect(model.annotations(1).filter((a) => a.subtype === 'Widget')).toEqual([]);
    expect(model.compactText(1)).toContain('GraceHopper');
  });
});
