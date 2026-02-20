# Implementation Roadmap

## Milestone 1 - Core runtime skeleton

### Deliverables
- Domain and Application projects with hexagonal ports.
- Minimal media entities, errors, and policy scaffolding.
- In-memory pipeline orchestration stubs.
- Minimal seperate CLI project for dev/debug and smoke tests (AOT-friendly).

### Technical Risks
- Over-coupling application and infrastructure boundaries.
- Missing AOT constraints early in design.

### Validation Strategy
- Unit tests for domain rules and policy evaluation.
- Build as Native AOT for a sample target to validate constraints.

## Milestone 2 - Plugin host

### Deliverables
- Plugin host process with gRPC server.
- Basic sandboxing and resource limit scaffolding per OS.
- Health and capability endpoints.

### Technical Risks
- OS-level sandboxing inconsistencies.
- IPC stability and performance.

### Validation Strategy
- Integration tests with a mock plugin.
- Kill/timeout tests to ensure isolation works.

## Milestone 3 - First paged media plugin

### Deliverables
- Reference paged plugin implementation.
- End-to-end search and page retrieval flow.
- Cache integration for images.

### Technical Risks
- Content normalization mismatches.
- Cache pressure on low memory targets.

### Validation Strategy
- End-to-end pipeline tests with controlled fixtures.
- Memory profiling under long reads.

## Milestone 4 - Video support

### Deliverables
- Video pipeline with adaptive streaming support.
- Segment cache and buffer management.
- Playback progress tracking and persistence.

### Technical Risks
- Segment retry behavior causing stalls.
- High bandwidth usage on poor networks.

### Validation Strategy
- Stress tests with simulated bandwidth changes.
- Buffer underrun and variant switch tests.

## Milestone 5 - API surface

### Deliverables
- Headless API layer (gRPC + optional REST).
- Authentication and rate limiting.
- Embedded mode composition root.

### Technical Risks
- API surface coupling to internal models.
- AOT trimming issues with serialization.

### Validation Strategy
- Contract tests for API stability.
- AOT build with API enabled.

## Milestone 6 - Hardening and sandboxing

### Deliverables
- Full resource enforcement and plugin signing.
- Quarantine flow and automated recovery.
- Observability stack with metrics and tracing.

### Technical Risks
- Platform-specific sandbox regressions.
- False positives in quarantine logic.

### Validation Strategy
- Chaos tests with misbehaving plugins.
- Security reviews and penetration tests.
