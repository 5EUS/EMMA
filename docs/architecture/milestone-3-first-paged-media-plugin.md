# Milestone 3 - First paged media plugin

## Scope

- Build a reference paged media plugin with search, chapter, and page flows.
- Wire end-to-end retrieval through the plugin host into the core pipeline.
- Integrate page image caching with bounded memory and disk spill.
- Validate normalized metadata and error handling paths.

## Work items

1) Plugin contract implementation
   - Implement `IMediaSourcePlugin`, `ISearchProvider`, and `IPageProvider`.
   - Provide a manifest and capability declaration for paged media.
   - Define stable IDs for media, chapters, and pages.
   - Add request context propagation and deadline handling.
   - Validate response normalization and error mapping.

2) Plugin host integration
   - Register the plugin via the host registry.
   - Verify gRPC service mappings for search/chapters/pages.
   - Add health and capability checks at startup.
   - Enforce concurrency and timeout budgets per call.
   - Wire plugin log forwarding with correlation IDs.

3) Pipeline integration
   - Map plugin responses to domain models.
   - Enforce per-page timeouts and retry policies.

4) Cache integration
   - Add page asset cache boundaries and eviction policy.
   - Ensure decoded images are not retained beyond budget.

5) Storage integration
   - Register EMMA.Storage services in the plugin host and pipeline.
   - Store page assets and normalized metadata via EMMA.Storage APIs.
   - Define storage keys/paths for media, chapter, and page records.
   - Add retention/cleanup rules for temp and cached assets.

6) Fixtures and sample data
   - Controlled dataset for search and page retrieval.
   - Deterministic media IDs for stable tests.

## Validation plan

- End-to-end tests: search -> chapters -> page fetch -> cache hit.
- Policy enforcement tests: denied capability fails fast.
- Memory profile: long chapter read stays within cache budget.

## Dependencies

- Plugin host gRPC endpoints available for paged media.
- Cache service available to the pipeline.
- EMMA.Storage baseline APIs available for page assets and metadata.

## Open questions

- Fixture source: local files vs embedded HTTP server. (EMMA.TestPlugin has a fixture)
- Image decoding pipeline: use a lightweight decoder or store raw bytes.

## Status

In progress (started 2026-02-21).

Completed:
- Item 1: plugin contract implementation (test plugin services, normalization, request context/timeout handling).
- Item 2: plugin host integration (registry registration, startup checks, per-call limits, correlation-aware probes).
