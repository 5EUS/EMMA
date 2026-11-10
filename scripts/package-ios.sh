#!/usr/bin/env sh
# Builds everything an iOS app links and wraps it in two XCFrameworks:
#
#   <output-dir>/EmmaNative.xcframework   libemma_native.a: the C ABI (NativeAOT, static), headers from src/EMMA.Native/include
#   <output-dir>/EmmaWasm.xcframework     libemma_wasm.a: the Wasm plugin host (Rust, static), headers from native/emma-wasm/include
#
#   sh scripts/package-ios.sh [output-dir] [rid...]
#
# output-dir defaults to artifacts/mobile/ios; rids default to ios-arm64 (devices) and iossimulator-arm64 (Apple silicon
# simulators). Add iossimulator-x64 for Intel simulators: the simulator slices are then merged with lipo. Needs macOS,
# Xcode and the Rust targets (rustup target add aarch64-apple-ios aarch64-apple-ios-sim x86_64-apple-ios). Each rid
# publishes with NativeAOT once: this is slow and memory-hungry. emmaui's tool/sync_native_mobile.sh installs the result.
#
# They are two frameworks because they are two libraries with their own headers and lifetimes (the Wasm host is rebuilt
# from Rust, the native library from C#); the app links both.
set -eu
. "$(dirname "$0")/common.sh"
require_xcode

out=${1:-"$root/artifacts/mobile/ios"}
[ $# -gt 0 ] && shift
rids=${*:-"ios-arm64 iossimulator-arm64"}

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

device=""
simulator=""
for rid in $rids; do
  resolve_rid "$rid"
  [ "$kind" = ios ] || { echo "$rid is not an iOS rid" >&2; exit 1; }
  sh "$root/native/build-host.sh" "$rid" "$work/$rid"
  sh "$root/scripts/publish-native.sh" "$rid" "$work/$rid"
  case "$rid" in
    ios-*) device="$work/$rid" ;;
    *) simulator="$simulator $work/$rid" ;;
  esac
done
[ -n "$device$simulator" ] || { echo "no rids given" >&2; exit 1; }

# One library per slice: several simulator architectures become one fat library.
slice_library() {
  library=$1
  shift
  if [ $# -eq 1 ]; then
    echo "$1/$library"
  else
    mkdir -p "$work/fat"
    # shellcheck disable=SC2046 # one path per argument, none contain spaces
    lipo -create $(for dir in "$@"; do echo "$dir/$library"; done) -output "$work/fat/$library"
    echo "$work/fat/$library"
  fi
}

rm -rf "$out"
mkdir -p "$out"
for framework in native wasm; do
  if [ "$framework" = native ]; then
    library=libemma_native.a; name=EmmaNative; headers="$root/src/EMMA.Native/include"
  else
    library=libemma_wasm.a; name=EmmaWasm; headers="$root/native/emma-wasm/include"
  fi
  args=""
  if [ -n "$device" ]; then
    args="$args -library $(slice_library "$library" "$device") -headers $headers"
  fi
  if [ -n "$simulator" ]; then
    # shellcheck disable=SC2086 # $simulator is a list of directories
    args="$args -library $(slice_library "$library" $simulator) -headers $headers"
  fi
  # shellcheck disable=SC2086
  xcodebuild -create-xcframework $args -output "$out/$name.xcframework"
done
echo "packaged $out"
