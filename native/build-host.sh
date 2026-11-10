#!/usr/bin/env sh
# Builds the native Wasm host (native/emma-wasm) for one runtime identifier.
#
#   sh native/build-host.sh [rid] [output-dir]
#
# rid defaults to the host's. Without an output-dir the host build goes to artifacts/native/lib/, where the .NET
# integration tests pick it up, and any other rid to artifacts/native/lib/<rid>/.
#
# Desktop gets the shared library (libemma_wasm.so, libemma_wasm.dylib, emma_wasm.dll) in release mode.
# Android gets libemma_wasm.so, which the app loads from its native library directory; it is built with the NDK and
#   needs `rustup target add <target>`.
# iOS gets libemma_wasm.a, a static library the app links in: an app cannot load a library it did not ship signed, and
#   libemma_native's P/Invokes then resolve in-process. It needs macOS, Xcode and the Rust target.
set -eu
. "$(dirname "$0")/../scripts/common.sh"

rid=${1:-}
if [ -z "$rid" ]; then
  rid=$(host_rid) || exit 1
  default_out="$root/artifacts/native/lib"
else
  default_out="$root/artifacts/native/lib/$rid"
fi
resolve_rid "$rid"
out=${2:-$default_out}
manifest="$root/native/emma-wasm/Cargo.toml"

if [ "$kind" = desktop ]; then
  mkdir -p "$out"
  cargo build --release --locked --manifest-path "$manifest"
  target="$root/native/emma-wasm/target/release"
  for file in libemma_wasm.so libemma_wasm.dylib emma_wasm.dll; do
    if [ -f "$target/$file" ]; then
      cp "$target/$file" "$out/$file"
    fi
  done
  echo "native host built into $out"
  exit 0
fi

if command -v rustup >/dev/null 2>&1 && ! rustup target list --installed | grep -Fxq "$rust_target"; then
  echo "missing Rust target: rustup target add $rust_target" >&2
  exit 1
fi

# Cargo reads the linker and the C compiler (ring, rustls' crypto, builds C) for a target from these variables.
if [ "$kind" = android ]; then
  android_toolchain
  env_target=$(printf '%s' "$rust_target" | tr 'a-z-' 'A-Z_')
  cc_target=$(printf '%s' "$rust_target" | tr '-' '_')
  export "CARGO_TARGET_${env_target}_LINKER=$ndk_clang"
  export "CC_${cc_target}=$ndk_clang"
  export "CXX_${cc_target}=${ndk_clang}++"
  export "AR_${cc_target}=$ndk_bin/llvm-ar"
  # Google Play requires 16 KiB page alignment for apps that target Android 15 or later; older NDKs default to 4 KiB.
  export "CARGO_TARGET_${env_target}_RUSTFLAGS=-C link-arg=-Wl,-z,max-page-size=16384"
  crate_type=cdylib
  built=libemma_wasm.so
else
  require_xcode
  # An iOS app cannot JIT, so the host there compiles for the Pulley interpreter (see wasm-host.md); that is chosen at
  # runtime, not here. The deployment target keeps the C code ring builds in step with the app's.
  export IPHONEOS_DEPLOYMENT_TARGET=${IPHONEOS_DEPLOYMENT_TARGET:-13.0}
  crate_type=staticlib
  built=libemma_wasm.a
fi

# --crate-type builds only the one library the platform needs, not all three the manifest lists.
cargo rustc --release --locked --lib --crate-type "$crate_type" --target "$rust_target" --manifest-path "$manifest"

mkdir -p "$out"
cp "$root/native/emma-wasm/target/$rust_target/release/$built" "$out/$built"
echo "built $out/$built"
