#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT_PATH="$ROOT_DIR/src/EMMA.Native/EMMA.Native.csproj"
RID="${1:-}"
OUTPUT_DIR="${2:-}"
ANDROID_CPP_LINKER=""
ANDROID_CPP_LIB_CREATOR=""
ANDROID_OBJCOPY=""

resolve_android_ndk_root() {
  local candidate=""
  local sdk_root=""

  for candidate in "${ANDROID_NDK_ROOT:-}" "${ANDROID_NDK_HOME:-}"; do
    if [[ -n "$candidate" && -d "$candidate/toolchains/llvm/prebuilt" ]]; then
      printf '%s\n' "$candidate"
      return 0
    fi
  done

  for sdk_root in "${ANDROID_SDK_ROOT:-}" "${ANDROID_HOME:-}" "$HOME/Android/Sdk"; do
    if [[ -z "$sdk_root" || ! -d "$sdk_root/ndk" ]]; then
      continue
    fi

    candidate="$(find "$sdk_root/ndk" -mindepth 1 -maxdepth 1 -type d | sort -V | tail -n 1)"
    if [[ -n "$candidate" && -d "$candidate/toolchains/llvm/prebuilt" ]]; then
      printf '%s\n' "$candidate"
      return 0
    fi
  done

  return 1
}

resolve_android_ndk_host_tag() {
  local host_os="$(uname -s 2>/dev/null || echo unknown)"
  local host_arch="$(uname -m 2>/dev/null || echo unknown)"

  case "$host_os" in
    Linux)
      printf '%s\n' "linux-x86_64"
      ;;
    Darwin)
      if [[ "$host_arch" == "arm64" ]]; then
        printf '%s\n' "darwin-arm64"
      else
        printf '%s\n' "darwin-x86_64"
      fi
      ;;
    CYGWIN*|MINGW*|MSYS*)
      printf '%s\n' "windows-x86_64"
      ;;
    *)
      return 1
      ;;
  esac
}

configure_android_nativeaot_toolchain() {
  local public_rid="$1"
  local android_api_level="${EMMA_ANDROID_NATIVE_API_LEVEL:-21}"
  local ndk_root=""
  local host_tag=""
  local toolchain_bin=""
  local linker_triple=""
  local linker_path=""

  ndk_root="$(resolve_android_ndk_root || true)"
  if [[ -z "$ndk_root" ]]; then
    echo "Android NativeAOT builds require an Android NDK installation."
    echo "Set ANDROID_NDK_ROOT or ANDROID_NDK_HOME, or install the NDK under ANDROID_SDK_ROOT/ndk."
    exit 1
  fi

  host_tag="$(resolve_android_ndk_host_tag || true)"
  if [[ -z "$host_tag" ]]; then
    echo "Unsupported host OS for Android NativeAOT builds: $(uname -s 2>/dev/null || echo unknown)"
    exit 1
  fi

  toolchain_bin="$ndk_root/toolchains/llvm/prebuilt/$host_tag/bin"
  if [[ ! -d "$toolchain_bin" ]]; then
    echo "Android NDK toolchain directory not found: $toolchain_bin"
    exit 1
  fi

  case "$public_rid" in
    android-arm64)
      linker_triple="aarch64-linux-android"
      ;;
    android-x64)
      linker_triple="x86_64-linux-android"
      ;;
    *)
      echo "Unsupported Android RID for NativeAOT toolchain: $public_rid"
      exit 1
      ;;
  esac

  linker_path="$toolchain_bin/${linker_triple}${android_api_level}-clang"
  if [[ ! -x "$linker_path" ]]; then
    echo "Android NativeAOT linker not found or not executable: $linker_path"
    echo "Set EMMA_ANDROID_NATIVE_API_LEVEL if your NDK only provides a different API level toolchain."
    exit 1
  fi

  export CC="$linker_path"
  export CXX="$toolchain_bin/${linker_triple}${android_api_level}-clang++"
  export AR="$toolchain_bin/llvm-ar"
  ANDROID_CPP_LINKER="$linker_path"
  ANDROID_CPP_LIB_CREATOR="$toolchain_bin/llvm-ar"
  ANDROID_OBJCOPY="$toolchain_bin/llvm-objcopy"

  echo "Using Android NDK toolchain: $ndk_root"
  echo "Using Android NativeAOT linker: $linker_path"
}

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
EMMA_ANDROID_AOT_BUILD="false"
VALIDATE_XCODE_VERSION="${VALIDATE_XCODE_VERSION:-false}"
IOS_NATIVE_LIB_TYPE="${IOS_NATIVE_LIB_TYPE:-Shared}"
PUBLISH_RID="$RID"
case "$RID" in
  ios-*|iossimulator-*)
    NATIVE_LIB_TYPE="$IOS_NATIVE_LIB_TYPE"
    PUBLISH_AOT_USING_RUNTIME_PACK="true"
    EMMA_IOS_AOT_BUILD="true"
    ;;
esac
case "$RID" in
  android-*)
    case "$RID" in
      android-arm64)
        PUBLISH_RID="linux-bionic-arm64"
        ;;
      android-x64)
        PUBLISH_RID="linux-bionic-x64"
        ;;
      *)
        echo "Unsupported Android RID: $RID"
        exit 1
        ;;
    esac
    PUBLISH_AOT_USING_RUNTIME_PACK="true"
    EMMA_ANDROID_AOT_BUILD="true"
    configure_android_nativeaot_toolchain "$RID"
    ;;
esac

rm -rf "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"

echo "Publishing EMMA.Native NativeAOT (static library) for $RID..."

PUBLISH_ARGS=(
  publish "$PROJECT_PATH"
  -f "$TARGET_FRAMEWORK"
  -c Release
  -r "$PUBLISH_RID"
  -p:PublishAot=true
  -p:PublishAotUsingRuntimePack="$PUBLISH_AOT_USING_RUNTIME_PACK"
  -p:SelfContained=true
  -p:NativeLib="$NATIVE_LIB_TYPE"
  -p:NativeLibName=emma_native
  -p:EMMAIosAotBuild="$EMMA_IOS_AOT_BUILD"
  -p:EMMAAndroidAotBuild="$EMMA_ANDROID_AOT_BUILD"
  -p:ValidateXcodeVersion="$VALIDATE_XCODE_VERSION"
  -o "$OUTPUT_DIR"
)

if [[ -n "$ANDROID_CPP_LINKER" ]]; then
  PUBLISH_ARGS+=("-p:CppCompilerAndLinker=$ANDROID_CPP_LINKER")
  PUBLISH_ARGS+=("-p:CppLinker=$ANDROID_CPP_LINKER")
fi

if [[ -n "$ANDROID_CPP_LIB_CREATOR" ]]; then
  PUBLISH_ARGS+=("-p:CppLibCreator=$ANDROID_CPP_LIB_CREATOR")
fi

if [[ -n "$ANDROID_OBJCOPY" ]]; then
  PUBLISH_ARGS+=("-p:ObjCopyName=$ANDROID_OBJCOPY")
fi

dotnet "${PUBLISH_ARGS[@]}"

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
