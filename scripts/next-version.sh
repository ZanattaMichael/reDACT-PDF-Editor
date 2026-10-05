#!/usr/bin/env bash
# Prints the version the next release candidate should carry, given the last *final* release
# tag ("vX.Y.Z", or empty when there is none yet) and the version in extension/manifest.json.
#
#   - Normally that is the last final release with its patch number bumped (v2.0.3 -> 2.0.4).
#   - A deliberate major or minor release is made by raising manifest.json above the last final
#     release (2.0.3 -> 3.0.0): when the manifest is ahead, its version is used as it stands, so
#     every RC on the way to 3.0.0 is a 3.0.0 candidate until v3.0.0 itself is tagged, after which
#     the patch bumping resumes from there.
#   - With no final release yet, the manifest version is the base and is patch-bumped.
#
# Usage: ./scripts/next-version.sh <previous-final-tag-or-empty> <manifest-version>
set -euo pipefail

previous_tag="${1:-}"
manifest_version="${2:?usage: next-version.sh <previous-final-tag-or-empty> <manifest-version>}"

# Only the first three parts matter: a manifest stamped by an RC build carries a fourth (build) part.
IFS='.' read -r m_major m_minor m_patch _ <<< "$manifest_version"
manifest_base="${m_major}.${m_minor:-0}.${m_patch:-0}"
for part in $m_major ${m_minor:-0} ${m_patch:-0}; do
  [[ "$part" =~ ^[0-9]+$ ]] || { echo "error: manifest version '$manifest_version' is not numeric" >&2; exit 1; }
done

if [[ -z "$previous_tag" ]]; then
  base="$manifest_base"
else
  [[ "$previous_tag" =~ ^v[0-9]+\.[0-9]+\.[0-9]+$ ]] \
    || { echo "error: '$previous_tag' is not a final release tag (vX.Y.Z)" >&2; exit 1; }
  base="${previous_tag#v}"
  highest=$(printf '%s\n%s\n' "$base" "$manifest_base" | sort -V | tail -1)
  if [[ "$highest" == "$manifest_base" && "$manifest_base" != "$base" ]]; then
    echo "$manifest_base"
    exit 0
  fi
fi

IFS='.' read -r major minor patch <<< "$base"
echo "${major}.${minor}.$((patch + 1))"
