#!/usr/bin/env sh
# Publishes the native library (src/EMMA.Native) with NativeAOT for one desktop runtime identifier and gives it its
# conventional name: libemma_native.so, libemma_native.dylib or emma_native.dll.
#
#   sh scripts/publish-native.sh [rid] [output-dir]
#
# rid defaults to the host's; output-dir defaults to artifacts/native/aot/<rid>.
# Only desktop targets are supported. iOS and Android are not published here: see the note in
# src/EMMA.Native/EMMA.Native.csproj.
set -eu
root=$(cd "$(dirname "$0")/.." && pwd)

rid=${1:-}
if [ -z "$rid" ]; then
  case "$(uname -s)-$(uname -m)" in
    Linux-x86_64) rid=linux-x64 ;;
    Linux-aarch64 | Linux-arm64) rid=linux-arm64 ;;
    Darwin-arm64) rid=osx-arm64 ;;
    Darwin-x86_64) rid=osx-x64 ;;
    MINGW* | MSYS* | CYGWIN*) rid=win-x64 ;;
    *) echo "cannot guess a runtime identifier for this host; pass one" >&2; exit 1 ;;
  esac
fi
out=${2:-"$root/artifacts/native/aot/$rid"}

# --no-restore: a restore with -r rewrites the lock files of every project it touches. The projects list their runtime
# identifiers, so the repository-wide `dotnet restore` has already fetched what a publish needs.
dotnet publish "$root/src/EMMA.Native/EMMA.Native.csproj" -c Release -r "$rid" --self-contained --no-restore -o "$out"

# The SDK names the output after the assembly; rename it to the library's public name.
case "$rid" in
  win-*) built="$out/EMMA.Native.dll"; public="$out/emma_native.dll" ;;
  osx-*) built="$out/EMMA.Native.dylib"; public="$out/libemma_native.dylib" ;;
  *) built="$out/EMMA.Native.so"; public="$out/libemma_native.so" ;;
esac
[ -f "$built" ] || { echo "expected $built was not produced" >&2; exit 1; }
mv -f "$built" "$public"
echo "published $public"
