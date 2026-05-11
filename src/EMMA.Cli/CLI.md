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
- `EMMA_PLUGIN_DEV_MODE` defaults to `1` inside the CLI unless the active profile disables plugin logging.
- The CLI writes `obj/EMMA.PluginDev.props` under the discovered plugin root so design-time builds can mirror the active profile's `PluginTransport` for VS Code linting and IntelliSense.

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
- `watch [start|stop|status]` uses profile watch globs plus the active
	`plugin.dev.json` file to debounce matching file changes through the shared
	session backend.

## WASM development commands

- `build` runs a normalized `dotnet publish` plan for the active `wasm-dev`
	profile.
- `build` also runs normalized native publish plans for `linux-dev` and
	`windows-dev`.
- `build all` iterates over every resolved profile and runs each normalized
	build plan in turn before restoring the original active profile.
- `pack` packages the active profile artifact using the normalized pack flow for
	the active runtime target.
- `pack all` iterates over every resolved profile and packages each one before
	restoring the original active profile.
- `build-pack` builds the active profile and then packages it.
- `build-pack all` iterates over every resolved profile, builds it, packages
	it, and then restores the original active profile.
- `reload` reports the reload semantics for the active runtime adapter.
- `watch start` begins recursive file watching rooted at the discovered plugin
	project directory. Matching changes are batched before reload is requested.
- `watch status` reports the current watch state, last observed change, and
	last reload outcome.
- For `wasm-dev`, source edits still require `build` so the emitted `.wasm`
	artifact changes before the next invocation. Watch is primarily responsible
	for surfacing change detection and refreshing the runtime once new artifacts
	are available.
- `scenario paged-smoke [query]` runs a built-in search -> chapters -> page
	smoke flow against the active runtime.
- `scenario <name> [query]` also runs config-defined scenarios from
	`plugin.dev.json` or `EMMA_PLUGIN_DEV_CONFIG` when the config declares a
	`scenarios` block.

## Native development commands

- `linux-dev` and `windows-dev` reuse the same command surface as `wasm-dev`
	for search, chapters, page, reload, and `scenario paged-smoke`.
- Native direct execution launches the published plugin executable locally and
	reuses the host-bridge API against the configured `HostUrl`.
- `reload` restarts the managed native plugin process for native direct
	profiles.
- `watch start` is most useful for direct native profiles because reload can
	restart the managed process immediately after a matching change batch.

## Session API and UI

- `serve [port]` hosts a local HTTP API and browser UI for the current working
	directory.
- The browser UI supports profile selection, build/reload actions, scenario
	execution, watch start/stop controls, session inspection, and a lightweight
	operation log feed.
- The CLI and the local API both execute through the same session application
	service so Phase 5 does not create a parallel orchestration path.

## Sample configuration

- `emma-test-plugin/plugin.dev.sample.json` provides a sample Phase 6 configuration with watch globs for `wasm-dev`, `linux-dev`, and `windows-dev`.
- Per-profile `logging` supports `plugin` (default `true`), `aspNetHost` (default `false`), and `httpClient` (default `false`).
- Per-profile `wasiSdkPath` lets `wasm-dev` carry a default `WASI_SDK_PATH` without relying on the shell environment.
- Per-profile `sync` supports `enabled`, `destinationPath`, `onBuild`, and `cleanDestination` for build/watch artifact mirroring into local `emmaui` plugin directories.
- `scenariosPath` lets plugin repos point at a folder of individual scenario DSL
	files so custom flows do not have to live inline in the main dev config.
- Point `EMMA_PLUGIN_DEV_CONFIG` at that file when you want to exercise the
	shared watch flow without creating a local bespoke config.

## Custom scenario DSL

- Custom scenarios live in a scenario folder, which defaults to `scenarios/`
	next to the resolved config file or can be overridden with `scenariosPath`.
- Each scenario file is its own JSON document and can declare `name`,
	`displayName`, `description`, `defaultQuery`, `supportsQuery`, `queryLabel`,
	optional `profiles`, and a `steps` array.
- A step always has `op`, can optionally `save` its output into a named
	variable, and can add arbitrary parameters depending on the operation.
- String parameters support direct expression references like `$query` and
	template interpolation like `Selected {{media.title}} with status {{media.metadata.status}}`.
- Expressions resolve previously saved variables and object properties using
	dot-paths. Metadata lists are exposed by key, so `media.metadata.status`
	reads the metadata entry whose key is `status`.

Supported step ops:

- `search`: `query`, optional `save`
- `chapters`: `mediaId`, optional `save`
- `page`: `mediaId`, `chapterId`, `index`, optional `save`
- `pages`: `mediaId`, `chapterId`, `startIndex`, `count`, optional `save`
- `selectFirst`: `from`, optional `save`
- `selectAt`: `from`, `index`, optional `save`
- `requireCount`: `from`, optional `min`, optional `max`
- `requireNotNull`: `value`, optional `message`, optional `save`
- `set`: `value`, required `save`
- `log`: `message`

Minimal example:

```json
{
	"name": "metadata-smoke",
	"displayName": "Metadata Smoke",
	"description": "Search and surface metadata from the first result.",
	"defaultQuery": "naruto",
	"steps": [
		{ "op": "search", "query": "$query", "save": "results" },
		{ "op": "requireCount", "from": "$results", "min": 1 },
		{ "op": "selectFirst", "from": "$results", "save": "media" },
		{ "op": "log", "message": "Selected {{media.title}} ({{media.id}})" },
		{ "op": "log", "message": "Status={{media.metadata.status}} Year={{media.metadata.year}}" }
	]
}
```

## AI workflow guidance

- A repo instruction file is the best default customization for AI workflows with this CLI because most tasks need the same always-available operational rules, not a separate agent mode.
- Prefer a custom agent only when you want an explicit, repeated multi-step workflow with isolated context, such as `doctor -> build -> scenario -> serve/watch diagnostics` as one named operation.
- For agent-driven troubleshooting, start with `session` or `doctor`, then keep the active profile explicit with `EMMA_PLUGIN_PROFILE` and the config explicit with `EMMA_PLUGIN_DEV_CONFIG` when you are using a non-default config file.
- For plugin-dev tasks, prefer the normalized CLI surface before falling back to helper scripts so AI tooling exercises the same path users rely on.

## CI smoke coverage

- `emma-test-plugin/.github/workflows/plugin-dev-smoke.yml` runs Phase 6 smoke scenarios for `linux-dev` and `wasm-dev` on Ubuntu.
- The workflow checks out `5EUS/EMMA`, builds the native WASM runtime bridge,
	then runs the shared CLI `build` and `scenario paged-smoke` commands instead
	of relying on bespoke packaging scripts.