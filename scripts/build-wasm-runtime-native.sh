#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RUNTIME_CRATE_DIR="$ROOT_DIR/src/EMMA.WasmRuntime.Native"
RID="${1:-}"
OUTPUT_DIR="${2:-}"
CARGO_PROFILE="${WASM_RUNTIME_CARGO_PROFILE:-release}"

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

configure_android_toolchain() {
  local android_api_level="${WASM_RUNTIME_ANDROID_API_LEVEL:-21}"
  local ndk_root=""
  local host_tag=""
  local toolchain_bin=""
  local linker_basename=""
  local linker_path=""
  local cargo_target_env=""
  local target_env_suffix=""

  ndk_root="$(resolve_android_ndk_root || true)"
  if [[ -z "$ndk_root" ]]; then
    echo "Android runtime builds require an Android NDK installation."
    echo "Set ANDROID_NDK_ROOT or ANDROID_NDK_HOME, or install the NDK under ANDROID_SDK_ROOT/ndk."
    exit 1
  fi

  host_tag="$(resolve_android_ndk_host_tag || true)"
  if [[ -z "$host_tag" ]]; then
    echo "Unsupported host OS for Android runtime builds: $(uname -s 2>/dev/null || echo unknown)"
    exit 1
  fi

  toolchain_bin="$ndk_root/toolchains/llvm/prebuilt/$host_tag/bin"
  if [[ ! -d "$toolchain_bin" ]]; then
    echo "Android NDK toolchain directory not found: $toolchain_bin"
    exit 1
  fi

  linker_basename="$TARGET_TRIPLE${android_api_level}-clang"
  linker_path="$toolchain_bin/$linker_basename"
  if [[ ! -x "$linker_path" ]]; then
    echo "Android linker not found or not executable: $linker_path"
    echo "Set WASM_RUNTIME_ANDROID_API_LEVEL if your NDK only provides a different API level toolchain."
    exit 1
  fi

  cargo_target_env="${TARGET_TRIPLE^^}"
  cargo_target_env="${cargo_target_env//-/_}"
  target_env_suffix="${TARGET_TRIPLE//-/_}"

  export "CARGO_TARGET_${cargo_target_env}_LINKER=$linker_path"
  export "CC_${target_env_suffix}=$linker_path"
  export "CXX_${target_env_suffix}=$toolchain_bin/${TARGET_TRIPLE}${android_api_level}-clang++"
  export "AR_${target_env_suffix}=$toolchain_bin/llvm-ar"

  echo "Using Android NDK toolchain: $ndk_root"
  echo "Using Android linker: $linker_path"
}

ensure_rust_target_installed() {
  local target_triple="$1"

  if ! command -v rustup >/dev/null 2>&1; then
    return 0
  fi

  if rustup target list --installed | grep -Fxq "$target_triple"; then
    return 0
  fi

  echo "Rust target '$target_triple' is not installed."
  echo "Install it with: rustup target add $target_triple"
  exit 1
}

if [[ -z "$RID" ]]; then
  echo "Usage: $0 <rid> [output-dir]"
  echo "Example: $0 linux-x64"
  exit 1
fi

if [[ -z "$OUTPUT_DIR" ]]; then
  OUTPUT_DIR="$ROOT_DIR/artifacts/wasm-runtime-native/$RID"
fi

case "$RID" in
  linux-x64)
    TARGET_TRIPLE="x86_64-unknown-linux-gnu"
    LIB_FILE="libemma_wasm_runtime.so"
    ;;
  osx-arm64)
    TARGET_TRIPLE="aarch64-apple-darwin"
    LIB_FILE="libemma_wasm_runtime.dylib"
    ;;
  osx-x64)
    TARGET_TRIPLE="x86_64-apple-darwin"
    LIB_FILE="libemma_wasm_runtime.dylib"
    ;;
  win-x64)
    TARGET_TRIPLE="x86_64-pc-windows-msvc"
    LIB_FILE="emma_wasm_runtime.dll"
    ;;
  ios-arm64)
    TARGET_TRIPLE="aarch64-apple-ios"
    LIB_FILE="libemma_wasm_runtime.a"
    ;;
  iossimulator-arm64)
    TARGET_TRIPLE="aarch64-apple-ios-sim"
    LIB_FILE="libemma_wasm_runtime.a"
    ;;
  iossimulator-x64)
    TARGET_TRIPLE="x86_64-apple-ios"
    LIB_FILE="libemma_wasm_runtime.a"
    ;;
  android-arm64)
    TARGET_TRIPLE="aarch64-linux-android"
    LIB_FILE="libemma_wasm_runtime.so"
    ;;
  android-x64)
    TARGET_TRIPLE="x86_64-linux-android"
    LIB_FILE="libemma_wasm_runtime.so"
    ;;    
  *)
    echo "Unsupported RID: $RID"
    exit 1
    ;;
esac

echo "Building emma_wasm_runtime for $RID ($TARGET_TRIPLE)..."

BUILD_TOOL="cargo"
BUILD_SUBCOMMAND="build"

if [[ "$RID" == "win-x64" ]]; then
  HOST_OS="$(uname -s 2>/dev/null || echo unknown)"
  ALLOW_NON_WINDOWS_WIN64_BUILD="${WASM_RUNTIME_ALLOW_NON_WINDOWS_WIN64_BUILD:-0}"
  IS_WINDOWS_HOST=0
  if [[ "$HOST_OS" == CYGWIN* || "$HOST_OS" == MINGW* || "$HOST_OS" == MSYS* ]]; then
    IS_WINDOWS_HOST=1
  fi

  if [[ "$IS_WINDOWS_HOST" -ne 1 ]]; then
    if command -v cargo-xwin >/dev/null 2>&1 || cargo xwin --help >/dev/null 2>&1; then
      BUILD_TOOL="cargo"
      BUILD_SUBCOMMAND="xwin build"
      echo "Detected non-Windows host ($HOST_OS); using cargo xwin for win-x64 cross-compilation."
    elif [[ "$ALLOW_NON_WINDOWS_WIN64_BUILD" != "1" ]]; then
      echo "win-x64 runtime builds on non-Windows hosts require cargo-xwin."
      echo "Detected host OS: $HOST_OS"
      echo ""
      echo "Install cross-compile prerequisites (Arch example):"
      echo "  sudo pacman -Syu --needed clang lld llvm cmake pkgconf base-devel rustup"
      echo "  rustup target add x86_64-pc-windows-msvc"
      echo "  cargo install cargo-xwin --locked"
      echo ""
      echo "Then re-run:"
      echo "  ./scripts/build-wasm-runtime-native.sh win-x64"
      echo ""
      echo "If you intentionally want to bypass this guard, set WASM_RUNTIME_ALLOW_NON_WINDOWS_WIN64_BUILD=1."
      exit 1
    fi
  fi

  if ! command -v rustc >/dev/null 2>&1; then
    echo "rustc is required to build the Windows native runtime."
    exit 1
  fi

  TARGET_CFG="$(rustc --print cfg --target "$TARGET_TRIPLE" 2>/dev/null || true)"
  if [[ -z "$TARGET_CFG" || "$TARGET_CFG" != *'target_env="msvc"'* ]]; then
    echo "win-x64 runtime builds require the MSVC Rust target ($TARGET_TRIPLE)."
    echo "Detected target configuration was not MSVC (this usually causes ittapi-sys to fail with GNU compiler warnings)."
    echo ""
    echo "Fix on Windows PowerShell:"
    echo "  rustup toolchain install stable-x86_64-pc-windows-msvc"
    echo "  rustup default stable-x86_64-pc-windows-msvc"
    echo "  rustup target add x86_64-pc-windows-msvc"
    echo ""
    echo "Then re-run:"
    echo "  ./scripts/build-wasm-runtime-native.sh win-x64"
    exit 1
  fi

  if [[ "$IS_WINDOWS_HOST" -eq 1 && "$BUILD_SUBCOMMAND" == "build" ]]; then
    # On Windows, native C dependencies require an MSVC-compatible C toolchain.
    HAS_LIB_EXE=0
    HAS_CLANG_CL=0
    HAS_LLVM_LIB=0
    if command -v lib.exe >/dev/null 2>&1; then
      HAS_LIB_EXE=1
    fi
    if command -v clang-cl >/dev/null 2>&1; then
      HAS_CLANG_CL=1
    fi
    if command -v llvm-lib >/dev/null 2>&1; then
      HAS_LLVM_LIB=1
    fi

    HAS_MSVC_C_TOOLCHAIN=0
    if [[ "$HAS_LIB_EXE" -eq 1 ]]; then
      HAS_MSVC_C_TOOLCHAIN=1
    elif [[ "$HAS_CLANG_CL" -eq 1 && "$HAS_LLVM_LIB" -eq 1 ]]; then
      HAS_MSVC_C_TOOLCHAIN=1
      export "CC_x86_64_pc_windows_msvc=clang-cl"
      export "AR_x86_64_pc_windows_msvc=llvm-lib"
    fi

    if [[ "$HAS_MSVC_C_TOOLCHAIN" -ne 1 ]]; then
      echo "win-x64 runtime builds require an MSVC-compatible C toolchain for native dependencies."
      echo "Current environment does not provide required tools (lib.exe, or clang-cl + llvm-lib)."
      echo "Host OS detected: $HOST_OS"
      echo ""
      echo "This is the root cause of errors like:"
      echo "  GNU compiler is not supported for this target"
      echo "  failed to find tool 'lib.exe'"
      echo ""
      echo "Fix options:"
      echo "  1) Build on Windows in a VS Developer Command Prompt (recommended)."
      echo "     Install Visual Studio Build Tools with C++ workload, then run:"
      echo "       rustup toolchain install stable-x86_64-pc-windows-msvc"
      echo "       rustup default stable-x86_64-pc-windows-msvc"
      echo "       rustup target add x86_64-pc-windows-msvc"
      echo "       ./scripts/build-wasm-runtime-native.sh win-x64"
      echo ""
      echo "  2) Provide clang-cl + llvm-lib in PATH and rerun this script."
      exit 1
    fi
  fi
fi

if [[ "$CARGO_PROFILE" == "release" ]]; then
  PROFILE_ARGS=(--release)
else
  PROFILE_ARGS=(--profile "$CARGO_PROFILE")
fi

case "$RID" in
  android-arm64|android-x64)
    configure_android_toolchain
    ensure_rust_target_installed "$TARGET_TRIPLE"
    ;;
esac

if [[ "$BUILD_SUBCOMMAND" == "xwin build" ]]; then
  cargo xwin build \
    --manifest-path "$RUNTIME_CRATE_DIR/Cargo.toml" \
    "${PROFILE_ARGS[@]}" \
    --target "$TARGET_TRIPLE"
else
  cargo build \
    --manifest-path "$RUNTIME_CRATE_DIR/Cargo.toml" \
    "${PROFILE_ARGS[@]}" \
    --target "$TARGET_TRIPLE"
fi

SOURCE_LIB="$RUNTIME_CRATE_DIR/target/$TARGET_TRIPLE/$CARGO_PROFILE/$LIB_FILE"
if [[ ! -f "$SOURCE_LIB" ]]; then
  echo "Expected native runtime library not found: $SOURCE_LIB"
  exit 1
fi

rm -rf "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"
cp "$SOURCE_LIB" "$OUTPUT_DIR/"

echo "Native runtime build succeeded: $OUTPUT_DIR/$LIB_FILE"
echo "Cargo profile used: $CARGO_PROFILE"
