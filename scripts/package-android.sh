#!/usr/bin/env sh
# Builds everything an Android app bundles and lays it out as a jniLibs directory:
#
#   <output-dir>/<abi>/libemma_native.so   the C ABI (NativeAOT, linux-bionic)
#   <output-dir>/<abi>/libemma_wasm.so     the Wasm plugin host (Rust)
#   <output-dir>/<abi>/libe_sqlite3.so     SQLite, from the SQLitePCLRaw Android package
#
#   sh scripts/package-android.sh [output-dir] [rid...]
#
# output-dir defaults to artifacts/mobile/android/jniLibs; rids default to android-arm64 and android-x64 (the second is
# for emulators). Copy the result into the app's android/app/src/main/jniLibs, which is what emmaui's
# tool/sync_native_mobile.sh does. Each rid publishes with NativeAOT once: this is slow and memory-hungry.
set -eu
. "$(dirname "$0")/common.sh"

out=${1:-"$root/artifacts/mobile/android/jniLibs"}
[ $# -gt 0 ] && shift
rids=${*:-"android-arm64 android-x64"}

# Keep in step with the SQLitePCLRaw.lib.e_sqlite3 version in src/EMMA.Storage/packages.lock.json.
sqlite_version=${SQLITEPCLRAW_ANDROID_VERSION:-2.1.13}
sqlite_package=sqlitepclraw.lib.e_sqlite3.android
nuget_root=${NUGET_PACKAGES:-"$HOME/.nuget/packages"}

# Writes the Android build of SQLite for $1 (an ABI) to $2. The glibc build the desktop restore fetches does not load on
# Android, so there is no fallback to it.
extract_sqlite() {
  nupkg="$nuget_root/$sqlite_package/$sqlite_version/$sqlite_package.$sqlite_version.nupkg"
  if [ ! -f "$nupkg" ]; then
    nupkg="$work/$sqlite_package.nupkg"
    [ -f "$nupkg" ] || curl -fsSL --retry 3 -o "$nupkg" \
      "https://api.nuget.org/v3-flatcontainer/$sqlite_package/$sqlite_version/$sqlite_package.$sqlite_version.nupkg"
  fi
  # unzip -p because the nupkg's entries carry no read permission; the aar is a zip inside it.
  unzip -p "$nupkg" 'lib/*/*.aar' > "$work/sqlite.aar"
  unzip -p "$work/sqlite.aar" "jni/$1/libe_sqlite3.so" > "$2"
}

# Fails when a library's segments are not 16 KiB aligned: Google Play rejects such an app when it targets Android 15 or later.
check_alignment() {
  if command -v llvm-readelf >/dev/null 2>&1; then reader=llvm-readelf; else reader=readelf; fi
  align=$("$reader" -lW "$1" | awk '$1 == "LOAD" { print $NF }' | sort -u | tr '\n' ' ')
  case "$align" in
    "0x4000 " | "0x10000 ") ;;
    *) echo "$1 is not 16 KiB aligned (LOAD alignment: $align)" >&2; exit 1 ;;
  esac
}

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

for rid in $rids; do
  resolve_rid "$rid"
  case "$rid" in android-*) ;; *) echo "$rid is not an Android rid" >&2; exit 1 ;; esac
  dir="$out/$abi"
  mkdir -p "$dir"
  sh "$root/native/build-host.sh" "$rid" "$work/$rid"
  sh "$root/scripts/publish-native.sh" "$rid" "$work/$rid"
  extract_sqlite "$abi" "$work/$rid/libe_sqlite3.so"
  for lib in libemma_native.so libemma_wasm.so libe_sqlite3.so; do
    check_alignment "$work/$rid/$lib"
    cp "$work/$rid/$lib" "$dir/$lib"
  done
  echo "packaged $dir"
done
