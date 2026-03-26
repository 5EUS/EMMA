#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RID="${1:-}"

if [[ -z "$RID" ]]; then
  echo "Usage: $0 <rid>"
  echo "Example: $0 osx-arm64"
  exit 1
fi

case "$RID" in
  osx-*)
    echo "Publishing EMMA.TestPlugin app bundle for $RID..."
    "$ROOT_DIR/src/EMMA.TestPlugin/scripts/build-plugin-macos-app.sh"
    ;;
  linux-*)
    echo "Publishing EMMA.TestPlugin Linux bundle for $RID..."
    bash "$ROOT_DIR/src/EMMA.TestPlugin/scripts/build-plugin-linux-bundle.sh" "$RID"
    ;;
  *)
    echo "Test plugin publish is currently supported only for macOS and Linux RIDs."
    exit 1
    ;;
esac

echo "Test plugin publish succeeded."
