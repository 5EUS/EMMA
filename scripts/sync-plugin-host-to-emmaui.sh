#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RID="${1:-}"
HOST_OUTPUT_DIR="${2:-}"
EMMAUI_DIR="${3:-}"

if [[ -z "$RID" ]]; then
  echo "Usage: $0 <rid> [pluginhost-output-dir] [emmaui-dir]"
  echo "Example: $0 osx-arm64"
  exit 1
fi

if [[ -z "$HOST_OUTPUT_DIR" ]]; then
  HOST_OUTPUT_DIR="$ROOT_DIR/artifacts/pluginhost/$RID"
fi

if [[ -z "$EMMAUI_DIR" ]]; then
  EMMAUI_DIR="$ROOT_DIR/../emmaui"
fi

if [[ ! -d "$HOST_OUTPUT_DIR" ]]; then
  echo "PluginHost output directory does not exist: $HOST_OUTPUT_DIR"
  echo "Run ./scripts/publish-plugin-host.sh $RID first."
  exit 1
fi

if [[ ! -d "$EMMAUI_DIR" ]]; then
  echo "emmaui directory does not exist: $EMMAUI_DIR"
  exit 1
fi

case "$RID" in
  osx-*)
    HOST_BINARY_NAME="EMMA.PluginHost"
    HOST_RUNTIME_DIR_NAME="EMMA.PluginHost.runtime"
    ;;
  win-*)
    HOST_BINARY_NAME="EMMA.PluginHost.exe"
    HOST_RUNTIME_DIR_NAME="EMMA.PluginHost.runtime"
    ;;
  linux-*)
    HOST_BINARY_NAME="EMMA.PluginHost"
    HOST_RUNTIME_DIR_NAME="EMMA.PluginHost.runtime"
    ;;
  *)
    echo "Unsupported RID for host binary sync: $RID"
    exit 1
    ;;
esac

SOURCE_BINARY="$HOST_OUTPUT_DIR/$HOST_BINARY_NAME"
if [[ ! -f "$SOURCE_BINARY" ]]; then
  echo "PluginHost binary not found: $SOURCE_BINARY"
  exit 1
fi

declare -a DEST_DIRS=()

case "$RID" in
  osx-*)
    DEST_DIRS+=("$EMMAUI_DIR/macos/Runner/Frameworks")
    ;;
  win-*)
    DEST_DIRS+=("$EMMAUI_DIR/build/windows/x64/runner/Debug")
    DEST_DIRS+=("$EMMAUI_DIR/build/windows/x64/runner/Profile")
    DEST_DIRS+=("$EMMAUI_DIR/build/windows/x64/runner/Release")
    ;;
  linux-*)
    DEST_DIRS+=("$EMMAUI_DIR/linux/runner")
    DEST_DIRS+=("$EMMAUI_DIR/build/linux/x64/debug/bundle")
    DEST_DIRS+=("$EMMAUI_DIR/build/linux/x64/debug/intermediates_do_not_run")
    DEST_DIRS+=("$EMMAUI_DIR/build/linux/x64/release/bundle")
    DEST_DIRS+=("$EMMAUI_DIR/build/linux/x64/release/intermediates_do_not_run")
    ;;
esac

echo "Syncing PluginHost binary for RID '$RID'"
echo "  source: $SOURCE_BINARY"
echo "  source runtime dir: $HOST_OUTPUT_DIR"

for dir in "${DEST_DIRS[@]}"; do
  mkdir -p "$dir"

  RUNTIME_DEST_DIR="$dir/$HOST_RUNTIME_DIR_NAME"
  rm -rf "$RUNTIME_DEST_DIR"
  mkdir -p "$RUNTIME_DEST_DIR"
  cp -R "$HOST_OUTPUT_DIR"/* "$RUNTIME_DEST_DIR/"

  chmod +x "$RUNTIME_DEST_DIR/$HOST_BINARY_NAME" || true
  find "$RUNTIME_DEST_DIR" -type f \( -name "EMMA.PluginHost" -o -name "*.so" -o -name "libhostfxr.so" -o -name "libcoreclr.so" \) -exec chmod +x {} \; || true
  echo "  -> $RUNTIME_DEST_DIR/$HOST_BINARY_NAME"
done

echo "PluginHost sync complete."
