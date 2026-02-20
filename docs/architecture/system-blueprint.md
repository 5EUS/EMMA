# System Blueprint

## 1) High-Level Architecture Diagram (ASCII)

```
+-----------------------+          +------------------------+
|   Host App / Daemon   |          |   Embedded Consumer    |
|  (Service or Library) |          |  (Mobile/Desktop/App)  |
+-----------+-----------+          +-----------+------------+
            |                                  |
            |        Hexagonal Core            |
            |      (UI-agnostic runtime)       |
            v                                  v
+-----------------------------------------------------------+
|                    Core Runtime                           |
|  Domain + Application Ports                               |
|  - Media Catalog & Library                                |
|  - Pipeline Orchestrators                                 |
|  - Capability Policy Engine                               |
|  - Plugin Registry                                        |
|  - Scheduling / Throttling                                |
+---------------------+-------------------------------------+
                      |
                      | Ports (gRPC IPC)
                      v
+---------------------+-------------------------------------+
|                  Plugin Host                              |
|  - Process Supervisor                                     |
|  - Sandbox + Resource Limits                              |
|  - gRPC Contract Adapter                                  |
|  - Crash / Timeout Isolation                              |
+---------------------+-------------------------------------+
                      |
          +-----------+-----------+
          |                       |
          v                       v
+------------------+     +------------------+
| Paged Plugins    |     | Video Plugins    |
| (Untrusted)      |     | (Untrusted)      |
+------------------+     +------------------+
```

## 2) Responsibilities

### Core runtime (domain + application)
- Owns media models, library state, and pipeline orchestration.
- Enforces capability policies and scheduling rules.
- Exposes ports for plugin discovery, media retrieval, and search.
- Remains headless and UI-agnostic; no platform-specific dependencies.

### Plugin host
- Runs plugins out-of-process to isolate faults and untrusted code.
- Enforces resource limits, timeouts, and network/file restrictions.
- Bridges the core to plugin processes via gRPC.
- Supervises lifecycle: start, health-check, restart, quarantine.

### Media pipeline
- Normalizes plugin data into stable domain models.
- Manages caching, streaming, and progressive loading.
- Applies retry, circuit breaker, and backpressure policies.
- Provides consistent errors and telemetry regardless of plugin behavior.

## 3) Cross-Platform Strategy (including iOS constraints)

- Core runtime as pure .NET 10 libraries, all shared logic in AOT-safe code.
- Plugin isolation via IPC, no dynamic in-process loading.
- iOS strategy:
  - Plugin processes cannot be spawned; use a remote plugin host model
    (device-to-server gRPC) or bundled trusted adapters that call a
    remote sandbox.
  - AOT-friendly contracts: generated gRPC stubs with static bindings.
- Desktop/server strategy:
  - OS-native sandbox tooling for isolation.
  - Plugin host runs as a separate process with strict resource limits.

## 4) Plugin Isolation Strategy Justification

- Out-of-process gRPC avoids AppDomains and reflection-heavy loading.
- Fault containment: plugin crashes do not crash the core runtime.
- Security control: per-plugin OS-level sandboxing is possible only with
  process boundaries.
- AOT-friendly: no dynamic assemblies, compatible with iOS and native
  AOT constraints.

## 5) Service Hosting Strategy (daemon + embedded mode)

### Daemon/service mode
- Long-running host (Windows service, systemd, launchd).
- Core runtime + plugin host in separate processes.
- Exposes API (gRPC/REST) for external consumers.

### Embedded mode
- Core runtime as a library inside another app.
- Plugin host can be:
  - Local process (desktop).
  - Remote host (mobile/iOS).
- Same ports/contract surface for consistent integration.

## Security Priorities (Phase 1 overview)

- Resource limits per plugin: CPU, memory, file handles, disk.
- Network restrictions: allowlist per plugin capability.
- File access restrictions: sandboxed directories only.
- Plugin signing: required for installation and execution.
- Rate limiting at IPC boundary.

## Performance Considerations (Phase 1 overview)

- Concurrent plugin execution with bounded scheduling.
- Streaming-oriented pipelines to avoid large memory spikes.
- Cache layers for large media libraries.

## Tradeoffs and Alternatives

- gRPC vs. REST IPC: gRPC preferred for performance, typed contracts, and
  streaming. REST is simpler but less efficient for streaming.
- Local vs. remote plugin host: local is lower latency but not available on
  iOS; remote is consistent and secure but needs connectivity.

## Observability and Recovery (brief)

- Structured logging with correlation IDs across core and plugin host.
- Metrics for per-plugin latency, timeouts, and error rates.
- Crash recovery: automatic restart with exponential backoff and quarantine.
