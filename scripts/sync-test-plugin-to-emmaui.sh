#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RID="${1:-}"
PLUGIN_OUT_DIR="${2:-}"
EMMAUI_DIR="${3:-}"

if [[ -z "$RID" ]]; then
  echo "Usage: $0 <rid> [plugin-output-dir] [emmaui-dir]"
  echo "Example: $0 osx-arm64"
  exit 1
fi

if [[ -z "$PLUGIN_OUT_DIR" ]]; then
  PLUGIN_OUT_DIR="$ROOT_DIR/src/EMMA.TestPlugin/artifacts"
fi

if [[ -z "$EMMAUI_DIR" ]]; then
  EMMAUI_DIR="$ROOT_DIR/../emmaui"
fi

if [[ ! -d "$PLUGIN_OUT_DIR" ]]; then
  echo "Test plugin output directory does not exist: $PLUGIN_OUT_DIR"
  exit 1
fi

if [[ ! -d "$EMMAUI_DIR" ]]; then
  echo "emmaui directory does not exist: $EMMAUI_DIR"
  exit 1
fi

case "$RID" in
  osx-*)
    PLUGIN_ID="emma.plugin.test"
    APP_NAME="EMMA.TestPlugin.app"
    MANIFEST_SOURCE="$ROOT_DIR/src/EMMA.TestPlugin/EMMA.TestPlugin.plugin.json"
    PLUGIN_APP_SOURCE="$PLUGIN_OUT_DIR/$PLUGIN_ID/$APP_NAME"
    ;;
  *)
    echo "Unsupported RID for test plugin sync: $RID"
    exit 1
    ;;
esac

declare -a DEST_DIRS=()

case "$RID" in
  osx-*)
    DEST_DIRS+=("$EMMAUI_DIR/macos/Runner/Resources")
    DEST_DIRS+=("$EMMAUI_DIR/build/macos/Build/Products/Debug/emmaui.app/Contents/Frameworks")
    DEST_DIRS+=("$EMMAUI_DIR/build/macos/Build/Products/Profile/emmaui.app/Contents/Frameworks")
    DEST_DIRS+=("$EMMAUI_DIR/build/macos/Build/Products/Release/emmaui.app/Contents/Frameworks")
    ;;
esac

if [[ ! -f "$MANIFEST_SOURCE" ]]; then
  echo "Test plugin manifest not found: $MANIFEST_SOURCE"
  exit 1
fi

if [[ ! -d "$PLUGIN_APP_SOURCE" ]]; then
  echo "Test plugin app bundle not found: $PLUGIN_APP_SOURCE"
  echo "Run ./scripts/publish-test-plugin.sh $RID first."
  exit 1
fi

echo "Syncing EMMA.TestPlugin seed bundle for RID '$RID'"
echo "  source app: $PLUGIN_APP_SOURCE"
echo "  source manifest: $MANIFEST_SOURCE"

for dir in "${DEST_DIRS[@]}"; do
  if [[ "$dir" == *"/Frameworks" ]]; then
    rm -rf "$dir/EMMA.Plugins"
    echo "  -> removed $dir/EMMA.Plugins"
    continue
  fi

  plugin_bundle_root="$dir/EMMA.Plugins"
  manifests_dir="$plugin_bundle_root/manifests"
  plugins_root="$plugin_bundle_root/plugins"
  plugin_dest_root="$plugins_root/$PLUGIN_ID"

  mkdir -p "$manifests_dir" "$plugins_root"
  rm -rf "$plugin_dest_root"
  mkdir -p "$plugin_dest_root"

  cp "$MANIFEST_SOURCE" "$manifests_dir/"
  cp -R "$PLUGIN_APP_SOURCE" "$plugin_dest_root/"

  chmod +x "$plugin_dest_root/$APP_NAME/Contents/MacOS/EMMA.TestPlugin" || true
  echo "  -> $plugin_dest_root/$APP_NAME"
done

echo "Seed plugin sync complete (Resources-based)."
