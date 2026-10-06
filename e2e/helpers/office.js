'use strict';

/**
 * A Word document for the import tests: the smallest valid .docx (content types, package
 * relationships and a document part, zipped), holding two paragraphs, "Quarterly Word Report" and
 * "Second paragraph here". Written by hand with Python's zipfile, not by any Office program, so
 * the suite needs no tooling to build it; LibreOffice converts it like any other .docx.
 */
const WORD_DOCX = Buffer.from(
  'UEsDBBQAAAAIAAAAIVzJTxqw6wAAAK4BAAATAAAAW0NvbnRlbnRfVHlwZXNdLnhtbH1QvU7DMBDeeQrLK4odGBBCSTrwMwJD' +
  'eYCTfUks7LPlc0v79jht6YAK4933q69b7YIXW8zsIvXyRrVSIJloHU29/Fi/NPdScAGy4CNhL/fIcjVcdet9QhZVTNzLuZT0' +
  'oDWbGQOwigmpImPMAUo986QTmE+YUN+27Z02kQpSacriIYfuCUfY+CKed/V9LJLRsxSPR+KS1UtIyTsDpeJ6S/ZXSnNKUFV5' +
  '4PDsEl9XgtQXExbk74CT7q0uk51F8Q65vEKoLP0Vs9U2mk2oSvW/zYWecRydwbN+cUs5GmSukwevzkgARz/99WHu4RtQSwME' +
  'FAAAAAgAAAAhXLmBRHGwAAAAKgEAAAsAAABfcmVscy8ucmVsc43POw7CMAwG4J1TRN5pWgaEUJMuCKkrKgeIEjeNaB5KwqO3' +
  'JwMDIAZG278/y233sDO5YUzGOwZNVQNBJ70yTjM4D8f1DkjKwikxe4cMFkzQ8VV7wlnkspMmExIpiEsMppzDntIkJ7QiVT6g' +
  'K5PRRytyKaOmQciL0Eg3db2l8d0A/mGSXjGIvWqADEvAf2w/jkbiwcurRZd/nPhKFFlEjZnB3UdF1atdFRYob+nHi/wJUEsD' +
  'BBQAAAAIAAAAIVye5kwgvQAAABUBAAARAAAAd29yZC9kb2N1bWVudC54bWxtj81qwzAQhO95CqF7IieHUozt3HLPT+l5I21s' +
  'g7UrVkpcv32lQAiUXr5lGWaGafY/flIPlDgytXq7qbRCsuxG6lv9dTmsP7WKCcjBxIStXjDqfbdq5tqxvXukpHICxXpu9ZBS' +
  'qI2JdkAPccMBKWs3Fg8pv9KbmcUFYYsx5gI/mV1VfRgPI+kuR17ZLeWGAilI3fEOklCmRX1nszphYEmNKVKhPBn+us5omZwK' +
  'INALhEENKPiPy7xKzXtQ9wtQSwECFAMUAAAACAAAACFcyU8asOsAAACuAQAAEwAAAAAAAAAAAAAAgAEAAAAAW0NvbnRlbnRf' +
  'VHlwZXNdLnhtbFBLAQIUAxQAAAAIAAAAIVy5gURxsAAAACoBAAALAAAAAAAAAAAAAACAARwBAABfcmVscy8ucmVsc1BLAQIU' +
  'AxQAAAAIAAAAIVye5kwgvQAAABUBAAARAAAAAAAAAAAAAACAAfUBAAB3b3JkL2RvY3VtZW50LnhtbFBLBQYAAAAAAwADALkA' +
  'AADhAgAAAAA=',
  'base64');

module.exports = { WORD_DOCX };
