# EMMA Plugin Template

Minimal starter plugin with transport wiring in place and domain behavior intentionally stubbed.

## Run

```bash
dotnet run --project EMMA.PluginTemplate.csproj
```

Default port is 5000 (via template defaults). Override with:

```bash
dotnet run --project EMMA.PluginTemplate.csproj -- --port 6001
```

or

```bash
EMMA_PLUGIN_PORT=6001 dotnet run --project EMMA.PluginTemplate.csproj
```

## Build and pack

From repo root:

```bash
./scripts/build-pack-plugin.sh ./EMMA.PluginTemplate.plugin.json
```

WASM package variant:

```bash
TARGETS="wasm" ./scripts/build-pack-plugin.sh ./EMMA.PluginTemplate.plugin.json
```

ASP.NET plugin package variant (example Linux x64):

```bash
TARGETS="linux-x64" ./scripts/build-pack-plugin-aspnet.sh ./EMMA.PluginTemplate.plugin.json
```

## What You Must Customize

1. Implement domain behavior in `Infrastructure/CoreClient.cs`.
2. Fill in TODOs in `Infrastructure/PluginTemplateHooks.cs` (search, chapters, streams, segment).
3. Implement provider URL strategy in `Infrastructure/PluginTemplateHooks.cs` if network-backed.
4. Implement payload fetch logic in `Infrastructure/WasmClient.cs` when needed.
5. Update plugin metadata and permissions in `EMMA.PluginTemplate.plugin.json`.
6. Rename files/types/namespace after scaffolding to your plugin identity.

## Notes

This template intentionally returns minimal/default values until you add your own logic.

## Suggested First Test

1. Build and run plugin.
2. Verify handshake/capabilities operations respond.
3. Add one hardcoded search item in `CoreClient.Search`.
4. Add one stream in `CoreClient.GetStreams`.
5. Iterate from there with real provider integration.
