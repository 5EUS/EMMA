#!/usr/bin/env bash
set -euo pipefail

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

echo "PluginHost publish succeeded: $OUTPUT_DIR"
echo "Set EMMA_PLUGIN_HOST_EXECUTABLE to the published host binary path."
