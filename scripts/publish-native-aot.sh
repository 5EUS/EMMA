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

HOST_OS="$(uname -s 2>/dev/null || echo unknown)"
ALLOW_CROSS_OS_NATIVE_AOT="${EMMA_ALLOW_CROSS_OS_NATIVE_AOT:-0}"

if [[ "$RID" == win-* && "$HOST_OS" != CYGWIN* && "$HOST_OS" != MINGW* && "$HOST_OS" != MSYS* && "$ALLOW_CROSS_OS_NATIVE_AOT" != "1" ]]; then
  echo "NativeAOT publish for '$RID' is not supported from host OS '$HOST_OS'."
  echo ""
  echo "Reason: .NET NativeAOT cross-OS native compilation is not supported for this lane."
  echo ""
  echo "Run this step on Windows instead:"
  echo "  ./scripts/publish-native-aot.sh $RID"
  echo ""
  echo "If you intentionally want to bypass this preflight, set EMMA_ALLOW_CROSS_OS_NATIVE_AOT=1."
  exit 1
fi

# Use static libraries only for iOS (App Store requirement)
# Use dynamic libraries for desktop/Linux/Android (easier deployment, code signing)
NATIVE_LIB_TYPE="Shared"
TARGET_FRAMEWORK="net10.0"
PUBLISH_AOT_USING_RUNTIME_PACK="false"
EMMA_IOS_AOT_BUILD="false"
VALIDATE_XCODE_VERSION="${VALIDATE_XCODE_VERSION:-false}"
IOS_NATIVE_LIB_TYPE="${IOS_NATIVE_LIB_TYPE:-Shared}"
case "$RID" in
  ios-*|iossimulator-*)
    NATIVE_LIB_TYPE="$IOS_NATIVE_LIB_TYPE"
    PUBLISH_AOT_USING_RUNTIME_PACK="true"
    EMMA_IOS_AOT_BUILD="true"
    ;;
esac

rm -rf "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"

echo "Publishing EMMA.Native NativeAOT (static library) for $RID..."

dotnet publish "$PROJECT_PATH" \
  -f "$TARGET_FRAMEWORK" \
  -c Release \
  -r "$RID" \
  -p:PublishAot=true \
  -p:PublishAotUsingRuntimePack="$PUBLISH_AOT_USING_RUNTIME_PACK" \
  -p:SelfContained=true \
  -p:NativeLib="$NATIVE_LIB_TYPE" \
  -p:NativeLibName=emma_native \
  -p:EMMAIosAotBuild="$EMMA_IOS_AOT_BUILD" \
  -p:ValidateXcodeVersion="$VALIDATE_XCODE_VERSION" \
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
    if [[ "$NATIVE_LIB_TYPE" == "Static" ]]; then
      EXPECTED_NAME="libemma_native.a"
      SOURCE_NAME="EMMA.Native.a"
    else
      EXPECTED_NAME="libemma_native.dylib"
      SOURCE_NAME="EMMA.Native.dylib"
    fi
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
