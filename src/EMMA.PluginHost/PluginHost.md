# Plugin Host (EMMA)

Plugin Host runs as a separate process that exposes gRPC control endpoints for
plugins and performs a lightweight startup handshake against plugin endpoints.

## Responsibilities

- Load plugin manifests from `PluginHost:ManifestDirectory`.
- Perform a startup handshake (health + capabilities) when enabled.
- Track handshake status in memory for inspection.

## Endpoints

- gRPC: `PluginControl` (health + capabilities).
- HTTP: `GET /plugins` returns the current manifest/handshake snapshot.
- HTTP: `POST /plugins/start?pluginId=demo&reset=true` starts a managed plugin.
- HTTP: `POST /plugins/stop?pluginId=demo` stops a managed plugin.
- HTTP: `POST /plugins/reset?pluginId=demo` clears quarantine/runtime state.
- HTTP: `GET /plugins/logs?pluginId=demo&take=100` returns recent process logs.
- HTTP: `GET /plugins/status` returns a summarized lifecycle snapshot.
- HTTP: `GET /plugins/summary` returns combined handshake/runtime state for dashboards.
- HTTP: `GET /probe/search?query=demo&pluginId=demo` forwards a search to a plugin.
- HTTP: `GET /probe/pages?mediaId=demo-1&chapterId=ch-1&index=0&pluginId=demo` fetches chapters and a page.
- HTTP: `GET /probe/video?mediaId=demo-video-1&streamId=stream-1&sequence=0&pluginId=demo` fetches streams and a segment.
- HTTP: `GET /probe/pipeline?query=demo&pluginId=demo` runs search + first page.

## Configuration

The defaults live in `appsettings.json` under the `PluginHost` section:

- `ManifestDirectory`: directory containing `*.plugin.json` manifests.
- `HandshakeTimeoutSeconds`: timeout for handshake RPCs.
- `HandshakeOnStartup`: enable/disable startup handshake.
- `SandboxRootDirectory`: root directory for per-plugin sandbox folders.
- `SandboxEnabled`: enables sandbox enforcement when implemented.
- `BudgetWatchIntervalSeconds`: polling interval for budget warnings.
- `MaxCpuBudgetMs`: warns when a plugin budget exceeds this (0 disables).
- `MaxMemoryMb`: warns when a plugin budget exceeds this (0 disables).
- `StartupTimeoutSeconds`: time to wait for a plugin endpoint to start.
- `StartupProbeIntervalMs`: port probe interval during startup.
- `TimeoutBackoffSeconds`: backoff between timeout retries.
- `MaxTimeoutRetries`: retries before a plugin is quarantined.
- `ProbeTimeoutSeconds`: max time for probe gRPC calls.
- `MaxConcurrentCallsPerPlugin`: concurrent probe calls allowed per plugin.
- `PluginLogMaxLines`: number of log lines retained per plugin.

## Example Manifest

Save files with the `.plugin.json` suffix inside the manifest directory:

```json
{
	"id": "demo",
	"name": "Demo Plugin",
	"version": "1.0.0",
	"description": "Local demo plugin host stub",
	"author": "EMMA",
	"entry": {
		"protocol": "grpc",
		"endpoint": "http://localhost:5005",
		"startup": null
	},
	"capabilities": {
		"network": ["https"],
		"fileSystem": ["read"],
		"cache": true,
		"cpuBudgetMs": 200,
		"memoryMB": 256
	},
	"mediaTypes": ["paged"],
	"permissions": {
		"domains": ["example.com"],
		"paths": ["/plugin-data"]
	}
}
```
