#!/usr/bin/env sh
# Builds everything an Android app bundles and lays it out as a jniLibs directory:
#
#   <output-dir>/<abi>/libemma_native.so   the C ABI (NativeAOT, linux-bionic)
#   <output-dir>/<abi>/libemma_wasm.so     the Wasm plugin host (Rust)
#   <output-dir>/<abi>/libe_sqlite3.so     SQLite, from the SQLitePCLRaw Android package
#   <output-dir>/<abi>/libssl.so           OpenSSL, built here from source (see build_openssl)
#   <output-dir>/<abi>/libcrypto.so        (libssl's dependency)
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

# NativeAOT's linux-bionic runtime pack links .NET's OpenSSL crypto shim, which dlopens libssl at the first use of SHA-256,
# RSA, random bytes or TLS and aborts the process ("No usable version of libssl was found") when it finds none. Android
# apps cannot reach the system's BoringSSL, so each ABI bundles its own OpenSSL. The shim tries libssl.so.3, libssl.so.1.1
# and so on, then plain libssl.so, which is the only name Gradle packages. Use a currently supported LTS line (3.5 is
# supported until April 2030; 3.0 ended in September 2026) and keep the digest in step with the version.
openssl_version=${OPENSSL_ANDROID_VERSION:-3.5.9}
openssl_sha256=${OPENSSL_ANDROID_SHA256:-603f5602e2eef00d77fbd429d34dcd5822bb301757a1bc9cdb24c670f1eb859a}
cache_dir=${EMMA_CACHE_DIR:-"$root/artifacts/cache"}

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

# Prints the SHA-256 of $1.
sha256_of() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi
}

# Downloads the OpenSSL source tarball into the cache (once) and fails unless it matches the pinned digest.
fetch_openssl() {
  mkdir -p "$cache_dir"
  tarball="$cache_dir/openssl-$openssl_version.tar.gz"
  if [ ! -f "$tarball" ]; then
    curl -fsSL --retry 3 -o "$tarball.part" \
      "https://github.com/openssl/openssl/releases/download/openssl-$openssl_version/openssl-$openssl_version.tar.gz"
    mv -f "$tarball.part" "$tarball"
  fi
  if [ "$(sha256_of "$tarball")" != "$openssl_sha256" ]; then
    rm -f "$tarball"
    echo "openssl-$openssl_version.tar.gz does not match its pinned SHA-256; deleted it" >&2
    exit 1
  fi
}

# Builds libssl.so and libcrypto.so for the rid in $1 (OpenSSL's target name in $2) into the directory $3. Needs
# android_toolchain to have run for this rid (ndk, ndk_bin and api) and perl and make on the host. OpenSSL's android
# targets name the libraries without a version suffix, which is what lets Gradle package them; the SONAME and NEEDED
# checks below fail the build if that ever changes.
build_openssl() {
  src="$work/openssl-$1"
  mkdir -p "$src"
  tar xzf "$cache_dir/openssl-$openssl_version.tar.gz" -C "$src" --strip-components=1
  (
    cd "$src"
    export ANDROID_NDK_ROOT="$ndk" PATH="$ndk_bin:$PATH"
    perl ./Configure "$2" "-D__ANDROID_API__=$api" shared no-tests no-docs no-legacy -Wl,-z,max-page-size=16384 >configure.log 2>&1 \
      || { cat configure.log >&2; exit 1; }
    make -j"$(getconf _NPROCESSORS_ONLN 2>/dev/null || echo 4)" build_libs >make.log 2>&1 || { tail -n 40 make.log >&2; exit 1; }
  )
  for lib in libssl.so libcrypto.so; do
    [ -f "$src/$lib" ] || { echo "OpenSSL did not produce $lib (found: $(cd "$src" && ls ./*.so* 2>/dev/null | tr '\n' ' '))" >&2; exit 1; }
    "$ndk_bin/llvm-strip" --strip-unneeded -o "$3/$lib" "$src/$lib"
  done
  soname=$("$ndk_bin/llvm-readelf" -d "$3/libssl.so" | sed -n 's/.*SONAME.*\[\(.*\)\]/\1/p')
  needed=$("$ndk_bin/llvm-readelf" -d "$3/libssl.so" | sed -n 's/.*NEEDED.*\[\(.*\)\]/\1/p' | tr '\n' ' ')
  [ "$soname" = libssl.so ] || { echo "libssl.so has SONAME '$soname'" >&2; exit 1; }
  case " $needed " in *" libcrypto.so "*) ;; *) echo "libssl.so needs '$needed', not libcrypto.so" >&2; exit 1 ;; esac
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
fetch_openssl

for rid in $rids; do
  resolve_rid "$rid"
  case "$rid" in android-*) ;; *) echo "$rid is not an Android rid" >&2; exit 1 ;; esac
  dir="$out/$abi"
  mkdir -p "$dir"
  sh "$root/native/build-host.sh" "$rid" "$work/$rid"
  sh "$root/scripts/publish-native.sh" "$rid" "$work/$rid"
  extract_sqlite "$abi" "$work/$rid/libe_sqlite3.so"
  case "$rid" in android-arm64) openssl_target=android-arm64 ;; *) openssl_target=android-x86_64 ;; esac
  android_toolchain
  build_openssl "$rid" "$openssl_target" "$work/$rid"
  for lib in libemma_native.so libemma_wasm.so libe_sqlite3.so libssl.so libcrypto.so; do
    check_alignment "$work/$rid/$lib"
    cp "$work/$rid/$lib" "$dir/$lib"
  done
  echo "packaged $dir"
done
