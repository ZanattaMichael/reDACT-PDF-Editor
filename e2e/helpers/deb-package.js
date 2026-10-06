'use strict';

// Builds and installs the real Debian package, so the package-install suite drives the extension
// against a host that got onto the machine exactly the way a user's does: dpkg unpacked it,
// the postinst ran, and the manifest landed in /etc where the browser looks for it.
//
// Everything here is deliberately the shipped artefact rather than a stand-in. The failure this
// guards against — a package that installs cleanly and is then never seen by the browser — is
// invisible to any test that registers the host itself.

const { execFileSync } = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const REPO_ROOT = path.resolve(__dirname, '..', '..');
const PACKAGE_NAME = 'pdf-editor-host';

// These commands run as root, so they are resolved against fixed system directories rather than
// $PATH (CWE-426; see system-binary.js).
const { systemBinary } = require('./system-binary');

/** Runs a command as root, going through sudo when the test user is not already root. */
function asRoot(command, args) {
  const root = typeof process.getuid === 'function' && process.getuid() === 0;
  const [bin, argv] = root
    ? [systemBinary(command), args]
    : [systemBinary('sudo'), ['-n', systemBinary(command), ...args]];
  return execFileSync(bin, argv, { stdio: 'inherit' });
}

/** True when dpkg already has the package installed and configured. */
function isInstalled() {
  try {
    const state = execFileSync(
      systemBinary('dpkg-query'), ['-W', "-f=${db:Status-Status}", PACKAGE_NAME],
      { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] });
    return state.trim() === 'installed';
  } catch {
    return false; // dpkg-query exits non-zero for a package it has never heard of
  }
}

/**
 * Builds the .deb, unless PDF_EDITOR_DEB names one already built (CI reuses the artefact from the
 * packaging job rather than paying for a second self-contained publish).
 */
function buildDeb() {
  const prebuilt = process.env.PDF_EDITOR_DEB;
  if (prebuilt) {
    if (!fs.existsSync(prebuilt)) throw new Error(`PDF_EDITOR_DEB does not exist: ${prebuilt}`);
    return prebuilt;
  }
  const outDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pdf-editor-deb-'));
  execFileSync(path.join(REPO_ROOT, 'scripts', 'package-deb.sh'), [outDir], { stdio: 'inherit' });
  const built = fs.readdirSync(outDir).filter((f) => f.endsWith('.deb'));
  if (built.length !== 1) throw new Error(`expected one .deb in ${outDir}, found ${built.length}`);
  return path.join(outDir, built[0]);
}

/**
 * Installs the package and returns what the suite needs to know about it. Refuses to run over an
 * existing install: this uninstalls afterwards, and a developer who had the host installed for
 * their own use should get it back, not lose it to a test run.
 */
function installDeb() {
  if (isInstalled()) {
    throw new Error(
      `${PACKAGE_NAME} is already installed. This suite installs and then removes the package, so `
      + 'it will not run over an existing install. Remove it first:\n'
      + `    sudo dpkg -r ${PACKAGE_NAME}`);
  }
  const deb = buildDeb();
  console.log(`Installing ${deb} ...`);
  asRoot('dpkg', ['-i', deb]);
  return deb;
}

/** Removes the package again, leaving the machine as the suite found it. */
function removeDeb() {
  if (!isInstalled()) return;
  asRoot('dpkg', ['--purge', PACKAGE_NAME]);
}

module.exports = { PACKAGE_NAME, installDeb, isInstalled, removeDeb, REPO_ROOT };
