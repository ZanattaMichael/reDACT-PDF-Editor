# reDACT PDF Editor 3.0.0

3.0 replaces the PDF library under every edit. Through 2.x, reDACT read and wrote PDFs with
iText, which is AGPL-licensed and pulled in BouncyCastle for its cryptography. 3.0 does that
work with its own engine instead, so reDACT no longer depends on either (#170, #115). Everything
you could do in 2.x still works, and everything you do produces the same result unless it is
listed under *What behaves differently* below.

## Highlights

- **No AGPL dependency.** reDACT is GPL-3.0 as before, with no AGPL component left in the
  distribution. That clears the way for anyone who could not ship or deploy reDACT because of
  iText's licence. BouncyCastle is gone as well. Signing and certificate generation now use
  .NET's built-in cryptography.
- **Your files no longer advertise the library.** iText stamped its own name and version into
  the `/Producer` field of every file it saved. Files saved by 3.0 just say `reDACT`.
- **Smaller output.** The engine writes only the objects a document still uses. An opaque PNG
  (a signature image, an inserted picture) is no longer given a soft mask that does nothing.
- **Bookmarks survive merging and re-arranging pages.** Only bookmarks that still lead to a page
  in the result are kept, so deleting a page also removes the bookmark that named it. A bookmark
  in a merged file points at its page in the merged document.
- **More awkward files work.** Text drawn in a font the page forgot to declare can now be found,
  edited and redacted. Under iText this failed with an internal error.

## What behaves differently

These are deliberate, and each one is pinned by a test or a golden recording.

- **Content that cannot be decoded is refused, not treated as empty.** If a page's content stream
  is corrupt (for example, it claims to be compressed but isn't), redacting or editing that page
  now stops with *"This PDF could not be read: … is malformed or corrupt"*. iText read such a
  page as blank. A "redaction" of that page then silently replaced everything on it with nothing,
  and the result looked like a success. The export validator flags these files (PDF030) as
  before. Rotating, merging and re-arranging still work, because they don't need to read the
  page's content.
- **Bookmarks with no destination are dropped when merging or re-arranging.** One that only runs a
  script or opens a web address is dropped too, since it no longer leads anywhere in the new
  document. Bookmarks that lead to a kept page are carried across, along with their parent
  bookmarks.
- **Password-protected output is unchanged.** It still uses AES-256 with printing allowed, and
  permission-restricted files ("printing only", "no copying") still open for editing without the
  owner password, as they did under 2.x.

## Installing / upgrading

- **The host package must be updated.** The engine lives in the native host, so the extension and
  the host must both be 3.0. The options page compares major and minor versions. With a 2.x host
  and the 3.0 extension it says *"The native host is older than this extension."* until you install
  the 3.0 host package for your platform.
- **From the Chrome Web Store:** the extension updates itself. 3.0 adds no permissions.

## For contributors

- The engine is in `src/PdfEditor.Core/Pdf/`: parser and cross-reference repair, filters, the
  standard security handler, a full writer and an incremental writer (used for signing), the
  content-stream interpreter behind search, text extraction and redaction, fonts, images and
  AcroForm.
- `PdfiumCrossCheckTests` checks the engine against PDFium, which shares no code with it. PDFium's
  ink for each glyph must fall inside the box the engine reports, and every kind of file the
  writer produces must render in PDFium.
- `DependencyLicenceGuardTests` fails the build if iText or BouncyCastle is referenced again.
- The release-candidate workflow now honours a version raised in `extension/manifest.json`
  (`scripts/next-version.sh`). This release is the first to use it: RCs are numbered 3.0.0
  until v3.0.0 is tagged, and patch numbering resumes from there.

## Notes

- Firefox is still not covered. It uses a different native-messaging manifest format.
- If you find something 2.x did that 3.0 does not, please file it with the
  *Backend capability gap* issue template.
