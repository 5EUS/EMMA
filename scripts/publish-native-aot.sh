#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT_PATH="$ROOT_DIR/src/EMMA.Native/EMMA.Native.csproj"
RID="${1:-}"
OUTPUT_DIR="${2:-}"

if [[ -z "$RID" ]]; then
  echo "Usage: $0 <rid> [output-dir]"
  echo "Example: $0 osx-arm64"
  exit 1
fi

if [[ -z "$OUTPUT_DIR" ]]; then
  OUTPUT_DIR="$ROOT_DIR/artifacts/aot/EMMA.Native/$RID"
fi

NATIVE_LIB_TYPE="Shared"
if [[ "$RID" == ios-* || "$RID" == iossimulator-* ]]; then
  NATIVE_LIB_TYPE="Static"
fi

rm -rf "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"

echo "Publishing EMMA.Native NativeAOT for $RID..."

dotnet publish "$PROJECT_PATH" \
  -c Release \
  -r "$RID" \
  -p:PublishAot=true \
  -p:SelfContained=true \
  -p:NativeLib="$NATIVE_LIB_TYPE" \
  -p:NativeLibName=emma_native \
  -o "$OUTPUT_DIR"

EXPECTED_NAME=""
SOURCE_NAME=""

case "$RID" in
  osx-*)
    EXPECTED_NAME="libemma_native.dylib"
    SOURCE_NAME="EMMA.Native.dylib"
    ;;
  win-*)
    EXPECTED_NAME="emma_native.dll"
    SOURCE_NAME="EMMA.Native.dll"
    ;;
  linux-*|android-*)
    EXPECTED_NAME="libemma_native.so"
    SOURCE_NAME="EMMA.Native.so"
    ;;
  ios-*|iossimulator-*)
    EXPECTED_NAME="libemma_native.a"
    SOURCE_NAME="libEMMA.Native.a"
    ;;
esac

if [[ -n "$EXPECTED_NAME" && -n "$SOURCE_NAME" ]]; then
  if [[ -f "$OUTPUT_DIR/$SOURCE_NAME" && ! -f "$OUTPUT_DIR/$EXPECTED_NAME" ]]; then
    cp "$OUTPUT_DIR/$SOURCE_NAME" "$OUTPUT_DIR/$EXPECTED_NAME"
  fi
fi

if [[ "$RID" == linux-* || "$RID" == android-* ]]; then
  if [[ ! -f "$OUTPUT_DIR/$EXPECTED_NAME" ]]; then
    for candidate in "EMMA.Native.so" "libEMMA.Native.so" "emma_native.so"; do
      if [[ -f "$OUTPUT_DIR/$candidate" ]]; then
        cp "$OUTPUT_DIR/$candidate" "$OUTPUT_DIR/$EXPECTED_NAME"
        break
      fi
    done
  fi
fi

echo "AOT publish succeeded. Output: $OUTPUT_DIR"
