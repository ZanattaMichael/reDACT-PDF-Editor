'use strict';

/**
 * A small PDF reader for the end-to-end tests, independent of the engine they test. It finds the
 * objects in a saved file, inflates their streams with Node's own zlib, and reads back the few
 * structures the checks need: the trailer, /Info, /Encrypt, the page list, bookmarks and a
 * signature's byte range. Reading the bytes this way lets a test say something about the file a
 * user ends up with, not just about what the viewer chooses to show.
 *
 * It covers what the engine writes and what the fixtures contain: classic cross-reference
 * sections, incremental updates (a later definition of an object replaces an earlier one), object
 * streams and Flate. It is not a general-purpose parser.
 */

const zlib = require('node:zlib');

const keyPattern = (key) => `/${key}(?![A-Za-z0-9#])`;

/** The value of `key` when it is an indirect reference, as an object number; else null. */
function refOf(dict, key) {
  const m = new RegExp(`${keyPattern(key)}\\s*(\\d+)\\s+\\d+\\s+R`).exec(dict);
  return m ? Number(m[1]) : null;
}

/** The value of `key` when it is a name, without the slash; else null. */
function nameOf(dict, key) {
  const m = new RegExp(`${keyPattern(key)}\\s*/([^\\s/<>\\[\\]()]+)`).exec(dict);
  return m ? m[1] : null;
}

/** The value of `key` when it is an integer; else null. */
function intOf(dict, key) {
  const m = new RegExp(`${keyPattern(key)}\\s+(-?\\d+)(?![\\d.]|\\s+\\d+\\s+R)`).exec(dict);
  return m ? Number(m[1]) : null;
}

/** The references listed in the array value of `key`, as object numbers. */
function refsIn(dict, key) {
  const m = new RegExp(`${keyPattern(key)}\\s*\\[([^\\]]*)\\]`).exec(dict);
  return m ? [...m[1].matchAll(/(\d+)\s+\d+\s+R/g)].map((r) => Number(r[1])) : [];
}

/** The `<< … >>` starting at `start` in `text`, nested dictionaries included. */
function balancedDict(text, start) {
  let depth = 0;
  for (let i = start; i < text.length - 1; i++) {
    if (text.startsWith('<<', i)) { depth++; i++; } else if (text.startsWith('>>', i)) {
      depth--; i++;
      if (depth === 0) return text.slice(start, i + 1);
    }
  }
  return text.slice(start);
}

const LITERAL_ESCAPES = { n: 10, r: 13, t: 9, b: 8, f: 12 };

/** Reads the octal escape whose first digit is at `i`; returns its byte and where it ends. */
function readOctal(text, i) {
  let j = i;
  while (j < i + 3 && /[0-7]/.test(text[j])) j++;
  return { byte: parseInt(text.slice(i, j), 8) & 0xff, next: j };
}

/** Decodes a literal string whose opening parenthesis is at `start`. */
function literalString(text, start) {
  const out = [];
  let depth = 1;
  let i = start + 1;
  while (i < text.length && depth > 0) {
    const c = text[i];
    if (c === '\\') {
      const e = text[i + 1];
      if (/[0-7]/.test(e)) {
        const { byte, next } = readOctal(text, i + 1);
        out.push(byte);
        i = next;
        continue;
      }
      if (e === '\r' || e === '\n') i += (e === '\r' && text[i + 2] === '\n') ? 3 : 2;
      else { out.push(LITERAL_ESCAPES[e] ?? e.charCodeAt(0)); i += 2; }
      continue;
    }
    if (c === '(') depth++;
    if (c === ')') depth--;
    if (depth > 0) out.push(c.charCodeAt(0));
    i++;
  }
  return Buffer.from(out);
}

/** A text string's characters: UTF-16BE when it starts with a byte-order mark, else Latin-1. */
function decodeText(bytes) {
  if (bytes[0] === 0xfe && bytes[1] === 0xff) return bytes.subarray(2).swap16().toString('utf16le');
  return bytes.toString('latin1');
}

/** The value of `key` when it is a string (literal or hex), as raw bytes; else null. */
function stringBytesOf(dict, key) {
  const m = new RegExp(`${keyPattern(key)}\\s*([(<])`).exec(dict);
  if (!m) return null;
  const at = m.index + m[0].length - 1;
  if (m[1] === '(') return literalString(dict, at);
  if (dict[at + 1] === '<') return null; // a dictionary, not a hex string
  const end = dict.indexOf('>', at);
  const hex = dict.slice(at + 1, end).replace(/\s+/g, '');
  return Buffer.from(hex.length % 2 ? hex + '0' : hex, 'hex');
}

/** The value of `key` when it is a text string, decoded; else null. */
function textOf(dict, key) {
  const bytes = stringBytesOf(dict, key);
  return bytes ? decodeText(bytes) : null;
}

/** Splits a file into its indirect objects: { gen, dict, raw } by object number. */
function topLevelObjects(bytes) {
  const text = bytes.toString('latin1');
  const objects = new Map();
  const header = /(\d+)\s+(\d+)\s+obj\b/g;
  let m;
  while ((m = header.exec(text))) {
    const start = m.index + m[0].length;
    let end = text.indexOf('endobj', start);
    if (end < 0) break;
    const streamAt = text.indexOf('stream', start);
    let dict = text.slice(start, end);
    let raw = null;
    if (streamAt >= 0 && streamAt < end) {
      dict = text.slice(start, streamAt);
      let dataStart = streamAt + 'stream'.length;
      if (text[dataStart] === '\r') dataStart++;
      if (text[dataStart] === '\n') dataStart++;
      const length = intOf(dict, 'Length');
      const endstream = text.indexOf('endstream', dataStart);
      raw = bytes.subarray(dataStart, length !== null ? dataStart + length : endstream);
      end = text.indexOf('endobj', endstream);
    }
    objects.set(Number(m[1]), { gen: Number(m[2]), dict, raw });
    header.lastIndex = end;
  }
  return objects;
}

/** The names in a stream's /Filter, in application order. */
function filtersOf(dict) {
  const single = nameOf(dict, 'Filter');
  if (single) return [single];
  const m = /\/Filter\s*\[([^\]]*)\]/.exec(dict);
  return m ? [...m[1].matchAll(/\/(\w+)/g)].map((f) => f[1]) : [];
}

/**
 * A stream's data with every Flate filter applied. Decoding stops at the first other filter
 * (an image codec, say), and data that does not inflate (an encrypted file's) is left as stored:
 * the tests only search those bytes.
 */
function decodeStream(obj) {
  let data = obj.raw;
  for (const filter of filtersOf(obj.dict)) {
    if (filter !== 'FlateDecode') break;
    try {
      data = zlib.inflateSync(data, { finishFlush: zlib.constants.Z_SYNC_FLUSH });
    } catch {
      break;
    }
  }
  return data;
}

/** Adds the objects packed in each object stream, unless a top-level object replaces them. */
function expandObjectStreams(objects) {
  for (const obj of [...objects.values()]) {
    if (!obj.raw || nameOf(obj.dict, 'Type') !== 'ObjStm') continue;
    const data = decodeStream(obj).toString('latin1');
    const first = intOf(obj.dict, 'First');
    const pairs = data.slice(0, first).trim().split(/\s+/).map(Number);
    for (let k = 0; k < pairs.length; k += 2) {
      const from = first + pairs[k + 1];
      const to = k + 3 < pairs.length ? first + pairs[k + 3] : data.length;
      if (!objects.has(pairs[k])) objects.set(pairs[k], { gen: 0, dict: data.slice(from, to), raw: null });
    }
  }
}

/** The last trailer dictionary: the classic `trailer` keyword's, else the last xref stream's. */
function lastTrailer(text, objects) {
  const at = text.lastIndexOf('trailer');
  if (at >= 0) return balancedDict(text, text.indexOf('<<', at));
  const xref = [...objects.values()].filter((o) => nameOf(o.dict, 'Type') === 'XRef').pop();
  return xref ? xref.dict : '';
}

/**
 * Parses a saved PDF.
 *
 * @param {Buffer} bytes
 */
function readPdf(bytes) {
  const text = bytes.toString('latin1');
  const objects = topLevelObjects(bytes);
  expandObjectStreams(objects);
  const trailer = lastTrailer(text, objects);

  const dictAt = (num) => objects.get(num)?.dict ?? '';

  /** The dictionary value of `key` in `dict`, whether written inline or by reference. */
  function dictOf(dict, key) {
    const ref = refOf(dict, key);
    if (ref !== null) return dictAt(ref);
    const m = new RegExp(`${keyPattern(key)}\\s*<<`).exec(dict);
    return m ? balancedDict(dict, m.index + m[0].length - 2) : null;
  }

  const catalog = () => dictAt(refOf(trailer, 'Root'));

  /** Object numbers of the pages, in page order. */
  function pageObjects() {
    const pages = [];
    const visit = (num, seen) => {
      if (seen.has(num)) return;
      seen.add(num);
      const node = dictAt(num);
      if (nameOf(node, 'Type') === 'Pages') refsIn(node, 'Kids').forEach((kid) => visit(kid, seen));
      else pages.push(num);
    };
    visit(refOf(catalog(), 'Pages'), new Set());
    return pages;
  }

  /** The page an outline item leads to, as a 1-based page number, or null. */
  function destinationPage(item, pages) {
    const explicit = /\/Dest\s*\[\s*(\d+)\s+\d+\s+R/.exec(item);
    const action = explicit ? null : dictOf(item, 'A');
    const goTo = action && nameOf(action, 'S') === 'GoTo' ? /\/D\s*\[\s*(\d+)\s+\d+\s+R/.exec(action) : null;
    const target = explicit ?? goTo;
    const index = target ? pages.indexOf(Number(target[1])) : -1;
    return index >= 0 ? index + 1 : null;
  }

  /**
   * The bookmarks, one line per item in document order: its title, indented two spaces per
   * level, then `-> N` for the page it leads to or `-> none`.
   */
  function outline() {
    const outlines = dictOf(catalog(), 'Outlines');
    if (!outlines) return null;
    const pages = pageObjects();
    const lines = [];
    const seen = new Set();
    const walk = (first, depth) => {
      for (let n = first; n !== null && !seen.has(n); n = refOf(dictAt(n), 'Next')) {
        seen.add(n);
        const item = dictAt(n);
        lines.push(`${'  '.repeat(depth)}${textOf(item, 'Title')} -> ${destinationPage(item, pages) ?? 'none'}`);
        walk(refOf(item, 'First'), depth + 1);
      }
    };
    walk(refOf(outlines, 'First'), 0);
    return lines;
  }

  /** Every stream's data, decoded as far as Flate goes. */
  function streams() {
    return [...objects.values()].filter((o) => o.raw).map(decodeStream);
  }

  /** The signature dictionary (the one carrying a /ByteRange), or null. */
  function signature() {
    const sig = [...objects.values()].map((o) => o.dict).find((d) => /\/ByteRange\s*\[/.test(d));
    if (!sig) return null;
    const range = /\/ByteRange\s*\[\s*(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*\]/.exec(sig).slice(1).map(Number);
    return { dict: sig, byteRange: range, contents: stringBytesOf(sig, 'Contents') };
  }

  return {
    objects,
    trailer,
    catalog,
    info: () => dictOf(trailer, 'Info'),
    encrypt: () => dictOf(trailer, 'Encrypt'),
    pageObjects,
    outline,
    streams,
    signature,
    dictOf,
  };
}

/**
 * Whether `needle` occurs anywhere in the file: in its raw bytes, or in the decoded data of any
 * stream it holds, whether or not anything still draws that stream. Text extraction only sees
 * what a page draws, so it cannot tell redacted content that is gone from content that is merely
 * no longer drawn.
 */
function appearsAnywhere(bytes, needle) {
  const target = Buffer.from(needle, 'latin1');
  if (bytes.includes(target)) return true;
  return readPdf(bytes).streams().some((data) => data.includes(target));
}

module.exports = {
  readPdf, appearsAnywhere, refOf, nameOf, intOf, textOf, stringBytesOf, decodeText,
};
