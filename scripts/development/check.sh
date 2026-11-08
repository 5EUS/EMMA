#!/usr/bin/env sh
# Everything CI checks, locally. Run before opening a PR.
set -eu
root=$(cd "$(dirname "$0")/../.." && pwd)
cd "$root"

# Rust is optional: repositories without a native/ Cargo workspace skip these blocks.
# Every Cargo manifest under native/ is checked with the same strictness as the C#.
if [ -d native ]; then
  # The Rust tests and the .NET integration tests load the Wasm plugins built here.
  # TODO(rewrite): this hook is EMMA-specific and not in the strictcommon template; upstream a generic
  # "native/build-*.sh" hook there if other repositories need it.
  for script in native/build-plugins.sh; do
    [ -f "$script" ] && sh "$script"
  done
  find native -name Cargo.toml -not -path '*/target/*' | while read -r manifest; do
    cargo fmt --manifest-path "$manifest" --all -- --check
    cargo clippy --manifest-path "$manifest" --all-targets --locked -- -D warnings
    cargo test --manifest-path "$manifest" --locked
  done
  # The native host the .NET tests load.
  [ -f native/build-host.sh ] && sh native/build-host.sh
fi

dotnet restore --locked-mode
dotnet format whitespace --no-restore --verify-no-changes
dotnet build -c Release --no-restore
dotnet test -c Release --no-build
dotnet list package --vulnerable --include-transitive

# Duplicate detection needs PMD (and Java); CI always runs it, locally it is optional.
if command -v pmd >/dev/null 2>&1; then
  sh scripts/development/cpd.sh
else
  echo "skipping CPD: pmd not installed" >&2
fi
