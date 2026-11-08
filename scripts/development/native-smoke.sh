#!/usr/bin/env sh
# Publishes the native library with NativeAOT and runs the C harness against it: the one check that proves the real
# shared library loads, exports what the header says and survives being driven from C. Needs a C compiler
# (and the NativeAOT toolchain). The harness links libm because libe_sqlite3 expects the host process to have it
# loaded, as any real application does; on Windows the managed tests cover behaviour and this is skipped.
set -eu
root=$(cd "$(dirname "$0")/../.." && pwd)
cd "$root"

case "$(uname -s)" in
  Linux) library=libemma_native.so; link="-ldl -lpthread -lm" ;;
  Darwin) library=libemma_native.dylib; link="" ;;
  *) echo "skipping native smoke test on $(uname -s)" >&2; exit 0 ;;
esac
command -v cc >/dev/null 2>&1 || { echo "skipping native smoke test: no C compiler" >&2; exit 0; }

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

sh scripts/publish-native.sh "" "$work/lib" >"$work/publish.log" 2>&1 || { cat "$work/publish.log" >&2; exit 1; }
# shellcheck disable=SC2086
cc -std=c11 -Wall -Wextra -Werror -I src/EMMA.Native/include tests/EMMA.Native.Harness/harness.c -o "$work/harness" $link
"$work/harness" "$work/lib/$library" "$work/data"
