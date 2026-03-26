# EMMA (Extensible Multimedia Aggregator)

EMMA is a headless, cross-platform runtime for aggregating multimedia from
pluggable sources. It focuses on a hexagonal core, strong isolation for
untrusted plugins, and streaming-friendly pipelines for paged and video media.

## Goals

- Provide a stable core runtime that stays UI-agnostic.
- Run plugins out-of-process for fault isolation and security.
- Support both paged media and adaptive video streaming pipelines.
- Offer a consistent IPC surface via gRPC.
- Stay AOT-friendly for platforms with dynamic loading limits (e.g., iOS).

## Repository Structure (current)

- Root gRPC host: bootstrap for local development and protocol validation.
- [src](src/): planned core runtime, plugin host, and supporting projects.
- [tests](tests/): unit and integration tests.
- [docs](docs/): design docs and milestones.

## Architecture Docs

- System blueprint: [docs/architecture/system-blueprint.md](docs/architecture/system-blueprint.md)
- Plugin system design: [docs/architecture/plugin-system-design.md](docs/architecture/plugin-system-design.md)
- Media pipeline design: [docs/architecture/media-pipeline-design.md](docs/architecture/media-pipeline-design.md)
- Solution structure: [docs/architecture/solution-structure.md](docs/architecture/solution-structure.md)
- Implementation roadmap: [docs/architecture/implementation-roadmap.md](docs/architecture/implementation-roadmap.md)
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

The development host exposes a gRPC endpoint. See the default gRPC template
message at http://localhost:5000/ for basic connectivity guidance.
Run:
```bash
dotnet run --project src/EMMA.PluginHost/EMMA.PluginHost.csproj
```

### Tests

```bash
dotnet test
```

## Roadmap (high level)

- Milestone 1: core runtime skeleton (domain + application).
- Milestone 2: plugin host with gRPC IPC and sandboxing scaffolding.
- Milestone 3: first paged media plugin and end-to-end flow.
- Milestone 4: video pipeline with adaptive streaming support.
- Milestone 5: external API surface (gRPC + optional REST).
- Milestone 6: hardening, signing, and observability.

## Contributing

- Keep domain logic free of IO and platform dependencies.
- Prefer explicit contracts for IPC boundaries.
- Validate changes with tests and a clean build.

## License

TBD.
