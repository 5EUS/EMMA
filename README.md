# EMMA (Extensible Multimedia Aggregator)

EMMA is a headless, cross-platform runtime for aggregating multimedia from
pluggable sources. It focuses on a hexagonal core, strong isolation for
untrusted plugins, and streaming-friendly pipelines for paged and video media.

## Current Implementation Status (2026-04-01)

- Core hexagonal runtime is implemented (`EMMA.Domain`, `EMMA.Application`,
	`EMMA.Infrastructure`, `EMMA.Bootstrap`).
- Plugin host is implemented with process lifecycle management, handshake,
	quarantine flows, repository/catalog services, and platform sandbox managers.
- API surface is implemented via gRPC and REST in `EMMA.Api` and
	`EMMA.ApiHost`, including API key auth and rate limiting.
- SQLite-backed persistence is implemented for catalog, library, progress,
	history, and download metadata in `EMMA.Storage`.
- Native/AOT integration projects exist (`EMMA.Native`,
	`EMMA.PluginHost.Library`, `EMMA.WasmRuntime.Native`) and are wired into
	tooling scripts.
- Video support is partial: contracts and plugin-host endpoints are present,
	while full adaptive streaming orchestration is still in progress.
- Hardening is partial: HMAC signing and quarantine logic are present,
	but delegated trust, full sandbox enforcement parity, and richer
	observability remain open.

## Goals

- Provide a stable core runtime that stays UI-agnostic.
- Run plugins out-of-process for fault isolation and security.
- Support both paged media and adaptive video streaming pipelines.
- Offer a consistent IPC surface via gRPC.
- Stay AOT-friendly for platforms with dynamic loading limits (e.g., iOS).

## Repository Structure (current)

- [src](src/): implemented runtime, hosts, plugin infrastructure, storage,
  contracts, and native/AOT bridges.
- [tests](tests/): unit and integration test suites for domain, application,
  plugin host, API host, and plugin-common behavior.
- [docs](docs/): design docs and milestones.

## Architecture Docs

- System blueprint: [docs/architecture/system-blueprint.md](docs/architecture/system-blueprint.md)
- Plugin system design: [docs/architecture/plugin-system-design.md](docs/architecture/plugin-system-design.md)
- Implementation roadmap: [docs/architecture/implementation-roadmap.md](docs/architecture/implementation-roadmap.md)
- Storage strategy: [docs/architecture/storage-strategy.md](docs/architecture/storage-strategy.md)
- Milestone 5 status: [docs/architecture/milestone-5-api-surface.md](docs/architecture/milestone-5-api-surface.md)
- Milestone 6 status: [docs/architecture/milestone-6-hardening-and-sandboxing.md](docs/architecture/milestone-6-hardening-and-sandboxing.md)
- Plugin signing delegated trust model: [docs/architecture/plugin-signing-delegated-trust-model.md](docs/architecture/plugin-signing-delegated-trust-model.md)
- Plugin scaffold workflow: [docs/architecture/plugin-scaffold-workflow.md](docs/architecture/plugin-scaffold-workflow.md)
- Tooling scripts reference: [docs/architecture/tooling-scripts-reference.md](docs/architecture/tooling-scripts-reference.md)

## Getting Started

### Prerequisites

- .NET SDK 10.0

### Build

```bash
dotnet build
```

### Run CLI (development)

```bash
dotnet run --project src/EMMA.Cli/EMMA.Cli.csproj
```

### Run Plugin Host (development)

```bash
dotnet run --project src/EMMA.PluginHost/EMMA.PluginHost.csproj
```

### Run API Host (development)

```bash
dotnet run --project src/EMMA.ApiHost/EMMA.ApiHost.csproj
```

The API host fronts the plugin host and exposes gRPC plus REST paged endpoints.

### Tests

```bash
dotnet test
```

## Roadmap (high level)

- Milestone 1: substantially complete (core runtime and ports).
- Milestone 2: substantially complete (plugin host and lifecycle).
- Milestone 3: substantially complete (paged pipeline and plugin workflows).
- Milestone 4: partial (video endpoints/contracts present; adaptive orchestration pending).
- Milestone 5: in progress (gRPC/REST/auth/rate limiting implemented; versioning and standardization open).
- Milestone 6: in progress (baseline signing/quarantine/sandbox scaffolding implemented; enforcement and observability expansion open).

## Contributing

- Keep domain logic free of IO and platform dependencies.
- Prefer explicit contracts for IPC boundaries.
- Validate changes with tests and a clean build.

## License

TBD.
