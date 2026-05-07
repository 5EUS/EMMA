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

## 6) Plugin SDK Helpers (EMMA.Plugin.Common)

The SDK provides a set of reusable, production-ready helpers in the `EMMA.Plugin.Common`
package. These helpers encapsulate patterns used consistently across all plugin transports
(ASP.NET, WASM, future protocols) and reduce boilerplate while ensuring best practices.

### Payload Resolution Contract

**`PluginPayloadResolvers`** implements a consistent precedence for operation payloads:

1. **Provided payload** (from host request)
   - Highest priority; contains request-specific data.
2. **Fetched or hint payload** (computed locally or from factory)
   - Used when no provided payload is available.
3. **Host bridge payload** (fallback to host provider)
   - Used when local resolution fails.
4. **Empty/null** (if all sources exhausted)
   - Safe default for optional payloads.

```csharp
// Simple fetch pattern (for payload computation)
var payload = ResolveProvidedOrFetched(
    provided: requestPayload,
    fetch: () => ComputePayloadLocally()
);

// Host bridge pattern (with deferred hint factory)
var payload = ResolveProvidedOrHostPayload(
    provided: requestPayload,
    operation: currentOperation,
    hintFactory: (op) => DeriveHintFromOperation(op),
    payloadProvider: (op) => HostBridge.GetPayload(op)
);
```

**See tests:** `PluginPayloadResolversTests.cs` documents the precedence contract with
comprehensive test cases.

### Search URL Resolution Pattern

**`PluginSearchUrlResolver`** centralizes search URL construction across transports:

```csharp
var searchUrl = PluginSearchUrlResolver.ResolveSearchAbsoluteUrl(
    parsedQuery: new PluginSearchQuery { SearchText = "manga" },
    queryResolver: (text) => MyProvider.ResolveSearchQuery(text),
    urlBuilder: (baseUrl, queryStr) => $"{baseUrl}/search?{queryStr}",
    payloadProvider: (op) => HostBridge.GetPayload(op),
    operation: currentOperation
);
```

This pattern keeps provider-specific search logic isolated and testable.

**See tests:** `PluginSearchUrlResolverTests.cs` verifies URL resolution contracts.

### Pagination Merge Pattern

**`PluginWasmPagingJsonHelpers`** provides utilities for handling large feeds:

- `MergeChapterFeedPages()`: Combines paginated results into a single response
- `SerializePageForCli()`: Formats single page for WASM output
- `SerializePagesForCli()`: Formats multiple pages for WASM output
- `MapChapterOperationItems()`: Normalizes raw chapter data to contract types

Used by both WASM and ASP.NET transports to handle media feeds that exceed
single-request limits.

### Operation Dispatch Pattern

**`PluginOperationDispatcher`** provides type-safe routing and error handling:

```csharp
var dispatcher = new PluginOperationDispatcher();

// Register operation handlers
dispatcher.Register<SearchRequest, SearchResponse>(
    operationId: "search",
    handler: (request, op) => HandleSearch(request, op)
);

// Dispatch with automatic error handling
var response = dispatcher.Dispatch<SearchResponse>(
    operationId: "search",
    operation: currentOp,
    request: searchRequest
);
```

Ensures consistent error formatting, logging, and payload resolution across all operations.

**See tests:** `PluginOperationDispatcherTests.cs` verifies dispatch contracts.

### Type-Safe WASM Invoke Pattern

**`PluginInvokeHelper`** provides generic, type-safe dispatch for WIT component exports:

```csharp
// Dispatch table (in Program.cs)
private static readonly IReadOnlyDictionary<string, Delegate> WasmDispatch 
    = new Dictionary<string, Delegate>(StringComparer.Ordinal)
{
    [PluginOperationNames.Search] = 
        (Func<string, string, SearchItem[]>)(
            (query, payload) => OperationHost.Search(query, payload)
        ),
    // ... other operations
};

// Type-safe invoke with automatic argument marshaling
public static SearchItem[] search(string query, string payload) 
    => PluginInvokeHelper.Invoke2<string, string, SearchItem[]>(
        WasmDispatch, 
        PluginOperationNames.Search, 
        query, 
        payload
    );
```

Eliminates manual type casting and reduces error-prone reflection code.

### When to Use These Helpers

- **Always use `PluginPayloadResolvers`** for operation payloads
- **Always use `PluginOperationDispatcher`** for operation routing
- **Always use `PluginSearchUrlResolver`** for search URL construction
- **Always use `PluginWasmPagingJsonHelpers`** for large feeds
- **Always use `PluginInvokeHelper`** for WASM WIT exports

These are production-ready, tested, and maintained by the EMMA team. Do not reimplement
these patterns; doing so increases maintenance burden and fragmentation across plugins.
