#!/usr/bin/env sh
# Builds the native Wasm host (libemma_wasm) in release mode into artifacts/native/lib/, where the
# .NET integration tests pick it up and copy it next to the test assemblies.
set -eu
root=$(cd "$(dirname "$0")/.." && pwd)
out="$root/artifacts/native/lib"
mkdir -p "$out"

cargo build --release --locked --manifest-path "$root/native/emma-wasm/Cargo.toml"
target="$root/native/emma-wasm/target/release"
for file in libemma_wasm.so libemma_wasm.dylib emma_wasm.dll; do
  if [ -f "$target/$file" ]; then
    cp "$target/$file" "$out/$file"
  fi
done

echo "native host built into $out"
