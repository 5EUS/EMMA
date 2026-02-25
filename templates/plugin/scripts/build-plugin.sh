#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
PLUGIN_DIR=$(cd "$SCRIPT_DIR/.." && pwd)
OUT_DIR="$PLUGIN_DIR/artifacts"

mkdir -p "$OUT_DIR"

dotnet publish "$PLUGIN_DIR/EMMA.PluginTemplate.csproj" -c Release -o "$OUT_DIR"

ENTRYPOINT="EMMA.PluginTemplate"
if [[ "$OSTYPE" == "msys"* || "$OSTYPE" == "cygwin"* || "$OSTYPE" == "win32"* ]]; then
  ENTRYPOINT+=".exe"
fi

echo "Built plugin to: $OUT_DIR"
echo "Entrypoint: $ENTRYPOINT"
