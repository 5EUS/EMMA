# Solution Structure

## Proposed Solution Layout

```
/src
  /Aggregator.Domain
  /Aggregator.Application
  /Aggregator.Infrastructure
  /Aggregator.Plugin.Abstractions
  /Aggregator.Plugin.Host
  /Aggregator.Contracts
  /Aggregator.Api
  /Aggregator.Storage
  /Aggregator.Observability
  /Aggregator.Bootstrap
```

## Project Responsibilities

### Aggregator.Domain
- Pure domain models: media entities, value objects, capability rules, errors.
- Domain services with no IO, no framework dependencies.

### Aggregator.Application
- Use cases, orchestration, pipelines, policies.
- Defines ports (interfaces) for persistence, plugin communication, caching.
- Owns scheduling, retry, circuit breaker policy configuration.

### Aggregator.Infrastructure
- Adapters implementing application ports: SQLite, file storage, cache, HTTP, etc.
- OS-specific helpers that are safe in server/desktop contexts.

### Aggregator.Plugin.Abstractions
- In-process compile-time contracts for plugin implementations.
- Defines plugin interfaces and data contracts used by plugin authors.

### Aggregator.Plugin.Host
- Out-of-process host service with gRPC adapters.
- Manages sandboxing, resource limits, and plugin lifecycle.
- Exposes gRPC server to core runtime.

### Aggregator.Contracts
- IPC message schemas and generated gRPC contracts.
- Shared between core runtime and plugin host (AOT-friendly).

### Aggregator.Api
- Headless external API layer (gRPC/REST) for consumers.
- Hosted by daemon or embedded app when needed.

### Aggregator.Storage
- Persistence and migration layer (SQLite schema + migration tooling).
- Keeps storage concerns centralized and testable.

### Aggregator.Observability
- Logging, metrics, tracing interfaces and common helpers.
- Standardized telemetry fields and correlation IDs.

### Aggregator.Bootstrap
- Composition root for daemon/service and embedded mode.
- Wires dependencies and configuration, no runtime logic.

## Dependency Direction Rules

- Domain has no dependencies.
- Application depends only on Domain and Contracts.
- Infrastructure depends on Application + Contracts (adapters).
- Plugin.Host depends on Contracts + Observability (not on Application).
- Api depends on Application + Contracts + Observability.
- Storage depends on Application + Domain (implements persistence ports).
- Bootstrap depends on everything, only for wiring.

## Reference Graph (Rules of Use)

- Domain: referenced by Application, Storage.
- Application: referenced by Infrastructure, Api, Bootstrap.
- Contracts: referenced by Application, Plugin.Host, Api, Plugin.Abstractions.
- Plugin.Abstractions: referenced by plugin projects only.
- Plugin.Host: referenced by Bootstrap (when running local host).
- Observability: referenced by Api, Plugin.Host, Infrastructure, Bootstrap.

## Tradeoffs and Alternatives

- Aggregator.Storage could be folded into Infrastructure for fewer projects;
  separate keeps migrations isolated.
- Aggregator.Contracts could live under Application; separate avoids pulling
  application logic into plugin host.
