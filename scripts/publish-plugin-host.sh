#!/usr/bin/env bash
set -euo pipefail

echo "================================================================================"
echo "WARNING: This script is DEPRECATED"
echo "================================================================================"
echo ""
echo "EMMA.PluginHost is now embedded in EMMA.Native as a static library."
echo "Use ./scripts/publish-native-aot.sh instead."
echo ""
echo "The PluginHost functionality is provided by EMMA.PluginHost.Library,"
echo "which is compiled into libemma_native.a (or emma_native.lib on Windows)."
echo ""
echo "This script is kept for backwards compatibility but does nothing useful."
echo "================================================================================"
exit 1

# Legacy code below (no longer used)

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT_PATH="$ROOT_DIR/src/EMMA.PluginHost/EMMA.PluginHost.csproj"
RID="${1:-}"
OUTPUT_DIR="${2:-}"

if [[ -z "$RID" ]]; then
  echo "Usage: $0 <rid> [output-dir]"
  echo "Example: $0 osx-arm64"
  exit 1
fi

if [[ -z "$OUTPUT_DIR" ]]; then
  OUTPUT_DIR="$ROOT_DIR/artifacts/pluginhost/$RID"
fi

rm -rf "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"

echo "Publishing PluginHost for $RID..."

dotnet publish "$PROJECT_PATH" \
  -c Release \
  -r "$RID" \
  --self-contained true \
  -p:UseAppHost=true \
  -o "$OUTPUT_DIR"

RUNTIME_LIB_DIR="$ROOT_DIR/artifacts/wasm-runtime-native/$RID"

case "$RID" in
  osx-*)
    RUNTIME_LIB_NAME="libemma_wasm_runtime.dylib"
    ;;
  win-*)
    RUNTIME_LIB_NAME="emma_wasm_runtime.dll"
    ;;
  linux-*)
    RUNTIME_LIB_NAME="libemma_wasm_runtime.so"
    ;;
  ios-*)
    RUNTIME_LIB_NAME="libemma_wasm_runtime.a"
    ;;
  *)
    RUNTIME_LIB_NAME=""
    ;;
esac

if [[ -n "$RUNTIME_LIB_NAME" ]]; then
  SOURCE_RUNTIME_LIB="$RUNTIME_LIB_DIR/$RUNTIME_LIB_NAME"
  if [[ -f "$SOURCE_RUNTIME_LIB" ]]; then
    cp "$SOURCE_RUNTIME_LIB" "$OUTPUT_DIR/$RUNTIME_LIB_NAME"
    echo "Bundled native WASM runtime: $OUTPUT_DIR/$RUNTIME_LIB_NAME"
  else
    echo "Warning: native WASM runtime library not found for RID '$RID'."
    echo "Run ./scripts/build-wasm-runtime-native.sh $RID before publish to enable WASM execution."
  fi
fi

echo "PluginHost publish succeeded: $OUTPUT_DIR"
echo "Set EMMA_PLUGIN_HOST_EXECUTABLE to the published host binary path."
