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
- HTTP: `GET /probe/search?query=demo&pluginId=demo` forwards a search to a plugin.

## Configuration

The defaults live in `appsettings.json` under the `PluginHost` section:

- `ManifestDirectory`: directory containing `*.plugin.json` manifests.
- `HandshakeTimeoutSeconds`: timeout for handshake RPCs.
- `HandshakeOnStartup`: enable/disable startup handshake.
- `SandboxRootDirectory`: root directory for per-plugin sandbox folders.
- `SandboxEnabled`: enables sandbox enforcement when implemented.

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
