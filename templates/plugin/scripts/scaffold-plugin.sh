#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
template_root="$(cd "$script_dir/.." && pwd)"

seed="$template_root"
destination=""
assembly_name=""
plugin_id=""
force="false"

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

seed_csproj="$(find "$seed" -maxdepth 1 -name '*.csproj' | head -n 1)"
seed_manifest="$(find "$seed" -maxdepth 1 -name '*.plugin.json' | head -n 1)"

if [[ -z "$seed_csproj" || -z "$seed_manifest" ]]; then
  echo "Seed is missing .csproj or .plugin.json: $seed" >&2
  exit 1
fi

seed_assembly_name="$(basename "$seed_csproj" .csproj)"
seed_manifest_name="$(basename "$seed_manifest" .plugin.json)"
seed_plugin_id="$(grep -E '"id"\s*:\s*"' "$seed_manifest" | head -n 1 | sed -E 's/.*"id"\s*:\s*"([^"]+)".*/\1/')"

if [[ -z "$seed_plugin_id" ]]; then
  echo "Could not parse plugin id from seed manifest: $seed_manifest" >&2
  exit 1
fi

rsync -a \
  --exclude '.git' \
  --exclude '.github' \
  --exclude 'bin' \
  --exclude 'obj' \
  --exclude 'artifacts' \
  --exclude 'pack' \
  --exclude 'build' \
  --exclude '.dart_tool' \
  --exclude '.DS_Store' \
  --exclude 'scripts/scaffold-plugin.sh' \
  "$seed/" "$destination/"

# Rename top-level project and manifest files.
if [[ -f "$destination/$seed_assembly_name.csproj" ]]; then
  mv "$destination/$seed_assembly_name.csproj" "$destination/$assembly_name.csproj"
fi

if [[ -f "$destination/$seed_manifest_name.plugin.json" ]]; then
  mv "$destination/$seed_manifest_name.plugin.json" "$destination/$assembly_name.plugin.json"
fi

# Replace seed identifiers with target identifiers in common text file types.
while IFS= read -r -d '' file; do
  perl -0pi -e "s/\Q$seed_assembly_name\E/$assembly_name/g" "$file"
  perl -0pi -e "s/\Q$seed_manifest_name\E/$assembly_name/g" "$file"
  perl -0pi -e "s/\Q$seed_plugin_id\E/$plugin_id/g" "$file"
done < <(find "$destination" -type f \( \
  -name '*.cs' -o -name '*.csproj' -o -name '*.json' -o -name '*.md' -o -name '*.yml' -o -name '*.yaml' -o -name '*.sh' -o -name '*.py' -o -name '*.props' -o -name '*.targets' -o -name '*.xml' -o -name '*.wit' -o -name '*.txt' \
\) -print0)

echo "Scaffold complete."
echo "  Seed:          $seed"
echo "  Destination:   $destination"
echo "  Assembly name: $assembly_name"
echo "  Plugin id:     $plugin_id"
echo
echo "Next steps:"
echo "  1) cd $destination"
echo "  2) dotnet build $assembly_name.csproj"
echo "  3) Review Program.cs, Infrastructure/, Services/, and *.plugin.json"
