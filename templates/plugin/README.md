# EMMA Plugin Template

This template now follows the same runtime shape as the current Mangadex test plugin:
- AspNet runtime uses a single `AspNetClient` that implements SDK runtime interfaces.
- Default provider services are registered through `AddDefaultPagedProviders<T>()` and `AddDefaultVideoProvider<T>()`.
- WASM transport uses a componentized host (`WasmPluginOperationHost`) plus typed WIT exports.
- Request URL construction and payload mapping are centralized under `Infrastructure/`.

## Run

```bash
dotnet run --project templates/plugin/EMMA.PluginTemplate.csproj
```

Default port is `5000`.

In dev mode, you can override with:

```bash
EMMA_PLUGIN_TEMPLATE_PORT=6001 dotnet run --project templates/plugin/EMMA.PluginTemplate.csproj
```

or:

```bash
dotnet run --project templates/plugin/EMMA.PluginTemplate.csproj -- --port 6001
```

## Build

AspNet build:

```bash
dotnet build templates/plugin/EMMA.PluginTemplate.csproj -p:PluginTransport=AspNet
```

WASM component build:

```bash
dotnet build templates/plugin/EMMA.PluginTemplate.csproj -p:PluginTransport=Wasm
```

## Package

From `templates/plugin/scripts`:

Build and package WASM:

```bash
./build-pack-plugin.sh ../EMMA.PluginTemplate.plugin.json
```

Build and package AspNet for Linux x64:

```bash
TARGETS="linux-x64" ./build-pack-plugin-aspnet.sh ../EMMA.PluginTemplate.plugin.json
```

## File map

- `Program.cs`: transport entrypoints and host wiring.
- `Services/AspNetClient.cs`: AspNet runtime implementation.
- `Infrastructure/ProviderRequestUrls.cs`: provider URL and HTTP profile defaults.
- `Infrastructure/CoreClient.cs` + `Infrastructure/PayloadMapper.cs`: payload parsing/mapping.
- `Infrastructure/WasmPluginOperationHost.cs`: WASM operation dispatch and CLI integration.
- `Infrastructure/WasmTypedExports.cs`: WIT export bridge.
- `wit/library.wit`: component interface contract.

## Customize this template

1. Rename namespace/project/manifest IDs from `EMMA.PluginTemplate`.
2. Replace Mangadex URL builders in `Infrastructure/ProviderRequestUrls.cs`.
3. Replace parsing logic in `Infrastructure/PayloadMapper.cs` to match your provider payload schema.
4. Update permissions, budgets, and metadata in `EMMA.PluginTemplate.plugin.json`.
5. Keep transport plumbing as-is unless your operation contract changes.
