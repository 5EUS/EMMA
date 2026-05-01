#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RUNTIME_CRATE_DIR="$ROOT_DIR/src/EMMA.WasmRuntime.Native"
RID="${1:-}"
OUTPUT_DIR="${2:-}"
CARGO_PROFILE="${WASM_RUNTIME_CARGO_PROFILE:-release}"

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
