# Plugin Scaffold Workflow

Use `templates/plugin/scripts/scaffold-plugin.sh` to bootstrap a new plugin from the template seed rather than relying on stale static templates.

## Why this flow

- Uses real, currently working plugin structure as source.
- Applies deterministic renaming for assembly and plugin id.
- Excludes build output and packaging artifacts.
- Supports removing temporary SDK backports for environments already on the latest SDK package.

## Command

```bash
templates/plugin/scripts/scaffold-plugin.sh \
  --seed templates/plugin \
  --destination ../emma-my-plugin \
  --assembly-name EMMA.MyPlugin \
  --plugin-id emma.my.plugin
```

## Options

- `--seed <path>`: optional explicit source plugin folder (defaults to `templates/plugin`).
- `--without-backport`: delete `Compatibility/` in generated plugin.
- `--force`: allow existing destination if empty.

## Recommended seed choice

- The default template is based on `emma-video-test` and is fixture-backed.
- Use `--seed` only when you intentionally want a different baseline.

## Post-scaffold checklist

1. Build:
```bash
dotnet build EMMA.MyPlugin.csproj
```
2. Update provider URL strategy in `Infrastructure/ProviderRequestUrls.cs`.
3. Confirm manifest fields in `EMMA.MyPlugin.plugin.json`.
4. Validate packaging/signing scripts in `scripts/` before first release.

## Notes

The scaffold intentionally copies script and runtime wiring exactly from the seed, because that is the most reliable way to keep compatibility with current host expectations. You can later trim features once your plugin behavior is stable.

## Three-Layer Architecture Overview

Every plugin scaffold follows this three-layer structure to keep domain logic separate from transport concerns:

### Layer 1: Domain Layer (`Infrastructure/CoreClient.cs`)

Your provider API integration and business logic. This layer knows nothing about
how the plugin communicates with the host (gRPC, WASM, etc.).

**What lives here:**
- HTTP calls to your provider API
- Data parsing and normalization
- Caching and rate limiting logic
- Search, chapters, pages retrieval

**What you modify first:** Replace `CoreClient` calls with your provider's API endpoints.

**Test pattern:** Unit test `CoreClient` in isolation. Your tests don't need the host.

### Layer 2: Transport Adapter Layer

Bridges between domain logic and the host's IPC protocol. Kept separate by transport
to keep each one focused:

**ASP.NET transport** (`Program.cs` + `Services/AspNetClient.cs`):
- Configures gRPC server and dependency injection
- Delegates to domain logic via `AspNetClient`
- Handles HTTP request context and response streaming

**WASM transport** (`Program.cs` + `Infrastructure/WasmPluginOperationHost.cs` + `Infrastructure/WasmClient.cs`):
- Coordinates CLI entry points and operation dispatch
- Delegates to domain logic via `WasmClient`
- Maps domain types to WIT component exports (in `WasmTypedExports.cs`)

**What you modify:** Keep transport code minimal. If you need to change operation handling,
it usually belongs in the domain layer, not here.

**Test pattern:** Integration test each transport separately. Test that domain calls
produce the right protocol outputs.

### Layer 3: SDK Helper Layer (`EMMA.Plugin.Common`)

Reusable infrastructure provided by the EMMA team. These helpers handle cross-cutting concerns
like payload precedence, operation routing, pagination, and type mapping.

**Key helpers:**
- `PluginPayloadResolvers`: Decides which payload to use (provided > fetched > host)
- `PluginOperationDispatcher`: Routes operations and normalizes errors
- `PluginWasmPagingJsonHelpers`: Handles large feeds across multiple requests
- `PluginSearchUrlResolver`: Builds search URLs consistently
- `PluginInvokeHelper`: Type-safe dispatch for WASM exports

**What you use:** Import and use these helpers. Don't reimplement them.

**Test pattern:** The SDK helpers are already tested by the EMMA team. Focus your tests on
domain logic and transport integration.

## Getting Started: Quick Steps

1. **Pick your transport**
   - Start with one (ASP.NET or WASM), get it working, add the other later if needed.

2. **Implement domain logic**
   - Open `Infrastructure/CoreClient.cs`
   - Replace Mangadex API calls with your provider's endpoints
   - Test locally: `dotnet build` and `dotnet run`

3. **Update URLs**
   - Edit `Infrastructure/ProviderRequestUrls.cs` to point to your API
   - Edit `Infrastructure/ProviderSearchQueryResolver.cs` if your search API has different query syntax

4. **Update manifest**
   - Edit `EMMA.MyPlugin.plugin.json`
   - Set your plugin id, name, version, capabilities, and domain allowlist

5. **Test end-to-end**
   - Run locally and verify the host can handshake and call search/chapters/pages
   - Use the test client to validate behavior

6. **Sign and package**
   - Set up your signing key (see Signing section in plugin README)
   - Run `build-pack-plugin.sh` to create the plugin package
   - Upload to your plugin registry

See [plugin-system-design.md](plugin-system-design.md) for detailed documentation
of the SDK helpers and their contracts.
