#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT_PATH="$ROOT_DIR/src/EMMA.Cli/EMMA.Cli.csproj"
PUBLISH_DIR="$ROOT_DIR/artifacts/aot/EMMA.Cli"

rm -rf "$PUBLISH_DIR"
mkdir -p "$PUBLISH_DIR"

echo "Validating Native AOT publish for EMMA.Cli..."

dotnet publish "$PROJECT_PATH" \
  -c Release \
  -r osx-arm64 \
  -p:PublishAot=true \
  -p:SelfContained=true \
  -o "$PUBLISH_DIR"

echo "AOT publish succeeded. Output: $PUBLISH_DIR"
