#!/usr/bin/env sh
# Merges the committed .vscode/*.recommended.json files (settings, launch, tasks) into your local, git-ignored
# .vscode/*.json. Anything you already defined wins: settings keys are never overwritten (nested objects are merged),
# and launch configurations, tasks and inputs are matched by name, label and id, so yours stay and only missing ones
# are added. A changed file is first saved as <name>.json.bak. Safe to re-run.
set -eu
root=$(cd "$(dirname "$0")/../.." && pwd)
dir=$root/.vscode

command -v jq >/dev/null 2>&1 || { echo "jq is required (brew install jq)" >&2; exit 1; }

# Entries of $b whose $k is missing from $a, appended to $a.
filter='
. as $f | $f[0] as $r | $f[1] as $l
| def add($a; $b; $k): ($a // []) + [($b // [])[] | . as $e | select((($a // []) | map(.[$k]) | index($e[$k])) | not)];
  def inputs: if (($r.inputs // []) + ($l.inputs // []) | length) == 0 then {} else {inputs: add($l.inputs; $r.inputs; "id")} end;
  if $kind == "settings" then $r * $l
  elif $kind == "launch" then ($r * $l) + {configurations: add($l.configurations; $r.configurations; "name")} + inputs
  else ($r * $l) + {tasks: add($l.tasks; $r.tasks; "label")} + inputs
  end'

for kind in settings launch tasks; do
  recommended=$dir/$kind.recommended.json
  local=$dir/$kind.json
  [ -f "$recommended" ] || continue
  if [ ! -f "$local" ]; then
    cp "$recommended" "$local"
    echo "created $local"
    continue
  fi
  # VS Code allows comments in these files; jq does not. Refuse rather than rewrite a file we cannot parse.
  jq empty "$local" 2>/dev/null || { echo "$local has comments or invalid JSON; merge $recommended by hand" >&2; continue; }
  tmp=$(mktemp)
  jq -s --arg kind "$kind" "$filter" "$recommended" "$local" >"$tmp"
  if [ "$(jq -S . "$tmp")" = "$(jq -S . "$local")" ]; then
    echo "$local already up to date"
  else
    cp "$local" "$local.bak"
    cp "$tmp" "$local"
    echo "merged into $local (backup: $kind.json.bak)"
  fi
  rm -f "$tmp"
done

cat <<'MSG'
Settings cannot disable an extension. To stop the Java language server in this repo, open the Extensions view,
choose "Language Support for Java(TM) by Red Hat", and pick Disable (Workspace).
MSG
