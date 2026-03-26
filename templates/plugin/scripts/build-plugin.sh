#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
PLUGIN_DIR=$(cd "$SCRIPT_DIR/.." && pwd)
OUT_DIR="$PLUGIN_DIR/artifacts"
PUBLISH_DIR="$OUT_DIR/publish"
PROJECT_PATH="${PROJECT_PATH:-}"

resolve_project_path() {
  if [[ -n "$PROJECT_PATH" ]]; then
    echo "$PROJECT_PATH"
    return 0
  fi

  local -a csproj_candidates=()
  while IFS= read -r candidate; do
    csproj_candidates+=("$candidate")
  done < <(find "$PLUGIN_DIR" -maxdepth 1 -type f -name "*.csproj" | sort)

  if [[ ${#csproj_candidates[@]} -eq 1 ]]; then
    echo "${csproj_candidates[0]}"
    return 0
  fi

  if [[ ${#csproj_candidates[@]} -eq 0 ]]; then
    echo "No .csproj found in plugin directory: $PLUGIN_DIR" >&2
  else
    echo "Multiple .csproj files found in plugin directory. Set PROJECT_PATH explicitly." >&2
    printf '  - %s\n' "${csproj_candidates[@]}" >&2
  fi

  exit 1
}

rm -rf "$PUBLISH_DIR"
mkdir -p "$PUBLISH_DIR"

RESOLVED_PROJECT_PATH="$(resolve_project_path)"
dotnet publish "$RESOLVED_PROJECT_PATH" -c Release -o "$PUBLISH_DIR"

ENTRYPOINT="$(basename "$RESOLVED_PROJECT_PATH" .csproj)"
if [[ "$OSTYPE" == "msys"* || "$OSTYPE" == "cygwin"* || "$OSTYPE" == "win32"* ]]; then
  ENTRYPOINT+=".exe"
fi

echo "Built plugin to: $PUBLISH_DIR"
echo "Entrypoint: $ENTRYPOINT"
