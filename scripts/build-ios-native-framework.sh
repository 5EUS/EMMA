#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUTPUT_DIR="${1:-$ROOT_DIR/artifacts/ios-native-framework}"

IOS_DEVICE_RID="ios-arm64"
IOS_SIM_RID="iossimulator-arm64"

BUILD_NATIVE_IOS_LIBS="${BUILD_NATIVE_IOS_LIBS:-0}"
BUILD_SIMULATOR="${BUILD_SIMULATOR:-1}"
ALLOW_DEVICE_ONLY_NATIVE="${ALLOW_DEVICE_ONLY_NATIVE:-1}"

NATIVE_DEVICE_LIB="${NATIVE_DEVICE_LIB:-$ROOT_DIR/artifacts/aot/EMMA.Native/$IOS_DEVICE_RID/libemma_native.a}"
NATIVE_SIM_LIB="${NATIVE_SIM_LIB:-$ROOT_DIR/artifacts/aot/EMMA.Native/$IOS_SIM_RID/libemma_native.a}"
EXISTING_NATIVE_XCFRAMEWORK="${EXISTING_NATIVE_XCFRAMEWORK:-}"

DEVICE_WASM_DIR="$ROOT_DIR/artifacts/wasm-runtime-native/$IOS_DEVICE_RID"
SIM_WASM_DIR="$ROOT_DIR/artifacts/wasm-runtime-native/$IOS_SIM_RID"

TMP_DIR="$OUTPUT_DIR/.tmp"
HEADERS_DIR="$TMP_DIR/Headers"
NATIVE_FRAMEWORKS_DIR="$TMP_DIR/native-frameworks"

echo "[1/4] Resolving EMMA.Native iOS artifacts..."
if [[ "$BUILD_NATIVE_IOS_LIBS" == "1" ]]; then
  echo "Publishing EMMA.Native NativeAOT iOS static libs via runtime-pack lane..."
  if ! "$ROOT_DIR/scripts/publish-native-aot.sh" "$IOS_DEVICE_RID"; then
    echo ""
    echo "NativeAOT iOS publish failed for $IOS_DEVICE_RID."
    echo "Use a prebuilt production EmmaNative.xcframework instead:"
    echo "  EXISTING_NATIVE_XCFRAMEWORK=/absolute/path/to/EmmaNative.xcframework BUILD_SIMULATOR=$BUILD_SIMULATOR ./scripts/publish-and-sync-ios-runtime.sh"
    exit 1
  fi
  if [[ "$BUILD_SIMULATOR" == "1" ]]; then
    if ! "$ROOT_DIR/scripts/publish-native-aot.sh" "$IOS_SIM_RID"; then
      echo ""
      echo "NativeAOT iOS publish failed for $IOS_SIM_RID."
      echo "Use a prebuilt production EmmaNative.xcframework that includes simulator support,"
      echo "or run with BUILD_SIMULATOR=0 for a device-only lane."
      exit 1
    fi
  fi
fi

echo "[2/4] Building WASM runtime static libraries for iOS device/simulator..."
"$ROOT_DIR/scripts/build-wasm-runtime-native.sh" "$IOS_DEVICE_RID"
if [[ "$BUILD_SIMULATOR" == "1" ]]; then
  "$ROOT_DIR/scripts/build-wasm-runtime-native.sh" "$IOS_SIM_RID"
fi

DEVICE_NATIVE_LIB="$NATIVE_DEVICE_LIB"
SIM_NATIVE_LIB="$NATIVE_SIM_LIB"
DEVICE_WASM_LIB="$DEVICE_WASM_DIR/libemma_wasm_runtime.a"
SIM_WASM_LIB="$SIM_WASM_DIR/libemma_wasm_runtime.a"

if [[ ! -f "$DEVICE_NATIVE_LIB" ]]; then
  candidate="${DEVICE_NATIVE_LIB%.a}.dylib"
  if [[ -f "$candidate" ]]; then
    DEVICE_NATIVE_LIB="$candidate"
  fi
fi

if [[ ! -f "$SIM_NATIVE_LIB" ]]; then
  candidate="${SIM_NATIVE_LIB%.a}.dylib"
  if [[ -f "$candidate" ]]; then
    SIM_NATIVE_LIB="$candidate"
  fi
fi

for required in \
  "$DEVICE_WASM_LIB"; do
  if [[ ! -f "$required" ]]; then
    echo "Missing expected library: $required"
    exit 1
  fi
done

if [[ "$BUILD_SIMULATOR" == "1" && ! -f "$SIM_WASM_LIB" ]]; then
  echo "Missing expected library: $SIM_WASM_LIB"
  exit 1
fi

echo "[3/4] Creating XCFramework artifacts..."

rm -rf "$OUTPUT_DIR"
mkdir -p "$HEADERS_DIR"
mkdir -p "$NATIVE_FRAMEWORKS_DIR"

cat > "$HEADERS_DIR/emma_native.h" <<'EOF'
#ifndef EMMA_NATIVE_H
#define EMMA_NATIVE_H

#ifdef __cplusplus
extern "C" {
#endif

int emma_runtime_start(void);

#ifdef __cplusplus
}
#endif

#endif
EOF

cat > "$HEADERS_DIR/emma_wasm_runtime.h" <<'EOF'
#ifndef EMMA_WASM_RUNTIME_H
#define EMMA_WASM_RUNTIME_H

#ifdef __cplusplus
extern "C" {
#endif

void emma_wasm_runtime_free_string(char* ptr);

#ifdef __cplusplus
}
#endif

#endif
EOF

if [[ "$BUILD_SIMULATOR" == "1" ]]; then
  xcodebuild -create-xcframework \
    -library "$DEVICE_WASM_LIB" -headers "$HEADERS_DIR" \
    -library "$SIM_WASM_LIB" -headers "$HEADERS_DIR" \
    -output "$OUTPUT_DIR/EmmaWasmRuntime.xcframework"
else
  xcodebuild -create-xcframework \
    -library "$DEVICE_WASM_LIB" -headers "$HEADERS_DIR" \
    -output "$OUTPUT_DIR/EmmaWasmRuntime.xcframework"
fi

if [[ -d "$EXISTING_NATIVE_XCFRAMEWORK" ]]; then
  cp -R "$EXISTING_NATIVE_XCFRAMEWORK" "$OUTPUT_DIR/EmmaNative.xcframework"
  echo "Using prebuilt native XCFramework: $EXISTING_NATIVE_XCFRAMEWORK"
else
  if [[ ! -f "$DEVICE_NATIVE_LIB" ]]; then
    echo "Missing native iOS device static lib: $DEVICE_NATIVE_LIB"
    echo "Provide prebuilt libs via NATIVE_DEVICE_LIB/NATIVE_SIM_LIB, or set EXISTING_NATIVE_XCFRAMEWORK to a prebuilt EmmaNative.xcframework."
    echo "No implicit fallback is used for EmmaNative.xcframework."
    echo "Current NativeAOT script cannot produce iOS libs in this repo (NETSDK1203)."
    exit 1
  fi

  if [[ "$DEVICE_NATIVE_LIB" == *.dylib ]]; then
    create_native_framework() {
      local dylib_path="$1"
      local platform_label="$2"
      local framework_root="$NATIVE_FRAMEWORKS_DIR/$platform_label/EmmaNative.framework"
      local framework_binary="$framework_root/EmmaNative"
      mkdir -p "$framework_root"
      cp "$dylib_path" "$framework_binary"
      install_name_tool -id "@rpath/EmmaNative.framework/EmmaNative" "$framework_binary"
      cat > "$framework_root/Info.plist" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key>
  <string>EmmaNative</string>
  <key>CFBundleIdentifier</key>
  <string>local.emma.native</string>
  <key>CFBundleVersion</key>
  <string>1.0.0</string>
  <key>CFBundleExecutable</key>
  <string>EmmaNative</string>
  <key>CFBundlePackageType</key>
  <string>FMWK</string>
</dict>
</plist>
EOF
      echo "$framework_root"
    }

    DEVICE_FRAMEWORK="$(create_native_framework "$DEVICE_NATIVE_LIB" "ios-arm64")"
    SIM_FRAMEWORK=""

    if [[ "$BUILD_SIMULATOR" == "1" && -f "$SIM_NATIVE_LIB" && "$SIM_NATIVE_LIB" == *.dylib ]]; then
      SIM_FRAMEWORK="$(create_native_framework "$SIM_NATIVE_LIB" "iossimulator")"
    elif [[ "$BUILD_SIMULATOR" == "1" && "$ALLOW_DEVICE_ONLY_NATIVE" != "1" ]]; then
      echo "Missing native iOS simulator dynamic lib: $SIM_NATIVE_LIB"
      echo "Set ALLOW_DEVICE_ONLY_NATIVE=1 to create a device-only XCFramework."
      exit 1
    fi

    if [[ -n "$SIM_FRAMEWORK" ]]; then
      xcodebuild -create-xcframework \
        -framework "$DEVICE_FRAMEWORK" \
        -framework "$SIM_FRAMEWORK" \
        -output "$OUTPUT_DIR/EmmaNative.xcframework"
    else
      xcodebuild -create-xcframework \
        -framework "$DEVICE_FRAMEWORK" \
        -output "$OUTPUT_DIR/EmmaNative.xcframework"
    fi
  else
    if [[ "$BUILD_SIMULATOR" == "1" && -f "$SIM_NATIVE_LIB" ]]; then
      xcodebuild -create-xcframework \
        -library "$DEVICE_NATIVE_LIB" -headers "$HEADERS_DIR" \
        -library "$SIM_NATIVE_LIB" -headers "$HEADERS_DIR" \
        -output "$OUTPUT_DIR/EmmaNative.xcframework"
    else
      if [[ "$BUILD_SIMULATOR" == "1" && "$ALLOW_DEVICE_ONLY_NATIVE" != "1" ]]; then
        echo "Missing native iOS simulator static lib: $SIM_NATIVE_LIB"
        echo "Set ALLOW_DEVICE_ONLY_NATIVE=1 to create a device-only XCFramework."
        exit 1
      fi

      if [[ "$BUILD_SIMULATOR" == "1" ]]; then
        echo "Warning: building device-only EmmaNative.xcframework (simulator slice missing)."
      else
        echo "Building device-only EmmaNative.xcframework (BUILD_SIMULATOR=0)."
      fi
      xcodebuild -create-xcframework \
        -library "$DEVICE_NATIVE_LIB" -headers "$HEADERS_DIR" \
        -output "$OUTPUT_DIR/EmmaNative.xcframework"
    fi
  fi
fi

rm -rf "$TMP_DIR"

echo "[4/4] iOS XCFramework build complete"
echo "  -> $OUTPUT_DIR/EmmaNative.xcframework"
echo "  -> $OUTPUT_DIR/EmmaWasmRuntime.xcframework"
