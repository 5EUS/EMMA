#!/usr/bin/env bash
set -euo pipefail

root_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)

plugin_host_manifest_dir="$root_dir/src/EMMA.PluginHost/plugins"
log_dir="$root_dir/.tmp"
plugin_log="$log_dir/test-plugin.log"

mkdir -p "$log_dir"

dotnet build "$root_dir/EMMA.sln"

dotnet run --no-build --project "$root_dir/src/EMMA.TestPlugin/EMMA.TestPlugin.csproj" \
  >"$plugin_log" 2>&1 &
plugin_pid=$!

wait_for_port() {
  local host=$1
  local port=$2
  local retries=30

  for _ in $(seq 1 "$retries"); do
    if (echo >"/dev/tcp/$host/$port") >/dev/null 2>&1; then
      return 0
    fi
    sleep 0.2
  done

  return 1
}

cleanup() {
  kill "$plugin_pid" 2>/dev/null || true
}
trap cleanup EXIT

if ! wait_for_port "localhost" 5005; then
  echo "Test plugin did not start on port 5005." >&2
  if ! kill -0 "$plugin_pid" 2>/dev/null; then
    echo "Test plugin exited early. Logs:" >&2
    tail -n 50 "$plugin_log" >&2 || true
  fi
  exit 1
fi

if ! kill -0 "$plugin_pid" 2>/dev/null; then
  echo "Test plugin exited before host start. Logs:" >&2
  tail -n 50 "$plugin_log" >&2 || true
  exit 1
fi

PluginHost__ManifestDirectory="$plugin_host_manifest_dir" \
  dotnet run --no-build --project "$root_dir/src/EMMA.PluginHost/EMMA.PluginHost.csproj"
