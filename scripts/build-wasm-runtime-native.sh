#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RUNTIME_CRATE_DIR="$ROOT_DIR/src/EMMA.WasmRuntime.Native"
RID="${1:-}"
OUTPUT_DIR="${2:-}"

if [[ -z "$RID" ]]; then
  echo "Usage: $0 <rid> [output-dir]"
  echo "Example: $0 linux-x64"
  exit 1
fi

if [[ -z "$OUTPUT_DIR" ]]; then
  OUTPUT_DIR="$ROOT_DIR/artifacts/wasm-runtime-native/$RID"
fi

case "$RID" in
  linux-x64)
    TARGET_TRIPLE="x86_64-unknown-linux-gnu"
    LIB_FILE="libemma_wasm_runtime.so"
    ;;
  osx-arm64)
    TARGET_TRIPLE="aarch64-apple-darwin"
    LIB_FILE="libemma_wasm_runtime.dylib"
    ;;
  osx-x64)
    TARGET_TRIPLE="x86_64-apple-darwin"
    LIB_FILE="libemma_wasm_runtime.dylib"
    ;;
  win-x64)
    TARGET_TRIPLE="x86_64-pc-windows-msvc"
    LIB_FILE="emma_wasm_runtime.dll"
    ;;
  ios-arm64)
    TARGET_TRIPLE="aarch64-apple-ios"
    LIB_FILE="libemma_wasm_runtime.a"
    ;;
  iossimulator-arm64)
    TARGET_TRIPLE="aarch64-apple-ios-sim"
    LIB_FILE="libemma_wasm_runtime.a"
    ;;
  iossimulator-x64)
    TARGET_TRIPLE="x86_64-apple-ios"
    LIB_FILE="libemma_wasm_runtime.a"
    ;;
  *)
    echo "Unsupported RID: $RID"
    exit 1
    ;;
esac

echo "Building emma_wasm_runtime for $RID ($TARGET_TRIPLE)..."

cargo build \
  --manifest-path "$RUNTIME_CRATE_DIR/Cargo.toml" \
  --release \
  --target "$TARGET_TRIPLE"

SOURCE_LIB="$RUNTIME_CRATE_DIR/target/$TARGET_TRIPLE/release/$LIB_FILE"
if [[ ! -f "$SOURCE_LIB" ]]; then
  echo "Expected native runtime library not found: $SOURCE_LIB"
  exit 1
fi

rm -rf "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"
cp "$SOURCE_LIB" "$OUTPUT_DIR/"

echo "Native runtime build succeeded: $OUTPUT_DIR/$LIB_FILE"
