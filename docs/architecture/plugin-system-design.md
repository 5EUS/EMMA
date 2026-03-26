# Plugin System Design

## 1) Plugin Contract Interfaces

These interfaces are defined in Aggregator.Plugin.Abstractions and shared via
Aggregator.Contracts for IPC compatibility. The IPC layer mirrors these
contracts in gRPC messages.

### IMediaSourcePlugin
- Identity, version, and manifest metadata.
- Provides capabilities and supported media types.
- Exposes provider discovery and health checks.

### ISearchProvider
- Search by text and optional filters.
- Returns normalized media summaries with stable IDs.

### IPageProvider
- Provides paged media content (chapters, pages, images).
- Supports progressive page retrieval and lazy loading.

### IVideoProvider
- Provides stream metadata, playlist variants, and segments.
- Supports adaptive streaming and range requests.

## 2) Plugin Manifest Structure (JSON schema outline)

```json
{
  "id": "string",
  "name": "string",
  "version": "semver",
  "description": "string",
  "author": "string",
  "protocol": "grpc",
  "endpoint": "string",
  "capabilities": {
    "network": ["https"],
    "fileSystem": ["read"],
    "cache": true,
    "cpuBudgetMs": 200,
    "memoryMB": 256
  },
  "mediaTypes": ["paged", "video"],
  "permissions": {
    "domains": ["example.com"],
    "paths": ["data"]
  },
  "signing": {
    "publisher": "string",
    "signature": "base64"
  }
}
```

Notes:
- `protocol` declares the transport; entrypoints are auto-resolved from the bundle/binary name.
- `permissions.paths` are sandbox-relative and normalized under the sandbox root.
- `capabilities` are enforced by the host; they do not imply trust.
- `signing` is required for installation and execution.

## 3) Capability Model

- Network: allowlist by domain and protocol; deny by default.
- File: per-plugin sandbox directory with read/write constraints.
- Cache: bounded size and TTL limits.
- CPU/memory budgets: enforced by host watchdog.
- Concurrency: max in-flight calls per plugin instance.
- Media types: explicitly declared to limit surface area.

## 4) Plugin Lifecycle Model

1) Install
   - Verify signature and manifest.
   - Create sandbox directories.

2) Initialize
   - Spawn isolated process with constrained permissions.
   - Handshake via gRPC and validate plugin version.

3) Active
   - Serve search and media requests.
   - Enforce timeouts, rate limits, and circuit breakers.

4) Quarantine
   - Triggered by repeated crashes or policy violations.
   - Disable plugin and notify registry.

5) Uninstall
   - Remove from registry and cleanup sandbox storage.

## 5) IPC Contract Design (gRPC schema example)

```proto
syntax = "proto3";
package aggregator.plugins;

service PluginControl {
  rpc GetHealth(HealthRequest) returns (HealthResponse);
  rpc GetCapabilities(CapabilitiesRequest) returns (CapabilitiesResponse);
}

service SearchProvider {
  rpc Search(SearchRequest) returns (SearchResponse);
}

service PageProvider {
  rpc GetChapters(ChaptersRequest) returns (ChaptersResponse);
  rpc GetPage(PageRequest) returns (PageResponse);
}

service VideoProvider {
  rpc GetStreams(StreamRequest) returns (StreamResponse);
  rpc GetSegment(SegmentRequest) returns (SegmentResponse);
}
```

Security and resilience notes:
- All calls include a request context with correlation ID and deadline.
- The host enforces deadlines even if plugins ignore them.
- Responses are normalized and validated by the host before entering core
  runtime.
- Circuit breaker per plugin prevents cascade failures.
