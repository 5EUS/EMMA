#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
PLUGIN_DIR=$(cd "$SCRIPT_DIR/.." && pwd)
ROOT_DIR=$(cd "$PLUGIN_DIR/../.." && pwd)
MANIFEST_PATH="${1:-$PLUGIN_DIR/EMMA.PluginTemplate.plugin.json}"
OUT_DIR="$PLUGIN_DIR/artifacts"
PACK_DIR="$OUT_DIR/pack"
TARGETS=${TARGETS:-"osx-arm64"}
WASM_MODULE_PATH="${WASM_MODULE_PATH:-$OUT_DIR/wasm/plugin.wasm}"
WASM_PACKAGE_FILE_NAME="${WASM_PACKAGE_FILE_NAME:-plugin.wasm}"
CWASM_WASMTIME_TARGET="${CWASM_WASMTIME_TARGET:-}"
CWASM_WASMTIME_BIN="${CWASM_WASMTIME_BIN:-wasmtime}"
CWASM_EXPECTED_WASMTIME_VERSION="${CWASM_EXPECTED_WASMTIME_VERSION:-34.0.2}"
CWASM_PRECOMPILE_TOOL="${CWASM_PRECOMPILE_TOOL:-$ROOT_DIR/tools/EMMA.CwasmPrecompile/target/release/emma_cwasm_precompile}"

resolve_default_cwasm_target() {
  local rust_host
  rust_host="$(rustc -vV 2>/dev/null | awk '/^host:/ {print $2}')"
  if [[ -n "$rust_host" ]]; then
    echo "$rust_host"
    return 0
  fi

  case "$(uname -s)-$(uname -m)" in
    Darwin-arm64)
      echo "aarch64-apple-darwin"
      ;;
    Darwin-x86_64)
      echo "x86_64-apple-darwin"
      ;;
    Linux-x86_64)
      echo "x86_64-unknown-linux-gnu"
      ;;
    Linux-aarch64)
      echo "aarch64-unknown-linux-gnu"
      ;;
    *)
      echo ""
      ;;
  esac
}

run_precompile_tool() {
  local input_wasm="$1"
  local output_cwasm="$2"
  local compile_target="$3"

  if [[ ! -x "$CWASM_PRECOMPILE_TOOL" ]]; then
    return 1
  fi

  "$CWASM_PRECOMPILE_TOOL" "$input_wasm" "$output_cwasm" "$compile_target"

  return 0
}

build_precompiled_cwasm() {
  local input_wasm="$1"
  local output_cwasm="$2"
  local compile_target="$3"

  if [[ ! -f "$input_wasm" ]]; then
    echo "Input wasm component not found: $input_wasm" >&2
    exit 1
  fi

  mkdir -p "$(dirname "$output_cwasm")"

  if [[ -z "$compile_target" ]]; then
    echo "Failed to resolve cwasm compile target." >&2
    exit 1
  fi

  if run_precompile_tool "$input_wasm" "$output_cwasm" "$compile_target"; then
    :
  else
    if ! command -v "$CWASM_WASMTIME_BIN" >/dev/null 2>&1; then
      echo "TARGETS includes cwasm but no compatible precompiler is available." >&2
      echo "Either build $CWASM_PRECOMPILE_TOOL or install Wasmtime ${CWASM_EXPECTED_WASMTIME_VERSION} and set CWASM_WASMTIME_BIN." >&2
      exit 1
    fi

    local wasmtime_version
    wasmtime_version="$($CWASM_WASMTIME_BIN --version 2>/dev/null | awk '{print $2}')"
    if [[ -n "$CWASM_EXPECTED_WASMTIME_VERSION" && "$wasmtime_version" != "$CWASM_EXPECTED_WASMTIME_VERSION" ]]; then
      echo "Incompatible Wasmtime CLI version for cwasm precompile: found $wasmtime_version, expected $CWASM_EXPECTED_WASMTIME_VERSION" >&2
      echo "Set CWASM_WASMTIME_BIN to a Wasmtime $CWASM_EXPECTED_WASMTIME_VERSION binary, or build the local precompile tool." >&2
      exit 1
    fi

    "$CWASM_WASMTIME_BIN" compile --target "$compile_target" -o "$output_cwasm" "$input_wasm"
  fi
}

if [[ ! -f "$MANIFEST_PATH" ]]; then
  echo "Manifest not found: $MANIFEST_PATH" >&2
  exit 1
fi

manifest_fields=()
while IFS= read -r line; do
  manifest_fields+=("$line")
done < <(python3 - "$MANIFEST_PATH" <<'PY'
import json
import sys

manifest_path = sys.argv[1]
with open(manifest_path, "r", encoding="utf-8") as f:
    manifest = json.load(f)

plugin_id = manifest.get("id") or "plugin"
plugin_name = manifest.get("name") or plugin_id
version = manifest.get("version") or "0.0.0"

print(plugin_id)
print(plugin_name)
print(version)
PY
)

if [[ ${#manifest_fields[@]} -lt 3 ]]; then
  echo "Failed to parse manifest fields." >&2
  exit 1
fi

PLUGIN_ID="${manifest_fields[0]}"
PLUGIN_NAME="${manifest_fields[1]}"
PLUGIN_VERSION="${manifest_fields[2]}"
APP_BUNDLE_NAME=$(echo "$PLUGIN_NAME" | tr -d '[:space:]')
if [[ -z "$APP_BUNDLE_NAME" ]]; then
  APP_BUNDLE_NAME="$PLUGIN_ID"
fi

APP_NAME="$APP_BUNDLE_NAME.app"
mkdir -p "$PACK_DIR"

for TARGET in $TARGETS; do
  BUILD_DIR="$OUT_DIR/build-$TARGET"
  PUBLISH_DIR="$BUILD_DIR/publish"
  PACKAGE_ROOT="$PACK_DIR/$PLUGIN_VERSION-$TARGET"
  MANIFEST_OUT_DIR="$PACKAGE_ROOT/manifest"
  PLUGIN_OUT_DIR="$PACKAGE_ROOT/$PLUGIN_ID"

  rm -rf "$BUILD_DIR" "$PACKAGE_ROOT" "$PACK_DIR/${PLUGIN_ID}_${PLUGIN_VERSION}_${TARGET}.zip"
  mkdir -p "$PUBLISH_DIR" "$MANIFEST_OUT_DIR" "$PLUGIN_OUT_DIR"

  if [[ "$TARGET" == osx-* ]]; then
    APP_DIR="$BUILD_DIR/$APP_NAME"
    CONTENTS_DIR="$APP_DIR/Contents"
    MACOS_DIR="$CONTENTS_DIR/MacOS"
    RESOURCES_DIR="$CONTENTS_DIR/Resources"

    mkdir -p "$MACOS_DIR" "$RESOURCES_DIR"

    dotnet publish "$PLUGIN_DIR/EMMA.PluginTemplate.csproj" -c Release -r "$TARGET" --self-contained true -p:UseAppHost=true -o "$PUBLISH_DIR"

    APP_RUNTIME_CONFIG=$(find "$PUBLISH_DIR" -maxdepth 1 -type f -name "*.runtimeconfig.json" | head -n 1)
    if [[ -z "$APP_RUNTIME_CONFIG" ]]; then
      echo "Failed to locate runtimeconfig in publish output." >&2
      exit 1
    fi

    APP_EXECUTABLE=$(basename "$APP_RUNTIME_CONFIG" .runtimeconfig.json)

    cp -R "$PUBLISH_DIR"/. "$MACOS_DIR/"
    rm -rf "$MACOS_DIR/artifacts"

    cat > "$CONTENTS_DIR/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleIdentifier</key>
  <string>$PLUGIN_ID</string>
  <key>CFBundleName</key>
  <string>$PLUGIN_NAME</string>
  <key>CFBundleDisplayName</key>
  <string>$PLUGIN_NAME</string>
  <key>CFBundleVersion</key>
  <string>$PLUGIN_VERSION</string>
  <key>CFBundleShortVersionString</key>
  <string>$PLUGIN_VERSION</string>
  <key>CFBundleExecutable</key>
  <string>$APP_EXECUTABLE</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
</dict>
</plist>
PLIST

    codesign --force --deep --sign - --entitlements "$PLUGIN_DIR/entitlements.plist" "$APP_DIR"
    cp -R "$APP_DIR" "$PLUGIN_OUT_DIR/"
  elif [[ "$TARGET" == linux-* ]]; then
    dotnet publish "$PLUGIN_DIR/EMMA.PluginTemplate.csproj" -c Release -r "$TARGET" --self-contained true -p:UseAppHost=true -o "$PUBLISH_DIR"

    APP_RUNTIME_CONFIG=$(find "$PUBLISH_DIR" -maxdepth 1 -type f -name "*.runtimeconfig.json" | head -n 1)
    if [[ -z "$APP_RUNTIME_CONFIG" ]]; then
      echo "Failed to locate runtimeconfig in publish output." >&2
      exit 1
    fi

    APP_EXECUTABLE=$(basename "$APP_RUNTIME_CONFIG" .runtimeconfig.json)
    ENTRYPOINT_NAME="$APP_BUNDLE_NAME"

    cp -R "$PUBLISH_DIR"/. "$PLUGIN_OUT_DIR/"
    if [[ -f "$PLUGIN_OUT_DIR/$APP_EXECUTABLE" && "$APP_EXECUTABLE" != "$ENTRYPOINT_NAME" ]]; then
      cp "$PLUGIN_OUT_DIR/$APP_EXECUTABLE" "$PLUGIN_OUT_DIR/$ENTRYPOINT_NAME"
    fi

    chmod +x "$PLUGIN_OUT_DIR/$APP_EXECUTABLE" || true
    chmod +x "$PLUGIN_OUT_DIR/$ENTRYPOINT_NAME" || true
    find "$PLUGIN_OUT_DIR" -maxdepth 1 -type f -name "*.so" -exec chmod +x {} \; || true
  elif [[ "$TARGET" == wasm* || "$TARGET" == cwasm* ]]; then
    if [[ ! -f "$WASM_MODULE_PATH" ]]; then
      echo "WASM component not found: $WASM_MODULE_PATH" >&2
      echo "Build the wasm component first or set WASM_MODULE_PATH=/absolute/path/plugin.wasm (or .cwasm)" >&2
      exit 1
    fi

    mkdir -p "$PLUGIN_OUT_DIR/wasm"
    package_file_name="$WASM_PACKAGE_FILE_NAME"
    if [[ "$TARGET" == cwasm* ]]; then
      package_file_name="plugin.cwasm"
      cwasm_source="$WASM_MODULE_PATH"
      cwasm_compile_target="$CWASM_WASMTIME_TARGET"
      if [[ -z "$cwasm_compile_target" ]]; then
        cwasm_compile_target="$(resolve_default_cwasm_target)"
      fi
      if [[ "${WASM_MODULE_PATH##*.}" != "cwasm" ]]; then
        cwasm_source="$BUILD_DIR/plugin.cwasm"
        build_precompiled_cwasm "$WASM_MODULE_PATH" "$cwasm_source" "$cwasm_compile_target"
      fi

      cp "$cwasm_source" "$PLUGIN_OUT_DIR/wasm/$package_file_name"
    else
      cp "$WASM_MODULE_PATH" "$PLUGIN_OUT_DIR/wasm/$package_file_name"
    fi
  else
    echo "Unsupported target for packaging: $TARGET" >&2
    exit 1
  fi

  MANIFEST_OUT="$MANIFEST_OUT_DIR/$PLUGIN_ID.json"
  cp "$MANIFEST_PATH" "$MANIFEST_OUT"

  if [[ -n "${EMMA_HMAC_KEY_BASE64:-}" ]]; then
    "$SCRIPT_DIR/sign-plugin.sh" "$MANIFEST_OUT"
  fi

  ( cd "$PACKAGE_ROOT" && zip -r "../${PLUGIN_ID}_${PLUGIN_VERSION}_${TARGET}.zip" . ) >/dev/null

  echo "Packaged plugin: $PACK_DIR/${PLUGIN_ID}_${PLUGIN_VERSION}_${TARGET}.zip"
done
