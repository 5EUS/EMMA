# Binary Transport and Catalog V2 Roadmap

This document defines the concrete implementation plan for two linked changes:

1. Replace JSON/base64 binary payload transport in EMMA's native, WASM, and
   plugin-dev bridges with first-class binary asset transport.
2. Evolve the current SQLite catalog from paged/video-specific tables into a
   generic media graph that can represent paged, video, audio, and future
   text-like media without repeated schema redesign.

The target audience is EMMA runtime, API, storage, CLI, and plugin SDK work.

## Why this work exists

The current stack already uses efficient binary payload transport in some
places, but not end-to-end.

Current strengths:

- Public API page assets already use `bytes payload` in
  `EMMA.Contracts/Protos/api_paged.proto`.
- Plugin gRPC video segments already use `bytes payload` in
  `EMMA.Contracts/Protos/video_provider.proto`.
- Page asset cache already stores raw bytes on disk in
  `EMMA.Storage/PageAssetStorageCache.cs`.

Current problems:

- `EMMA.Native` still returns page assets and video segments through JSON.
- `EMMA.PluginHost.Library` still serializes page assets and video segments to
  JSON for the native bridge.
- WASM video segment transport still base64-encodes bytes into JSON.
- Plugin-dev host endpoints still expose some binary results as base64 JSON.
- The SQLite catalog is centered on `media`, `media_chapters`, `media_pages`,
  and `media_streams`, which is workable for today's paged/video mix but does
  not scale cleanly to audio, text, alternate renditions, or multi-level media
  hierarchies.

The result is duplicated transport logic, unnecessary byte inflation, extra
copying, and a catalog model that will become harder to evolve as EMMA adds
media types.

## Design goals

### Performance goals

- Eliminate base64 for binary payloads on hot paths.
- Eliminate JSON serialization for page assets and video segments on the native
  path.
- Keep payload metadata cheap and structured.
- Preserve or improve current caching behavior.
- Make performance observable at every transport boundary.

### Genericity goals

- Represent all media as a single typed graph instead of hard-coded paged/video
  child tables.
- Keep the catalog focused on identity, structure, and locators.
- Keep raw asset bytes in cache or artifact stores, not in the relational
  catalog.
- Support future node kinds such as episodes, tracks, paragraphs, books,
  segments, galleries, transcripts, subtitles, posters, and alternate
  renditions without schema churn.

### Developer experience goals

- Preserve one coherent dev workflow across host-bridge, native direct, and
  WASM direct profiles.
- Keep the CLI as the normalized surface for plugin-dev inspection and smoke
  workflows.
- Make transport capabilities visible in `session`, `doctor`, and `serve`.
- Avoid separate “special” flows for video and page assets when a shared asset
  model can handle both.

## Non-goals

- This work does not try to remove all JSON from EMMA.
- This work does not put raw binary assets into SQLite.
- This work does not force public API consumers off protobuf.
- This work does not preserve legacy binary JSON compatibility just for its own
  sake.

## Current-state inventory

### Binary already handled well

- Public API paged asset payloads:
  `EMMA.Contracts/Protos/api_paged.proto`
- Plugin gRPC video segment payloads:
  `EMMA.Contracts/Protos/video_provider.proto`
- File-backed page asset cache:
  `EMMA.Storage/PageAssetStorageCache.cs`

### Binary still handled poorly

- Native page asset JSON entry point:
  `EMMA.Native/NativeExports.Pipeline.cs`
- Native page asset JSON serialization:
  `EMMA.Native/NativeExports.Serialization.cs`
- Plugin-host page asset JSON path:
  `EMMA.PluginHost.Library/PluginHostExports.cs`
- Native video segment JSON entry point:
  `EMMA.Native/NativeExports.Pipeline.cs`
- WASM video segment base64 wire format:
  `EMMA.PluginHost/Services/WasmResponseJsonContext.cs`
- WASM video segment decode path:
  `EMMA.PluginHost/Services/WasmPluginRuntimeHost.cs`
- Plugin-dev video segment base64 endpoint:
  `EMMA.Plugin.AspNetCore/PluginSdkHost.cs`
- CLI video segment model still base64-centric:
  `EMMA.Cli/PluginDevRuntimeAdapter.cs`

### Catalog v1 constraints

The current catalog interface in `EMMA.Application/Ports/IMediaCatalogPort.cs`
is paged-media-centric.

It supports:

- media upsert and lookup
- chapter upsert and lookup
- page upsert and lookup

The current schema in `EMMA.Storage/Sql/001_initial.sql` includes:

- `media`
- `media_chapters`
- `media_pages`
- `media_streams`

This makes the next media type awkward because each new content shape pushes
the schema toward more specialized tables.

## Architecture principles

1. Use structured transport for metadata.
2. Use binary transport for binary payloads.
3. Separate catalog metadata from asset storage.
4. Model content structure generically as nodes and locators.
5. Prefer a clean cutover when it reduces complexity and the affected ecosystem
  is small enough to absorb it.
6. Keep CLI and plugin-dev flows transport-aware, not transport-specific.

## Proposed transport model

The new model has two lanes.

### Lane 1: metadata lane

Use protobuf or compact JSON for:

- search results
- media summaries
- chapters
- pages
- streams
- tracks
- progress
- diagnostics
- capability discovery

These objects are relatively small and strongly typed.

### Lane 2: asset lane

Use binary transport for:

- page assets
- video segments
- thumbnails
- posters
- subtitle files
- audio chunks
- transcript blobs
- future downloaded/derived artifacts

These payloads should never be base64-encoded for native, WASM, or plugin-dev
runtime transport.

## Shared asset model

Introduce a single asset abstraction across application, host, native, API,
and CLI layers.

### Domain types

```csharp
public enum MediaAssetKind
{
    Page,
    VideoSegment,
    Thumbnail,
    Poster,
    Subtitle,
    AudioChunk,
    Transcript,
    DerivedArtifact
}

public sealed record MediaAssetLocator(
    MediaAssetKind Kind,
    string MediaId,
    string? ContainerId = null,
    string? VariantId = null,
    int? Index = null,
    int? Sequence = null,
    string? Role = null);

public sealed record MediaBinaryAsset(
    MediaAssetLocator Locator,
    string ContentType,
    byte[] Payload,
    DateTimeOffset FetchedAtUtc,
    string? ETag = null,
    string? IntegrityHash = null);
```

### Application port

```csharp
public interface IMediaAssetPort
{
    Task<MediaBinaryAsset?> GetAssetAsync(
        MediaAssetLocator locator,
        CancellationToken cancellationToken);
}
```

This allows page assets and video segments to converge on a shared fetch path.

## Native FFI v2 design

The native bridge is the highest-priority transport refactor because it still
forces binary payloads through JSON.

### Problems in the current native ABI

- Asset bytes are serialized to JSON strings.
- Base64 expands payload size.
- Dart decodes JSON and base64 on the UI side.
- There is no shared asset concept; page asset and video segment are separate,
  shape-specific functions.

### FFI v2 requirements

- Zero JSON for binary payloads.
- Explicit content-type and payload length.
- Safe lifetime management from Dart.
- Extensible enough for non-page/video asset kinds.
- Straightforward enough to replace existing JSON functions without keeping a
  long-lived parallel ABI.

### Proposed C ABI

```c
typedef struct EmmaBinarySlice {
    const uint8_t* data;
    int32_t length;
} EmmaBinarySlice;

typedef struct EmmaUtf8Slice {
    const char* data;
    int32_t length;
} EmmaUtf8Slice;

typedef struct EmmaRuntimeBinaryAsset {
    EmmaUtf8Slice content_type;
    EmmaBinarySlice payload;
    EmmaUtf8Slice fetched_at_utc;
    EmmaUtf8Slice etag;
    EmmaUtf8Slice integrity_hash;
    int32_t status_code;
} EmmaRuntimeBinaryAsset;

typedef struct EmmaRuntimeAssetRequest {
    int32_t asset_kind;
    EmmaUtf8Slice media_id;
    EmmaUtf8Slice container_id;
    EmmaUtf8Slice variant_id;
    int32_t index;
    int32_t sequence;
    EmmaUtf8Slice role;
} EmmaRuntimeAssetRequest;

EMMA_EXPORT int32_t emma_runtime_get_asset(
    int32_t handle,
    const EmmaRuntimeAssetRequest* request,
    EmmaRuntimeBinaryAsset* result);

EMMA_EXPORT void emma_runtime_free_asset(EmmaRuntimeBinaryAsset* result);
```

### Why this shape

- It is generic enough for all binary asset kinds.
- It keeps allocation ownership on the native side until explicit free.
- It avoids forcing page/video-specific native entry points forever.
- It supports future metadata without growing a JSON blob.

### Cutover policy

The plugin ecosystem is currently small enough that EMMA should prefer a clean
ABI replacement instead of carrying parallel binary and JSON asset entry points.

Replace these legacy functions outright:

- `emma_runtime_get_page_asset_json`
- `emma_runtime_get_video_segment_json`

The target state is one binary asset ABI, one Dart asset binding path, and no
runtime support burden for asset JSON transport.

### Dart binding model

`emmaui/lib/emma_runtime.dart` should gain:

- a struct binding for `EmmaRuntimeAssetRequest`
- a struct binding for `EmmaRuntimeBinaryAsset`
- a typed `getAsset(...)`
- `getPageAsset(...)` and `getVideoSegment(...)` as thin adapters over the new
  generic asset lane

Pseudo-shape:

```dart
EmmaBinaryAsset getAsset(EmmaAssetRequest request) {
  final nativeRequest = calloc<EmmaRuntimeAssetRequest>();
  final nativeResult = calloc<EmmaRuntimeBinaryAsset>();
  try {
    final ok = _getAsset(_handle, nativeRequest, nativeResult);
    if (ok == 0) {
      throw _readLastError();
    }
    return EmmaBinaryAsset.fromNative(nativeResult.ref);
  } finally {
    _freeAsset(nativeResult);
    calloc.free(nativeRequest);
  }
}
```

The key rule is that the Dart layer copies bytes once into `Uint8List` and does
not decode JSON or base64 for asset payloads.

## API and plugin-dev v2 design

The public API already supports bytes for page assets, so API v2 should focus
on unifying and generalizing asset endpoints rather than reinventing them.

### Public API v2

Add a generic asset API and simplify the surface around it. Current
page/video-specific methods can remain only where they materially help API
clarity, not as a compatibility obligation.

#### gRPC surface

```proto
service MediaAssetApi {
  rpc GetAsset (AssetRequest) returns (AssetResponse);
}

message AssetRequest {
  string media_id = 1;
  string container_id = 2;
  string variant_id = 3;
  AssetKind kind = 4;
  int32 index = 5;
  int32 sequence = 6;
  string role = 7;
  ApiRequestContext context = 100;
}

message AssetResponse {
  oneof outcome {
    ApiBinaryAsset asset = 1;
    ApiError error = 100;
  }
}

message ApiBinaryAsset {
  string content_type = 1;
  bytes payload = 2;
  string etag = 3;
  string integrity_hash = 4;
}
```

#### HTTP surface

For larger or streaming-oriented assets, prefer raw HTTP responses instead of
JSON wrappers.

Examples:

- `GET /api/v2/assets/page/{mediaId}/{chapterId}/{index}`
- `GET /api/v2/assets/video-segment/{mediaId}/{streamId}/{sequence}`
- `GET /api/v2/assets/{kind}` with query parameters for generic lookup

Response behavior:

- body is raw bytes
- `Content-Type` is the asset content type
- `ETag` and cache headers where available
- `X-EMMA-Asset-Kind`, `X-EMMA-Integrity-Hash` as optional metadata headers

This is a better fit than base64 JSON for browser and CLI tooling.

### Plugin-dev API v2

The plugin-dev host endpoints must stop returning base64 JSON for binary data.

Current issue:

- `/dev/video/segment` returns base64 JSON in `PluginSdkHost.cs`.

Required change:

- `/dev/video/segment` returns raw bytes.
- Add `/dev/page/asset` returning raw bytes.
- Add a shared `/dev/assets` endpoint if the generic asset abstraction is ready
  at the same time.

Headers for plugin-dev responses:

- `Content-Type`
- `X-EMMA-Asset-Kind`
- `X-EMMA-Fetched-At`
- `X-EMMA-Asset-Size`

This keeps host-bridge flows and native direct flows behaviorally aligned.

## WASM transport v2 design

WASM currently has two different transport patterns:

- metadata results are JSON payloads
- video segment bytes are base64 inside JSON payloads

The second pattern must be removed.

### Desired WASM operation result model

Introduce a binary-capable operation result.

```csharp
public sealed record OperationResult(
    bool IsError,
    string? ErrorCode,
    string ContentType,
    string? PayloadJson,
    byte[]? PayloadBytes);
```

Rules:

- metadata operations use `PayloadJson`
- binary asset operations use `PayloadBytes`
- an operation must not use both payload lanes for the primary body

### SDK changes needed

Affected surfaces include:

- `EMMA.Plugin.Common/PluginWasmInvokeScaffold.cs`
- `EMMA.Plugin.Common/PluginWasmVideoOperationScaffold.cs`
- `EMMA.Plugin.Common/PluginModels.cs`
- `EMMA.PluginHost/Services/WasmResponseJsonContext.cs`
- `EMMA.PluginHost/Services/WasmPluginRuntimeHost.cs`

The WASM host scaffolding should expose:

- `BuildJsonResult(...)`
- `BuildBinaryResult(...)`

The `VideoSegmentOperationItem` should stop carrying base64 text and instead
produce raw bytes through the binary result lane.

### Cutover approach

There is no strong reason to preserve legacy base64 WASM segment results for a
two-plugin development ecosystem.

The preferred implementation is:

- convert the WASM scaffolding to binary results directly
- update the sample plugins and templates in the same change set
- remove the base64 path instead of keeping a compatibility lane

## SQL catalog v2 design

The storage redesign should make EMMA generic without turning SQLite into a raw
blob store.

### Core idea

Represent media as:

- items
- nodes
- locators
- relations
- variants
- progress cursors

Keep raw bytes in:

- page/video asset cache
- download artifact store
- proxy/cache directories

### V2 schema

```sql
CREATE TABLE media_items (
    id TEXT PRIMARY KEY,
    source_id TEXT NOT NULL,
    external_id TEXT NOT NULL,
    media_type TEXT NOT NULL,
    title TEXT NOT NULL,
    sort_title TEXT,
    rating TEXT,
    synopsis TEXT,
    language TEXT,
    attributes_json TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE TABLE media_item_tags (
    media_id TEXT NOT NULL,
    tag TEXT NOT NULL,
    PRIMARY KEY (media_id, tag),
    FOREIGN KEY (media_id) REFERENCES media_items(id) ON DELETE CASCADE
);

CREATE TABLE media_nodes (
    id TEXT PRIMARY KEY,
    media_id TEXT NOT NULL,
    parent_node_id TEXT,
    node_kind TEXT NOT NULL,
    external_id TEXT,
    title TEXT,
    ordinal REAL,
    sequence_key TEXT,
    language TEXT,
    availability_state TEXT,
    metadata_json TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    FOREIGN KEY (media_id) REFERENCES media_items(id) ON DELETE CASCADE,
    FOREIGN KEY (parent_node_id) REFERENCES media_nodes(id) ON DELETE CASCADE
);

CREATE TABLE media_locators (
    id TEXT PRIMARY KEY,
    media_id TEXT NOT NULL,
    node_id TEXT,
    locator_kind TEXT NOT NULL,
    role TEXT,
    uri TEXT NOT NULL,
    content_type_hint TEXT,
    headers_json TEXT,
    cookies TEXT,
    byte_range_start INTEGER,
    byte_range_end INTEGER,
    sequence INTEGER,
    variant_id TEXT,
    metadata_json TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    FOREIGN KEY (media_id) REFERENCES media_items(id) ON DELETE CASCADE,
    FOREIGN KEY (node_id) REFERENCES media_nodes(id) ON DELETE CASCADE
);

CREATE TABLE media_variants (
    id TEXT PRIMARY KEY,
    media_id TEXT NOT NULL,
    node_id TEXT,
    variant_kind TEXT NOT NULL,
    label TEXT,
    quality_label TEXT,
    codec TEXT,
    bitrate INTEGER,
    language TEXT,
    drm_scheme TEXT,
    is_default INTEGER NOT NULL DEFAULT 0,
    metadata_json TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    FOREIGN KEY (media_id) REFERENCES media_items(id) ON DELETE CASCADE,
    FOREIGN KEY (node_id) REFERENCES media_nodes(id) ON DELETE CASCADE
);

CREATE TABLE media_relations (
    id TEXT PRIMARY KEY,
    media_id TEXT NOT NULL,
    from_node_id TEXT,
    to_node_id TEXT,
    relation_kind TEXT NOT NULL,
    metadata_json TEXT,
    created_at TEXT NOT NULL,
    FOREIGN KEY (media_id) REFERENCES media_items(id) ON DELETE CASCADE,
    FOREIGN KEY (from_node_id) REFERENCES media_nodes(id) ON DELETE CASCADE,
    FOREIGN KEY (to_node_id) REFERENCES media_nodes(id) ON DELETE CASCADE
);

CREATE TABLE media_progress_cursors (
    id TEXT PRIMARY KEY,
    media_id TEXT NOT NULL,
    node_id TEXT,
    locator_id TEXT,
    plugin_id TEXT NOT NULL,
    user_id TEXT NOT NULL,
    cursor_kind TEXT NOT NULL,
    numeric_value REAL,
    text_value TEXT,
    completed INTEGER NOT NULL,
    updated_at TEXT NOT NULL,
    FOREIGN KEY (media_id) REFERENCES media_items(id) ON DELETE CASCADE,
    FOREIGN KEY (node_id) REFERENCES media_nodes(id) ON DELETE SET NULL,
    FOREIGN KEY (locator_id) REFERENCES media_locators(id) ON DELETE SET NULL
);

CREATE TABLE media_asset_cache_index (
    id TEXT PRIMARY KEY,
    media_id TEXT NOT NULL,
    node_id TEXT,
    locator_id TEXT,
    asset_kind TEXT NOT NULL,
    cache_key TEXT NOT NULL,
    content_type TEXT,
    size_bytes INTEGER,
    integrity_hash TEXT,
    fetched_at TEXT NOT NULL,
    expires_at TEXT,
    metadata_json TEXT,
    FOREIGN KEY (media_id) REFERENCES media_items(id) ON DELETE CASCADE,
    FOREIGN KEY (node_id) REFERENCES media_nodes(id) ON DELETE SET NULL,
    FOREIGN KEY (locator_id) REFERENCES media_locators(id) ON DELETE SET NULL
);
```

### How current media types map into v2

Paged media:

- media item = series/work
- chapter = node kind `chapter`
- page = node kind `page`
- image URI = locator kind `content`

Video media:

- media item = movie/episode/work
- stream = node kind `stream`
- segment = node kind `segment` or locator sequence row
- playlist URI = locator kind `playlist`
- poster/thumb = locator kind `poster` or `thumbnail`
- subtitles/audio tracks = variants or child nodes depending on use case

Audio media:

- media item = album/podcast/work
- episode/track = node kind `episode` or `track`
- audio stream/chunk = locator or segment nodes

Text-like media:

- media item = novel/article/book/work
- chapter = node kind `chapter`
- page/section/paragraph = nodes
- body HTML/text/archive = locators or derived artifacts

### Why this model is better

- New media shapes do not require new top-level tables.
- Alternate locators and renditions become first-class.
- Progress can point at nodes or locators generically.
- Catalog remains relational and queryable.
- Raw assets remain outside SQLite.

## Catalog port v2

Do not mutate the current paged-centric port into an everything-bag. Add a new
catalog contract and cut consumers over decisively.

### Proposed interfaces

```csharp
public interface IMediaCatalogPortV2
{
    Task UpsertItemAsync(MediaItemRecord item, CancellationToken cancellationToken);
    Task UpsertNodesAsync(string mediaId, IReadOnlyList<MediaNodeRecord> nodes, CancellationToken cancellationToken);
    Task UpsertLocatorsAsync(string mediaId, IReadOnlyList<MediaLocatorRecord> locators, CancellationToken cancellationToken);
    Task UpsertVariantsAsync(string mediaId, IReadOnlyList<MediaVariantRecord> variants, CancellationToken cancellationToken);

    Task<MediaItemRecord?> GetItemAsync(string mediaId, CancellationToken cancellationToken);
    Task<IReadOnlyList<MediaNodeRecord>> GetNodesAsync(string mediaId, CancellationToken cancellationToken);
    Task<IReadOnlyList<MediaLocatorRecord>> GetLocatorsAsync(string mediaId, CancellationToken cancellationToken);
}
```

### Cutover layer

`IMediaCatalogPortV2` should become the working catalog surface for the runtime
once the storage rewrite starts. Keep `IMediaCatalogPort` only long enough to
avoid a half-converted tree in a single branch, then remove it.

The target sequence is:

- land storage and port v2
- update runtime and pipeline consumers in the same implementation phase
- delete v1-specific read and write paths instead of preserving a long-lived
  facade

## Developer experience review

The transport change is not complete unless the plugin-dev experience is kept in
sync.

### CLI issues that must be fixed

1. `PluginDevVideoSegment` is base64-centric today.
2. Host-bridge video inspection still relies on base64 JSON dev endpoints.
3. Page asset support is inconsistent across adapters.
4. CLI output is oriented around text/JSON inspection, not binary asset
   inspection.
5. `doctor` does not explicitly report transport capability versions.

### CLI design changes required

#### Runtime adapter model

`EMMA.Cli/PluginDevRuntimeAdapter.cs` should move from shape-specific methods to
an asset-aware runtime capability surface.

Example:

```csharp
public interface IPluginDevRuntimeAdapter
{
    Task<PluginDevBinaryAsset?> GetAssetAsync(
        PluginDevAssetRequest request,
        CancellationToken cancellationToken);
}
```

Keep `GetVideoSegmentAsync(...)` and `GetPageAssetAsync(...)` as temporary
convenience wrappers if needed.

#### CLI command surface

Add or normalize:

- `asset page <mediaId> <chapterId> <index> [--out file]`
- `asset video-segment <mediaId> <streamId> <sequence> [--out file]`
- `asset get --kind <kind> ... [--out file]`

Default behavior:

- print content type, size, hash, and source metadata
- do not print raw bytes or base64 to the terminal by default
- support `--out` for writing to disk when inspection is needed

#### Session and doctor output

`session` and `doctor` should display:

- transport version (`v1-json`, `v2-binary`, mixed)
- supported asset kinds
- whether page asset/video segment inspection is available
- whether the runtime adapter is using gRPC, dev HTTP, or native FFI asset
  transport

#### Serve/UI impact

The local session API and UI should expose binary asset inspection without base64.

Browser behavior:

- fetch raw bytes
- show metadata separately
- allow download/open-in-tab for supported content types

### Sample plugins and templates

Required updates:

- `EMMA/templates/plugin`
- `emma-test-plugin`

The templates should model:

- metadata operations as JSON/protobuf
- binary asset operations as raw bytes

WASM scaffolds must stop teaching plugin authors to emit base64 video segment
payloads.

## Adjacent cleanup required

This program touches more than one implementation layer, so cleanup work should
be planned explicitly.

### Runtime and host cleanup

- Replace page/video-specific asset response shapes with one shared asset model.
- Remove duplicated JSON serializers for binary results as part of the cutover.
- Normalize timing logs so all asset fetches log bytes, transport, and elapsed
  time.

### API cleanup

- Keep current page/video-specific endpoints only where they remain the clearest
  public surface.
- Route them internally through the same asset service used by generic asset
  endpoints.

### Storage cleanup

- Keep page/video caches outside SQLite.
- Add a cache index only if EMMA needs queryable visibility into cached asset
  state.
- Remove widening pressure from `media_pages` and `media_streams` by moving new
  work to v2 tables.

### Documentation cleanup

- Update architecture docs so v1 schema docs are clearly framed as current-state
  storage, not the long-term model.
- Update CLI docs once binary dev endpoints and asset commands exist.

## Implementation roadmap

### Phase 0 - Baseline and instrumentation

Scope:

- Add timing and byte-count logs to every asset boundary.
- Log native serialization cost separately from fetch cost.
- Log plugin-dev binary endpoint sizes and response timings.

Deliverables:

- measurable before/after benchmark data
- agreed hot-path budget for page asset and video segment fetches

Validation:

- regression benchmark for page asset fetch
- regression benchmark for video segment fetch

### Phase 1 - Domain and contract preparation

Scope:

- Introduce `MediaAssetKind`, `MediaAssetLocator`, and `MediaBinaryAsset`.
- Add `IMediaAssetPort`.
- Add generic API contract for asset fetches.
- Add CLI-side `PluginDevAssetRequest` and `PluginDevBinaryAsset` models.

Validation:

- unit tests for locator parsing and mapping
- tests ensuring page/video flows map cleanly onto the shared asset model

### Phase 2 - Native binary asset FFI

Scope:

- Add binary FFI structs and free functions.
- Implement native page asset and video segment fetch through the new ABI.
- Update `emmaui/lib/emma_runtime.dart` to consume the binary ABI.
- Remove the legacy JSON asset entry points from native and Dart bindings.

Validation:

- native integration tests for page assets and video segments
- UI smoke tests confirming page viewer and video proxy still work
- byte-size and CPU improvement measurements

### Phase 3 - Plugin-host and plugin-dev binary transport

Scope:

- Replace plugin-dev base64 video segment endpoint with raw bytes.
- Add page asset dev endpoint with raw bytes.
- Update host-bridge runtime adapter to consume raw bytes.
- Add generic asset dev endpoint if the shared model is ready.
- Remove base64-centric CLI and plugin-dev models.

Validation:

- CLI `video-segment` and new `asset` command smoke tests
- host-bridge scenario tests

### Phase 4 - WASM binary result lane

Scope:

- Add binary result support to WASM operation scaffolding.
- Update plugin host runtime to consume binary operation payloads.
- Update templates and sample plugins.
- Remove the legacy base64 video segment path.

Validation:

- WASM direct runtime smoke tests
- `emma-video-test` parity validation on Linux and macOS first
- sample/template validation against the new binary-only path

### Phase 5 - Catalog v2 storage foundation

Scope:

- Add the new v2 SQL schema and storage implementation.
- Implement `IMediaCatalogPortV2`.
- Move paged and video metadata writes directly to v2 tables.
- Remove or freeze further work on v1 tables once v2 lands.

Validation:

- schema creation tests
- query-plan review for item/node/locator lookups

### Phase 6 - Pipeline cutover to v2 catalog

Scope:

- Move paged and video pipelines to read through catalog v2.
- Introduce generic progress cursors.
- Add locator-based lookup paths where appropriate.
- Remove v1 catalog reads and writes.

Validation:

- paged flow integration tests
- video flow integration tests
- progress cutover tests

### Phase 7 - CLI and developer experience completion

Scope:

- Add generic asset inspection commands.
- Update `doctor`, `session`, `serve`, and scenario flows to expose transport
  versions and asset capabilities.
- Update CLI docs and sample workflow docs.

Validation:

- CLI command smoke coverage for `host-bridge`, `linux-dev`, `windows-dev`, and
  `wasm-dev` where supported
- scenario coverage for page asset and video segment inspection

### Phase 8 - Final cleanup

Scope:

- Remove any remaining temporary shims left behind during implementation.
- Confirm the codebase is binary-only for asset transport and v2-only for the
  catalog.

Validation:

- no remaining callers of asset JSON/base64 transport
- no remaining runtime dependencies on catalog v1

## Order-of-operations recommendation

Do this in the following order:

1. Instrument first.
2. Ship native binary asset FFI.
3. Fix plugin-dev and CLI binary inspection.
4. Ship WASM binary result support.
5. Introduce catalog v2 and move runtime writes to it.
6. Migrate pipelines and progress fully.
7. Delete the old paths.

This order gives the largest performance gain earliest while still allowing a
clean cutover instead of dragging legacy transport and storage code forward.

## Risks

### Risk: ABI breakage in `emmaui`

Mitigation:

- land native and Dart changes together in one scoped implementation slice
- validate viewer and video playback immediately after the FFI cutover

### Risk: WASM typed export incompatibility

Mitigation:

- update scaffolds, templates, and both dev plugins in the same change window
- keep the binary result model single-path so authors are not choosing between
  incompatible transport modes

### Risk: CLI workflow fragmentation

Mitigation:

- one shared asset model in runtime adapters
- `doctor`/`session` expose transport capability clearly

### Risk: schema replacement complexity

Mitigation:

- stage the implementation by layer, but cut consumers over decisively once v2
  is ready
- keep the scope narrow enough that v1 can be deleted rather than maintained

## Success criteria

This roadmap is complete when:

- page assets and video segments no longer traverse native, WASM, or plugin-dev
  hot paths as base64 JSON
- `emmaui` consumes asset bytes directly through a shared asset ABI
- CLI asset inspection uses binary endpoints and does not print base64 by
  default
- the catalog can represent paged, video, audio, and text-like content through
  a shared item/node/locator model
- current paged and video behaviors remain operational throughout the cutover
- templates, samples, runtime, API, storage, and CLI all describe the same
  transport and asset model
- there is no remaining production dependency on asset JSON/base64 transport or
  catalog v1 code