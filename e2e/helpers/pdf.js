'use strict';

/**
 * Builds a minimal, uncompressed, single- or multi-page PDF fixture with
 * absolutely positioned Helvetica text. Deterministic and dependency-free —
 * default page size is A4 (595 x 842 pt), matching the .NET test fixtures.
 *
 * Pass `{ mediaBox: [llx, lly, urx, ury] }` to give the pages a non-(0,0) origin, and/or
 * `{ rotate: 90|180|270 }` to rotate them — both regression-test the coordinate mapping
 * used for redaction.
 */
function buildPdf(pages, { mediaBox = [0, 0, 595, 842], cropBox = null, rotate = 0 } = {}) {
  const objects = [];
  const pageObjectNumbers = pages.map((_, i) => 4 + i * 2);
  const box = mediaBox.join(' ');
  const rotateEntry = rotate ? ` /Rotate ${rotate}` : '';
  const cropEntry = cropBox ? ` /CropBox [${cropBox.join(' ')}]` : '';

  const kids = pageObjectNumbers.map((n) => `${n} 0 R`).join(' ');
  objects.push('<< /Type /Catalog /Pages 2 0 R >>'); // 1
  objects.push(`<< /Type /Pages /Kids [${kids}] /Count ${pages.length} >>`); // 2
  objects.push('<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>'); // 3

  for (const lines of pages) {
    const content = lines
      .map(({ text, x, y, size = 14 }) =>
        `BT /F1 ${size} Tf ${x} ${y} Td (${text.replace(/([\\()])/g, '\\$1')}) Tj ET`)
      .join('\n');
    objects.push(
      `<< /Type /Page /Parent 2 0 R /MediaBox [${box}]${cropEntry}${rotateEntry} ` +
      `/Resources << /Font << /F1 3 0 R >> >> /Contents ${4 + objects.length - 2} 0 R >>`);
    objects.push(`<< /Length ${content.length} >>\nstream\n${content}\nendstream`);
  }

  let body = '%PDF-1.4\n';
  const offsets = [0];
  objects.forEach((obj, i) => {
    offsets.push(body.length);
    body += `${i + 1} 0 obj\n${obj}\nendobj\n`;
  });
  const xrefStart = body.length;
  body += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n`;
  for (let i = 1; i <= objects.length; i++) {
    body += `${String(offsets[i]).padStart(10, '0')} 00000 n \n`;
  }
  body += `trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${xrefStart}\n%%EOF\n`;
  return Buffer.from(body, 'latin1');
}

/**
 * Builds a page that mimics how Chrome / Skia print-to-PDF (Google Docs "Download as PDF")
 * structures content: a top-level scale + Y-flip matrix applied *outside* any q/Q, so it is never
 * restored and is still active at the end of the content stream. The text is drawn under that
 * matrix (via a text matrix) so it renders upright at a normal absolute position — but anything
 * naively appended to the page inherits the leftover matrix. Regression fixture for redaction/edit
 * landing in the wrong place on such documents. MediaBox is [0 0 400 600]; the word renders around
 * absolute (50, 300).
 *
 * `control` is a second word drawn well clear of the first (around absolute (50, 150)). It is not
 * decoration: without a word that has to survive, a test can only assert where the black box
 * landed, and a build that redacts nothing at all sails past it.
 */
function buildLeftoverCtmPdf(word = 'SECRET', control = 'KEEPME') {
  const esc = (s) => s.replace(/([\\()])/g, '\\$1');
  const content =
    '0.5 0 0 -0.5 0 600 cm\n' +   // unbalanced top-level transform (never restored)
    'q\n' +
    '0 0 800 1200 re W n\n' +     // clip to the page in the scaled space
    `BT\n/F1 48 Tf\n1 0 0 -1 100 600 Tm\n(${esc(word)}) Tj\nET\n` +
    `BT\n/F1 48 Tf\n1 0 0 -1 100 900 Tm\n(${esc(control)}) Tj\nET\n` +
    'Q\n';

  const objects = [
    '<< /Type /Catalog /Pages 2 0 R >>',
    '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 600] ' +
      '/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>',
    `<< /Length ${content.length} >>\nstream\n${content}endstream`,
    '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>',
  ];

  let body = '%PDF-1.4\n';
  const offsets = [0];
  objects.forEach((obj, i) => {
    offsets.push(body.length);
    body += `${i + 1} 0 obj\n${obj}\nendobj\n`;
  });
  const xrefStart = body.length;
  body += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n`;
  for (let i = 1; i <= objects.length; i++) {
    body += `${String(offsets[i]).padStart(10, '0')} 00000 n \n`;
  }
  body += `trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${xrefStart}\n%%EOF\n`;
  return Buffer.from(body, 'latin1');
}

/**
 * Serialises a 1-indexed list of object bodies into a valid PDF with an xref table.
 * `trailerEntries` is added to the trailer dictionary as written (an /Encrypt reference, an /ID).
 */
function assemble(objects, trailerEntries = '') {
  let body = '%PDF-1.4\n';
  const offsets = [0];
  objects.forEach((obj, i) => {
    offsets.push(body.length);
    body += `${i + 1} 0 obj\n${obj}\nendobj\n`;
  });
  const xrefStart = body.length;
  body += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n`;
  for (let i = 1; i <= objects.length; i++) {
    body += `${String(offsets[i]).padStart(10, '0')} 00000 n \n`;
  }
  body += `trailer\n<< /Size ${objects.length + 1} /Root 1 0 R ${trailerEntries}>>\n`
    + `startxref\n${xrefStart}\n%%EOF\n`;
  return Buffer.from(body, 'latin1');
}

/**
 * A single page carrying a solid-colour, uncompressed /DeviceRGB image XObject drawn into
 * `rect` ([llx, lly, urx, ury] in user space), plus optional text lines drawn *over* it.
 *
 * Fixture for every operation that has to leave a picture intact. A solid colour is the point:
 * "the image survived" then reduces to "these pixels are still that exact colour", which no
 * amount of status-line optimism can fake — the bugs this catches (a black rectangle punched
 * through the image when text over it was edited; a redaction that scrubbed the whole XObject)
 * both show up as the colour going away.
 */
function buildImagePdf({ rect = [72, 500, 400, 700], rgb = [0, 102, 204], text = [] } = {}) {
  const [llx, lly, urx, ury] = rect;
  // 2x2 pixels of one colour: big enough to be a real image, small enough to inline.
  const px = String.fromCharCode(...rgb);
  const raw = px + px + px + px;
  const lines = text
    .map(({ text: t, x, y, size = 14 }) =>
      `BT /F1 ${size} Tf ${x} ${y} Td (${t.replace(/([\\()])/g, '\\$1')}) Tj ET`)
    .join('\n');
  const content =
    `q ${urx - llx} 0 0 ${ury - lly} ${llx} ${lly} cm /Im0 Do Q\n${lines}`;

  return assemble([
    `<< /Type /Catalog /Pages 2 0 R >>`,
    `<< /Type /Pages /Kids [3 0 R] /Count 1 >>`,
    `<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R ` +
      `/Resources << /Font << /F1 5 0 R >> /XObject << /Im0 6 0 R >> >> >>`,
    `<< /Length ${content.length} >>\nstream\n${content}\nendstream`,
    `<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>`,
    `<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB ` +
      `/BitsPerComponent 8 /Length ${raw.length} >>\nstream\n${raw}\nendstream`,
  ]);
}

/** A single-page document with one AcroForm text field. */
function buildFormPdf(fieldName = 'fullName', value = '') {
  return assemble([
    `<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /DA (/Helv 0 Tf 0 g) /NeedAppearances true >> >>`,
    `<< /Type /Pages /Kids [3 0 R] /Count 1 >>`,
    `<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Annots [5 0 R] /Resources << /Font << /Helv 6 0 R >> >> >>`,
    `<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>`,
    `<< /FT /Tx /T (${fieldName}) /V (${value}) /Type /Annot /Subtype /Widget ` +
      `/Rect [100 700 300 724] /P 3 0 R /DA (/Helv 12 Tf 0 g) >>`,
    `<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>`,
  ]);
}

/**
 * A single-page AcroForm with two number text fields ("a", "b"), an empty "total" text field, and
 * a push-button field whose click action runs a calculation script summing "a" and "b" into
 * "total". Regression fixture for the viewer's in-panel button-script simulation (#18): the
 * button carries real PDF JavaScript, and clicking its "Run" control in the Forms panel should
 * update "total" without a round trip to Acrobat/Chrome.
 */
function buildFormWithButtonScriptPdf(script) {
  const calc = script ??
    'this.getField("total").value = this.getField("a").value + this.getField("b").value;';
  return assemble([
    `<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R 6 0 R 7 0 R 8 0 R] ` +
      `/DA (/Helv 0 Tf 0 g) /NeedAppearances true >> >>`, // 1
    `<< /Type /Pages /Kids [3 0 R] /Count 1 >>`, // 2
    `<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Annots [5 0 R 6 0 R 7 0 R 8 0 R] ` +
      `/Resources << /Font << /Helv 4 0 R >> >> >>`, // 3
    `<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>`, // 4
    `<< /FT /Tx /T (a) /V (2) /Type /Annot /Subtype /Widget ` +
      `/Rect [100 700 180 724] /P 3 0 R /DA (/Helv 12 Tf 0 g) >>`, // 5
    `<< /FT /Tx /T (b) /V (3) /Type /Annot /Subtype /Widget ` +
      `/Rect [200 700 280 724] /P 3 0 R /DA (/Helv 12 Tf 0 g) >>`, // 6
    `<< /FT /Tx /T (total) /V () /Type /Annot /Subtype /Widget ` +
      `/Rect [300 700 380 724] /P 3 0 R /DA (/Helv 12 Tf 0 g) >>`, // 7
    `<< /FT /Btn /Ff 65536 /T (calc) /Type /Annot /Subtype /Widget /MK << /CA (Calculate) >> ` +
      `/Rect [400 700 480 724] /P 3 0 R /DA (/Helv 12 Tf 0 g) /A << /S /JavaScript /JS (${calc}) >> >>`, // 8
  ]);
}

/** A single-page document that runs JavaScript on open (document-level active content). */
function buildJavaScriptPdf(script = "app.alert('hello from the pdf');") {
  const content = 'BT /F1 18 Tf 72 700 Td (Has script) Tj ET';
  return assemble([
    `<< /Type /Catalog /Pages 2 0 R /OpenAction << /S /JavaScript /JS (${script}) >> >>`,
    `<< /Type /Pages /Kids [3 0 R] /Count 1 >>`,
    `<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R ` +
      `/Resources << /Font << /F1 5 0 R >> >> >>`,
    `<< /Length ${content.length} >>\nstream\n${content}\nendstream`,
    `<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>`,
  ]);
}

/** A single-page document with a link annotation pointing at the given URL. */
function buildLinkPdf(url = 'https://github.com/example/repo') {
  return assemble([
    `<< /Type /Catalog /Pages 2 0 R >>`,
    `<< /Type /Pages /Kids [3 0 R] /Count 1 >>`,
    `<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Annots [4 0 R] >>`,
    `<< /Type /Annot /Subtype /Link /Rect [72 700 272 720] /Border [0 0 1] ` +
      `/A << /S /URI /URI (${url}) >> >>`,
  ]);
}

/**
 * A single page carrying one URI link annotation per URL, stacked down the page. Fixture for the
 * link-heavy documents the async link pipeline (#19) has to stay responsive on, and for telling a
 * stale document's overlay apart from the current one by hotspot count.
 */
function buildMultiLinkPdf(urls) {
  const annotStart = 4; // objects 1..3 are the catalog, page tree and page
  const refs = urls.map((_, i) => `${annotStart + i} 0 R`).join(' ');
  return assemble([
    `<< /Type /Catalog /Pages 2 0 R >>`,
    `<< /Type /Pages /Kids [3 0 R] /Count 1 >>`,
    `<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Annots [${refs}] >>`,
    ...urls.map((url, i) => {
      const y = 760 - i * 30;
      return `<< /Type /Annot /Subtype /Link /Rect [72 ${y} 372 ${y + 20}] /Border [0 0 1] ` +
        `/A << /S /URI /URI (${url}) >> >>`;
    }),
  ]);
}

/**
 * A two-page document with a link annotation on the SECOND page. Regression fixture for the
 * on-page link overlay: the hotspot must still be drawn once you scroll down to a later page
 * (even when that page's image comes from the render cache).
 */
function buildLinkOnPage2Pdf(url = 'https://github.com/example/repo') {
  return assemble([
    `<< /Type /Catalog /Pages 2 0 R >>`,
    `<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>`,
    `<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] >>`,
    `<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Annots [5 0 R] >>`,
    `<< /Type /Annot /Subtype /Link /Rect [72 700 272 720] /Border [0 0 1] ` +
      `/A << /S /URI /URI (${url}) >> >>`,
  ]);
}

/**
 * A single page with a line of TEXT and a URI link annotation laid directly over that text.
 * Regression fixture for layer stacking: the (invisible, selectable) text layer must not cover
 * the link hotspot — the hotspot has to stay hoverable/clickable even though text sits under it.
 */
function buildLinkOverTextPdf(url = 'https://github.com/example/repo') {
  const content = 'BT /F1 14 Tf 72 704 Td (Visit our website now for details) Tj ET';
  return assemble([
    `<< /Type /Catalog /Pages 2 0 R >>`,
    `<< /Type /Pages /Kids [3 0 R] /Count 1 >>`,
    `<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R ` +
      `/Resources << /Font << /F1 5 0 R >> >> /Annots [6 0 R] >>`,
    `<< /Length ${content.length} >>\nstream\n${content}\nendstream`,
    `<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>`,
    `<< /Type /Annot /Subtype /Link /Rect [72 700 320 720] /Border [0 0 1] ` +
      `/A << /S /URI /URI (${url}) >> >>`,
  ]);
}

/** A page with a JavaScript-action link annotation (like Salesforce "Close Window"). */
function buildJsLinkPdf(script = 'window.close();') {
  const content = 'BT /F1 14 Tf 72 704 Td (Close Window) Tj ET';
  return assemble([
    `<< /Type /Catalog /Pages 2 0 R >>`,
    `<< /Type /Pages /Kids [3 0 R] /Count 1 >>`,
    `<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R ` +
      `/Resources << /Font << /F1 5 0 R >> >> /Annots [6 0 R] >>`,
    `<< /Length ${content.length} >>\nstream\n${content}\nendstream`,
    `<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>`,
    `<< /Type /Annot /Subtype /Link /Rect [72 700 200 718] /A << /S /JavaScript /JS (${script}) >> >>`,
  ]);
}

// ------------------------------------------------------- fixtures for the 3.0 engine suite

const escapeText = (t) => t.replace(/([\\()])/g, String.raw`\$1`);

/** A Helvetica text-showing block for each `{ text, x, y, size }` line. */
const showText = (lines, font = 'F1') => lines
  .map(({ text, x, y, size = 14 }) => `BT /${font} ${size} Tf ${x} ${y} Td (${escapeText(text)}) Tj ET`)
  .join('\n');

/** A stream object body holding `content` (a string) under the given extra dictionary entries. */
const streamObject = (entries, content) =>
  `<< ${entries} /Length ${content.length} >>\nstream\n${content}\nendstream`;

/**
 * One page that draws a form XObject, which in turn draws a second form showing `secret`; the
 * page also shows `control` directly, well below them. The inner form's text lands at about
 * (92, 640) in page space, inside the 300x100 outer form placed at (72, 600).
 *
 * The fixture for redaction through nested forms: the editor rewrites each form on a copy, and
 * the originals, which still hold the text, must not be left in the saved file.
 */
function buildNestedFormPdf({ secret, control }) {
  return assemble([
    '<< /Type /Catalog /Pages 2 0 R >>',
    '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R '
      + '/Resources << /Font << /F1 5 0 R >> /XObject << /Fm0 6 0 R >> >> >>',
    streamObject('', `q 1 0 0 1 72 600 cm /Fm0 Do Q\n${showText([{ text: control, x: 72, y: 400 }])}`),
    '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>',
    streamObject('/Type /XObject /Subtype /Form /BBox [0 0 300 100] /Resources << /XObject << /Fm1 7 0 R >> >>',
      'q 1 0 0 1 10 10 cm /Fm1 Do Q'),
    streamObject('/Type /XObject /Subtype /Form /BBox [0 0 280 80] /Resources << /Font << /F1 5 0 R >> >>',
      showText([{ text: secret, x: 10, y: 30, size: 18 }])),
  ]);
}

/**
 * One page whose content stream says it is Flate-compressed but holds plain text, so it cannot
 * be decoded. iText read such a page as empty, which made "redact" silently wipe it.
 */
function buildUndecodableContentPdf(text = 'Corrupt page text') {
  return assemble([
    '<< /Type /Catalog /Pages 2 0 R >>',
    '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R '
      + '/Resources << /Font << /F1 5 0 R >> >> >>',
    streamObject('/Filter /FlateDecode', showText([{ text, x: 72, y: 700 }])),
    '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>',
  ]);
}

/**
 * One page that shows `lines` in a font it never declares: /Resources has no /Font at all. iText
 * threw NullReferenceException on such text; 3.0 reads it with a standard font's metrics.
 */
function buildUndeclaredFontPdf(lines) {
  return assemble([
    '<< /Type /Catalog /Pages 2 0 R >>',
    '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << >> >>',
    streamObject('', showText(lines)),
  ]);
}

/**
 * Three pages ("<prefix> page N") and bookmarks reaching them every way a bookmark can lead
 * somewhere, plus one that leads nowhere in the document:
 *
 *   <prefix> one            -> page 1 (explicit destination)
 *   <prefix> two            -> page 2 (GoTo action)
 *     <prefix> two point one -> page 2 (explicit destination)
 *   <prefix> three          -> page 3 (explicit destination)
 *   <prefix> site           -> a web address only (URI action)
 */
function buildBookmarkedPdf(prefix) {
  // 1 catalog, 2 page tree, 3..8 three pages and their content, 9 font, 10 outline root, 11.. items.
  const page = (n) => 3 + (n - 1) * 2;
  const pages = [1, 2, 3].flatMap((n) => [
    `<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents ${page(n) + 1} 0 R `
      + '/Resources << /Font << /F1 9 0 R >> >> >>',
    streamObject('', showText([{ text: `${prefix} page ${n}`, x: 72, y: 700 }])),
  ]);
  const item = (title, extra) => `<< /Title (${escapeText(title)}) /Parent 10 0 R ${extra} >>`;
  const childTitle = escapeText(`${prefix} two point one`);
  return assemble([
    '<< /Type /Catalog /Pages 2 0 R /Outlines 10 0 R /PageMode /UseOutlines >>',
    `<< /Type /Pages /Kids [${page(1)} 0 R ${page(2)} 0 R ${page(3)} 0 R] /Count 3 >>`,
    ...pages,
    '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>',
    '<< /Type /Outlines /First 11 0 R /Last 15 0 R /Count 4 >>',
    item(`${prefix} one`, `/Dest [${page(1)} 0 R /Fit] /Next 12 0 R`),
    item(`${prefix} two`, `/A << /S /GoTo /D [${page(2)} 0 R /Fit] >> /Prev 11 0 R /Next 14 0 R `
      + '/First 13 0 R /Last 13 0 R /Count 1'),
    `<< /Title (${childTitle}) /Parent 12 0 R `
      + `/Dest [${page(2)} 0 R /XYZ null null null] >>`,
    item(`${prefix} three`, `/Dest [${page(3)} 0 R /Fit] /Prev 12 0 R /Next 15 0 R`),
    item(`${prefix} site`, '/A << /S /URI /URI (https://example.com/) >> /Prev 14 0 R'),
  ]);
}

/**
 * One page showing `lines`, encrypted the way 2.x-era files are: `cipher` under the standard
 * security handler, an empty open password (so it opens without asking) and an owner password
 * restricting it to printing. See legacy-encryption.js.
 *
 * @param {'AES-128'|'RC4-128'} cipher
 */
function buildRestrictedPdf(cipher, lines, ownerPassword = 'owner-only') {
  const { standardSecurity, PRINT_ONLY } = require('./legacy-encryption');
  const security = standardSecurity(cipher, { ownerPassword, permissions: PRINT_ONLY });
  const content = security.encrypt(4, Buffer.from(showText(lines), 'latin1')).toString('latin1');
  return assemble([
    '<< /Type /Catalog /Pages 2 0 R >>',
    '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R '
      + '/Resources << /Font << /F1 5 0 R >> >> >>',
    streamObject('', content),
    '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>',
    security.encryptDict,
  ], `/Encrypt 6 0 R ${security.trailerId} `);
}

/**
 * One page with a 1x1 red inline image (BI … ID … EI) drawn into `rect`, plus `lines` of text.
 * Inline images live inside the content stream itself, so a redaction has to drop the operator.
 */
function buildInlineImagePdf({ rect = [72, 500, 172, 600], lines = [] } = {}) {
  const [llx, lly, urx, ury] = rect;
  const content = `q ${urx - llx} 0 0 ${ury - lly} ${llx} ${lly} cm `
    + `BI /W 1 /H 1 /CS /RGB /BPC 8 ID \xff\x00\x00 EI Q\n${showText(lines)}`;
  return assemble([
    '<< /Type /Catalog /Pages 2 0 R >>',
    '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R '
      + '/Resources << /Font << /F1 5 0 R >> >> >>',
    streamObject('', content),
    '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>',
  ]);
}

/**
 * One page carrying every kind of "hidden information" the sanitiser removes: authored /Info
 * entries (standard and custom), an XMP packet, an embedded file, a JavaScript open action, a
 * comment annotation, a bookmark, an optional-content layer, and a link that must survive.
 */
function buildHiddenInfoPdf() {
  const xmp = '<x:xmpmeta xmlns:x="adobe:ns:meta/"><dc:creator>Hidden Author</dc:creator></x:xmpmeta>';
  return assemble([
    '<< /Type /Catalog /Pages 2 0 R /Metadata 6 0 R /Names << /EmbeddedFiles << /Names [(notes.txt) 7 0 R] >> >> '
      + '/OpenAction << /S /JavaScript /JS (app.alert\(1\);) >> /Outlines 9 0 R '
      + '/OCProperties << /OCGs [11 0 R] /D << /Order [11 0 R] >> >> >>',
    '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R '
      + '/Resources << /Font << /F1 5 0 R >> >> /Annots [12 0 R 13 0 R] >>',
    streamObject('', showText([{ text: 'Shareable body text', x: 72, y: 700 }])),
    '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>',
    streamObject('/Type /Metadata /Subtype /XML', xmp),
    '<< /Type /Filespec /F (notes.txt) /EF << /F 8 0 R >> >>',
    streamObject('/Type /EmbeddedFile', 'attached secret notes'),
    '<< /Type /Outlines /First 10 0 R /Last 10 0 R /Count 1 >>',
    '<< /Title (Hidden chapter) /Parent 9 0 R /Dest [3 0 R /Fit] >>',
    '<< /Type /OCG /Name (Hidden layer) >>',
    '<< /Type /Annot /Subtype /Text /Rect [400 700 420 720] /Contents (reviewer comment) >>',
    '<< /Type /Annot /Subtype /Link /Rect [72 600 272 620] /Border [0 0 0] '
      + '/A << /S /URI /URI (https://example.com/) >> >>',
    '<< /Author (Hidden Author) /Title (Internal draft) /Subject (Secret subject) '
      + '/Keywords (alpha, beta) /Creator (Draft tool) /Department (Legal) >>',
  ], '/Info 14 0 R ');
}

/**
 * One page with an AcroForm text field holding `value` and a square comment annotation that has
 * its own appearance, for the flatten modes: forms, annotations, or everything.
 */
function buildFlattenPdf(value = 'Flattened value') {
  const square = 'q 1 0 0 RG 2 w 2 2 96 46 re S Q';
  return assemble([
    '<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /DA (/Helv 0 Tf 0 g) /NeedAppearances true >> >>',
    '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Annots [5 0 R 6 0 R] '
      + '/Resources << /Font << /Helv 4 0 R >> >> >>',
    '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>',
    `<< /FT /Tx /T (name) /V (${escapeText(value)}) /Type /Annot /Subtype /Widget `
      + '/Rect [100 700 300 724] /P 3 0 R /DA (/Helv 12 Tf 0 g) >>',
    '<< /Type /Annot /Subtype /Square /Rect [100 500 200 550] /C [1 0 0] /AP << /N 7 0 R >> >>',
    streamObject('/Type /XObject /Subtype /Form /BBox [0 0 100 50]', square),
  ]);
}

module.exports = {
  buildInlineImagePdf, buildHiddenInfoPdf, buildFlattenPdf,
  buildPdf, buildLeftoverCtmPdf, buildImagePdf, buildFormPdf, buildFormWithButtonScriptPdf,
  buildJavaScriptPdf,
  buildLinkPdf, buildJsLinkPdf, buildLinkOnPage2Pdf, buildLinkOverTextPdf, buildMultiLinkPdf,
  buildNestedFormPdf, buildUndecodableContentPdf, buildUndeclaredFontPdf, buildBookmarkedPdf,
  buildRestrictedPdf,
};
