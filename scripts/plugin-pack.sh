#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 1 ]]; then
  cat >&2 <<'USAGE'
Usage: scripts/plugin-pack.sh <plugin-dir> [manifest-path]

Environment:
  TARGETS                      Packaging targets (default delegated to plugin build-pack script)
  EMMA_HMAC_KEY_BASE64         Optional manifest signing key
  WASM_MODULE_PATH             Optional prebuilt wasm/cwasm artifact path
  SKIP_WASM_BUILD              Set to 1 to skip wasm build in plugin script
USAGE
  exit 1
fi

PLUGIN_DIR="$1"
MANIFEST_PATH="${2:-}"

if [[ ! -d "$PLUGIN_DIR" ]]; then
  echo "Plugin directory not found: $PLUGIN_DIR" >&2
  exit 1
fi

PACK_SCRIPT="$PLUGIN_DIR/scripts/build-pack-plugin.sh"
if [[ ! -x "$PACK_SCRIPT" ]]; then
  echo "Plugin pack script not found or not executable: $PACK_SCRIPT" >&2
  exit 1
fi

if [[ -z "$MANIFEST_PATH" ]]; then
  MANIFEST_PATH="$(find "$PLUGIN_DIR" -maxdepth 1 -type f -name "*.plugin.json" | head -n 1)"
fi

if [[ -z "$MANIFEST_PATH" || ! -f "$MANIFEST_PATH" ]]; then
  echo "Plugin manifest not found. Provide it explicitly as second argument." >&2
  exit 1
fi

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
"$ROOT_DIR/scripts/plugin-validate-manifest.sh" "$MANIFEST_PATH"

"$PACK_SCRIPT" "$MANIFEST_PATH"
