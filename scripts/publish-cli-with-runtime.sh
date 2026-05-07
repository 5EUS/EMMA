#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CLI_PROJECT="$ROOT_DIR/src/EMMA.Cli/EMMA.Cli.csproj"
RID="${1:-}"
OUTPUT_DIR="${2:-}"
CONFIGURATION="${CONFIGURATION:-Release}"
SELF_CONTAINED="${SELF_CONTAINED:-false}"

if [[ -z "$RID" ]]; then
  case "$(uname -s)" in
    Linux)
      RID="linux-x64"
      ;;
    Darwin)
      if [[ "$(uname -m)" == "arm64" ]]; then
        RID="osx-arm64"
      else
        RID="osx-x64"
      fi
      ;;
    CYGWIN*|MINGW*|MSYS*)
      RID="win-x64"
      ;;
    *)
      echo "Unable to infer RID for host OS '$(uname -s)'. Pass an explicit RID like linux-x64." >&2
      exit 1
      ;;
  esac
fi

if [[ -z "$OUTPUT_DIR" ]]; then
  OUTPUT_DIR="$ROOT_DIR/artifacts/cli/$RID"
fi

echo "Building native WASM runtime sidecar for $RID..."
"$ROOT_DIR/scripts/build-wasm-runtime-native.sh" "$RID"

echo "Publishing EMMA.Cli for $RID into $OUTPUT_DIR..."
dotnet publish "$CLI_PROJECT" \
  -c "$CONFIGURATION" \
  -r "$RID" \
  --self-contained "$SELF_CONTAINED" \
  -o "$OUTPUT_DIR"

RUNTIME_DIR="$OUTPUT_DIR/runtimes/wasm-runtime-native/$RID"
if [[ ! -d "$RUNTIME_DIR" ]]; then
  echo "Published CLI did not include packaged native runtime artifacts under $RUNTIME_DIR" >&2
  exit 1
fi

echo "CLI publish succeeded with packaged runtime artifacts: $OUTPUT_DIR"
echo "Packaged runtime sidecar: $RUNTIME_DIR"