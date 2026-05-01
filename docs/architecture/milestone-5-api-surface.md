# Milestone 5 - API surface

## Scope

- Deliver a headless API layer for EMMA (gRPC with optional REST).
- Provide authentication, rate limiting, and request quotas.
- Introduce an embedded mode composition root for in-process usage.
- Preserve AOT trimming and stable API contracts.

## Current Status (2026-04-01)

In progress. Core API hosting is implemented and operational for paged media.
The remaining work is primarily API maturity (versioning, consistency,
documentation depth), plus broader video parity.

## Work items

1) API contracts
   - Done: gRPC contracts for paged APIs are in place and consumed.
   - Done: shared request context and provider contracts exist.
   - Open: API versioning and compatibility/deprecation annotations.
   - Open: stronger cross-surface (gRPC + REST) error/status standardization.

2) Transport and hosting
   - Done: `EMMA.Api` and `EMMA.ApiHost` are implemented.
   - Done: minimal REST endpoints for paged flows are implemented.
   - Done: request correlation and structured logging are present.
   - In progress: continue validating AOT compatibility across deployment lanes.

3) Authentication and rate limiting
   - Done: API key auth middleware/interceptor and key validation are implemented.
   - Done: per-client fixed-window limits and global concurrency limiter are implemented.
   - In progress: continue policy integration depth and operational tuning.

4) Embedded mode
   - Done: embedded runtime composition root is implemented.
   - Done: in-process pipeline usage is wired through API services.
   - In progress: keep behavior parity validation between host and embedded paths.

5) Documentation and samples
   - In progress: architecture docs and README are being refreshed.
   - Open: versioning/deprecation policy doc.
   - Open: expand client snippets and usage examples.

## Validation Status

- Done: API host test coverage includes gRPC paged calls and page-asset proxying.
- Done: auth and throttling behavior are exercised in API host tests.
- In progress: additional contract compatibility regression suites.
- In progress: broader AOT publish validation across all targeted RIDs.

## Dependencies

- Milestone 4 video contracts if video API is exposed.
- Policy evaluator and cache services available in the API host.

## Open questions

- REST surface: full parity with gRPC or limited subset.
- Auth provider: static keys vs pluggable provider interface.

## Status

In progress.
