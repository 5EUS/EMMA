#!/usr/bin/env sh
# Publishes the native library (src/EMMA.Native) with NativeAOT for one runtime identifier and gives it its public name.
#
#   sh scripts/publish-native.sh [rid] [output-dir]
#
# rid defaults to the host's; output-dir defaults to artifacts/native/aot/<rid>.
#
# Desktop: libemma_native.so, libemma_native.dylib or emma_native.dll, with everything the project copies beside it (SQLite,
#   the Wasm host if built). The rid must be the host's; NativeAOT cannot cross-compile between desktop platforms.
# android-*: libemma_native.so alone, cross-compiled with the NDK's clang (the .NET Android runtime pack is the
#   "linux-bionic" one). Needs the NDK, see common.sh.
# ios-*, iossimulator-*: libemma_native.a alone, a static library the app links together with libemma_wasm.a. Needs macOS
#   and Xcode.
#
# NativeAOT publishes are memory-hungry: run one per rid, not in a loop. A desktop publish relies on the repository-wide
# `dotnet restore` (the project lists its desktop rids), hence --no-restore. A mobile publish restores here instead: the
# NativeAOT runtime pack for a mobile rid is only fetched by a restore that names the rid, and such a restore cannot share
# the desktop lock files. EmmaMobileRid (Directory.Build.props) gives it its own obj/mobile-<rid>/ and an unlocked restore;
# the desktop restore and `--no-restore` builds are left as they were.
set -eu
. "$(dirname "$0")/common.sh"

rid=${1:-}
[ -n "$rid" ] || rid=$(host_rid) || exit 1
resolve_rid "$rid"
out=${2:-"$root/artifacts/native/aot/$rid"}
project="$root/src/EMMA.Native/EMMA.Native.csproj"

if [ "$kind" = desktop ]; then
  dotnet publish "$project" -c Release -r "$rid" --self-contained --no-restore -o "$out"
  # The SDK names the output after the assembly; rename it to the library's public name.
  case "$rid" in
    win-*) built="$out/EMMA.Native.dll"; public="$out/emma_native.dll" ;;
    osx-*) built="$out/EMMA.Native.dylib"; public="$out/libemma_native.dylib" ;;
    *) built="$out/EMMA.Native.so"; public="$out/libemma_native.so" ;;
  esac
  [ -f "$built" ] || { echo "expected $built was not produced" >&2; exit 1; }
  mv -f "$built" "$public"
  echo "published $public"
  exit 0
fi

if [ "$kind" = android ]; then
  android_toolchain
  export CC="$ndk_clang" CXX="${ndk_clang}++" AR="$ndk_bin/llvm-ar"
  library="-p:NativeLib=Shared -p:CppCompilerAndLinker=$ndk_clang -p:CppLinker=$ndk_clang -p:CppLibCreator=$ndk_bin/llvm-ar -p:ObjCopyName=$ndk_bin/llvm-objcopy"
  built=EMMA.Native.so
  public=libemma_native.so
else
  require_xcode
  library="-p:NativeLib=Static"
  built=EMMA.Native.a
  public=libemma_native.a
fi

# The publish directory also holds the managed assemblies and the desktop SQLite and Wasm host libraries the project copies
# (wrong for a phone), so it is a scratch directory and only the library itself is kept.
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
# The project's desktop rids are replaced by this one so that only its packs are fetched. _IsPublishing makes the restore
# choose the NativeAOT runtime pack, as the restore inside `dotnet publish` does.
isolate="-p:EmmaMobileRid=$rid -p:RuntimeIdentifiers=$dotnet_rid"
# shellcheck disable=SC2086 # $library and $isolate are lists of options.
dotnet restore "$project" -r "$dotnet_rid" -p:PublishAotUsingRuntimePack=true -p:_IsPublishing=true $isolate
# shellcheck disable=SC2086
dotnet publish "$project" -c Release -r "$dotnet_rid" --self-contained --no-restore \
  -p:PublishAotUsingRuntimePack=true $library $isolate -o "$work"

[ -f "$work/$built" ] || { echo "expected $built was not produced" >&2; exit 1; }
mkdir -p "$out"
mv -f "$work/$built" "$out/$public"
echo "published $out/$public"
