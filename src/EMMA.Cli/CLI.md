# EMMA CLI

Simple CLI that calls the embedded API adapter backed by the plugin host pipeline.
The CLI now boots through a `PluginDevSession` so profile/config resolution and
session metadata are available before commands run.

## Usage

```bash
EMMA_PLUGIN_HOST_URL=http://localhost:5001 EMMA_PLUGIN_ID=demo dotnet run --project src/EMMA.Cli demo
```

Arguments:
- First argument is the search query (defaults to `demo`).

Environment:
- `EMMA_PLUGIN_HOST_URL` (default: `http://localhost:5001`)
- `EMMA_PLUGIN_ID` (default: `demo`)
- `EMMA_PLUGIN_PROFILE` selects a profile from `plugin.dev.json`.
- `EMMA_PLUGIN_DEV_CONFIG` points to an explicit `plugin.dev.json` file.
- `EMMA_PLUGIN_TARGET` overrides the resolved runtime target for session metadata.
- `EMMA_PLUGIN_EXECUTION_MODE` overrides the resolved execution mode.

## Session bootstrap

- The CLI looks for `plugin.dev.json` in the current working directory and then
	walks up parent directories until it finds one.
- If no config is found, the CLI falls back to an inferred `host-bridge`
	profile using the current environment variables.
- `session` prints the resolved session state, profile, diagnostics, and host
	configuration.