#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
PLUGIN_DIR=$(cd "$SCRIPT_DIR/.." && pwd)
ROOT_DIR="$PLUGIN_DIR"
MANIFEST_PATH="${1:-$PLUGIN_DIR/EMMA.TemplatePlugin.plugin.json}"
OUT_DIR="$PLUGIN_DIR/artifacts"
PACK_DIR="$OUT_DIR/pack"
ASPNET_BUILD_CONFIGURATION="${ASPNET_BUILD_CONFIGURATION:-Release}"
ASPNET_PROJECT_PATH="${ASPNET_PROJECT_PATH:-$PLUGIN_DIR/EMMA.TemplatePlugin.csproj}"
EMMA_SDK_VERSION="${EMMA_SDK_VERSION:-}"
ASPNET_NO_RESTORE="${ASPNET_NO_RESTORE:-0}"
HOST_OS="$(uname -s)"
DEFAULT_TARGETS="osx-arm64"
if [[ "$HOST_OS" == "Linux" ]]; then
  DEFAULT_TARGETS="linux-x64"
fi
TARGETS="${TARGETS:-$DEFAULT_TARGETS}"

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

SIGNING_KEY_BASE64="${EMMA_PLUGIN_SIGNING_PRIVATE_KEY_BASE64:-${EMMA_HMAC_KEY_BASE64:-}}"
SIGNING_KEY_PEM="${EMMA_PLUGIN_SIGNING_PRIVATE_KEY_PEM:-}"
SIGNING_KEY_ID="${EMMA_PLUGIN_SIGNING_KEY_ID:-${EMMA_PLUGIN_SIGNATURE_KEY_ID:-change-me-release-key}}"
SIGNING_REPOSITORY_ID="${EMMA_PLUGIN_REPOSITORY_ID:-change-me}"
SIGNING_ISSUED_AT_UTC="${EMMA_PLUGIN_SIGNATURE_ISSUED_AT_UTC:-}"
SIGNING_EXPIRES_AT_UTC="${EMMA_PLUGIN_SIGNATURE_EXPIRES_AT_UTC:-}"
REQUIRE_SIGNED_PLUGINS_VALUE="${EMMA_REQUIRE_SIGNED_PLUGINS:-${PluginSignature__RequireSignedPlugins:-}}"
REQUIRE_SIGNING="$(resolve_env_flag "$REQUIRE_SIGNED_PLUGINS_VALUE")"
SIGN_MANIFEST_IN_PLACE="${SIGN_MANIFEST_IN_PLACE:-0}"

if [[ ! -f "$MANIFEST_PATH" ]]; then
  echo "Manifest not found: $MANIFEST_PATH" >&2
  exit 1
fi

if [[ "${MANIFEST_PATH##*.}" == "csproj" ]]; then
  echo "Expected a plugin manifest JSON, but got a .csproj: $MANIFEST_PATH" >&2
  echo "Usage: TARGETS=\"linux-x64\" ./build-pack-plugin-aspnet.sh /path/to/plugin.plugin.json" >&2
  exit 1
fi

if [[ ! -f "$ASPNET_PROJECT_PATH" ]]; then
  echo "ASP.NET project not found: $ASPNET_PROJECT_PATH" >&2
  exit 1
fi

if [[ -x "$ROOT_DIR/scripts/plugin-validate-manifest.sh" ]]; then
  "$ROOT_DIR/scripts/plugin-validate-manifest.sh" "$MANIFEST_PATH"
fi

if [[ "$REQUIRE_SIGNING" == "1" && -z "$SIGNING_KEY_BASE64" && -z "$SIGNING_KEY_PEM" ]]; then
  echo "Signed plugins are required, but no signing key is set." >&2
  echo "Set EMMA_PLUGIN_SIGNING_PRIVATE_KEY_BASE64 (or EMMA_HMAC_KEY_BASE64 alias) or EMMA_PLUGIN_SIGNING_PRIVATE_KEY_PEM." >&2
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

PLUGIN_ID="${manifest_fields[0]}"
PLUGIN_NAME="${manifest_fields[1]}"
PLUGIN_VERSION="${manifest_fields[2]}"
ENTRYPOINT_NAME=$(echo "$PLUGIN_NAME" | tr -d '[:space:]')
if [[ -z "$ENTRYPOINT_NAME" ]]; then
  ENTRYPOINT_NAME="$PLUGIN_ID"
fi

mkdir -p "$PACK_DIR"

for TARGET in $TARGETS; do
  BUILD_DIR="$OUT_DIR/build-$TARGET"
  PUBLISH_DIR="$BUILD_DIR/publish"
  PACKAGE_ROOT="$PACK_DIR/$PLUGIN_VERSION-$TARGET"
  MANIFEST_OUT_DIR="$PACKAGE_ROOT/manifest"
  PLUGIN_OUT_DIR="$PACKAGE_ROOT/$PLUGIN_ID"

  rm -rf "$BUILD_DIR" "$PACKAGE_ROOT" "$PACK_DIR/${PLUGIN_ID}_${PLUGIN_VERSION}_${TARGET}.zip"
  mkdir -p "$PUBLISH_DIR" "$MANIFEST_OUT_DIR" "$PLUGIN_OUT_DIR"

  publish_args=(
    -c "$ASPNET_BUILD_CONFIGURATION"
    -r "$TARGET"
    --self-contained false
    -p:UseAppHost=true
    -p:PublishSingleFile=true
    -o "$PUBLISH_DIR"
  )

  if [[ -n "$EMMA_SDK_VERSION" ]]; then
    publish_args+=("-p:EmmaSdkVersion=$EMMA_SDK_VERSION")
  fi

  if [[ "$ASPNET_NO_RESTORE" == "1" ]]; then
    publish_args+=("--no-restore")
  fi

  dotnet publish "$ASPNET_PROJECT_PATH" "${publish_args[@]}"

  find "$PUBLISH_DIR" -maxdepth 1 -type f \( -name "*.pdb" -o -name "*.dbg" -o -name "*.xml" \) -delete || true
  rm -f "$PUBLISH_DIR/createdump"

  cp -R "$PUBLISH_DIR"/. "$PLUGIN_OUT_DIR/"

  MANIFEST_OUT="$MANIFEST_OUT_DIR/$PLUGIN_ID.json"
  cp "$MANIFEST_PATH" "$MANIFEST_OUT"

  if [[ -n "$SIGNING_KEY_BASE64" || -n "$SIGNING_KEY_PEM" ]]; then
    EMMA_PLUGIN_SIGNING_PRIVATE_KEY_BASE64="$SIGNING_KEY_BASE64" \
    EMMA_PLUGIN_SIGNING_PRIVATE_KEY_PEM="$SIGNING_KEY_PEM" \
    EMMA_PLUGIN_SIGNING_KEY_ID="$SIGNING_KEY_ID" \
    EMMA_PLUGIN_REPOSITORY_ID="$SIGNING_REPOSITORY_ID" \
    EMMA_PLUGIN_SIGNATURE_ISSUED_AT_UTC="$SIGNING_ISSUED_AT_UTC" \
    EMMA_PLUGIN_SIGNATURE_EXPIRES_AT_UTC="$SIGNING_EXPIRES_AT_UTC" \
    "$SCRIPT_DIR/sign-plugin.sh" "$MANIFEST_OUT" "$PLUGIN_OUT_DIR"

    if [[ "$SIGN_MANIFEST_IN_PLACE" == "1" ]]; then
      cp "$MANIFEST_OUT" "$MANIFEST_PATH"
    fi
  fi

  ( cd "$PACKAGE_ROOT" && zip -r "../${PLUGIN_ID}_${PLUGIN_VERSION}_${TARGET}.zip" . ) >/dev/null
  echo "Packaged ASP.NET plugin: $PACK_DIR/${PLUGIN_ID}_${PLUGIN_VERSION}_${TARGET}.zip"
done