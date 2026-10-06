'use strict';

/**
 * OpenSSL as a second opinion on what the engine signs and reads: it verifies PDF signatures and
 * makes certificates the way other tools do. Shares no code with the engine (.NET's SignedCms).
 */

const { spawnSync } = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { systemBinary, TOOL_DIRS } = require('./system-binary');

/** OpenSSL, found in a fixed system directory rather than through $PATH. */
const openssl = () => systemBinary('openssl', TOOL_DIRS);

/** The length of the DER value at the start of `der`: a signature's /Contents is zero-padded. */
function derLength(der) {
  if (der[1] < 0x80) return 2 + der[1];
  const lengthBytes = der[1] & 0x7f;
  let length = 0;
  for (let i = 0; i < lengthBytes; i++) length = length * 256 + der[2 + i];
  return 2 + lengthBytes + length;
}

/** The bytes a signature's /ByteRange covers in `fileBytes`. */
function signedBytes(fileBytes, [start, firstLength, secondStart, secondLength]) {
  return Buffer.concat([
    fileBytes.subarray(start, start + firstLength),
    fileBytes.subarray(secondStart, secondStart + secondLength),
  ]);
}

/**
 * Asks OpenSSL to verify a CMS signature (a PDF signature's /Contents) over `content`. The
 * signature must match the content; the self-signed certificate is not checked against any trust
 * store. Returns OpenSSL's exit status and output, and the signer certificate's subject.
 */
function verifySignature(content, contents, workDir) {
  const dir = fs.mkdtempSync(path.join(workDir, 'cms-'));
  const sigFile = path.join(dir, 'signature.der');
  const dataFile = path.join(dir, 'signed-bytes.bin');
  const signerFile = path.join(dir, 'signer.pem');
  fs.writeFileSync(sigFile, contents.subarray(0, derLength(contents)));
  fs.writeFileSync(dataFile, content);
  const verify = spawnSync(openssl(), ['cms', '-verify', '-binary', '-inform', 'DER', '-in', sigFile,
    '-content', dataFile, '-noverify', '-signer', signerFile, '-out', os.devNull], { encoding: 'utf8' });
  const subject = verify.status === 0
    ? spawnSync(openssl(), ['x509', '-in', signerFile, '-noout', '-subject'], { encoding: 'utf8' }).stdout
    : '';
  return { status: verify.status, output: `${verify.stdout}${verify.stderr}`, subject };
}

/**
 * A PKCS#12 bundle (.p12) holding a fresh self-signed RSA certificate for `commonName`, made by
 * OpenSSL the way users bring certificates from other tools. Returns the path.
 */
function makePkcs12(workDir, commonName, password) {
  const dir = fs.mkdtempSync(path.join(workDir, 'pkcs12-'));
  const key = path.join(dir, 'key.pem');
  const cert = path.join(dir, 'cert.pem');
  const p12 = path.join(dir, 'identity.p12');
  const run = (args) => {
    const r = spawnSync(openssl(), args, { encoding: 'utf8' });
    if (r.status !== 0) throw new Error(`openssl ${args[0]} failed: ${r.stderr}`);
  };
  run(['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-keyout', key, '-out', cert,
    '-days', '30', '-subj', `/CN=${commonName}`]);
  run(['pkcs12', '-export', '-inkey', key, '-in', cert, '-out', p12, '-passout', `pass:${password}`,
    '-name', commonName]);
  return p12;
}

module.exports = { derLength, signedBytes, verifySignature, makePkcs12 };
