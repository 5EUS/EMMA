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

echo "[1/6] Publishing EMMA.Native ($RID)..."
"$ROOT_DIR/scripts/publish-native-aot.sh" "$RID"

echo "[2/6] Syncing EMMA.Native into emmaui..."
"$ROOT_DIR/scripts/sync-native-aot-to-emmaui.sh" "$RID" "" "$EMMAUI_DIR"

echo "[3/6] Publishing EMMA.PluginHost ($RID)..."
"$ROOT_DIR/scripts/publish-plugin-host.sh" "$RID"

echo "[4/6] Syncing EMMA.PluginHost into emmaui..."
"$ROOT_DIR/scripts/sync-plugin-host-to-emmaui.sh" "$RID" "" "$EMMAUI_DIR"

if [[ "$RID" == osx-* ]]; then
  echo "[5/6] Publishing EMMA.TestPlugin ($RID)..."
  "$ROOT_DIR/scripts/publish-test-plugin.sh" "$RID"

  echo "[6/6] Cleaning framework-bundled EMMA.TestPlugin artifacts..."
  "$ROOT_DIR/scripts/sync-test-plugin-to-emmaui.sh" "$RID" "" "$EMMAUI_DIR"
else
  echo "[5/6] Skipping EMMA.TestPlugin publish for non-macOS RID '$RID'."
  echo "[6/6] Skipping EMMA.TestPlugin sync for non-macOS RID '$RID'."
fi

echo "Done. Native + PluginHost + TestPlugin artifacts are published and synced for $RID."
