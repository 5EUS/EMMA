#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
PLUGIN_DIR=$(cd "$SCRIPT_DIR/.." && pwd)
ROOT_DIR="$PLUGIN_DIR"
MANIFEST_PATH="${1:-$PLUGIN_DIR/EMMA.TemplatePlugin.plugin.json}"
OUT_DIR="$PLUGIN_DIR/artifacts"
PACK_DIR="$OUT_DIR/pack"
HOST_OS="$(uname -s)"
DEFAULT_TARGETS="wasm"
TARGETS=${TARGETS:-"$DEFAULT_TARGETS"}
WASM_MODULE_PATH="${WASM_MODULE_PATH:-$OUT_DIR/wasm/plugin.wasm}"
WASM_PACKAGE_FILE_NAME="${WASM_PACKAGE_FILE_NAME:-plugin.wasm}"
WASM_PROJECT_PATH="${WASM_PROJECT_PATH:-$PLUGIN_DIR/EMMA.TemplatePlugin.Wasm.csproj}"
WASM_BUILD_CONFIGURATION="${WASM_BUILD_CONFIGURATION:-Release}"
WASM_BUILD_RID="${WASM_BUILD_RID:-wasi-wasm}"
WASM_BUILD_OUTPUT="${WASM_BUILD_OUTPUT:-$OUT_DIR/wasm-publish}"
WASM_OUTPUT_NAME="${WASM_OUTPUT_NAME:-}"
WASM_BUILD_TOOLCHAIN="${WASM_BUILD_TOOLCHAIN:-componentize}"
WASM_NATIVE_CODEGEN="${WASM_NATIVE_CODEGEN:-none}"
SKIP_WASM_BUILD="${SKIP_WASM_BUILD:-0}"
CWASM_WASMTIME_TARGET="${CWASM_WASMTIME_TARGET:-}"
CWASM_WASMTIME_BIN="${CWASM_WASMTIME_BIN:-wasmtime}"
CWASM_EXPECTED_WASMTIME_VERSION="${CWASM_EXPECTED_WASMTIME_VERSION:-34.0.2}"
CWASM_PRECOMPILE_TOOL="${CWASM_PRECOMPILE_TOOL:-$ROOT_DIR/tools/emma_cwasm_precompile}"
SIGNING_PRIVATE_KEY_BASE64="${EMMA_PLUGIN_SIGNING_PRIVATE_KEY_BASE64:-${EMMA_HMAC_KEY_BASE64:-}}"
SIGNING_PRIVATE_KEY_PEM="${EMMA_PLUGIN_SIGNING_PRIVATE_KEY_PEM:-}"
SIGNING_KEY_ID="${EMMA_PLUGIN_SIGNING_KEY_ID:-${EMMA_PLUGIN_SIGNATURE_KEY_ID:-change-me-release-key}}"
SIGNING_REPOSITORY_ID="${EMMA_PLUGIN_REPOSITORY_ID:-change-me}"
SIGNING_ISSUED_AT_UTC="${EMMA_PLUGIN_SIGNATURE_ISSUED_AT_UTC:-}"
SIGNING_EXPIRES_AT_UTC="${EMMA_PLUGIN_SIGNATURE_EXPIRES_AT_UTC:-}"

resolve_env_flag() {
  local value="$1"
  if [[ -z "$value" ]]; then
    echo "0"
    return 0
  fi

  case "${value,,}" in
    1|true|yes|on)
      echo "1"
      ;;
    *)
      echo "0"
      ;;
  esac
}

REQUIRE_SIGNED_PLUGINS_VALUE="${EMMA_REQUIRE_SIGNED_PLUGINS:-${PluginSignature__RequireSignedPlugins:-}}"
REQUIRE_SIGNING="$(resolve_env_flag "$REQUIRE_SIGNED_PLUGINS_VALUE")"

build_wasm_component() {
  rm -rf "$WASM_BUILD_OUTPUT"
  mkdir -p "$WASM_BUILD_OUTPUT"

  if [[ "$WASM_BUILD_RID" == "wasi-wasm" ]]; then
    if [[ -z "${WASI_SDK_PATH:-}" ]]; then
      echo "WASM build target '$WASM_BUILD_RID' requires WASI SDK." >&2
      echo "Set WASI_SDK_PATH to your extracted wasi-sdk directory." >&2
      exit 1
    fi

    if [[ ! -d "$WASI_SDK_PATH" ]]; then
      echo "WASI_SDK_PATH does not exist: $WASI_SDK_PATH" >&2
      exit 1
    fi
  fi

  local project_dir
  project_dir="$(dirname "$WASM_PROJECT_PATH")"
  rm -rf "$project_dir/bin/" "$project_dir/obj/"

  dotnet restore "$WASM_PROJECT_PATH" --no-cache --force-evaluate --runtime "$WASM_BUILD_RID" >/dev/null

  WASI_SDK_PATH="$WASI_SDK_PATH" dotnet publish "$WASM_PROJECT_PATH" \
    -c "$WASM_BUILD_CONFIGURATION" \
    -r "$WASM_BUILD_RID" \
    --self-contained true \
    -p:PublishAot=false \
    -p:NativeCodeGen="$WASM_NATIVE_CODEGEN" \
    -p:DebugType=None \
    -p:DebugSymbols=false \
    -p:WasmSingleFileBundle=true \
    -o "$WASM_BUILD_OUTPUT"

  local expected_name
  if [[ -n "$WASM_OUTPUT_NAME" ]]; then
    expected_name="$WASM_OUTPUT_NAME"
  else
    expected_name="$(basename "$WASM_PROJECT_PATH" .csproj).wasm"
  fi

  local built_wasm=""
  mapfile -t wasm_candidates_by_name < <(find "$WASM_BUILD_OUTPUT" -type f -name "$expected_name" 2>/dev/null)
  if [[ ${#wasm_candidates_by_name[@]} -eq 1 ]]; then
    built_wasm="${wasm_candidates_by_name[0]}"
  fi

  if [[ -z "$built_wasm" ]]; then
    mapfile -t wasm_candidates < <(find "$WASM_BUILD_OUTPUT" -type f -name "*.wasm" ! -name "dotnet.wasm" 2>/dev/null)
    if [[ ${#wasm_candidates[@]} -eq 1 ]]; then
      built_wasm="${wasm_candidates[0]}"
    fi
  fi

  if [[ -z "$built_wasm" ]]; then
    echo "No bundled .wasm output found for: $expected_name" >&2
    exit 1
  fi

  mkdir -p "$(dirname "$WASM_MODULE_PATH")"
  cp "$built_wasm" "$WASM_MODULE_PATH"
}

if [[ ! -f "$MANIFEST_PATH" ]]; then
  echo "Manifest not found: $MANIFEST_PATH" >&2
  exit 1
fi

if [[ -x "$ROOT_DIR/scripts/plugin-validate-manifest.sh" ]]; then
  "$ROOT_DIR/scripts/plugin-validate-manifest.sh" "$MANIFEST_PATH"
fi

if [[ "$REQUIRE_SIGNING" == "1" && -z "$SIGNING_PRIVATE_KEY_BASE64" && -z "$SIGNING_PRIVATE_KEY_PEM" ]]; then
  echo "Signed plugins are required, but no delegated signing key is configured." >&2
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

print(manifest.get("id") or "plugin")
print(manifest.get("version") or "0.0.0")
PY
)

PLUGIN_ID="${manifest_fields[0]}"
PLUGIN_VERSION="${manifest_fields[1]}"

mkdir -p "$PACK_DIR"

for TARGET in $TARGETS; do
  PACKAGE_ROOT="$PACK_DIR/$PLUGIN_VERSION-$TARGET"
  MANIFEST_OUT_DIR="$PACKAGE_ROOT/manifest"
  PLUGIN_OUT_DIR="$PACKAGE_ROOT/$PLUGIN_ID"

  rm -rf "$PACKAGE_ROOT" "$PACK_DIR/${PLUGIN_ID}_${PLUGIN_VERSION}_${TARGET}.zip"
  mkdir -p "$MANIFEST_OUT_DIR" "$PLUGIN_OUT_DIR"

  case "$TARGET" in
    wasm)
      if [[ "$SKIP_WASM_BUILD" != "1" ]]; then
        build_wasm_component
      fi
      cp "$WASM_MODULE_PATH" "$PLUGIN_OUT_DIR/$WASM_PACKAGE_FILE_NAME"
      ;;
    *)
      echo "Unsupported packaging target: $TARGET" >&2
      exit 1
      ;;
  esac

  MANIFEST_OUT="$MANIFEST_OUT_DIR/$PLUGIN_ID.json"
  cp "$MANIFEST_PATH" "$MANIFEST_OUT"

  if [[ -n "$SIGNING_PRIVATE_KEY_BASE64" || -n "$SIGNING_PRIVATE_KEY_PEM" ]]; then
    EMMA_PLUGIN_SIGNING_PRIVATE_KEY_BASE64="$SIGNING_PRIVATE_KEY_BASE64" \
    EMMA_PLUGIN_SIGNING_PRIVATE_KEY_PEM="$SIGNING_PRIVATE_KEY_PEM" \
    EMMA_PLUGIN_SIGNING_KEY_ID="$SIGNING_KEY_ID" \
    EMMA_PLUGIN_REPOSITORY_ID="$SIGNING_REPOSITORY_ID" \
    EMMA_PLUGIN_SIGNATURE_ISSUED_AT_UTC="$SIGNING_ISSUED_AT_UTC" \
    EMMA_PLUGIN_SIGNATURE_EXPIRES_AT_UTC="$SIGNING_EXPIRES_AT_UTC" \
    "$SCRIPT_DIR/sign-plugin.sh" "$MANIFEST_OUT" "$PLUGIN_OUT_DIR"
  fi

  ( cd "$PACKAGE_ROOT" && zip -r "../${PLUGIN_ID}_${PLUGIN_VERSION}_${TARGET}.zip" . ) >/dev/null
  echo "Packaged plugin: $PACK_DIR/${PLUGIN_ID}_${PLUGIN_VERSION}_${TARGET}.zip"
done