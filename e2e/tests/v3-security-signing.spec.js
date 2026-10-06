'use strict';

/**
 * Passwords and signatures, end to end: removing encryption, opening with the owner password or
 * after a wrong one, signing (twice, and with a certificate made by OpenSSL), and what the viewer
 * says about a signed file that was changed afterwards. Encryption is read from the saved file's
 * trailer, and every signature is verified by OpenSSL over the bytes its /ByteRange names.
 */

const { test, expect } = require('@playwright/test');
const { buildPdf } = require('../helpers/pdf');
const { readPdf, appearsAnywhere } = require('../helpers/pdf-inspect');
const { pdfModel, plain } = require('../helpers/pdf-model');
const { signedBytes, verifySignature, makePkcs12 } = require('../helpers/openssl');
const { ui, fillDialog, expectText, extensionSuite } = require('../helpers/viewer');

const session = extensionSuite(test, 'pdf-editor-security-');
const { openViewerWith, openCapturingViewerWith, saveExport, writeFixture } = session;

const agreement = () => buildPdf([[{ text: 'Agreement text', x: 72, y: 700 }]]);

/** Protects the open document with a user and (optionally) an owner password. */
async function protect(page, userPassword, ownerPassword = null) {
  await ui(page, '#btn-protect');
  await fillDialog(page, [userPassword, ownerPassword], 'Encrypt');
  await expect(page.locator('#status')).toContainText('encrypted');
}

/** A protected copy of `pdf`, made through the viewer and saved; returns the saved file. */
async function protectedFile(name, pdf, userPassword, ownerPassword) {
  const page = await openCapturingViewerWith(writeFixture(`${name}.pdf`, pdf));
  await protect(page, userPassword, ownerPassword);
  const exported = await saveExport(page, `${name}-protected.pdf`);
  await page.close();
  expect(readPdf(exported.bytes).encrypt()).not.toBeNull();
  return exported;
}

/** Signs the open document with a new self-signed certificate for `signer`. */
async function signSelfSigned(page, signer, reason = '') {
  await ui(page, '#btn-digital');
  await fillDialog(page, [reason, '', 'certpw'], 'Continue');
  await page.locator('dialog#modal').getByRole('button', { name: 'Create self-signed' }).click();
  await fillDialog(page, [signer, 'certpw'], 'Create & sign');
  await expect(page.locator('#status')).toContainText('digitally signed');
}

/** Signs the open document with a PKCS#12 file through "Use certificate file…". */
async function signWithFile(page, p12, password, { reason = '', location = '' } = {}) {
  await ui(page, '#btn-digital');
  await fillDialog(page, [reason, location, password], 'Continue');
  const chooser = page.waitForEvent('filechooser');
  await page.locator('dialog#modal').getByRole('button', { name: /Use certificate file/ }).click();
  await (await chooser).setFiles(p12);
}

/** OpenSSL's verdict on a signature in `bytes`. */
function verify(bytes, signature) {
  return verifySignature(signedBytes(bytes, signature.byteRange), signature.contents, session.fixtureDir);
}

/** The signature dictionary (the /V of the document's signature field), via the AcroForm. */
function signatureDictionaries(model) {
  return model.fields().filter((f) => f.type === 'Sig').map((f) => model.get(f.dict, 'V'));
}

test.describe('Passwords', () => {
  test('removing encryption from a protected file saves it unencrypted, with its content intact', async () => {
    const encrypted = await protectedFile('decrypt', agreement(), 'open-sesame');
    const page = await openCapturingViewerWith(encrypted.file, { password: 'open-sesame' });
    await ui(page, '#btn-decrypt');
    await expect(page.locator('#status')).toContainText('Encryption removed');
    await expect(page.locator('#badges .badge.locked')).toHaveCount(0);
    const exported = await saveExport(page, 'decrypted.pdf');
    await page.close();

    expect(readPdf(exported.bytes).encrypt()).toBeNull();
    expect(pdfModel(exported.bytes).compactText(1)).toBe('Agreementtext');
    // It opens with no password at all.
    const reopened = await openViewerWith(exported.file);
    await expectText(reopened).toContain('Agreement text');
    await reopened.close();
  });

  test('a file protected with separate passwords opens with the owner password too', async () => {
    const encrypted = await protectedFile('owner', agreement(), 'user-pw', 'owner-pw');
    for (const password of ['user-pw', 'owner-pw']) {
      const page = await openCapturingViewerWith(encrypted.file, { password });
      await expectText(page).toContain('Agreement text');
      await page.close();
    }
  });

  test('a wrong password is asked again, and the right one then opens the file', async () => {
    const encrypted = await protectedFile('retry', agreement(), 'right-pw');
    const page = await openCapturingViewerWith(encrypted.file, { password: ['wrong-pw', 'right-pw'] });
    await expectText(page).toContain('Agreement text');
    await expect(page.locator('#badges .badge.locked')).toBeVisible();
    await page.close();
  });

  test('a protected file\'s content is not readable without the password', async () => {
    const encrypted = await protectedFile('opaque', buildPdf([[{ text: 'UNREADABLEWORD', x: 72, y: 700 }]]), 'pw');
    expect(appearsAnywhere(encrypted.bytes, 'UNREADABLEWORD')).toBe(false);
  });
});

test.describe('Digital signatures', () => {
  test('a second signature keeps the first valid: both verify, each over its own revision', async () => {
    const page = await openCapturingViewerWith(writeFixture('twice.pdf', agreement()));
    await signSelfSigned(page, 'First Signer', 'Prepared');
    await signSelfSigned(page, 'Second Signer', 'Approved');
    const exported = await saveExport(page, 'twice-signed.pdf');
    await page.close();

    const [first, second] = readPdf(exported.bytes).signatures();
    expect(second).toBeDefined();
    // The second covers the whole file; the first covers only the revision it signed, which the
    // second signature's update was appended after.
    const end = ([, , start, length]) => start + length;
    expect(end(second.byteRange)).toBe(exported.bytes.length);
    expect(end(first.byteRange)).toBeLessThan(second.byteRange[1]);
    const firstCheck = verify(exported.bytes, first);
    const secondCheck = verify(exported.bytes, second);
    expect(firstCheck.output).toContain('Verification successful');
    expect(firstCheck.subject).toContain('First Signer');
    expect(secondCheck.output).toContain('Verification successful');
    expect(secondCheck.subject).toContain('Second Signer');

    const model = pdfModel(exported.bytes);
    const reasons = signatureDictionaries(model).map((v) => plain(model.get(v, 'Reason'))).toSorted();
    expect(reasons).toEqual(['Approved', 'Prepared']);
  });

  test('a certificate made by OpenSSL signs the file, with the reason and location given', async () => {
    const p12 = makePkcs12(session.fixtureDir, 'OpenSSL Signer', 'p12-pass');
    const page = await openCapturingViewerWith(writeFixture('openssl-cert.pdf', agreement()));
    await signWithFile(page, p12, 'p12-pass', { reason: 'Reviewed', location: 'Sydney' });
    await expect(page.locator('#status')).toContainText('digitally signed');
    await expect(page.locator('#badges .badge.signed')).toContainText('OpenSSL Signer');
    const exported = await saveExport(page, 'openssl-signed.pdf');
    await page.close();

    const [signature] = readPdf(exported.bytes).signatures();
    const check = verify(exported.bytes, signature);
    expect(check.output).toContain('Verification successful');
    expect(check.subject).toContain('OpenSSL Signer');
    const model = pdfModel(exported.bytes);
    const [dict] = signatureDictionaries(model);
    expect(plain(model.get(dict, 'Reason'))).toBe('Reviewed');
    expect(plain(model.get(dict, 'Location'))).toBe('Sydney');
    expect(plain(model.get(dict, 'SubFilter'))).toBe('adbe.pkcs7.detached');
  });

  test('the wrong certificate password signs nothing and says why', async () => {
    const p12 = makePkcs12(session.fixtureDir, 'Locked Signer', 'real-pass');
    const page = await openCapturingViewerWith(writeFixture('bad-cert-pw.pdf', agreement()));
    await signWithFile(page, p12, 'not-the-pass');
    await expect(page.locator('#status')).toContainText('⚠');
    await expect(page.locator('#badges .badge.signed')).toHaveCount(0);
    const exported = await saveExport(page, 'bad-cert-pw.pdf');
    await page.close();

    expect(readPdf(exported.bytes).signatures()).toEqual([]);
  });

  test('a signed file changed afterwards is shown as no longer valid', async () => {
    const page = await openCapturingViewerWith(writeFixture('tamper.pdf', agreement()));
    await signSelfSigned(page, 'Tamper Check');
    const exported = await saveExport(page, 'tamper-signed.pdf');
    await page.close();

    // Change one digit of a number inside the signed bytes (the page's MediaBox width), which
    // leaves the file well-formed and every offset where it was.
    const at = exported.bytes.indexOf('595');
    const [, firstLength] = readPdf(exported.bytes).signature().byteRange;
    expect(at).toBeGreaterThan(0);
    expect(at).toBeLessThan(firstLength);
    const tampered = Buffer.from(exported.bytes);
    tampered[at + 2] = '4'.codePointAt(0);
    const file = writeFixture('tampered.pdf', tampered);

    const reopened = await openViewerWith(file);
    await expect(reopened.locator('#badges .badge.signed')).toContainText('Tamper Check');
    await expect(reopened.locator('#badges .badge.signed')).toContainText('✗');
    await reopened.close();
  });

  test('signing a protected document keeps it encrypted, and the signature verifies', async () => {
    const page = await openCapturingViewerWith(writeFixture('sign-encrypted.pdf', agreement()));
    await protect(page, 'enc-pw');
    await signSelfSigned(page, 'Encrypted Signer');
    const exported = await saveExport(page, 'sign-encrypted.pdf');
    await page.close();

    const pdf = readPdf(exported.bytes);
    expect(pdf.encrypt()).not.toBeNull();
    const [signature] = pdf.signatures();
    expect(verify(exported.bytes, signature).output).toContain('Verification successful');
    const reopened = await openCapturingViewerWith(exported.file, { password: 'enc-pw' });
    await expect(reopened.locator('#badges .badge.signed')).toContainText('✓');
    await reopened.close();
  });
});
