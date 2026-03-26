#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RID="${1:-}"
EMMAUI_DIR="${2:-}"

if [[ -z "$RID" ]]; then
  echo "Usage: $0 <rid> [emmaui-dir]"
  echo "Example: $0 osx-arm64"
  exit 1
fi

if [[ -z "$EMMAUI_DIR" ]]; then
  EMMAUI_DIR="$ROOT_DIR/../emmaui"
fi

if [[ ! -d "$EMMAUI_DIR" ]]; then
  echo "emmaui directory does not exist: $EMMAUI_DIR"
  exit 1
fi

if [[ "$RID" == osx-* || "$RID" == linux-* ]]; then
  echo "[1/5] Building WASM runtime ($RID)..."
  "$ROOT_DIR/scripts/build-wasm-runtime-native.sh" "$RID"

  echo "[2/5] Publishing EMMA.Native (with embedded PluginHost) ($RID)..."
  "$ROOT_DIR/scripts/publish-native-aot.sh" "$RID"

  echo "[3/5] Syncing EMMA.Native + WASM runtime into emmaui..."
  "$ROOT_DIR/scripts/sync-native-aot-to-emmaui.sh" "$RID" "" "$EMMAUI_DIR"

  echo "[4/5] Publishing EMMA.TestPlugin ($RID)..."
  "$ROOT_DIR/scripts/publish-test-plugin.sh" "$RID"

  echo "[5/5] Syncing EMMA.TestPlugin artifacts..."
  if [[ "$RID" == linux-* ]]; then
    PLUGIN_MODE=linux "$ROOT_DIR/scripts/sync-test-plugin-to-emmaui.sh" "$RID" "" "$EMMAUI_DIR"
  else
    "$ROOT_DIR/scripts/sync-test-plugin-to-emmaui.sh" "$RID" "" "$EMMAUI_DIR"
  fi

  echo "Done. WASM runtime + Native AOT (with embedded PluginHost) + TestPlugin artifacts are published and synced for $RID."
elif [[ "$RID" == ios-* || "$RID" == iossimulator-* ]]; then
  IOS_BUILD_SIMULATOR="${BUILD_SIMULATOR:-}"
  if [[ -z "$IOS_BUILD_SIMULATOR" ]]; then
    if [[ "$RID" == ios-* ]]; then
      IOS_BUILD_SIMULATOR="0"
    else
      IOS_BUILD_SIMULATOR="1"
    fi
  fi

  echo "[1/1] Running iOS runtime publish+sync lane..."
  BUILD_SIMULATOR="$IOS_BUILD_SIMULATOR" \
    "$ROOT_DIR/scripts/publish-and-sync-ios-runtime.sh" "$EMMAUI_DIR"

  echo "Done. iOS static XCFramework artifacts are published and synced for $RID."
else
  echo "[1/3] Building WASM runtime ($RID)..."
  "$ROOT_DIR/scripts/build-wasm-runtime-native.sh" "$RID"

  echo "[2/3] Publishing EMMA.Native (with embedded PluginHost) ($RID)..."
  "$ROOT_DIR/scripts/publish-native-aot.sh" "$RID"

  echo "[3/3] Syncing EMMA.Native + WASM runtime into emmaui..."
  "$ROOT_DIR/scripts/sync-native-aot-to-emmaui.sh" "$RID" "" "$EMMAUI_DIR"

  echo "TestPlugin steps are macOS-only and intentionally skipped for non-macOS RID '$RID'."

  echo "Done. WASM runtime + Native AOT (with embedded PluginHost) artifacts are published and synced for $RID (TestPlugin skipped by design)."
fi
