#!/usr/bin/env sh
# Shared by the build scripts (publish-native.sh, native/build-host.sh, package-*.sh); sourced, never run. Names the
# runtime identifiers (rids) EMMA builds for and maps each to what the toolchains call it, and finds the Android NDK.
#
#   rid                 kind     Rust target              .NET rid (NativeAOT)    Android ABI
#   linux-x64 linux-arm64 osx-x64 osx-arm64 win-x64 win-arm64   desktop   (the host's own)   (the same)
#   android-arm64       android  aarch64-linux-android    linux-bionic-arm64      arm64-v8a
#   android-x64         android  x86_64-linux-android     linux-bionic-x64        x86_64
#   ios-arm64           ios      aarch64-apple-ios        ios-arm64
#   iossimulator-arm64  ios      aarch64-apple-ios-sim    iossimulator-arm64
#   iossimulator-x64    ios      x86_64-apple-ios         iossimulator-x64
#
# NativeAOT and Cargo cannot cross-compile between desktop platforms, so a desktop rid must be the host's own. 32-bit
# Android (armeabi-v7a, x86) is not built: nothing needs it and neither .NET nor the Wasm host has been tried there.

# Both script directories (scripts/, native/) sit directly under the repository root.
root=$(cd "$(dirname "$0")/.." && pwd)

# Prints the rid of the machine running the script.
host_rid() {
  case "$(uname -s)-$(uname -m)" in
    Linux-x86_64) echo linux-x64 ;;
    Linux-aarch64 | Linux-arm64) echo linux-arm64 ;;
    Darwin-arm64) echo osx-arm64 ;;
    Darwin-x86_64) echo osx-x64 ;;
    MINGW*-x86_64 | MSYS*-x86_64 | CYGWIN*-x86_64) echo win-x64 ;;
    MINGW*-aarch64 | MSYS*-aarch64 | CYGWIN*-aarch64 | MINGW*-arm64 | MSYS*-arm64) echo win-arm64 ;;
    *) echo "cannot guess a runtime identifier for this host; pass one" >&2; return 1 ;;
  esac
}

# Sets kind, rust_target, dotnet_rid and abi for $1, or exits. A desktop rid other than the host's is refused.
resolve_rid() {
  abi=""
  rust_target=""
  case "$1" in
    linux-x64 | linux-arm64 | osx-x64 | osx-arm64 | win-x64 | win-arm64)
      kind=desktop
      dotnet_rid=$1
      if [ "$1" != "$(host_rid)" ]; then
        echo "'$1' is not this host's rid ($(host_rid)): desktop libraries are built on their own platform" >&2
        exit 1
      fi
      ;;
    android-arm64) kind=android; rust_target=aarch64-linux-android; dotnet_rid=linux-bionic-arm64; abi=arm64-v8a ;;
    android-x64) kind=android; rust_target=x86_64-linux-android; dotnet_rid=linux-bionic-x64; abi=x86_64 ;;
    ios-arm64) kind=ios; rust_target=aarch64-apple-ios; dotnet_rid=ios-arm64 ;;
    iossimulator-arm64) kind=ios; rust_target=aarch64-apple-ios-sim; dotnet_rid=iossimulator-arm64 ;;
    iossimulator-x64) kind=ios; rust_target=x86_64-apple-ios; dotnet_rid=iossimulator-x64 ;;
    *) echo "unsupported rid '$1'" >&2; exit 1 ;;
  esac
}

# Exits unless the machine can build for an iOS rid: Apple's linker and SDK exist only on macOS.
require_xcode() {
  [ "$(uname -s)" = Darwin ] || { echo "iOS builds need macOS and Xcode" >&2; exit 1; }
}

# Prints the NDK root: $ANDROID_NDK_ROOT or $ANDROID_NDK_HOME if set, else the newest stable NDK in the SDK.
android_ndk_root() {
  for candidate in "${ANDROID_NDK_ROOT:-}" "${ANDROID_NDK_HOME:-}"; do
    if [ -n "$candidate" ] && [ -d "$candidate/toolchains/llvm/prebuilt" ]; then
      printf '%s\n' "$candidate"
      return 0
    fi
  done
  for sdk in "${ANDROID_SDK_ROOT:-}" "${ANDROID_HOME:-}" "$HOME/Android/Sdk" "$HOME/Library/Android/sdk"; do
    [ -n "$sdk" ] && [ -d "$sdk/ndk" ] || continue
    # The newest stable NDK: a pre-release (Pkg.Revision ends in -beta1, -rc1) is only used when nothing else is installed.
    candidate=$(find "$sdk/ndk" -mindepth 1 -maxdepth 1 -type d | sort -V | while read -r dir; do
      grep -q '^Pkg.Revision *= *[0-9.]*$' "$dir/source.properties" 2>/dev/null && echo "$dir"
    done | tail -n 1)
    [ -n "$candidate" ] || candidate=$(find "$sdk/ndk" -mindepth 1 -maxdepth 1 -type d | sort -V | tail -n 1)
    if [ -n "$candidate" ] && [ -d "$candidate/toolchains/llvm/prebuilt" ]; then
      printf '%s\n' "$candidate"
      return 0
    fi
  done
  echo "Android builds need the NDK: set ANDROID_NDK_ROOT, or install one under the Android SDK's ndk/ directory" >&2
  return 1
}

# Sets ndk_bin to the NDK's LLVM tool directory, and ndk_clang to the clang that targets rust_target at the API level
# (EMMA_ANDROID_API, default 24, which is Flutter's minimum). Needs rust_target from resolve_rid.
android_toolchain() {
  ndk=$(android_ndk_root) || exit 1
  case "$(uname -s)-$(uname -m)" in
    Linux-*) host_tag=linux-x86_64 ;;
    Darwin-*) host_tag=darwin-x86_64 ;; # the NDK's macOS toolchain is universal and keeps this name
    MINGW*-* | MSYS*-* | CYGWIN*-*) host_tag=windows-x86_64 ;;
    *) echo "no Android NDK host toolchain for $(uname -s)" >&2; exit 1 ;;
  esac
  ndk_bin="$ndk/toolchains/llvm/prebuilt/$host_tag/bin"
  api=${EMMA_ANDROID_API:-24}
  ndk_clang="$ndk_bin/${rust_target%%-linux-android}-linux-android$api-clang"
  [ -x "$ndk_clang" ] || { echo "no $ndk_clang: set EMMA_ANDROID_API to a level this NDK has" >&2; exit 1; }
}
