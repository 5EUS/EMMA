# EMMA Template Plugin

A scaffold-ready EMMA plugin seed that mirrors the current split-transport repo shape:

- `EMMA.TemplatePlugin.Core.csproj`: transport-agnostic provider logic
- `EMMA.TemplatePlugin.csproj`: ASP.NET host transport
- `EMMA.TemplatePlugin.Wasm.csproj`: WASM transport

The shipped implementation is deterministic and fixture-backed so a freshly scaffolded
plugin can exercise search, chapters, and pages before you integrate a real provider.

## What To Customize First

1. Replace the fixture data in `Core/CoreClient.cs` with your provider API integration.
2. Update `EMMA.TemplatePlugin.plugin.json` with your plugin id, name, version, and permissions.
3. Review `plugin.dev.sample.json` and set the sync destinations for your local host environment.
4. Adjust the workflows and signing metadata for your repository.

## Run

```bash
dotnet run --project EMMA.TemplatePlugin.csproj
```

Override the port in development with:

```bash
dotnet run --project EMMA.TemplatePlugin.csproj -- --port 6001
```

Build the WASM transport with:

```bash
WASI_SDK_PATH=/path/to/wasi-sdk dotnet build EMMA.TemplatePlugin.Wasm.csproj
```

## Validate and Pack

From repo root:

```bash
./scripts/build-pack-plugin.sh ./EMMA.TemplatePlugin.plugin.json
```

Build the ASP.NET package variant for Linux x64:

```bash
TARGETS="linux-x64" ./scripts/build-pack-plugin-aspnet.sh ./EMMA.TemplatePlugin.plugin.json
```

## Notes

- The generated plugin defaults to paged media only.
- The template includes sample scenarios and CI workflows so `emma plugin-dev` flows work immediately after scaffolding.
- Replace every remaining fixture string before publishing a real provider.