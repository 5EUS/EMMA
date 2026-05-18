#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
template_root="$(cd "$script_dir/.." && pwd)"

seed="$template_root"
destination=""
assembly_name=""
plugin_id=""
force="false"

slugify_assembly_name() {
  local input="$1"
  local slug

  slug="$(printf '%s' "$input" | sed -E 's/([a-z0-9])([A-Z])/\1-\2/g; s/[._[:space:]]+/-/g' | tr '[:upper:]' '[:lower:]')"
  slug="$(printf '%s' "$slug" | sed -E 's/^-+//; s/-+$//; s/--+/-/g')"
  printf '%s' "$slug"
}

rename_paths_with_token() {
  local root="$1"
  local old_token="$2"
  local new_token="$3"

  if [[ -z "$old_token" || "$old_token" == "$new_token" ]]; then
    return 0
  fi

  while IFS= read -r -d '' path; do
    local parent
    local base
    local renamed

    parent="$(dirname "$path")"
    base="$(basename "$path")"
    renamed="${base//${old_token}/${new_token}}"
    if [[ "$renamed" != "$base" ]]; then
      mv "$path" "$parent/$renamed"
    fi
  done < <(find "$root" -depth -mindepth 1 \( -type f -o -type d \) -name "*${old_token}*" -print0)
}

replace_token_in_text_files() {
  local root="$1"
  local old_token="$2"
  local new_token="$3"

  if [[ -z "$old_token" || "$old_token" == "$new_token" ]]; then
    return 0
  fi

  while IFS= read -r -d '' file; do
    perl -0pi -e "s/\Q$old_token\E/$new_token/g" "$file"
  done < <(find "$root" -type f \( \
    -name '*.cs' -o -name '*.csproj' -o -name '*.json' -o -name '*.md' -o -name '*.yml' -o -name '*.yaml' -o -name '*.sh' -o -name '*.py' -o -name '*.props' -o -name '*.targets' -o -name '*.xml' -o -name '*.wit' -o -name '*.txt' \
  \) -print0)
}

declare -a seed_project_names=()
declare -a replacement_pairs=()

collect_seed_project_names() {
  local root="$1"

  while IFS= read -r project_name; do
    seed_project_names+=("$project_name")
  done < <(find "$root" -maxdepth 1 -name '*.csproj' -print | sort | while IFS= read -r project_path; do
    basename "$project_path" .csproj
  done)
}

build_replacement_pairs() {
  local root_name="$1"
  local target_root_name="$2"

  replacement_pairs=()

  for seed_project_name in "${seed_project_names[@]}"; do
    local suffix=""
    local target_name

    if [[ "$seed_project_name" == "$root_name"* ]]; then
      suffix="${seed_project_name#$root_name}"
      target_name="$target_root_name$suffix"
    else
      target_name="$seed_project_name"
    fi

    replacement_pairs+=("$seed_project_name=$target_name")
  done
}

apply_project_name_replacements() {
  local root="$1"

  for pair in "${replacement_pairs[@]}"; do
    local old_name="${pair%%=*}"
    local new_name="${pair#*=}"
    rename_paths_with_token "$root" "$old_name" "$new_name"
  done

  for pair in "${replacement_pairs[@]}"; do
    local old_name="${pair%%=*}"
    local new_name="${pair#*=}"
    replace_token_in_text_files "$root" "$old_name" "$new_name"
  done
}

usage() {
  cat <<'EOF'
Usage:
  templates/plugin/scripts/scaffold-plugin.sh \
    --destination <path> \
    --assembly-name <EMMA.MyPlugin> \
    --plugin-id <emma.my.plugin> [options]

Options:
  --seed <path>            Optional explicit seed directory. If omitted, uses templates/plugin.
  --destination <path>     Target directory for the new plugin (required).
  --assembly-name <name>   Assembly/namespace root, e.g. EMMA.MyPlugin (required).
  --plugin-id <id>         Plugin id, e.g. emma.my.plugin (required).
  --force                  Allow destination directory to exist if empty.
  --help                   Show this help.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --seed)
      seed="$2"
      shift 2
      ;;
    --destination)
      destination="$2"
      shift 2
      ;;
    --assembly-name)
      assembly_name="$2"
      shift 2
      ;;
    --plugin-id)
      plugin_id="$2"
      shift 2
      ;;
    --force)
      force="true"
      shift
      ;;
    --help|-h)
      usage
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      usage
      exit 1
      ;;
  esac
done

if [[ -z "$destination" || -z "$assembly_name" || -z "$plugin_id" ]]; then
  echo "Missing required arguments." >&2
  usage
  exit 1
fi

if [[ ! -d "$seed" ]]; then
  echo "Seed directory not found: $seed" >&2
  exit 1
fi

if [[ -e "$destination" ]]; then
  if [[ "$force" != "true" ]]; then
    if [[ -n "$(find "$destination" -mindepth 1 -maxdepth 1 -print -quit 2>/dev/null)" ]]; then
      echo "Destination exists and is not empty: $destination" >&2
      echo "Use an empty directory or pass a new destination path." >&2
      exit 1
    fi
  fi
else
  mkdir -p "$destination"
fi

seed_csproj="$(find "$seed" -maxdepth 1 -name '*.csproj' | sort | head -n 1)"
seed_manifest="$(find "$seed" -maxdepth 1 -name '*.plugin.json' | head -n 1)"
seed_solution="$(find "$seed" -maxdepth 1 -name '*.sln' | head -n 1)"

if [[ -z "$seed_csproj" || -z "$seed_manifest" ]]; then
  echo "Seed is missing .csproj or .plugin.json: $seed" >&2
  exit 1
fi

seed_assembly_name="$(basename "$seed_csproj" .csproj)"
seed_manifest_name="$(basename "$seed_manifest" .plugin.json)"
seed_solution_name=""
if [[ -n "$seed_solution" ]]; then
  seed_solution_name="$(basename "$seed_solution" .sln)"
fi
seed_plugin_id="$(grep -E '"id"\s*:\s*"' "$seed_manifest" | head -n 1 | sed -E 's/.*"id"\s*:\s*"([^"]+)".*/\1/')"
target_solution_name="$assembly_name"
collect_seed_project_names "$seed"
build_replacement_pairs "$seed_manifest_name" "$assembly_name"

if [[ -z "$seed_plugin_id" ]]; then
  echo "Could not parse plugin id from seed manifest: $seed_manifest" >&2
  exit 1
fi

rsync -a \
  --exclude '.git' \
  --exclude 'bin' \
  --exclude 'obj' \
  --exclude 'artifacts' \
  --exclude 'pack' \
  --exclude 'build' \
  --exclude '.dart_tool' \
  --exclude '.DS_Store' \
  --exclude 'scripts/scaffold-plugin.sh' \
  "$seed/" "$destination/"

apply_project_name_replacements "$destination"
rename_paths_with_token "$destination" "$seed_manifest_name" "$assembly_name"

if [[ -n "$seed_solution_name" && -f "$destination/$seed_solution_name.sln" ]]; then
  mv "$destination/$seed_solution_name.sln" "$destination/$target_solution_name.sln"
fi

replace_token_in_text_files "$destination" "$seed_manifest_name" "$assembly_name"
replace_token_in_text_files "$destination" "$seed_plugin_id" "$plugin_id"
if [[ -n "$seed_solution_name" ]]; then
  replace_token_in_text_files "$destination" "$seed_solution_name" "$target_solution_name"
fi

echo "Scaffold complete."
echo "  Seed:          $seed"
echo "  Destination:   $destination"
echo "  Assembly name: $assembly_name"
echo "  Plugin id:     $plugin_id"
echo
echo "Next steps:"
echo "  1) cd $destination"
echo "  2) dotnet build $assembly_name.csproj"
echo "  3) WASI_SDK_PATH=/path/to/wasi-sdk dotnet build $assembly_name.Wasm.csproj"
echo "  4) Review the stub comments in Core/, ASPNET/, WASM/, Program.cs, and *.plugin.json"
echo "  5) Start with Core/CoreClient.cs for paged media, then Core/SourceFeatures.cs for optional search metadata and suggestion flows"
