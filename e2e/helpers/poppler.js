'use strict';

/**
 * Poppler as a second opinion on encrypted files: it decrypts every standard security handler,
 * shares no code with the engine, and reads a document the way a desktop reader does. Needs
 * poppler-utils (pdftotext, pdfinfo) installed, as CI's e2e job does.
 */

const { spawnSync } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');
const { systemBinary, TOOL_DIRS } = require('./system-binary');

/** Writes `bytes` to a fresh file under `workDir` and runs `tool` on it with `passwords`. */
function runOn(tool, bytes, workDir, { user, owner } = {}, extra = []) {
  const file = path.join(fs.mkdtempSync(path.join(workDir, 'poppler-')), 'document.pdf');
  fs.writeFileSync(file, bytes);
  const args = [...extra];
  if (user !== undefined) args.push('-upw', user);
  if (owner !== undefined) args.push('-opw', owner);
  const output = tool === 'pdftotext' ? ['-'] : [];
  return spawnSync(systemBinary(tool, TOOL_DIRS), [...args, file, ...output], { encoding: 'utf8' });
}

/**
 * The text poppler extracts from `bytes` opened with the given password ({ user } or { owner }),
 * with runs of whitespace collapsed; null when poppler cannot open it with that password.
 */
function popplerText(bytes, workDir, passwords = {}) {
  const result = runOn('pdftotext', bytes, workDir, passwords);
  return result.status === 0 ? result.stdout.replace(/\s+/g, ' ').trim() : null;
}

/** What pdfinfo reports, as { field: value } (e.g. Pages, Encrypted); null when it cannot open it. */
function popplerInfo(bytes, workDir, passwords = {}) {
  const result = runOn('pdfinfo', bytes, workDir, passwords);
  if (result.status !== 0) return null;
  const info = {};
  for (const line of result.stdout.split('\n')) {
    const colon = line.indexOf(':');
    if (colon > 0) info[line.slice(0, colon).trim()] = line.slice(colon + 1).trim();
  }
  return info;
}

module.exports = { popplerText, popplerInfo };
