#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
EMMAUI_DIR="${1:-$ROOT_DIR/../emmaui}"
IOS_FRAMEWORK_DIR="${2:-$ROOT_DIR/artifacts/ios-native-framework}"

BUILD_SIMULATOR="${BUILD_SIMULATOR:-1}"
RUN_POD_INSTALL="${RUN_POD_INSTALL:-1}"
BUILD_NATIVE_IOS_LIBS="${BUILD_NATIVE_IOS_LIBS:-0}"

if [[ ! -d "$EMMAUI_DIR" ]]; then
  echo "emmaui directory does not exist: $EMMAUI_DIR"
  exit 1
fi

if [[ "$BUILD_NATIVE_IOS_LIBS" != "1" && -z "${EXISTING_NATIVE_XCFRAMEWORK:-}" && ! -f "${NATIVE_DEVICE_LIB:-}" ]]; then
  if [[ ! -f "$ROOT_DIR/artifacts/aot/EMMA.Native/ios-arm64/libemma_native.a" ]]; then
    echo "No iOS native input configured."
    echo "Provide one of:"
    echo "  - EXISTING_NATIVE_XCFRAMEWORK=/absolute/path/to/EmmaNative.xcframework"
    echo "  - NATIVE_DEVICE_LIB=/absolute/path/to/libemma_native.a (and optional NATIVE_SIM_LIB)"
    echo "  - BUILD_NATIVE_IOS_LIBS=1 (builds in-repo NativeAOT iOS static lib lane)"
    exit 1
  fi
fi

echo "[1/2] Building iOS XCFramework artifacts..."
BUILD_SIMULATOR="$BUILD_SIMULATOR" \
BUILD_NATIVE_IOS_LIBS="$BUILD_NATIVE_IOS_LIBS" \
EXISTING_NATIVE_XCFRAMEWORK="${EXISTING_NATIVE_XCFRAMEWORK:-}" \
NATIVE_DEVICE_LIB="${NATIVE_DEVICE_LIB:-}" \
NATIVE_SIM_LIB="${NATIVE_SIM_LIB:-}" \
"$ROOT_DIR/scripts/build-ios-native-framework.sh" "$IOS_FRAMEWORK_DIR"

echo "[2/2] Syncing iOS XCFrameworks into emmaui local pod..."
RUN_POD_INSTALL="$RUN_POD_INSTALL" \
"$ROOT_DIR/scripts/sync-ios-native-framework-to-emmaui.sh" "$EMMAUI_DIR" "$IOS_FRAMEWORK_DIR"

echo "Done. iOS runtime lane completed successfully."
