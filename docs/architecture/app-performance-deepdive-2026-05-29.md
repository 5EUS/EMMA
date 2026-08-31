# App Performance Deepdive

Date: 2026-05-29

## Scope

This review focuses on hot user-visible paths across the Flutter app and native host stack, with emphasis on:

- app startup
- search
- item loading and detail retrieval
- cross-cutting lag spikes during normal usage

The analysis is code-driven. It is based on the current implementation in emmaui, EMMA.Native, EMMA.PluginHost.Library, and the representative plugin runtimes in emma-test-plugin and emma-video-test. This document does not include fresh benchmark numbers; it identifies high-confidence bottlenecks, why they happen, and what should be changed first.

## Executive Summary

The lag is not coming from a single slow function. It is the result of several expensive layers stacking together:

1. Startup does too much synchronous filesystem and host setup work before the app is really idle.
2. Several hot interactions create a fresh background isolate, then start and stop the native runtime inside that isolate for every request.
3. Search and library/item flows cross the Dart FFI boundary using full JSON serialization and deserialization, often with large payloads.
4. Plugin resolution is on the hot path for search, chapters, pages, and detail loading, and can trigger runtime ensure-start and handshake work.
5. Library listing is doing an avoidable storage N+1 pattern: load library entries, then fetch media metadata one item at a time.
6. Startup currently triggers repository catalog refresh work from the main tab scaffold even when the user never opens Settings.
7. Detail loading combines multiple independent runtime calls into one large blocking background job, so latency spikes compound instead of overlapping or being deferred.

The biggest wins are likely to come from:

- removing startup-time repository refresh
- reusing a long-lived worker/runtime for hot background calls instead of starting and stopping it per request
- fixing the N+1 library listing path in the host storage layer
- reducing repeated plugin resolution/open work on search and detail flows
- adding real timings around host init, plugin resolution, search execution, JSON marshalling, and storage lookups

## Architecture View Of The Hot Path

For the main user operations, the path usually looks like this:

1. Flutter screen calls `LoadingService.runInBackground(...)`.
2. `compute(...)` creates or reuses a background isolate for a top-level function.
3. That function configures `EmmaRuntime`, starts it, configures host mode, and often opens or resolves a plugin.
4. The Dart runtime calls into EMMA.Native over FFI.
5. EMMA.Native delegates into `PluginHostExports`.
6. `PluginHostExports` often calls `EnsureInitialized()` and `TryResolvePlugin(...)`.
7. Plugin resolution can trigger sandbox preparation, process start, endpoint probe, and handshake for non-WASM plugins.
8. Results return as JSON strings back through FFI and are decoded into Dart model objects.

That architecture is functional, but it is expensive for hot, repeated operations.

## Findings

### 1. Startup does substantial filesystem work up front

`emmaui/lib/main.dart` runs `EmmaBootstrap.initialize()`, `AppShellService.initialize()`, and `PluginHostRoutingService.applyForLibrary(null)` inside the startup gate before the app reaches steady state.

`emmaui/lib/emma_bootstrap.dart` moves directory prep off the UI isolate with `compute(...)`, which is good, but the background work is still heavy:

- multiple `existsSync`, `createSync`, `writeAsStringSync`
- trust directory replication
- on macOS, full copy of bundled manifests and plugin directories from app resources into the app support directory
- recursive directory copy using `listSync(recursive: true)` and `copySync`

This means the UI isolate is spared direct blocking, but cold start still waits for a large amount of disk I/O before bootstrap completes. On slower disks, larger plugin bundles, or first-run installs, this will present as slow startup rather than jank.

### 2. Startup triggers Settings-related repository refresh work too early

`emmaui/lib/screens/main_tab_scaffold.dart` calls `_refreshSettingsUpdateCount(refreshCatalog: true)` during `initState()`.

That path immediately does:

- `runtime.listPlugins()`
- `runtime.listAllRepositoryPlugins(refreshCatalog: true)`

`EMMA/src/EMMA.PluginHost.Library/PluginHostExports.cs` then calls `PluginRepositoryService.GetAllRepositoryPluginsAsync(refreshCatalog, ...)` synchronously via `GetAwaiter().GetResult()`.

This is startup work that is unrelated to the user's first screen. It is especially expensive because catalog refresh may involve file and network activity, and it happens before the user has even chosen to inspect plugin updates.

This matches the existing repo note that Settings entry should not force catalog refresh from tab navigation.

### 3. Host routing verification adds real work to many flows

`emmaui/lib/services/plugin_host_routing_service.dart` uses `_configureAndVerify(...)`, which calls:

- `runtime.start()`
- `runtime.configureHost(...)`
- `EmmaRuntime.instance.listPlugins()`

That verification is used in startup and in many background request flows before the actual work begins. Even if each individual call is not catastrophic, it adds overhead to nearly every hot action.

### 4. Search spins up a full background runtime per request

`emmaui/lib/screens/search_page.dart` uses `_searchMediaInBackground(...)` for each search and page navigation request. That background function does all of the following every time:

- `runtime.configurePaths(...)`
- `runtime.start()`
- `runtime.configureHost(...)`
- `runtime.openPlugin(pluginId)`
- `runtime.searchMedia(...)`
- `runtime.stop()`

That is a lot of setup and teardown for a hot operation that users expect to feel interactive.

The same pattern exists for search suggestions in `_searchSuggestionsInBackground(...)`.

This is a likely root cause of the search lag spikes: even before provider latency is counted, each query pays for worker setup, runtime lifecycle work, plugin resolution, and JSON conversion.

### 5. Search results are fully marshalled through JSON across FFI

On the Dart side, `emmaui/lib/emma_runtime.dart` search APIs call native functions that return JSON strings, then decode full lists into model objects.

On the host side, `PluginHostExports.SearchMediaManaged(...)` builds result lists and serializes them back to JSON.

This means every search pays for:

- native runtime search work
- managed object materialization
- JSON serialization in C#
- FFI string allocation and copy
- JSON parsing in Dart
- Dart model materialization

That design is convenient, but it is expensive for large result sets and repeated paging.

### 6. Search page has a built-in landing search on open

`PluginSearchSheet` automatically performs a landing search with query `a` on first open.

That behavior is not wrong, but it guarantees background runtime startup and search execution before the user has expressed intent. On plugins with expensive query resolution, remote calls, or large result payloads, opening search feels slow even before active search begins.

### 7. Representative search runtime cost is dominated by network and parsing, but host overhead is still meaningful

The representative search implementation in `emma-test-plugin/ASPNET/AspNetClient.cs` does this on search:

- parse structured query
- resolve query enrichments
- build provider path
- perform HTTP request
- read full payload string
- parse JSON document
- map search entries and metadata

The plugin already avoids statistics enrichment during primary search, which is correct and should stay that way. Even so, this path is still expensive because the plugin fetches and parses full provider payloads, while the app layer adds runtime lifecycle, plugin resolution, and JSON crossing overhead on top.

In other words: search is slow both because the provider work is nontrivial and because the app/host plumbing around it is heavier than it needs to be.

### 8. Library item loading uses a storage N+1 pattern

`PluginHostExports.ListLibraryMediaJsonManaged(...)` is a major hotspot for home, updates, and other "get items" flows.

Its current pattern is:

1. load all library entries with `ILibraryPort.GetLibraryAsync(...)`
2. for each entry, call `IMediaCatalogPort.GetMediaAsync(...)`
3. build a `MediaSummary`
4. serialize the full result list to JSON

Because `SqliteMediaCatalogPort.GetMediaAsync(...)` opens a SQLite connection and performs a single-item query, this becomes one query per library item. That is an avoidable N+1 pattern.

For medium or large libraries, this will produce exactly the kind of "normal usage" lag spikes the user is seeing when opening the home screen, updates, and any flows that reload library state.

### 9. Home page background loading compounds the library and plugin listing costs

`emmaui/lib/screens/home_page.dart` background loading does:

- `runtime.listLibraries()`
- `runtime.listLibraryMedia(libraryName: selectedLibrary)`
- `runtime.listPlugins()`

All of that is wrapped in the same per-request runtime start/stop cycle.

So one home reload can include:

- routing application and verification
- runtime start
- library list query
- N+1 media metadata fetches
- plugin list snapshot and summary generation
- JSON serialization back to Dart
- runtime stop

This is a prime candidate for visible startup and navigation delay.

### 10. Detail loading is overly serialized and heavyweight

`emmaui/lib/screens/series_detail_page.dart` background detail load does much more than load chapters. In one background job it can do:

- `runtime.openPlugin(pluginId)`
- `runtime.getChapters(mediaId)`
- `runtime.listDownloads(limit: 2000)`
- optional `runtime.getVideoStreams(...)` probe
- multiple `runtime.getMediaProgress(...)` calls for root media and chapter media IDs
- `runtime.isMediaInLibrary(...)`
- `runtime.listLibraries()`
- `runtime.getReadChapterIds(...)`

This is a latency amplifier. Even if each call is individually acceptable, serializing them into one large worker task means one slow subcall becomes a visible spike on the whole screen.

The chapter fetch path itself can also be expensive. `PluginHostExports.GetChaptersManagedInternal(...)` may resolve the plugin, execute runtime calls, and then upsert the resulting chapter set into storage.

### 11. Plugin resolution is on the hot path and can trigger startup logic

`PluginHostExports.TryResolvePlugin(...)` calls `_pluginResolution.ResolveAsync(...)`.

`EMMA/src/EMMA.PluginHost/Services/PluginResolutionService.cs` can do all of the following if runtime state is not already healthy:

- manifest load
- sandbox preparation
- process startup for non-WASM plugins
- endpoint allocation
- endpoint readiness probing
- handshake execution

This is correct for robustness, but it means search, chapters, pages, and related operations are coupled to plugin runtime health checks. When runtime state is cold, stale, or partially initialized, the user-facing operation pays the recovery cost.

### 12. `listPlugins()` is more expensive than it looks

`PluginHostExports.ListPluginsJsonManaged()` does more than return static manifest IDs. It also builds preference summaries via `PluginPreferencesService.GetSummaryAsync(...)` for every plugin.

The app calls `listPlugins()` often:

- startup route verification
- search page plugin list
- home page background hint mapping
- settings badge refresh
- several other screens

That makes plugin listing a cross-cutting cost center.

### 13. Thumbnail resolution and pruning can add background churn during normal use

`emmaui/lib/services/thumbnail_cache_service.dart` resolves disk-cached thumbnails and can perform startup prune work. It still uses synchronous directory checks and listing in several places:

- `existsSync()`
- `listSync()`

This is less likely to be the primary cause of startup slowness than the host and library paths, but it can contribute to lag spikes during scrolling or first image-heavy visits, especially when many items appear at once.

### 14. Some work is already correctly deferred

Not everything is problematic. A few current decisions are good and should be preserved:

- startup filesystem preparation is moved off the UI isolate with `compute(...)`
- search metadata/statistics enrichment is deferred in the Mangadex plugin instead of running in primary search
- some rich presence work is pushed into background startup work instead of blocking the gate further

The app is not missing every performance practice. The main issue is that the hot path still has too many layers of repeated setup and repeated full-payload work.

## Root Cause Breakdown By User Operation

### Startup

Highest-confidence causes:

- bootstrap filesystem preparation and copy work
- host routing verification calling into runtime/plugin listing
- startup-time settings badge refresh and repository catalog refresh
- plugin listing doing more than a lightweight manifest summary

Expected user symptom:

- cold app open is slower than expected
- occasional startup spikes depending on plugin/repository state and filesystem state

### Search

Highest-confidence causes:

- fresh background isolate request model
- runtime start/configure/stop on each search or paging request
- plugin resolution/open on each search request
- full JSON marshalling over FFI
- provider HTTP + payload parse cost inside plugin runtime
- landing search executed automatically on plugin search open

Expected user symptom:

- search entry feels slow even before typing
- typed searches and page navigation feel heavier than normal
- lag spikes vary based on plugin/runtime warmness and result size

### Get Items / Detail / Chapters

Highest-confidence causes:

- N+1 library metadata fetch pattern
- full library payload serialization
- detail screen doing many serial runtime calls in one worker pass
- chapter fetch path potentially re-entering plugin resolution and storage update work

Expected user symptom:

- home and updates views sometimes pause when loading items
- opening detail pages can spike more than expected
- libraries scale poorly with item count

## Priority Recommendations

### Priority 0: Remove avoidable startup work

1. Stop refreshing repository catalogs from `MainTabScaffold.initState()`.
2. Compute the settings badge lazily when the Settings tab is opened, or use cached repository data first and refresh later.
3. Keep startup focused on the minimum required for the first frame plus first usable navigation.

Why this matters:

This is the cleanest high-impact change. It removes obviously unrelated work from startup without needing deep architecture changes.

### Priority 1: Replace per-request runtime lifecycle with a warm worker/runtime model

1. Do not `start()` and `stop()` the native runtime for every search, home, detail, and suggestion request.
2. Introduce a long-lived worker service for hot background operations.
3. Reuse configured runtime and host mode across requests until configuration actually changes.

Why this matters:

This likely reduces search and detail latency more than any single micro-optimization because it removes repeated lifecycle overhead from the hottest paths.

### Priority 2: Fix the library listing N+1 query pattern

1. Replace `ListLibraryMediaJsonManaged(...)` per-item catalog fetches with a joined or batched lookup path in storage.
2. Add a single query that returns library entry plus media metadata in one pass.
3. Reuse one connection and stream rows rather than reopening SQLite connections per item.

Why this matters:

This directly targets home, updates, and several library-related reloads. It is also a structural fix, not a UI workaround.

### Priority 3: Split detail loading into critical and deferred phases

1. Load chapters and minimal library state first.
2. Defer downloads scan, secondary progress reconciliation, and video probing until after initial detail render.
3. Run independent background calls concurrently where correctness allows.

Why this matters:

The current detail load is a serialized bundle of unrelated work. Breaking it apart will reduce perceived latency even before backend changes land.

### Priority 4: Make plugin resolution cheaper on warm paths

1. Cache successful plugin resolution/open state for the current runtime session.
2. Avoid repeated handshake or readiness work unless state is stale or known-bad.
3. Separate "lightweight resolve existing plugin" from "ensure process started and handshaken".

Why this matters:

This reduces the cost that search, chapters, and pages all currently inherit from host correctness logic.

### Priority 5: Reduce FFI JSON payload overhead on hot operations

1. Prefer typed/native result transfer for the most frequent calls if feasible.
2. If full redesign is too large, reduce payload size first:
   - slimmer summary models for home/search
   - avoid sending fields the screen does not use yet
   - defer metadata enrichment even more aggressively
3. Add pagination or chunking to large library result sets where possible.

Why this matters:

Even after logic optimizations, full JSON roundtrips will continue to be a cost floor on hot paths.

## Recommended Instrumentation

Before and during fixes, add durable timing around these points:

- Flutter startup gate total time
- `EmmaBootstrap.initialize()` duration
- `PluginHostRoutingService.applyForLibrary(...)` duration
- `listPlugins()` duration, including preference summary generation
- `listAllRepositoryPlugins(refreshCatalog: true)` duration
- background worker queue and isolate creation time
- `runtime.start()` and `runtime.stop()` duration
- `runtime.openPlugin(...)` duration
- `TryResolvePlugin(...)` and `PluginResolutionService.ResolveAsync(...)` duration
- plugin search runtime execution duration
- host JSON serialization duration
- Dart JSON decode duration
- `ListLibraryMediaJsonManaged(...)` total duration and item count
- per-screen detail load substep timings

Without this, it will be hard to tell whether gains are coming from UI, host, storage, or plugin runtime changes.

## Suggested Rollout Order

### Phase 1

- remove startup settings badge catalog refresh
- stop automatic landing search, or delay it until the sheet is visibly settled
- add timing instrumentation for startup, search, and library listing

Expected result:

- immediate improvement in perceived startup
- clearer visibility into remaining hotspots

### Phase 2

- implement joined or batched library/media query path
- slim library payloads where possible
- split detail load into critical and deferred work

Expected result:

- smoother home and detail navigation
- lower spikes in normal browsing

### Phase 3

- introduce warm runtime/worker reuse
- reduce repeated plugin resolution/open work
- evaluate typed transport or reduced JSON payload contracts for hot operations

Expected result:

- search and repeated navigation become materially faster
- cold-vs-warm interaction variance narrows

## Risks And Tradeoffs

- Reusing runtime state improves performance but requires careful invalidation when manifests, host mode, or plugin configuration change.
- Deferring detail work improves perceived speed, but the UI must tolerate partial state and progressive enhancement.
- Joining library and media queries will likely change storage abstractions and needs to preserve current sort and fallback behavior.
- Reducing JSON payloads may require new host export surfaces or versioned contracts.

## High-Confidence Conclusions

- Startup is slower than necessary mainly because unrelated repository refresh work is being done too early and because bootstrap/host verification still performs substantial real work before steady state.
- Search is slower than necessary because every request pays repeated worker/runtime/plugin setup costs before it even pays provider latency.
- Item loading scales poorly because library reads use an N+1 metadata lookup pattern and detail loading serializes too many runtime calls into one blocking step.
- The app's largest performance opportunities are structural, not cosmetic. The right fixes are lifecycle reuse, startup deferral, storage batching, and better separation of critical vs deferred work.

## Concrete Next Actions

1. Remove `refreshCatalog: true` startup badge refresh from `MainTabScaffold`.
2. Add timing logs around startup gate, search background job, and `ListLibraryMediaJsonManaged(...)`.
3. Implement a batched library listing path in storage and switch home/updates to it.
4. Break series detail loading into initial render data and deferred enrichments.
5. Design a warm background runtime service so search and suggestions stop paying runtime lifecycle cost on every request.