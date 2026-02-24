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

- `entry.executable` with an absolute path to `dotnet` (or your self-contained executable).
- `entry.arguments` and `entry.workingDirectory` with absolute paths.
- `entry.endpoint` and `EMMA_PLUGIN_PORT` if you change ports.
- `signature.value` if the host requires signed manifests.

## HTTP + JSON helper

`HttpJsonClient` wraps `HttpClient` with helpers:

- `GetJsonAsync` returns a `JsonDocument` you own and must dispose.
- `GetJsonAsync<T>` deserializes JSON into a model.
- JSON helper methods `GetObject`, `GetArray`, `GetString`, `PickMapString` reduce boilerplate.

## Next steps

- Replace the stub provider services with your real implementation.
- Add a typed API client for your data source and reuse `HttpJsonClient`.
- Update `PluginControlService` capabilities with real budgets and permissions.
