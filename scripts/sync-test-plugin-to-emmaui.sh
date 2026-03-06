#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RID="${1:-}"
PLUGIN_OUT_DIR="${2:-}"
EMMAUI_DIR="${3:-}"
PLUGIN_MODE="${PLUGIN_MODE:-}"

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
  linux-*)
    PLUGIN_ID="emma.plugin.test"
    MANIFEST_SOURCE="$ROOT_DIR/src/EMMA.TestPlugin/EMMA.TestPlugin.plugin.json"
    PLUGIN_DIR_SOURCE="$PLUGIN_OUT_DIR/$PLUGIN_ID"
    WASM_COMPONENT_SOURCE="$ROOT_DIR/src/EMMA.TestPlugin/artifacts/wasm/plugin.wasm"
    WASM_RUNTIME_SOURCE_DIR="$ROOT_DIR/src/EMMA.TestPlugin/artifacts/wasm-publish"
    XDG_DATA_HOME="${XDG_DATA_HOME:-$HOME/.local/share}"
    LINUX_EMMA_ROOT="$XDG_DATA_HOME/com.example.emmaui/emmaui"
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
  linux-*)
    DEST_DIRS+=("$LINUX_EMMA_ROOT")
    ;;
esac

if [[ ! -f "$MANIFEST_SOURCE" ]]; then
  echo "Test plugin manifest not found: $MANIFEST_SOURCE"
  exit 1
fi

if [[ "$RID" == osx-* ]]; then
  if [[ ! -d "$PLUGIN_APP_SOURCE" ]]; then
    echo "Test plugin app bundle not found: $PLUGIN_APP_SOURCE"
    echo "Run ./scripts/publish-test-plugin.sh $RID first."
    exit 1
  fi
fi

if [[ "$RID" == linux-* ]]; then
  if [[ -z "$PLUGIN_MODE" ]]; then
    PLUGIN_MODE="linux"
  fi

  if [[ "$PLUGIN_MODE" == "auto" ]]; then
    if [[ -f "$WASM_COMPONENT_SOURCE" ]]; then
      PLUGIN_MODE="wasm"
    else
      PLUGIN_MODE="linux"
    fi
  fi

  if [[ "$PLUGIN_MODE" != "wasm" && "$PLUGIN_MODE" != "linux" ]]; then
    echo "Unsupported PLUGIN_MODE for linux sync: $PLUGIN_MODE"
    echo "Use PLUGIN_MODE=auto|wasm|linux"
    exit 1
  fi

  if [[ "$PLUGIN_MODE" == "wasm" ]]; then
    if [[ ! -f "$WASM_COMPONENT_SOURCE" ]]; then
      echo "WASM component not found: $WASM_COMPONENT_SOURCE"
      echo "Build it first with: TARGETS=\"wasm\" ./src/EMMA.TestPlugin/scripts/build-pack-plugin.sh"
      exit 1
    fi

    if [[ ! -d "$WASM_RUNTIME_SOURCE_DIR" ]]; then
      echo "WASM runtime payload directory not found: $WASM_RUNTIME_SOURCE_DIR"
      echo "Build it first with: TARGETS=\"wasm\" ./src/EMMA.TestPlugin/scripts/build-pack-plugin.sh"
      exit 1
    fi
  else
    if [[ ! -d "$PLUGIN_DIR_SOURCE" ]]; then
      echo "Test plugin Linux bundle not found: $PLUGIN_DIR_SOURCE"
      echo "Run ./scripts/publish-test-plugin.sh $RID first."
      exit 1
    fi
  fi
fi

echo "Syncing EMMA.TestPlugin seed bundle for RID '$RID'"
if [[ "$RID" == osx-* ]]; then
  echo "  source app: $PLUGIN_APP_SOURCE"
else
  if [[ "$PLUGIN_MODE" == "wasm" ]]; then
    echo "  source wasm: $WASM_COMPONENT_SOURCE"
  else
    echo "  source dir: $PLUGIN_DIR_SOURCE"
  fi
fi
echo "  source manifest: $MANIFEST_SOURCE"
if [[ "$RID" == linux-* ]]; then
  echo "  plugin mode: $PLUGIN_MODE"
fi

for dir in "${DEST_DIRS[@]}"; do
  if [[ "$RID" == linux-* ]]; then
    manifests_dir="$dir/manifests"
    plugins_root="$dir/plugins"
    plugin_dest_root="$plugins_root/$PLUGIN_ID"

    mkdir -p "$manifests_dir" "$plugins_root"
    rm -rf "$plugin_dest_root"
    mkdir -p "$plugin_dest_root"

    cp "$MANIFEST_SOURCE" "$manifests_dir/$PLUGIN_ID.plugin.json"
    if [[ "$PLUGIN_MODE" == "wasm" ]]; then
      mkdir -p "$plugin_dest_root/wasm"
      cp -R "$WASM_RUNTIME_SOURCE_DIR"/. "$plugin_dest_root/wasm/"
      cp "$WASM_COMPONENT_SOURCE" "$plugin_dest_root/wasm/plugin.wasm"
    else
      cp -R "$PLUGIN_DIR_SOURCE"/. "$plugin_dest_root/"
      find "$plugin_dest_root" -type f \( -name "EMMATestPlugin" -o -name "EMMA.TestPlugin" -o -name "*.so" \) -exec chmod +x {} \; || true
    fi

    echo "  -> $plugin_dest_root"
    continue
  fi

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
