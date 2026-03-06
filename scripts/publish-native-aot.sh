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

# Use static libraries only for iOS (App Store requirement)
# Use dynamic libraries for desktop/Linux/Android (easier deployment, code signing)
NATIVE_LIB_TYPE="Shared"
case "$RID" in
  ios-*|iossimulator-*)
    NATIVE_LIB_TYPE="Static"
    ;;
esac

rm -rf "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"

echo "Publishing EMMA.Native NativeAOT (static library) for $RID..."

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
    if [[ "$NATIVE_LIB_TYPE" == "Static" ]]; then
      EXPECTED_NAME="libemma_native.a"
      SOURCE_NAME="EMMA.Native.a"
    else
      EXPECTED_NAME="libemma_native.dylib"
      SOURCE_NAME="EMMA.Native.dylib"
    fi
    ;;
  win-*)
    if [[ "$NATIVE_LIB_TYPE" == "Static" ]]; then
      EXPECTED_NAME="emma_native.lib"
      SOURCE_NAME="EMMA.Native.lib"
    else
      EXPECTED_NAME="emma_native.dll"
      SOURCE_NAME="EMMA.Native.dll"
    fi
    ;;
  linux-*)
    if [[ "$NATIVE_LIB_TYPE" == "Static" ]]; then
      EXPECTED_NAME="libemma_native.a"
      SOURCE_NAME="EMMA.Native.a"
    else
      EXPECTED_NAME="libemma_native.so"
      SOURCE_NAME="EMMA.Native.so"
    fi
    ;;
  android-*)
    if [[ "$NATIVE_LIB_TYPE" == "Static" ]]; then
      EXPECTED_NAME="libemma_native.a"
      SOURCE_NAME="EMMA.Native.a"
    else
      EXPECTED_NAME="libemma_native.so"
      SOURCE_NAME="EMMA.Native.so"
    fi
    ;;
  ios-*|iossimulator-*)
    EXPECTED_NAME="libemma_native.a"
    SOURCE_NAME="EMMA.Native.a"
    ;;
esac

if [[ -n "$EXPECTED_NAME" && -n "$SOURCE_NAME" ]]; then
  if [[ -f "$OUTPUT_DIR/$SOURCE_NAME" && ! -f "$OUTPUT_DIR/$EXPECTED_NAME" ]]; then
    cp "$OUTPUT_DIR/$SOURCE_NAME" "$OUTPUT_DIR/$EXPECTED_NAME"
  fi
fi

LIB_TYPE_DESC="dynamic"
if [[ "$NATIVE_LIB_TYPE" == "Static" ]]; then
  LIB_TYPE_DESC="static"
fi

echo "AOT publish succeeded. Output: $OUTPUT_DIR"
echo "Native library ($LIB_TYPE_DESC, with embedded PluginHost): $OUTPUT_DIR/$EXPECTED_NAME"
