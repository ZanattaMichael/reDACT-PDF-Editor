'use strict';

/**
 * The standard security handler's revision 3 (RC4, 128-bit) and revision 4 (AES-128) algorithms,
 * written from ISO 32000-1 §7.6.3 for building encrypted fixtures. These are the files 2.x users
 * already have, and 3.0 has to keep opening and editing them. Independent of the engine under
 * test: MD5 and AES come from Node's crypto, and RC4 is written out here because OpenSSL 3 only
 * ships it in its legacy provider.
 */

const crypto = require('node:crypto');

// The padding string every password is completed with (Algorithm 2, step a).
const PAD = Buffer.from('28BF4E5E4E758A4164004E56FFFA01082E2E00B6D0683E802F0CA9FE6453697A', 'hex');

function rc4(key, data) {
  const s = Array.from({ length: 256 }, (_, i) => i);
  let j = 0;
  for (let i = 0; i < 256; i++) {
    j = (j + s[i] + key[i % key.length]) & 0xff;
    [s[i], s[j]] = [s[j], s[i]];
  }
  const out = Buffer.alloc(data.length);
  let i = 0;
  j = 0;
  for (let k = 0; k < data.length; k++) {
    i = (i + 1) & 0xff;
    j = (j + s[i]) & 0xff;
    [s[i], s[j]] = [s[j], s[i]];
    out[k] = data[k] ^ s[(s[i] + s[j]) & 0xff];
  }
  return out;
}

const md5 = (...parts) => crypto.createHash('md5').update(Buffer.concat(parts)).digest();
const padded = (password) => Buffer.concat([Buffer.from(password, 'latin1'), PAD]).subarray(0, 32);
const xorEach = (key, i) => key.map((b) => b ^ i);

/** MD5 applied 50 more times, as revisions 3 and 4 strengthen every 128-bit key. */
function md5Rounds(digest) {
  let h = digest;
  for (let i = 0; i < 50; i++) h = md5(h);
  return h;
}

/** RC4 with the key, then 19 more passes with the key XORed with 1…19 (Algorithms 3 and 5). */
function rc4Rounds(key, data) {
  let out = rc4(key, data);
  for (let i = 1; i <= 19; i++) out = rc4(xorEach(key, i), out);
  return out;
}

/** Algorithm 3: the /O entry. */
function ownerEntry(ownerPassword, userPassword) {
  return rc4Rounds(md5Rounds(md5(padded(ownerPassword))), padded(userPassword));
}

/** Algorithm 2: the file key. */
function fileKey(userPassword, owner, permissions, id) {
  const p = Buffer.alloc(4);
  p.writeInt32LE(permissions);
  return md5Rounds(md5(padded(userPassword), owner, p, id));
}

/** Algorithm 5: the /U entry (16 meaningful bytes, padded to 32). */
function userEntry(key, id) {
  return Buffer.concat([rc4Rounds(key, md5(PAD, id)), Buffer.alloc(16)]);
}

/** Algorithm 1: the key for one object's strings and streams. */
function objectKey(key, number, aes) {
  const n = Buffer.from([number & 0xff, (number >> 8) & 0xff, (number >> 16) & 0xff, 0, 0]);
  return md5(key, n, aes ? Buffer.from('sAlT', 'latin1') : Buffer.alloc(0)).subarray(0, 16);
}

/**
 * A security handler for one fixture: the /Encrypt dictionary, the trailer /ID, and a function
 * that encrypts object `number`'s data.
 *
 * Permissions are the /P value: which operations a reader is asked to allow (§7.6.3.2, Table 22).
 *
 * @param {'AES-128'|'RC4-128'} cipher
 */
function standardSecurity(cipher, { userPassword = '', ownerPassword, permissions }) {
  const aes = cipher === 'AES-128';
  const id = crypto.randomBytes(16);
  const owner = ownerEntry(ownerPassword, userPassword);
  const key = fileKey(userPassword, owner, permissions, id);
  const user = userEntry(key, id);
  const filter = aes
    ? '/V 4 /R 4 /Length 128 /CF << /StdCF << /CFM /AESV2 /AuthEvent /DocOpen /Length 16 >> >> '
      + '/StmF /StdCF /StrF /StdCF'
    : '/V 2 /R 3 /Length 128';
  return {
    encryptDict: `<< /Filter /Standard ${filter} /O <${owner.toString('hex')}> `
      + `/U <${user.toString('hex')}> /P ${permissions} >>`,
    trailerId: `/ID [<${id.toString('hex')}> <${id.toString('hex')}>]`,
    encrypt(number, data) {
      const k = objectKey(key, number, aes);
      if (!aes) return rc4(k, data);
      const iv = crypto.randomBytes(16);
      const cipherStream = crypto.createCipheriv('aes-128-cbc', k, iv);
      return Buffer.concat([iv, cipherStream.update(data), cipherStream.final()]);
    },
  };
}

/**
 * /P for a document that may be printed and nothing else: bit 3 (print) set, the operations a
 * restricted file withholds — modify (4), copy (5), annotate (6), fill (9), extract (10),
 * assemble (11), high-quality print (12) — clear, and the reserved bits as the standard requires.
 */
const PRINT_ONLY = 0xfffff0c0 | 0b100; // a bitwise OR yields the signed 32-bit value, -3900

module.exports = { standardSecurity, PRINT_ONLY };
