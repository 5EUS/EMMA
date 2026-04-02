# Implementation Roadmap

This roadmap tracks the current implementation state of EMMA as of 2026-04-01.
It is intentionally status-first rather than plan-only.

## Milestone Summary

| Milestone | Status | Notes |
| --- | --- | --- |
| 1 - Core runtime skeleton | Substantially complete | Domain/application ports, orchestration, policy, and runtime composition implemented |
| 2 - Plugin host | Substantially complete | Host process, handshake, lifecycle, quarantine, and platform sandbox managers implemented |
| 3 - First paged media plugin flow | Substantially complete | End-to-end paged search/chapters/pages pipeline exists with caching and storage integration |
| 4 - Video support | Partial | Contracts and plugin-host video endpoints exist; adaptive orchestration and segment cache strategy still open |
| 5 - API surface | In progress | gRPC + REST + auth + rate limiting implemented; versioning/error standardization pending |
| 6 - Hardening and sandboxing | In progress | Baseline signing, quarantine, and budget monitoring implemented; delegated trust and enforcement parity pending |

## Milestone 1 - Core runtime skeleton

### Implemented
- Domain and application projects with hexagonal ports are in production use.
- Media entities, identifiers, capability policy, and error models are implemented.
- Paged pipeline orchestration is implemented (search -> chapters -> page -> asset flow).
- CLI project exists and is actively used for dev/debug.

### Remaining
- Continue preventing cross-layer coupling as features expand.
- Expand native/AOT validation coverage for additional targets.

### Validation
- Unit tests cover domain and policy behavior.
- Existing AOT/native scripts validate core publishing flows.

## Milestone 2 - Plugin host

### Implemented
- Plugin host process with gRPC services and REST pipeline endpoints.
- Manifest loading, entrypoint resolution, process lifecycle management,
	handshake checks, idle cleanup, and quarantine flows.
- Platform-specific sandbox manager implementations (Linux, Windows, macOS,
	iOS, Android) plus sandbox directory preparation.
- Repository/catalog services for plugin acquisition and install orchestration.

### Remaining
- Strengthen sandbox enforcement parity across platforms.
- Harden runtime limits from monitoring-first to strict enforcement.

### Validation
- Integration and lifecycle tests exercise handshake, timeout, restart, and
	quarantine behavior.

## Milestone 3 - First paged media plugin

### Implemented
- End-to-end paged media flow is operational via host + pipeline layers.
- Page asset fetch/cache path is implemented.
- SQLite-backed catalog/library/history/progress/download storage is wired.

### Remaining
- Broaden plugin compatibility and normalization robustness.
- Continue cache tuning under constrained memory environments.

### Validation
- Application and API tests cover paged media flow and proxy behavior.

## Milestone 4 - Video support

### Implemented
- Video contracts exist in shared contract definitions.
- Plugin host exposes video REST probe endpoints for stream and segment routes.
- Progress storage ports include video progress persistence interfaces.

### Remaining
- Add full application-layer video orchestration and variant selection logic.
- Add segment caching and buffer management strategy.
- Complete end-to-end playback progress integration through API surfaces.

### Validation
- Contract and host endpoint behavior is testable now; adaptive behavior tests
	are still pending implementation.

## Milestone 5 - API surface

### Implemented
- Headless API layer is present via `EMMA.Api` and `EMMA.ApiHost`.
- gRPC service for paged media is implemented.
- REST paged endpoints are implemented.
- API key authentication middleware/interceptor is implemented.
- Per-client and global rate limiting is implemented.
- Embedded runtime composition is in use.

### Remaining
- API versioning and deprecation policy.
- Stronger cross-surface error response standardization.
- Decide and document long-term REST parity vs gRPC-first strategy.

### Validation
- API host test project validates key paged and proxy flows.

## Milestone 6 - Hardening and sandboxing

### Implemented
- HMAC-based plugin signature verification with environment-configured
	requirements.
- Quarantine and lifecycle monitoring services.
- Budget monitoring/background watcher services.
- Correlation IDs and structured logging in API and host flows.

### Remaining
- Delegated trust/signing model rollout (trust store, rotation, revocation).
- Enforced resource limits (not only monitoring and quarantine signals).
- Observability expansion (metrics, traces, dashboards).
- Stronger automated recovery and operational notifications.

### Validation
- Existing tests cover core security and lifecycle behavior; dedicated
	observability and enforcement chaos coverage remains to be added.
