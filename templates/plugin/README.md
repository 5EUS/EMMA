# EMMA Template Plugin

A scaffold-ready EMMA plugin seed that mirrors the current split-transport repo shape:

- `EMMA.TemplatePlugin.Core.csproj`: transport-agnostic provider logic
- `EMMA.TemplatePlugin.csproj`: ASP.NET host transport
- `EMMA.TemplatePlugin.Wasm.csproj`: WASM transport

The canonical `v0.7.0` author path is documented in
`EMMA/docs/architecture/plugin-sdk-v0.7.0-author-workflow.md` from the main
EMMA repository. This template is expected to follow that path exactly.

The shipped implementation is stub-backed so a freshly scaffolded plugin builds
immediately, returns no results, and exposes clear implementation surfaces before
you integrate a real provider.

The scaffold also includes dormant transport wiring for common source-specific
features such as deferred search metadata and search suggestions. Those hooks
default to no-op behavior so the generated plugin stays instantly compilable,
but you can turn them on by editing one file instead of reworking both
transports.

## What To Customize First

1. Replace the stub methods in `Core/CoreClient.cs` with your provider API integration.
2. Update `EMMA.TemplatePlugin.plugin.json` with your plugin id, name, version, and permissions.
3. Review `Core/SourceFeatures.cs` if your source needs search suggestions or deferred search metadata.
4. Review `plugin.dev.sample.json` and `plugin.dev.json`, then set the sync destinations for your local host environment.
5. Adjust the workflows and signing metadata for your repository.

`plugin.dev.json` is the auto-discovered local config. `plugin.dev.sample.json`
is the committed baseline that matches the documented `v0.7.0` workflow.

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

Build the full split-transport solution with:

```bash
dotnet build EMMA.TemplatePlugin.sln
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

Validate package mode explicitly once the `0.7.0` SDK packages are staged on a
feed visible to the repository:

```bash
UseLocalEmmaSdk=false EMMA_SDK_VERSION=0.7.0 \
dotnet run --project EMMA.TemplatePlugin.csproj

UseLocalEmmaSdk=false EMMA_SDK_VERSION=0.7.0 \
./scripts/build-pack-plugin.sh ./EMMA.TemplatePlugin.plugin.json
```

## Notes

- The generated plugin defaults to paged media only.
- The template includes sample scenarios and CI workflows that treat empty search results as the expected pre-integration baseline.
- Search, chapter, and page hooks are stubbed intentionally in `Core/CoreClient.cs` so the generated project compiles cleanly before provider-specific code exists.
- Optional search metadata and search suggestion hooks live in `Core/SourceFeatures.cs`; by default they return the incoming items or no suggestions, which keeps the scaffold safe to run before provider integration.
- Both ASP.NET and WASM transports are already wired to those optional hooks, so adding source-specific search behavior does not require transport-specific boilerplate first.