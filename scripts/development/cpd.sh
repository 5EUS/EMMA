#!/usr/bin/env sh
# Copy/paste detection with PMD CPD. Needs `pmd` (https://pmd.github.io) on PATH.
#
# Two profiles, each with its own token threshold:
#   C#    src/ and tests/ *.cs
#   Rust  *.rs under native/ (only when the repository has any)
# The Rust profile is a no-op in a pure C# repository.
set -eu
root=$(cd "$(dirname "$0")/../.." && pwd)
cd "$root"

if ! command -v pmd >/dev/null 2>&1; then
  echo "pmd not found; install it (e.g. 'brew install pmd') to run duplicate detection." >&2
  exit 1
fi

# Tune per language. Rust is terser than C#, so its threshold is lower.
cs_tokens=${CPD_CS_MIN_TOKENS:-100}
rust_tokens=${CPD_RUST_MIN_TOKENS:-80}

list=$(mktemp)
trap 'rm -f "$list"' EXIT

# Hand-written sources only: build output (obj/, bin/, target/) holds generated
# code that is duplicated per configuration. Exits non-zero on duplicates.
find src tests \( -name obj -o -name bin \) -prune -o -name '*.cs' -print > "$list"
if [ -s "$list" ]; then
  pmd cpd --language cs --minimum-tokens "$cs_tokens" --file-list "$list"
fi

if [ -d native ]; then
  # Generated bindings (wit-bindgen, bindgen!, build.rs output) are excluded by
  # name: keep them in files named *.generated.rs or under a generated/ directory.
  find native \( -name target -o -name generated \) -prune -o -name '*.rs' ! -name '*.generated.rs' -print > "$list"
  if [ -s "$list" ]; then
    pmd cpd --language rust --minimum-tokens "$rust_tokens" --file-list "$list"
  fi
fi
