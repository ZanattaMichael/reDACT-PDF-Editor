'use strict';

/**
 * A structured view of a saved PDF for the end-to-end tests, built on pdf-inspect.js's object
 * table and, like it, sharing no code with the engine under test. It parses objects into values
 * (dictionaries, arrays, strings, names, references), parses content streams into operators, and
 * answers the questions the feature tests ask of a file: what each page shows and draws, which
 * annotations and form fields it carries, which scripts, metadata and attachments it holds.
 */

const { readPdf, decodeStream, decodeText, readLiteral } = require('./pdf-inspect');

// ------------------------------------------------------------------ values

class Name {
  constructor(value) { this.value = value; }
}

class Str {
  constructor(bytes) { this.bytes = bytes; }
  get text() { return decodeText(this.bytes); }
}

class Ref {
  constructor(num) { this.num = num; }
}

class Dict {
  constructor(map) { this.map = map; }
  has(key) { return this.map.has(key); }
  raw(key) { return this.map.get(key); }
  keys() { return [...this.map.keys()]; }
}

/** A PDF value as plain JavaScript: names and strings as their text, the rest unchanged. */
function plain(value) {
  if (value instanceof Name) return value.value;
  if (value instanceof Str) return value.text;
  if (Array.isArray(value)) return value.map(plain);
  return value;
}

// ------------------------------------------------------------------ lexer

const isWhite = (c) => c === ' ' || c === '\n' || c === '\r' || c === '\t' || c === '\f' || c === '\0';
const isDelimiter = (c) => '()<>[]{}/%'.includes(c);
const NUMBER = /^[+-]?(\d+\.?\d*|\.\d+)$/;

/** Splits PDF syntax into tokens: strings, names, numbers, keywords and the four brackets. */
class Lexer {
  constructor(text, pos = 0) {
    this.text = text;
    this.pos = pos;
  }

  skipSpace() {
    while (this.pos < this.text.length) {
      const c = this.text[this.pos];
      if (c === '%') this.skipComment();
      else if (isWhite(c)) this.pos++;
      else return;
    }
  }

  skipComment() {
    while (this.pos < this.text.length && this.text[this.pos] !== '\n' && this.text[this.pos] !== '\r') this.pos++;
  }

  /** The characters up to the next whitespace or delimiter. */
  word() {
    const start = this.pos;
    while (this.pos < this.text.length && !isWhite(this.text[this.pos]) && !isDelimiter(this.text[this.pos])) this.pos++;
    return this.text.slice(start, this.pos);
  }

  next() {
    this.skipSpace();
    if (this.pos >= this.text.length) return null;
    const c = this.text[this.pos];
    const two = this.text.slice(this.pos, this.pos + 2);
    if (two === '<<' || two === '>>') {
      this.pos += 2;
      return { kind: two };
    }
    if (c === '[' || c === ']' || c === '{' || c === '}') {
      this.pos++;
      return { kind: c };
    }
    if (c === '(') return this.literal();
    if (c === '<') return this.hex();
    if (c === '/') return this.name();
    const word = this.word() || this.text[this.pos++];
    return NUMBER.test(word) ? { kind: 'num', value: Number(word) } : { kind: 'kw', value: word };
  }

  literal() {
    const { bytes, end } = readLiteral(this.text, this.pos);
    this.pos = end;
    return { kind: 'str', value: new Str(bytes) };
  }

  hex() {
    const end = this.text.indexOf('>', this.pos);
    const digits = this.text.slice(this.pos + 1, end < 0 ? this.text.length : end).replace(/\s+/g, '');
    this.pos = end < 0 ? this.text.length : end + 1;
    return { kind: 'str', value: new Str(Buffer.from(digits.length % 2 ? `${digits}0` : digits, 'hex')) };
  }

  name() {
    this.pos++;
    const raw = this.word();
    return { kind: 'name', value: new Name(raw.replace(/#([0-9A-Fa-f]{2})/g, (_, h) => String.fromCodePoint(Number.parseInt(h, 16)))) };
  }
}

// ------------------------------------------------------------------ parser

const KEYWORD_VALUES = { true: true, false: false, null: null };

/** Parses one value, starting from `token` (the next token when omitted). */
function parseValue(lexer, token = lexer.next()) {
  if (!token) return undefined;
  switch (token.kind) {
    case '<<': return parseDict(lexer);
    case '[': return parseArray(lexer);
    case 'num': return parseNumberOrRef(lexer, token.value);
    case 'kw': return token.value in KEYWORD_VALUES ? KEYWORD_VALUES[token.value] : { keyword: token.value };
    default: return token.value;
  }
}

function parseDict(lexer) {
  const map = new Map();
  for (let key = lexer.next(); key && key.kind !== '>>'; key = lexer.next()) {
    if (key.kind === 'name') map.set(key.value.value, parseValue(lexer));
  }
  return new Dict(map);
}

function parseArray(lexer) {
  const items = [];
  for (let token = lexer.next(); token && token.kind !== ']'; token = lexer.next()) items.push(parseValue(lexer, token));
  return items;
}

/** A number, or an `n g R` reference when the next two tokens complete one. */
function parseNumberOrRef(lexer, value) {
  const saved = lexer.pos;
  const gen = lexer.next();
  const r = gen?.kind === 'num' ? lexer.next() : null;
  if (r?.kind === 'kw' && r.value === 'R') return new Ref(value);
  lexer.pos = saved;
  return value;
}

// ------------------------------------------------------------------ content streams

/** The parameters of an inline image (`BI … ID`), skipping its data up to `EI`. */
function readInlineImage(lexer) {
  const map = new Map();
  for (let key = lexer.next(); key && !(key.kind === 'kw' && key.value === 'ID'); key = lexer.next()) {
    if (key.kind === 'name') map.set(key.value.value, parseValue(lexer));
  }
  const end = /\sEI(?=\s|$)/g;
  end.lastIndex = lexer.pos + 1;
  const m = end.exec(lexer.text);
  lexer.pos = m ? m.index + m[0].length : lexer.text.length;
  return new Dict(map);
}

/** A content stream's operators in order, each with its operands. */
function contentOperations(data) {
  const lexer = new Lexer(data.toString('latin1'));
  const ops = [];
  let operands = [];
  for (let token = lexer.next(); token; token = lexer.next()) {
    if (token.kind !== 'kw' || token.value in KEYWORD_VALUES) {
      operands.push(parseValue(lexer, token));
    } else if (token.value === 'BI') {
      ops.push({ op: 'BI', operands: [readInlineImage(lexer)] });
      operands = [];
    } else {
      ops.push({ op: token.value, operands });
      operands = [];
    }
  }
  return ops;
}

const latin1 = (bytes) => bytes.toString('latin1');

/**
 * The strings a text-showing operator shows, decoded with `decode` (Latin-1 unless a font says
 * otherwise) and joined; null for any other operator.
 */
function shownText(op, decode = latin1) {
  if (op.op === 'Tj' || op.op === "'" || op.op === '"') {
    const last = op.operands.at(-1);
    return last instanceof Str ? decode(last.bytes) : '';
  }
  if (op.op === 'TJ' && Array.isArray(op.operands[0])) {
    return op.operands[0].filter((item) => item instanceof Str).map((s) => decode(s.bytes)).join('');
  }
  return null;
}

// ------------------------------------------------------------------ ToUnicode CMaps

/** UTF-16BE bytes (a ToUnicode CMap's destination strings) as text. */
function utf16be(bytes) {
  return Buffer.from(bytes.subarray(0, bytes.length - (bytes.length % 2))).swap16().toString('utf16le');
}

/** Bytes read as one big-endian unsigned number: a character code. */
const codeOf = (bytes) => bytes.reduce((n, b) => n * 256 + b, 0);

/** The values up to the keyword `end`. */
function valuesUntil(lexer, end) {
  const values = [];
  for (let token = lexer.next(); token && !(token.kind === 'kw' && token.value === end); token = lexer.next()) {
    values.push(parseValue(lexer, token));
  }
  return values;
}

/** One bfrange entry: codes low..high map to consecutive strings, or to the strings of an array. */
function addRange(map, low, high, destination) {
  const first = codeOf(low.bytes);
  const last = Math.min(codeOf(high.bytes), first + 0xffff);
  for (let code = first; code <= last; code++) {
    if (Array.isArray(destination)) {
      const item = destination[code - first];
      if (item instanceof Str) map.set(code, utf16be(item.bytes));
    } else if (destination.bytes.length >= 2) {
      const units = Buffer.from(destination.bytes);
      const lastUnit = units.length - 2;
      units.writeUInt16BE((units.readUInt16BE(lastUnit) + code - first) & 0xffff, lastUnit);
      map.set(code, utf16be(units));
    }
  }
}

const CMAP_SECTIONS = {
  begincodespacerange: (lexer, cmap) => {
    const [low] = valuesUntil(lexer, 'endcodespacerange');
    if (low instanceof Str) cmap.width = low.bytes.length;
  },
  beginbfchar: (lexer, cmap) => {
    const values = valuesUntil(lexer, 'endbfchar');
    for (let i = 0; i + 1 < values.length; i += 2) cmap.map.set(codeOf(values[i].bytes), utf16be(values[i + 1].bytes));
  },
  beginbfrange: (lexer, cmap) => {
    const values = valuesUntil(lexer, 'endbfrange');
    for (let i = 0; i + 2 < values.length; i += 3) addRange(cmap.map, values[i], values[i + 1], values[i + 2]);
  },
};

/**
 * A ToUnicode CMap as { width, map }: how many bytes a character code takes, and each code's text.
 * Only what a ToUnicode CMap uses is read: the code space, bfchar and bfrange.
 */
function parseToUnicode(data) {
  const lexer = new Lexer(data.toString('latin1'));
  const cmap = { width: 1, map: new Map() };
  for (let token = lexer.next(); token; token = lexer.next()) {
    if (token.kind === 'kw') CMAP_SECTIONS[token.value]?.(lexer, cmap);
  }
  return cmap;
}

/** Decodes string bytes code by code through a parsed ToUnicode CMap. */
function cmapDecoder({ width, map }) {
  return (bytes) => {
    let text = '';
    for (let i = 0; i + width <= bytes.length; i += width) {
      const code = codeOf(bytes.subarray(i, i + width));
      text += map.get(code) ?? (width === 1 ? String.fromCodePoint(code) : '');
    }
    return text;
  };
}

/** The product of two transformation matrices [a b c d e f], `m` applied first. */
function multiply(m, n) {
  return [
    m[0] * n[0] + m[1] * n[2], m[0] * n[1] + m[1] * n[3],
    m[2] * n[0] + m[3] * n[2], m[2] * n[1] + m[3] * n[3],
    m[4] * n[0] + m[5] * n[2] + n[4], m[4] * n[1] + m[5] * n[3] + n[5],
  ];
}

const IDENTITY = [1, 0, 0, 1, 0, 0];
const PATH_CONSTRUCTION = new Set(['m', 'l', 'c', 'v', 'y', 'h', 're']);
const PATH_PAINTING = new Set(['S', 's', 'f', 'F', 'f*', 'B', 'B*', 'b', 'b*', 'n']);

/**
 * The graphics and text state a page's operators build up, followed far enough to say where and
 * how text, paths and images are drawn: the transformation matrix (q, Q, cm), the fill and
 * stroke colours (rg, g, RG, G), the line width, cap and join (w, J, j), the graphics state
 * dictionary (gs), the font and rendering mode (Tf, Tr) and the text line matrix (BT, Td, TD,
 * Tm, T*, TL, ', "). Glyph advance is not followed, so a position is where the current line
 * starts, which is where a run that opens a line (as every run the tests check does) starts.
 */
class GraphicsState {
  constructor() {
    this.stack = [];
    this.ctm = IDENTITY;
    this.fill = [0, 0, 0];
    this.stroke = [0, 0, 0];
    this.lineWidth = 1;
    this.cap = 0;
    this.join = 0;
    this.extGState = null;
    this.render = 0;
    this.font = null;
    this.size = null;
    this.line = IDENTITY;
    this.leading = 0;
  }

  apply(op) {
    STATE_OPERATORS[op.op]?.(this, op.operands);
  }

  save() {
    const { ctm, fill, stroke, lineWidth, cap, join, extGState, render, font, size, leading } = this;
    this.stack.push({ ctm, fill, stroke, lineWidth, cap, join, extGState, render, font, size, leading });
  }

  restore() {
    Object.assign(this, this.stack.pop() ?? {});
  }

  nextLine(tx, ty) {
    this.line = multiply([1, 0, 0, 1, tx, ty], this.line);
  }

  /** The current text line's matrix in user space: its rotation and scale, and where it starts. */
  textMatrix() {
    return multiply(this.line, this.ctm);
  }
}

const STATE_OPERATORS = {
  q: (s) => s.save(),
  Q: (s) => s.restore(),
  cm: (s, m) => { s.ctm = multiply(m, s.ctm); },
  rg: (s, rgb) => { s.fill = rgb; },
  g: (s, [gray]) => { s.fill = [gray, gray, gray]; },
  RG: (s, rgb) => { s.stroke = rgb; },
  G: (s, [gray]) => { s.stroke = [gray, gray, gray]; },
  w: (s, [width]) => { s.lineWidth = width; },
  J: (s, [cap]) => { s.cap = cap; },
  j: (s, [join]) => { s.join = join; },
  gs: (s, [name]) => { s.extGState = plain(name); },
  Tf: (s, [font, size]) => { s.font = plain(font); s.size = size; },
  Tr: (s, [mode]) => { s.render = mode; },
  BT: (s) => { s.line = IDENTITY; },
  Td: (s, [tx, ty]) => s.nextLine(tx, ty),
  TD: (s, [tx, ty]) => { s.leading = -ty; s.nextLine(tx, ty); },
  Tm: (s, m) => { s.line = m.slice(0, 6); },
  TL: (s, [leading]) => { s.leading = leading; },
  'T*': (s) => s.nextLine(0, -s.leading),
  "'": (s) => s.nextLine(0, -s.leading),
  '"': (s) => s.nextLine(0, -s.leading),
};

// ------------------------------------------------------------------ the document

/**
 * Parses a saved PDF into a structured model.
 *
 * @param {Buffer} bytes
 */
function pdfModel(bytes) {
  const pdf = readPdf(bytes);
  const parsed = new Map();

  /** The parsed value of indirect object `num`. */
  function object(num) {
    if (!parsed.has(num)) {
      const entry = pdf.objects.get(num);
      parsed.set(num, entry ? parseValue(new Lexer(entry.dict)) : null);
    }
    return parsed.get(num);
  }

  const resolve = (value) => (value instanceof Ref ? object(value.num) : value);
  const get = (dict, key) => (dict instanceof Dict ? resolve(dict.raw(key)) : undefined);
  const trailer = parseValue(new Lexer(pdf.trailer));
  const catalog = get(trailer, 'Root');

  /** A stream's data decoded as far as Flate goes, given a reference to it. */
  function streamData(value) {
    const entry = value instanceof Ref ? pdf.objects.get(value.num) : null;
    return entry?.raw ? decodeStream(entry) : Buffer.alloc(0);
  }

  /** The pages in order, each as { num, dict, parents }. */
  function pages() {
    const list = [];
    const visit = (ref, parents, seen) => {
      if (!(ref instanceof Ref) || seen.has(ref.num)) return;
      seen.add(ref.num);
      const node = object(ref.num);
      if (plain(get(node, 'Type')) === 'Pages') {
        for (const kid of get(node, 'Kids') ?? []) visit(kid, [node, ...parents], seen);
      } else {
        list.push({ num: ref.num, dict: node, parents });
      }
    };
    visit(catalog.raw('Pages'), [], new Set());
    return list;
  }

  /** An attribute of the page at (1-based) `pageNum`, inherited from the page tree if need be. */
  function inherited(pageNum, key) {
    const page = pages()[pageNum - 1];
    for (const node of [page.dict, ...page.parents]) {
      if (node.has(key)) return get(node, key);
    }
    return undefined;
  }

  /** The decoded, concatenated content streams of a page. */
  function pageContent(pageNum) {
    const contents = pages()[pageNum - 1].dict.raw('Contents');
    const refs = Array.isArray(resolve(contents)) ? resolve(contents) : [contents];
    return Buffer.concat(refs.flatMap((ref) => [streamData(ref), Buffer.from('\n')]));
  }

  /** The reference a `Do` operator names in `resources`' /XObject dictionary, if any. */
  function xobjectRef(resources, op) {
    const xobjects = get(resources, 'XObject');
    return xobjects instanceof Dict ? xobjects.raw(plain(op.operands[0])) : undefined;
  }

  /**
   * Walks the operators a page draws, following form XObjects into their own content, and calls
   * `visit(op, resources, depth)` for each.
   */
  function walkOperations(pageNum, visit) {
    const descend = (ops, resources, depth) => {
      for (const op of ops) {
        visit(op, resources, depth);
        if (op.op !== 'Do' || depth > 8) continue;
        const ref = xobjectRef(resources, op);
        const xobject = resolve(ref);
        if (plain(get(xobject, 'Subtype')) !== 'Form') continue;
        descend(contentOperations(streamData(ref)), get(xobject, 'Resources') ?? resources, depth + 1);
      }
    };
    descend(contentOperations(pageContent(pageNum)), inherited(pageNum, 'Resources'), 0);
  }

  /** The page's operators, forms included, as { op, operands, depth }. */
  function pageOperations(pageNum) {
    const ops = [];
    walkOperations(pageNum, (op, _resources, depth) => ops.push({ ...op, depth }));
    return ops;
  }

  const decoders = new WeakMap();

  /** How a font's string bytes become text: through its ToUnicode CMap, or as Latin-1. */
  function decoderFor(font) {
    if (!(font instanceof Dict)) return latin1;
    if (!decoders.has(font)) {
      const toUnicode = font.raw('ToUnicode');
      decoders.set(font, toUnicode instanceof Ref ? cmapDecoder(parseToUnicode(streamData(toUnicode))) : latin1);
    }
    return decoders.get(font);
  }

  /** Every string a page shows, forms included, one entry per text-showing operator. */
  function pageStrings(pageNum) {
    const strings = [];
    let decode = latin1;
    walkOperations(pageNum, (op, resources) => {
      if (op.op === 'Tf') decode = decoderFor(get(get(resources, 'Font'), plain(op.operands[0])));
      const text = shownText(op, decode);
      if (text !== null) strings.push(text);
    });
    return strings;
  }

  /** What a page shows, with all whitespace removed (redaction splits runs into single glyphs). */
  const compactText = (pageNum) => pageStrings(pageNum).join('').replace(/\s+/g, '');

  /** The image XObjects a page (or a form it draws) draws with Do, as { name, dict }. */
  function drawnImages(pageNum) {
    const images = [];
    walkOperations(pageNum, (op, resources) => {
      if (op.op !== 'Do') return;
      const ref = xobjectRef(resources, op);
      const xobject = resolve(ref);
      if (plain(get(xobject, 'Subtype')) === 'Image') images.push({ name: plain(op.operands[0]), dict: xobject, ref });
    });
    return images;
  }

  /**
   * Where the page itself (not a form it draws) draws each image: { name, ctm }, the current
   * transformation matrix at its `Do`, which maps the unit square onto the image's place.
   */
  function imagePlacements(pageNum) {
    const resources = inherited(pageNum, 'Resources');
    const placements = [];
    const state = new GraphicsState();
    for (const op of contentOperations(pageContent(pageNum))) {
      state.apply(op);
      if (op.op === 'Do' && plain(get(resolve(xobjectRef(resources, op)), 'Subtype')) === 'Image') {
        placements.push({ name: plain(op.operands[0]), ctm: state.ctm });
      }
    }
    return placements;
  }

  /**
   * The runs of text the page itself shows: { text, font, size, baseFont, x, y, matrix, fill,
   * render, extGState }, the text decoded through the font's ToUnicode CMap when it has one, the
   * font being the resource name the last `Tf` selected, baseFont the font dictionary's /BaseFont,
   * (x, y) where the run's line starts in user space, matrix the line's text matrix there, fill
   * the fill colour's components, render the text rendering mode (3 is invisible) and extGState
   * the graphics state dictionary the last `gs` selected.
   */
  function textRuns(pageNum) {
    const resources = inherited(pageNum, 'Resources');
    const fonts = get(resources, 'Font');
    const runs = [];
    const state = new GraphicsState();
    for (const op of contentOperations(pageContent(pageNum))) {
      state.apply(op);
      const text = shownText(op, decoderFor(get(fonts, state.font)));
      if (text === null) continue;
      const { font, size, fill, render } = state;
      const matrix = state.textMatrix();
      const baseFont = plain(get(get(fonts, font), 'BaseFont'));
      const extGState = get(get(resources, 'ExtGState'), state.extGState);
      runs.push({ text, font, size, baseFont, x: matrix[4], y: matrix[5], matrix, fill, render, extGState });
    }
    return runs;
  }

  /**
   * The paths the page itself paints, in order: { paint, segments, fill, stroke, lineWidth, cap,
   * join, ctm, extGState }, paint being the painting operator (f, S, B…), segments the operators
   * that built the path, and the rest the graphics state it was painted in.
   */
  function paintedPaths(pageNum) {
    const resources = inherited(pageNum, 'Resources');
    const paths = [];
    const state = new GraphicsState();
    let segments = [];
    for (const op of contentOperations(pageContent(pageNum))) {
      state.apply(op);
      if (PATH_CONSTRUCTION.has(op.op)) segments.push(op);
      if (!PATH_PAINTING.has(op.op)) continue;
      if (op.op !== 'n') {
        const { fill, stroke, lineWidth, cap, join, ctm } = state;
        const extGState = get(get(resources, 'ExtGState'), state.extGState);
        paths.push({ paint: op.op, segments, fill, stroke, lineWidth, cap, join, ctm, extGState });
      }
      segments = [];
    }
    return paths;
  }

  /** The page's annotations, each as { subtype, dict }. */
  function annotations(pageNum) {
    return (inherited(pageNum, 'Annots') ?? []).map(resolve).filter(Boolean)
      .map((dict) => ({ subtype: plain(get(dict, 'Subtype')), dict }));
  }

  /** A field attribute, looked up through the field's parents as the form inherits it. */
  function fieldAttribute(chain, key) {
    for (const node of chain) {
      if (node.has(key)) return get(node, key);
    }
    return undefined;
  }

  /**
   * The terminal form fields: full name, type (/FT), flags (/Ff), value (/V), options (/Opt), the
   * number of widgets, and the field dictionary.
   */
  function fields() {
    const out = [];
    const visit = (ref, parentNames, chain, seen) => {
      if (!(ref instanceof Ref) || seen.has(ref.num)) return;
      seen.add(ref.num);
      const node = object(ref.num);
      const partial = plain(get(node, 'T'));
      const names = partial === undefined ? parentNames : [...parentNames, partial];
      const kids = (get(node, 'Kids') ?? []).filter((k) => k instanceof Ref && object(k.num).has('T'));
      const nodeChain = [node, ...chain];
      if (kids.length > 0) {
        kids.forEach((kid) => visit(kid, names, nodeChain, seen));
        return;
      }
      out.push({
        name: names.join('.'),
        type: plain(fieldAttribute(nodeChain, 'FT')),
        flags: fieldAttribute(nodeChain, 'Ff') ?? 0,
        value: plain(fieldAttribute(nodeChain, 'V')),
        options: plain(fieldAttribute(nodeChain, 'Opt')),
        widgets: (get(node, 'Kids') ?? [ref]).length,
        dict: node,
      });
    };
    for (const field of get(get(catalog, 'AcroForm'), 'Fields') ?? []) visit(field, [], [], new Set());
    return out;
  }

  /** The (name, value) pairs of a name tree, leaves and intermediate nodes alike. */
  function nameTree(root) {
    const entries = [];
    const visit = (node, depth) => {
      if (!node || depth > 16) return;
      const names = get(node, 'Names') ?? [];
      for (let i = 0; i + 1 < names.length; i += 2) entries.push([plain(names[i]), resolve(names[i + 1])]);
      for (const kid of get(node, 'Kids') ?? []) visit(resolve(kid), depth + 1);
    };
    visit(root, 0);
    return entries;
  }

  /** The source text of a JavaScript action: its /JS string or stream. */
  function scriptSource(action) {
    const js = action?.raw('JS');
    if (js instanceof Ref && pdf.objects.get(js.num)?.raw) return streamData(js).toString('latin1');
    return plain(resolve(js)) ?? '';
  }

  /** The document-level scripts (the /Names /JavaScript tree), as { name, source }. */
  function documentScripts() {
    return nameTree(get(get(catalog, 'Names'), 'JavaScript')).map(([name, action]) => ({ name, source: scriptSource(action) }));
  }

  /** The names of the document's embedded files (the /Names /EmbeddedFiles tree). */
  function embeddedFiles() {
    return nameTree(get(get(catalog, 'Names'), 'EmbeddedFiles')).map(([name]) => name);
  }

  /** The /Info entries as text. */
  function info() {
    const dict = get(trailer, 'Info');
    return dict ? Object.fromEntries(dict.keys().map((k) => [k, plain(get(dict, k))])) : {};
  }

  return {
    pdf,
    catalog,
    trailer,
    object,
    resolve,
    get,
    plain,
    streamData,
    pages,
    pageCount: () => pages().length,
    inherited,
    pageContent,
    pageOperations,
    pageStrings,
    compactText,
    drawnImages,
    imagePlacements,
    textRuns,
    paintedPaths,
    annotations,
    fields,
    documentScripts,
    embeddedFiles,
    info,
  };
}

module.exports = { pdfModel, contentOperations, shownText, plain, Name, Str, Ref, Dict };
