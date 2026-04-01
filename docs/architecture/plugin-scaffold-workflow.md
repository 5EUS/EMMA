# Plugin Scaffold Workflow

Use `templates/plugin/scripts/scaffold-plugin.sh` to bootstrap a new plugin from the template seed rather than relying on stale static templates.

## Why this flow

- Uses real, currently working plugin structure as source.
- Applies deterministic renaming for assembly and plugin id.
- Excludes build output and packaging artifacts.
- Supports removing temporary SDK backports for environments already on the latest SDK package.

## Command

```bash
templates/plugin/scripts/scaffold-plugin.sh \
  --seed templates/plugin \
  --destination ../emma-my-plugin \
  --assembly-name EMMA.MyPlugin \
  --plugin-id emma.my.plugin
```

## Options

- `--seed <path>`: optional explicit source plugin folder (defaults to `templates/plugin`).
- `--without-backport`: delete `Compatibility/` in generated plugin.
- `--force`: allow existing destination if empty.

## Recommended seed choice

- The default template is based on `emma-video-test` and is fixture-backed.
- Use `--seed` only when you intentionally want a different baseline.

## Post-scaffold checklist

1. Build:
```bash
dotnet build EMMA.MyPlugin.csproj
```
2. Update provider URL strategy in `Infrastructure/ProviderRequestUrls.cs`.
3. Confirm manifest fields in `EMMA.MyPlugin.plugin.json`.
4. Validate packaging/signing scripts in `scripts/` before first release.

## Notes

The scaffold intentionally copies script and runtime wiring exactly from the seed, because that is the most reliable way to keep compatibility with current host expectations. You can later trim features once your plugin behavior is stable.
