#!/usr/bin/env sh
set -eu
root=$(cd "$(dirname "$0")/../.." && pwd)
cd "$root"
dotnet format whitespace --no-restore
