#!/usr/bin/env sh
set -eu
root=$(cd "$(dirname "$0")/../.." && pwd)
git -C "$root" config core.hooksPath .githooks
printf 'git hooks installed (core.hooksPath = .githooks)\n'
