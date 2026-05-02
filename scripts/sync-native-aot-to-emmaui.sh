#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RID="${1:-}"
AOT_DIR="${2:-}"
EMMAUI_DIR="${3:-}"

if [[ -z "$RID" ]]; then
  echo "Usage: $0 <rid> [aot-output-dir] [emmaui-dir]"
  echo "Example: $0 osx-arm64"
  echo "Example: $0 osx-arm64 ./artifacts/aot/EMMA.Native/osx-arm64 ../emmaui"
  exit 1
fi

if [[ -z "$AOT_DIR" ]]; then
  AOT_DIR="$ROOT_DIR/artifacts/aot/EMMA.Native/$RID"
fi

if [[ -z "$EMMAUI_DIR" ]]; then
  EMMAUI_DIR="$ROOT_DIR/../emmaui"
fi

if [[ ! -d "$AOT_DIR" ]]; then
  echo "AOT output directory does not exist: $AOT_DIR"
  exit 1
fi

if [[ ! -d "$EMMAUI_DIR" ]]; then
  echo "emmaui directory does not exist: $EMMAUI_DIR"
  exit 1
fi

ARTIFACT_NAME=""
case "$RID" in
  osx-*)
    ARTIFACT_NAME="libemma_native.dylib"
    ;;
  win-*)
    ARTIFACT_NAME="emma_native.dll"
    ;;
  linux-*)
    ARTIFACT_NAME="libemma_native.so"
    ;;
  android-*)
    ARTIFACT_NAME="libemma_native.so"
    ;;
  ios-*|iossimulator-*)
    ARTIFACT_NAME="libemma_native.a"
    ;;
  *)
    echo "Unsupported RID: $RID"
    exit 1
    ;;
esac

SOURCE_FILE="$AOT_DIR/$ARTIFACT_NAME"
if [[ ! -f "$SOURCE_FILE" ]]; then
  # Accept legacy/default NativeAOT output names and normalize to expected destination name.
  ALT_SOURCE_FILE=""
  case "$RID" in
    win-*)
      if [[ "$ARTIFACT_NAME" == "emma_native.dll" && -f "$AOT_DIR/EMMA.Native.dll" ]]; then
        ALT_SOURCE_FILE="$AOT_DIR/EMMA.Native.dll"
      elif [[ "$ARTIFACT_NAME" == "emma_native.lib" && -f "$AOT_DIR/EMMA.Native.lib" ]]; then
        ALT_SOURCE_FILE="$AOT_DIR/EMMA.Native.lib"
      fi
      ;;
    linux-*)
      if [[ "$ARTIFACT_NAME" == "libemma_native.so" && -f "$AOT_DIR/EMMA.Native.so" ]]; then
        ALT_SOURCE_FILE="$AOT_DIR/EMMA.Native.so"
      elif [[ "$ARTIFACT_NAME" == "libemma_native.a" && -f "$AOT_DIR/EMMA.Native.a" ]]; then
        ALT_SOURCE_FILE="$AOT_DIR/EMMA.Native.a"
      fi
      ;;
    osx-*)
      if [[ "$ARTIFACT_NAME" == "libemma_native.dylib" && -f "$AOT_DIR/EMMA.Native.dylib" ]]; then
        ALT_SOURCE_FILE="$AOT_DIR/EMMA.Native.dylib"
      elif [[ "$ARTIFACT_NAME" == "libemma_native.a" && -f "$AOT_DIR/EMMA.Native.a" ]]; then
        ALT_SOURCE_FILE="$AOT_DIR/EMMA.Native.a"
      fi
      ;;
  esac

  if [[ -n "$ALT_SOURCE_FILE" ]]; then
    SOURCE_FILE="$ALT_SOURCE_FILE"
    echo "Info: using alternate artifact name: $SOURCE_FILE"
  else
    echo "Expected artifact not found: $SOURCE_FILE"
    echo "Run ./scripts/publish-native-aot.sh $RID first (or pass a custom AOT dir)."
    exit 1
  fi
fi

declare -a DEST_DIRS=()

case "$RID" in
  osx-*)
    DEST_DIRS+=("$EMMAUI_DIR/macos/Runner/Frameworks")
    ;;

  ios-*|iossimulator-*)
    DEST_DIRS+=("$EMMAUI_DIR/ios/Runner/Frameworks")
    ;;

  android-*)
    ABI=""
    case "$RID" in
      android-arm64) ABI="arm64-v8a" ;;
      android-x64) ABI="x86_64" ;;
      android-arm) ABI="armeabi-v7a" ;;
      android-x86) ABI="x86" ;;
      *)
        echo "Unsupported Android RID for ABI mapping: $RID"
        exit 1
        ;;
    esac
    DEST_DIRS+=("$EMMAUI_DIR/android/app/src/main/jniLibs/$ABI")
    ;;

  linux-*)
    DEST_DIRS+=("$EMMAUI_DIR/linux/runner")
    DEST_DIRS+=("$EMMAUI_DIR/build/linux/x64/debug/bundle/lib")
    DEST_DIRS+=("$EMMAUI_DIR/build/linux/x64/debug/intermediates_do_not_run/lib")
    DEST_DIRS+=("$EMMAUI_DIR/build/linux/x64/release/bundle/lib")
    DEST_DIRS+=("$EMMAUI_DIR/build/linux/x64/release/intermediates_do_not_run/lib")
    ;;

  win-*)
    FLUTTER_ARCH=""
    case "$RID" in
      win-x64) FLUTTER_ARCH="x64" ;;
      win-arm64) FLUTTER_ARCH="arm64" ;;
      win-x86) FLUTTER_ARCH="x86" ;;
      *)
        echo "Unsupported Windows RID for Flutter build mapping: $RID"
        exit 1
        ;;
    esac
    DEST_DIRS+=("$EMMAUI_DIR/build/windows/$FLUTTER_ARCH/runner/Debug")
    DEST_DIRS+=("$EMMAUI_DIR/build/windows/$FLUTTER_ARCH/runner/Profile")
    DEST_DIRS+=("$EMMAUI_DIR/build/windows/$FLUTTER_ARCH/runner/Release")
    ;;
esac

if [[ ${#DEST_DIRS[@]} -eq 0 ]]; then
  echo "No destination directories were resolved for RID: $RID"
  exit 1
fi

echo "Syncing $ARTIFACT_NAME for RID '$RID'"
echo "  source: $SOURCE_FILE"

for dir in "${DEST_DIRS[@]}"; do
  mkdir -p "$dir"
  cp "$SOURCE_FILE" "$dir/$ARTIFACT_NAME"
  echo "  -> $dir/$ARTIFACT_NAME"
done

# Also sync the WASM runtime library (required by embedded PluginHost)
RUNTIME_LIB_DIR="$ROOT_DIR/artifacts/wasm-runtime-native/$RID"
RUNTIME_LIB_NAME=""

case "$RID" in
  osx-*)
    RUNTIME_LIB_NAME="libemma_wasm_runtime.dylib"
    ;;
  win-*)
    RUNTIME_LIB_NAME="emma_wasm_runtime.dll"
    ;;
  linux-*)
    RUNTIME_LIB_NAME="libemma_wasm_runtime.so"
    ;;
  ios-*|iossimulator-*)
    RUNTIME_LIB_NAME="libemma_wasm_runtime.a"
    ;;
  android-*)
    RUNTIME_LIB_NAME="libemma_wasm_runtime.so"
    ;;
esac

if [[ -n "$RUNTIME_LIB_NAME" ]]; then
  SOURCE_RUNTIME_LIB="$RUNTIME_LIB_DIR/$RUNTIME_LIB_NAME"
  if [[ -f "$SOURCE_RUNTIME_LIB" ]]; then
    echo ""
    echo "Syncing WASM runtime: $RUNTIME_LIB_NAME"
    for dir in "${DEST_DIRS[@]}"; do
      cp "$SOURCE_RUNTIME_LIB" "$dir/$RUNTIME_LIB_NAME"
      echo "  -> $dir/$RUNTIME_LIB_NAME"
    done
  else
    echo ""
    echo "Warning: WASM runtime library not found: $SOURCE_RUNTIME_LIB"
    echo "The embedded PluginHost requires this library at runtime."
    echo "Run ./scripts/build-wasm-runtime-native.sh $RID to build it."
  fi
fi

# Also sync the SQLite native library (required by storage layer)
SQLITE_LIB_NAME=""
case "$RID" in
  osx-*)
    SQLITE_LIB_NAME="libe_sqlite3.dylib"
    ;;
  win-*)
    SQLITE_LIB_NAME="e_sqlite3.dll"
    ;;
  linux-*|android-*)
    SQLITE_LIB_NAME="libe_sqlite3.so"
    ;;
  ios-*|iossimulator-*)
    # For iOS, libe_sqlite3 is statically linked, no separate file needed
    SQLITE_LIB_NAME=""
    ;;
esac

if [[ -n "$SQLITE_LIB_NAME" ]]; then
  SOURCE_SQLITE_LIB="$AOT_DIR/$SQLITE_LIB_NAME"
  if [[ -f "$SOURCE_SQLITE_LIB" ]]; then
    echo ""
    echo "Syncing SQLite native library: $SQLITE_LIB_NAME"
    for dir in "${DEST_DIRS[@]}"; do
      cp "$SOURCE_SQLITE_LIB" "$dir/$SQLITE_LIB_NAME"
      echo "  -> $dir/$SQLITE_LIB_NAME"
    done
  else
    echo ""
    echo "Warning: SQLite native library not found: $SOURCE_SQLITE_LIB"
    echo "This is bundled automatically during NativeAOT publish."
  fi
fi

echo "Sync complete."
