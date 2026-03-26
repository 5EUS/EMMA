# Milestone 5 - API surface

## Scope

- Deliver a headless API layer for EMMA (gRPC with optional REST).
- Provide authentication, rate limiting, and request quotas.
- Introduce an embedded mode composition root for in-process usage.
- Preserve AOT trimming and stable API contracts.

## Work items

1) API contracts
   - Define gRPC service contracts for search, chapters, pages, and video segments.
   - Add API versioning and compatibility annotations.
   - Provide DTOs that decouple external contracts from domain models.
   - Standardize error and status mapping with stable codes.

2) Transport and hosting
   - Create `EMMA.Api` project with gRPC service implementations.
   - Add optional REST endpoints via minimal API where feasible.
   - Add request correlation and structured logging per call.
   - Ensure endpoints are compatible with Native AOT.

3) Authentication and rate limiting
   - Implement API key authentication and configurable key store.
   - Add per-client rate limiting and global concurrency caps.
   - Propagate client identity into request context for policy checks.

4) Embedded mode
   - Add an embedded composition root for local integration.
   - Expose in-process API adapters for unit tests and local apps.
   - Ensure the same policy and cache behavior as the hosted API.

5) Documentation and samples
   - Provide API usage examples and a simple client snippet.
   - Document versioning and deprecation policy.

## Validation plan

- Contract tests to verify stable request/response shapes.
- Authentication tests for allow/deny and rate limit enforcement.
- AOT publish for the API host with trimming enabled.
- Embedded mode tests using in-process adapters.

## Dependencies

- Milestone 4 video contracts if video API is exposed.
- Policy evaluator and cache services available in the API host.

## Open questions

- REST surface: full parity with gRPC or limited subset.
- Auth provider: static keys vs pluggable provider interface.

## Status

Not started.
