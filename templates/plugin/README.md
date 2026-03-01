# EMMA Plugin Template

A minimal gRPC plugin template that is easy to copy and customize.

## Quick start

1. Copy this folder to a new location in the repo.
2. Rename the project and namespaces (search for `EMMA.PluginTemplate`).
3. Update the project reference in the csproj if needed.
4. Update the manifest paths in `EMMA.PluginTemplate.plugin.json`.
5. Run the plugin.

```bash
dotnet run --project templates/plugin/EMMA.PluginTemplate.csproj
```

Default port is 5005. Override with:

```bash
dotnet run --project templates/plugin/EMMA.PluginTemplate.csproj -- --port 6001
```

or

```bash
EMMA_PLUGIN_PORT=6001 dotnet run --project templates/plugin/EMMA.PluginTemplate.csproj
```

## Structure

- `Program.cs`: gRPC host + HTTP/2 Kestrel setup.
- `Infrastructure/HttpJsonClient.cs`: HTTP + JSON helpers for external APIs.
- `Infrastructure/PluginEnvironment.cs`: port helper.
- `Services/PluginControlService.cs`: health + capabilities endpoints.
- `Services/SearchProviderService.cs`: stub search provider.
- `Services/PageProviderService.cs`: stub page provider.
- `Services/VideoProviderService.cs`: stub video provider.
- `Services/PluginRpcGuard.cs`: deadline/cancel guard + correlation id.

## Manifest

The template includes a starter manifest:

- [templates/plugin/EMMA.PluginTemplate.plugin.json](templates/plugin/EMMA.PluginTemplate.plugin.json)

Before using it, replace:

- `protocol` declares the transport; entrypoints are auto-resolved from the bundle/binary name.
- `endpoint` is optional; the host assigns a free port when omitted.
- `permissions.paths` with sandbox-relative paths.
- `signature.value` if the host requires signed manifests.

## Build + signing scripts

Scripts live under `templates/plugin/scripts`:

- `build-plugin.sh` publishes a Release build to `templates/plugin/artifacts`.
- `build-plugin-macos-app.sh` builds and signs a self-contained macOS `.app` bundle for App Sandbox.
- `build-pack-plugin.sh` builds and packages versioned zips for `osx-*`, `linux-*`, and `wasm*` targets.
- `sign-plugin.sh` updates the manifest signature using `EMMA_HMAC_KEY_BASE64`.
- `generate-hmac-key.sh` prints a random base64 key (default 32 bytes).
- `sign-plugin-macos-app.sh` re-signs a macOS `.app` bundle with entitlements.

For full behavior, platform constraints, and root-level sync/publish tooling, see:

- [docs/architecture/tooling-scripts-reference.md](../../docs/architecture/tooling-scripts-reference.md)

`build-pack-plugin.sh` supports multiple targets via `TARGETS` (space-separated). Each zip is named:

`PLUGINID_VERSION_TARGET.zip`

Examples:

```bash
TARGETS="osx-arm64 linux-x64" templates/plugin/scripts/build-pack-plugin.sh
```

```bash
TARGETS="wasm" WASM_MODULE_PATH=/absolute/path/plugin.wasm templates/plugin/scripts/build-pack-plugin.sh
```

Notes:

- `wasm*` targets package `plugin.wasm` from `WASM_MODULE_PATH` (default: `templates/plugin/artifacts/wasm/plugin.wasm`).
- Dev sync scripts still use unzipped runtime artifacts; zip packages are for distribution.

## HTTP + JSON helper

`HttpJsonClient` wraps `HttpClient` with helpers:

- `GetJsonAsync` returns a `JsonDocument` you own and must dispose.
- `GetJsonAsync<T>` deserializes JSON into a model.
- JSON helper methods `GetObject`, `GetArray`, `GetString`, `PickMapString` reduce boilerplate.

## Next steps

- Replace the stub provider services with your real implementation.
- Add a typed API client for your data source and reuse `HttpJsonClient`.
- Update `PluginControlService` capabilities with real budgets and permissions.
