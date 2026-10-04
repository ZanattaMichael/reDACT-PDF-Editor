#!/usr/bin/env bash
# Tests for scripts/next-version.sh, the release-candidate version rule.
# Usage: ./scripts/test-next-version.sh
set -euo pipefail

SCRIPT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/next-version.sh"
failures=0

expect() {
  local want="$1" tag="$2" manifest="$3" got
  got=$("$SCRIPT" "$tag" "$manifest" 2>&1) || got="error"
  if [[ "$got" == "$want" ]]; then
    echo "  ok:   tag='$tag' manifest='$manifest' -> $got"
  else
    echo "  FAIL: tag='$tag' manifest='$manifest' -> '$got', expected '$want'"
    failures=$((failures + 1))
  fi
}

echo "Release-candidate version rule..."
expect 2.0.4  v2.0.3  2.0.2        # ordinary merge: patch bump from the last final release
expect 2.0.4  v2.0.3  2.0.3        # manifest equal to the release: still a patch bump
expect 2.0.4  v2.0.3  2.0.3.57     # an RC-stamped manifest (four parts) is not "ahead"
expect 3.0.0  v2.0.3  3.0.0        # deliberate major release: the manifest version wins
expect 2.1.0  v2.0.3  2.1.0        # deliberate minor release
expect 3.0.1  v3.0.0  3.0.0        # once v3.0.0 is tagged, patch bumping resumes
expect 2.0.10 v2.0.9  2.0.2        # versions compare numerically, not as text
expect 1.0.1  ""      1.0.0        # no final release yet: the manifest is the base
expect error  v2.0    2.0.2        # malformed tag
expect error  v2.0.3  two.0.0      # malformed manifest version

if (( failures > 0 )); then
  echo "$failures case(s) failed."
  exit 1
fi
echo "Version rule OK."
