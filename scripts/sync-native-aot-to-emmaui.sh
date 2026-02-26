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
  linux-*|android-*)
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
  echo "Expected artifact not found: $SOURCE_FILE"
  echo "Run ./scripts/publish-native-aot.sh $RID first (or pass a custom AOT dir)."
  exit 1
fi

declare -a DEST_DIRS=()

case "$RID" in
  osx-*)
    DEST_DIRS+=("$EMMAUI_DIR/macos/Runner/Frameworks")
    DEST_DIRS+=("$EMMAUI_DIR/build/macos/Build/Products/Debug/emmaui.app/Contents/Frameworks")
    DEST_DIRS+=("$EMMAUI_DIR/build/macos/Build/Products/Profile/emmaui.app/Contents/Frameworks")
    DEST_DIRS+=("$EMMAUI_DIR/build/macos/Build/Products/Release/emmaui.app/Contents/Frameworks")
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
    FLUTTER_ARCH=""
    case "$RID" in
      linux-x64) FLUTTER_ARCH="x64" ;;
      linux-arm64) FLUTTER_ARCH="arm64" ;;
      *)
        echo "Unsupported Linux RID for Flutter build mapping: $RID"
        exit 1
        ;;
    esac
    DEST_DIRS+=("$EMMAUI_DIR/build/linux/$FLUTTER_ARCH/debug/bundle/lib")
    DEST_DIRS+=("$EMMAUI_DIR/build/linux/$FLUTTER_ARCH/profile/bundle/lib")
    DEST_DIRS+=("$EMMAUI_DIR/build/linux/$FLUTTER_ARCH/release/bundle/lib")
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

echo "Sync complete."
