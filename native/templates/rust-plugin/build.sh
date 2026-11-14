#!/usr/bin/env sh
# Builds the plugin to a Wasm component, puts it where the manifest expects it, and packs it.
#
#   sh build.sh                    build, then pack an unsigned package into dist/ (for development)
#   sh build.sh --no-pack          build only: leaves plugin.wasm next to plugin.json, ready for `emma plugin run .`
#   sh build.sh --sign-key signer.pem --key-id my-key --repository my-repo --valid-days 30
#                                  build, then pack and sign (every other argument goes to `emma plugin pack`)
#
# Needs Rust with the Wasm component target (`rustup target add wasm32-wasip2`) and the `emma` command-line tool on
# the PATH, or named by the EMMA environment variable. The crate name comes from Cargo.toml, so renaming the crate
# needs no change here. Extra cargo flags (for example --locked) go in the CARGO_FLAGS environment variable.
set -eu
cd "$(dirname "$0")"

pack=1
if [ "${1:-}" = "--no-pack" ]; then
  pack=0
  shift
fi

crate=$(sed -n 's/^name *= *"\(.*\)"/\1/p' Cargo.toml | head -n 1)
if [ -z "$crate" ]; then
  echo "cannot find the crate name in Cargo.toml" >&2
  exit 1
fi

if [ ! -d wit ]; then
  echo "wit/ is missing: it is the EMMA contract, copied in by 'emma plugin new'" >&2
  exit 1
fi

# shellcheck disable=SC2086 # CARGO_FLAGS is a word list on purpose
cargo build --release --target wasm32-wasip2 ${CARGO_FLAGS:-}
cp "target/wasm32-wasip2/release/$crate.wasm" plugin.wasm
echo "built plugin.wasm ($(wc -c < plugin.wasm | tr -d ' ') bytes)"

if [ "$pack" -eq 1 ]; then
  # Default the output directory to dist/ unless the caller names one.
  case " $* " in
    *" --out "*) "${EMMA:-emma}" plugin pack . "$@" ;;
    *) "${EMMA:-emma}" plugin pack . --out dist/ "$@" ;;
  esac
fi
