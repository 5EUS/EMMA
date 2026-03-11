# EMMA Plugin Template (Phase 3 SDK)

This template demonstrates the minimal secure startup shape using `PluginBuilder`.

## Run

```bash
dotnet run --project templates/plugin/EMMA.PluginTemplate.csproj
```

Build (AspNet transport):

```bash
dotnet build templates/plugin/EMMA.PluginTemplate.csproj
```

Build (WASM transport):

```bash
dotnet build templates/plugin/EMMA.PluginTemplate.csproj -p:PluginTransport=Wasm
```

## Why this template is minimal
- Startup uses `PluginBuilder` with default security interceptor.
- Control service is provided by `UseDefaultControlService(...)`.
- Provider handlers contain plugin logic only.
- Policy enforcement remains host-side and manifest-authoritative.

## Next steps
1. Rename namespace and project from `EMMA.PluginTemplate`.
2. Implement logic in `Services/*ProviderService.cs`.
3. Update `EMMA.PluginTemplate.plugin.json` metadata, budgets, and permissions.
4. Pack with:

```bash
./scripts/plugin-pack.sh ./templates/plugin
```
