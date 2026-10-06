'use strict';

// Command-line tools are resolved against a fixed set of system directories rather than $PATH.
// A $PATH entry anyone can write to would decide which binary runs (CWE-426). The package tests
// run commands as root, and the other helpers hand OpenSSL and poppler the files under test;
// neither should be relaxed about that, even on a throwaway CI machine.

const fs = require('node:fs');
const path = require('node:path');

/** Root-owned directories that hold system commands on Linux. */
const SYSTEM_BIN_DIRS = ['/usr/bin', '/bin', '/usr/sbin', '/sbin'];

/** Where a developer's tools are, on Linux and with the macOS package managers. */
const TOOL_DIRS = ['/usr/bin', '/bin', '/usr/local/bin', '/opt/homebrew/bin'];

/** Absolute path of a command found in `dirs`, or a clear error naming where it was looked for. */
function systemBinary(name, dirs = SYSTEM_BIN_DIRS) {
  const found = dirs
    .map((dir) => path.join(dir, name))
    .find((candidate) => fs.existsSync(candidate));
  if (!found) throw new Error(`${name} is not in ${dirs.join(', ')}`);
  return found;
}

module.exports = { systemBinary, SYSTEM_BIN_DIRS, TOOL_DIRS };
