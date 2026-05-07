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
- The CLI also walks up parent directories looking for a nearby plugin manifest
	(`*.plugin.json`) and project file (`*.csproj`) so it can infer targets and
	artifact locations.
- If no config is found, the CLI falls back to an inferred `host-bridge`
	profile using the current environment variables.
- When a nearby plugin project is found, the CLI infers `wasm-dev`,
	`linux-dev`, and `windows-dev` profiles where the project metadata supports
	them.
- `wasm-dev` now uses a direct native runtime adapter against the built WASM
	component artifact.
- `linux-dev` and `windows-dev` now use a local native-process adapter when the
	current host OS can run the selected target. On unsupported hosts, `doctor`
	explains the fallback path.
- `session` prints the resolved session state, profile, diagnostics, and host
	configuration.
- `doctor` prints discovery results, inferred profiles, artifact candidates,
	and pre-launch diagnostics.
- `serve [port]` starts a local session API and lightweight browser UI on top
	of the same backend used by the CLI commands.

## WASM development commands

- `build` runs a normalized `dotnet publish` plan for the active `wasm-dev`
	profile.
- `build` also runs normalized native publish plans for `linux-dev` and
	`windows-dev`.
- `pack` creates a simple WASM plugin package zip from the discovered manifest
	and resolved `.wasm` artifact.
- `reload` reports the reload semantics for the active runtime adapter.
- `scenario paged-smoke [query]` runs a built-in search -> chapters -> page
	smoke flow against the active runtime.

## Native development commands

- `linux-dev` and `windows-dev` reuse the same command surface as `wasm-dev`
	for search, chapters, page, reload, and `scenario paged-smoke`.
- Native direct execution launches the published plugin executable locally and
	reuses the host-bridge API against the configured `HostUrl`.
- `reload` restarts the managed native plugin process for native direct
	profiles.

## Session API and UI

- `serve [port]` hosts a local HTTP API and browser UI for the current working
	directory.
- The browser UI supports profile selection, build/reload actions, scenario
	execution, session inspection, and a lightweight operation log feed.
- The CLI and the local API both execute through the same session application
	service so Phase 5 does not create a parallel orchestration path.