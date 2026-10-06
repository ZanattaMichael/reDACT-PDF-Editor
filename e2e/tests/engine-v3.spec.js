'use strict';

/**
 * End-to-end checks for reDACT 3.0, which replaced iText with an in-house PDF engine.
 *
 * Every test here drives the real extension against the real .NET host, as a user would, and
 * then reads the file the user would end up with. The saved bytes are captured from the Save
 * button and examined with helpers/pdf-inspect.js, a reader that shares no code with the engine,
 * and where a second opinion matters, with OpenSSL. The suite pins down what 3.0 promises in
 * docs/RELEASE_NOTES_3.0.0.md: what a redaction leaves in the file, the deliberate behaviour
 * changes, and the 2.x behaviour that had to survive the engine swap.
 */

const { test, expect } = require('@playwright/test');
const fs = require('node:fs');
const path = require('node:path');
const { REPO_ROOT } = require('../helpers/harness');
const {
  buildPdf, buildImagePdf, buildNestedFormPdf, buildUndecodableContentPdf, buildUndeclaredFontPdf,
  buildBookmarkedPdf, buildRestrictedPdf,
} = require('../helpers/pdf');
const { readPdf, appearsAnywhere, nameOf, intOf, textOf, stringBytesOf } = require('../helpers/pdf-inspect');
const { signedBytes, verifySignature } = require('../helpers/openssl');
const { popplerText, popplerInfo } = require('../helpers/poppler');
const {
  ui, dragPdfRect, fillDialog, expectText, expectCompactText, applyRedaction, searchAndRedact,
  extensionSuite,
} = require('../helpers/viewer');

const session = extensionSuite(test, 'pdf-editor-v3-');
const { openViewerWith, openCapturingViewerWith, saveExport, writeFixture } = session;

/** Opens a password-protected file, answering the viewer's password prompt. */
const openProtectedViewerWith = (file, password) => openCapturingViewerWith(file, { password });

test.describe('reDACT 3.0 engine, end to end (extension + native host)', () => {
  // ----------------------------------------------------------------- the engine swap itself

  test('the extension talks to a 3.0 host that ships without iText, its cryptography BouncyCastle', async () => {
    const page = await session.ext.context.newPage();
    await page.goto(session.ext.optionsUrl);
    await expect(page.locator('#host-status')).toContainText('✓ connected (host v3.');
    await page.close();

    // The host the options page just reached is this build: its folder holds the in-house engine,
    // none of iText (AGPL), and BouncyCastle (MIT) as the one cryptography library, used directly
    // rather than through iText's adapter.
    const hostDir = path.join(REPO_ROOT, 'src', 'PdfEditor.NativeHost', 'bin', 'Release', 'net8.0');
    const files = fs.readdirSync(hostDir);
    expect(files).toContain('PdfEditor.Core.dll');
    expect(files.filter((f) => /itext/i.test(f))).toEqual([]);
    expect(files.filter((f) => /bouncycastle/i.test(f))).toEqual(['BouncyCastle.Cryptography.dll']);
    expect(files).not.toContain('System.Security.Cryptography.Pkcs.dll');
  });

  test('a saved file names reDACT as its producer, and nothing in it names iText', async () => {
    const file = writeFixture('producer.pdf', buildPdf([[{ text: 'ORIGINALWORD', x: 72, y: 700 }]]));
    const page = await openCapturingViewerWith(file);

    await ui(page, '#btn-find');
    await fillDialog(page, ['ORIGINALWORD', 'EDITEDWORD'], 'Replace all');
    await expect(page.locator('#status')).toContainText('Replaced 1 occurrence');
    const exported = await saveExport(page, 'producer-exported.pdf');
    await page.close();

    const pdf = readPdf(exported.bytes);
    expect(textOf(pdf.info(), 'Producer')).toBe('reDACT');
    expect(appearsAnywhere(exported.bytes, 'iText')).toBe(false);
    expect(appearsAnywhere(exported.bytes, 'EDITEDWORD')).toBe(true);
  });

  // ----------------------------------------------------------------- what a redaction leaves behind

  test('redacting text inside nested forms leaves no copy of it anywhere in the saved file', async () => {
    // The page draws a form that draws a second form holding the secret. The editor rewrites
    // each form on a copy; the originals still hold the text, and if the page keeps naming them
    // the writer saves them: invisible, because nothing draws them, but there for anyone to read.
    const file = writeFixture('nested-forms.pdf',
      buildNestedFormPdf({ secret: 'INNERSECRET', control: 'CONTROL WORDS' }));
    expect(appearsAnywhere(fs.readFileSync(file), 'INNERSECRET')).toBe(true);
    const page = await openCapturingViewerWith(file);
    await expectText(page).toContain('INNERSECRET');

    await searchAndRedact(page, 'INNERSECRET', 1);
    await expectText(page).toContain('CONTROL WORDS');
    await expectText(page).not.toContain('INNERSECRET');
    const exported = await saveExport(page, 'nested-forms-redacted.pdf');
    await page.close();

    expect(appearsAnywhere(exported.bytes, 'INNERSECRET')).toBe(false);
    expect(appearsAnywhere(exported.bytes, 'CONTROL WORDS')).toBe(true);
  });

  test('an image a redaction covers completely is not saved in the file', async () => {
    const file = writeFixture('covered-image.pdf', buildImagePdf({ rect: [72, 500, 400, 700] }));
    const page = await openCapturingViewerWith(file);

    await ui(page, '#tool-redact');
    await dragPdfRect(page, { x: 60, y: 490, width: 360, height: 220 });
    await expect(page.locator('#redact-list li')).toHaveCount(1);
    await applyRedaction(page);
    const exported = await saveExport(page, 'covered-image-redacted.pdf');
    await page.close();

    const images = [...readPdf(exported.bytes).objects.values()]
      .filter((o) => o.raw && nameOf(o.dict, 'Subtype') === 'Image');
    expect(images).toEqual([]);
  });

  // ----------------------------------------------------------------- deliberate behaviour changes

  test('text in a font the page never declared can be found and redacted', async () => {
    const file = writeFixture('undeclared-font.pdf', buildUndeclaredFontPdf([
      { text: 'UNDECLARED SECRET', x: 72, y: 700 },
      { text: 'kept line', x: 72, y: 600 },
    ]));
    const page = await openCapturingViewerWith(file);
    await expectText(page).toContain('UNDECLARED SECRET');

    await searchAndRedact(page, 'UNDECLARED SECRET', 1);
    await expectCompactText(page).toContain('keptline');
    await expectCompactText(page).not.toContain('UNDECLARED');
    const exported = await saveExport(page, 'undeclared-font-redacted.pdf');
    await page.close();

    expect(appearsAnywhere(exported.bytes, 'UNDECLARED')).toBe(false);
    expect(appearsAnywhere(exported.bytes, 'kept line')).toBe(true);
  });

  test('a page whose content cannot be decoded is refused with a clear error, not wiped', async () => {
    // iText read the undecodable stream as empty, so this redaction "succeeded" by replacing the
    // whole page with nothing. 3.0 refuses, says why, and leaves the document as it was.
    const file = writeFixture('undecodable.pdf', buildUndecodableContentPdf());
    const page = await openViewerWith(file);

    await ui(page, '#tool-redact');
    await dragPdfRect(page, { x: 60, y: 690, width: 200, height: 30 });
    await expect(page.locator('#redact-list li')).toHaveCount(1);
    await page.click('#redact-apply');
    await expect(page.locator('#status')).toContainText('could not be read');
    await expect(page.locator('#status')).toContainText('malformed or corrupt');
    await expect(page.locator('#status')).not.toContainText('content removed');
    await expect(page.locator('#btn-undo')).toBeDisabled();

    // Operations that never read the page's content still work on it.
    await ui(page, '#btn-rotate-right');
    await expect(page.locator('#status')).toContainText('Rotated page 1');
    await page.close();
  });

  // ----------------------------------------------------------------- encryption

  test('password protection writes AES-256 (revision 6), allows printing only, and reopens with the password', async () => {
    const file = writeFixture('protect.pdf', buildPdf([[{ text: 'CLASSIFIEDWORD', x: 72, y: 700 }]]));
    const page = await openCapturingViewerWith(file);
    await ui(page, '#btn-protect');
    await fillDialog(page, ['v3-secret', null], 'Encrypt');
    await expect(page.locator('#status')).toContainText('encrypted');
    const exported = await saveExport(page, 'protected.pdf');
    await page.close();

    const encrypt = readPdf(exported.bytes).encrypt();
    expect(nameOf(encrypt, 'Filter')).toBe('Standard');
    expect(intOf(encrypt, 'V')).toBe(5);
    expect(intOf(encrypt, 'R')).toBe(6);
    expect(nameOf(encrypt, 'CFM')).toBe('AESV3');
    expect(stringBytesOf(encrypt, 'O')).toHaveLength(48);
    expect(stringBytesOf(encrypt, 'U')).toHaveLength(48);
    expect(stringBytesOf(encrypt, 'OE')).toHaveLength(32);
    expect(stringBytesOf(encrypt, 'UE')).toHaveLength(32);
    const permissions = intOf(encrypt, 'P');
    expect(permissions & 0b100).not.toBe(0); // bit 3: print
    expect(permissions & 0b1000).toBe(0); // bit 4: modify
    expect(permissions & 0b10000).toBe(0); // bit 5: copy
    expect(appearsAnywhere(exported.bytes, 'CLASSIFIEDWORD')).toBe(false);

    const reopened = await openProtectedViewerWith(exported.file, 'v3-secret');
    await expectText(reopened).toContain('CLASSIFIEDWORD');
    await reopened.close();
  });

  for (const cipher of ['AES-128', 'RC4-128']) {
    test(`a ${cipher} file restricted to printing is redacted without the owner password, and stays restricted`, async () => {
      // A 2.x-era file: empty open password, owner password withholding everything but printing.
      // 2.x edited these without asking for the owner password, and wrote the result unencrypted,
      // which dropped the restrictions. The edit now keeps the file's own encryption.
      const original = buildRestrictedPdf(cipher, [
        { text: 'LEGACY SECRETWORD', x: 72, y: 700 },
        { text: 'public words', x: 72, y: 600 },
      ]);
      const file = writeFixture(`restricted-${cipher}.pdf`, original);
      const page = await openCapturingViewerWith(file);
      await expectText(page).toContain('LEGACY SECRETWORD');

      await searchAndRedact(page, 'SECRETWORD', 1);
      await expectCompactText(page).toContain('publicwords');
      await expectCompactText(page).not.toContain('SECRETWORD');
      const exported = await saveExport(page, `restricted-${cipher}-redacted.pdf`);
      await page.close();

      // The same scheme, key and permissions, as poppler reads them...
      const encrypt = readPdf(exported.bytes).encrypt();
      expect(encrypt).not.toBeNull();
      expect(stringBytesOf(encrypt, 'O')).toEqual(stringBytesOf(readPdf(original).encrypt(), 'O'));
      expect(popplerInfo(exported.bytes, session.fixtureDir).Encrypted)
        .toBe(popplerInfo(original, session.fixtureDir).Encrypted);
      // ...and still no password to open it, with the redaction in it.
      const text = popplerText(exported.bytes, session.fixtureDir);
      expect(text).toContain('public words');
      expect(text).not.toContain('SECRETWORD');
      expect(appearsAnywhere(exported.bytes, 'SECRETWORD')).toBe(false);
    });
  }

  // ----------------------------------------------------------------- bookmarks

  test('removing a page drops the bookmarks that led to it and repoints the rest', async () => {
    const file = writeFixture('bookmarks-arrange.pdf', buildBookmarkedPdf('A'));
    const page = await openCapturingViewerWith(file);
    await ui(page, '#btn-organize');
    await page.locator('#organize-list .organize-item').nth(1)
      .getByRole('button', { name: 'Remove page' }).click();
    await page.click('#organize-apply');
    await expect(page.locator('#status')).toContainText('reorganized');
    await expect(page.locator('#page-total')).toHaveText('2');
    const exported = await saveExport(page, 'bookmarks-arranged.pdf');
    await page.close();

    // "A two" and its child led to the removed page, and "A site" never led to a page at all.
    expect(readPdf(exported.bytes).outline()).toEqual(['A one -> 1', 'A three -> 2']);
  });

  test('merging keeps both documents\' bookmarks, each pointing at its merged page', async () => {
    const first = writeFixture('bookmarks-a.pdf', buildBookmarkedPdf('A'));
    const second = writeFixture('bookmarks-b.pdf', buildBookmarkedPdf('B'));
    const page = await openCapturingViewerWith(first);
    const chooser = page.waitForEvent('filechooser');
    await ui(page, '#btn-merge');
    await (await chooser).setFiles(second);
    await page.locator('dialog#modal').getByRole('button', { name: 'Merge' }).click();
    await expect(page.locator('#status')).toContainText('Merged 1 file');
    await expect(page.locator('#page-total')).toHaveText('6');
    const exported = await saveExport(page, 'bookmarks-merged.pdf');
    await page.close();

    expect(readPdf(exported.bytes).outline()).toEqual([
      'A one -> 1', 'A two -> 2', '  A two point one -> 2', 'A three -> 3',
      'B one -> 4', 'B two -> 5', '  B two point one -> 5', 'B three -> 6',
    ]);
  });

  // ----------------------------------------------------------------- signing

  test('a digital signature covers the whole saved file and verifies with OpenSSL', async () => {
    const file = writeFixture('sign.pdf', buildPdf([[{ text: 'Agreement text', x: 72, y: 700 }]]));
    const page = await openCapturingViewerWith(file);
    await ui(page, '#btn-digital');
    await fillDialog(page, ['Approval', '', 'certpw'], 'Continue');
    await page.locator('dialog#modal').getByRole('button', { name: 'Create self-signed' }).click();
    await fillDialog(page, ['V3 Signer', 'certpw'], 'Create & sign');
    await expect(page.locator('#status')).toContainText('digitally signed');
    const exported = await saveExport(page, 'signed.pdf');
    await page.close();

    // The signed ranges are everything but the signature's own hex string.
    const { byteRange: [start, firstLength, secondStart, secondLength], contents } =
      readPdf(exported.bytes).signature();
    expect(start).toBe(0);
    expect(secondStart + secondLength).toBe(exported.bytes.length);
    expect(exported.bytes.subarray(firstLength, secondStart).toString('latin1'))
      .toMatch(/^<[0-9A-Fa-f]+>$/);

    const content = signedBytes(exported.bytes, [start, firstLength, secondStart, secondLength]);
    const verified = verifySignature(content, contents, session.fixtureDir);
    expect(verified.output).toContain('Verification successful');
    expect(verified.subject).toContain('V3 Signer');

    // The check has teeth: one changed byte in the signed content and verification fails.
    const tampered = Buffer.from(content);
    tampered[100] ^= 0x01;
    expect(verifySignature(tampered, contents, session.fixtureDir).status).not.toBe(0);

    // Reopened from the saved file, the viewer reports the signature as valid.
    const reopened = await openViewerWith(exported.file);
    await expect(reopened.locator('#badges .badge.signed')).toContainText('V3 Signer');
    await expect(reopened.locator('#badges .badge.signed')).toContainText('✓');
    await reopened.close();
  });

  // ----------------------------------------------------------------- images

  test('an opaque PNG opened as a document carries no soft mask', async () => {
    // A 16x16 solid-red PNG with no alpha channel: iText gave such images an /SMask anyway.
    const pngFile = writeFixture('opaque.png', Buffer.from(
      'iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAIAAACQkWg2AAAAFklEQVR4nGO4IyJCEmIY1TCqYfhqAAACcQQQFwFJQgAAAABJRU5ErkJggg==',
      'base64'));
    const page = await openCapturingViewerWith(pngFile);
    await expect(page.locator('#page-total')).toHaveText('1');
    const exported = await saveExport(page, 'opaque-png.pdf');
    await page.close();

    const images = [...readPdf(exported.bytes).objects.values()]
      .filter((o) => o.raw && nameOf(o.dict, 'Subtype') === 'Image');
    expect(images).toHaveLength(1);
    expect(images[0].dict).not.toContain('/SMask');
  });
});
