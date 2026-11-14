#!/usr/bin/env sh
# Builds the Rust plugins to Wasm components under artifacts/native/plugins/.
# Rust compiles straight to a component with the wasm32-wasip2 target: no LLVM, wasi-sdk or Docker.
# The host's tests and the .NET integration tests load these files.
set -eu
root=$(cd "$(dirname "$0")/.." && pwd)
out="$root/artifacts/native/plugins"
mkdir -p "$out"

# Fixture without the optional search-assist extension.
cargo build --release --target wasm32-wasip2 --locked --manifest-path "$root/native/plugins/fixture/Cargo.toml"
cp "$root/native/plugins/fixture/target/wasm32-wasip2/release/emma_fixture.wasm" "$out/fixture.wasm"

# Fixture with it (its own target dir inside target/, which is already ignored).
cargo build --release --target wasm32-wasip2 --locked --features assist \
  --manifest-path "$root/native/plugins/fixture/Cargo.toml" \
  --target-dir "$root/native/plugins/fixture/target/assist"
cp "$root/native/plugins/fixture/target/assist/wasm32-wasip2/release/emma_fixture.wasm" "$out/fixture-assist.wasm"

# Fixture built against a copy of the contract at version 1.0.1: a compatible newer patch release.
# A host built for 1.0.0 must still load it (wasmtime matches versions by semver).
newer="$root/native/plugins/fixture/target/wit-newer-patch"
rm -rf "$newer"
mkdir -p "$newer"
for file in "$root"/src/EMMA.Contracts/wit/*.wit; do
  sed 's/emma:core@1\.0\.0/emma:core@1.0.1/' "$file" > "$newer/$(basename "$file")"
done
cargo build --release --target wasm32-wasip2 --locked --features newer-patch \
  --manifest-path "$root/native/plugins/fixture/Cargo.toml" \
  --target-dir "$root/native/plugins/fixture/target/newer-patch"
cp "$root/native/plugins/fixture/target/newer-patch/wasm32-wasip2/release/emma_fixture.wasm" "$out/fixture-newer-patch.wasm"

# The plugin template (`emma plugin new` scaffolds from it). Its wit directory is the contract, copied here and
# ignored by git; the build proves the template compiles against the current contract. It goes through the
# template's own build.sh, so the script users get is exercised too (packing needs the CLI, so it is skipped here).
template="$root/native/templates/rust-plugin"
rm -rf "$template/wit"
cp -r "$root/src/EMMA.Contracts/wit" "$template/wit"
CARGO_FLAGS=--locked sh "$template/build.sh" --no-pack
cp "$template/plugin.wasm" "$out/template.wasm"

echo "plugins built into $out"
