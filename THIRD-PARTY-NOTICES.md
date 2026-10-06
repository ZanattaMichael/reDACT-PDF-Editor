# Third-party notices

reDACT PDF Editor is licensed under GPL-3.0 (see `LICENSE`). It includes or depends on the
following third-party components, each under its own licence.

## Bundled with the native host

| Component | Used for | Licence |
|---|---|---|
| [PDFtoImage](https://github.com/sungaila/PDFtoImage) | Page rendering (previews, OCR input) | MIT |
| [PDFium](https://pdfium.googlesource.com/pdfium/) (binaries from [pdfium-binaries](https://github.com/bblanchon/pdfium-binaries), via PDFtoImage) | Rendering engine | BSD-3-Clause / Apache-2.0 |
| [SkiaSharp](https://github.com/mono/SkiaSharp) (via PDFtoImage) | Image encoding and decoding | MIT |
| [BouncyCastle.Cryptography](https://github.com/bcgit/bc-csharp) | All cryptography: PDF encryption, CMS signatures, PKCS#12 files, certificate generation | MIT |
| .NET runtime (self-contained builds) | Runtime | MIT |

## Embedded data files

These are in `src/PdfEditor.Core/Pdf/Fonts/Resources/` and are distributed unmodified.

- **Adobe Core 14 font metrics** (`Core14/*.afm`): © Adobe. Redistributed under the terms in
  `Core14/MustRead.html`, which the terms require to be shipped alongside the files.
- **Adobe Glyph List and ZapfDingbats glyph list** (`glyphlist.txt`, `zapfdingbats.txt`), from
  [adobe-type-tools/agl-aglfn](https://github.com/adobe-type-tools/agl-aglfn): © Adobe,
  BSD-3-Clause. The licence text is in each file's header.

## No longer used

Up to and including 2.x, reDACT used [iText Core](https://github.com/itext/itext-dotnet) (AGPL-3.0),
and reached BouncyCastle through iText's adapter (`itext.bouncy-castle-adapter`, AGPL-3.0). From
3.0, iText and its adapter are not dependencies; BouncyCastle is used directly. See
`docs/RELEASE_NOTES_3.0.0.md`.
