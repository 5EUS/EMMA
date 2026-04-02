# Milestone 6 - Hardening and sandboxing

## Scope

- Enforce plugin resource limits and sandbox policies across OS targets.
- Add plugin signing and trust verification.
- Implement quarantine and recovery flow for misbehaving plugins.
- Ship an observability stack with metrics, tracing, and diagnostics.

## Current Status (2026-04-01)

In progress. Baseline hardening is implemented (signature verification,
quarantine lifecycle hooks, budget monitoring, and structured logs), while
full trust model rollout, strict enforcement parity, and deep observability are
still open.

## Work items

1) Resource enforcement
   - Done: budget monitoring/watcher services and timeout-driven lifecycle paths.
   - Partial: filesystem/network policies are represented in manifest + sandbox
     preparation, but enforcement parity varies by platform.
   - Partial: per-call timeout handling exists in key host endpoints.
   - Open: strict CPU/memory/IO enforcement and policy-audit metric depth.

2) Sandboxing
   - Done: OS-specific sandbox manager implementations exist for Linux,
     Windows, macOS, iOS, and Android.
   - Partial: feature detection/fallback behavior exists, but enforcement depth
     is not yet equivalent across targets.
   - Open: stronger production-mode guarantees and clearer operator diagnostics.

3) Plugin signing and trust
   - Done: HMAC signature verification during plugin load.
   - Partial: unsigned plugin behavior can be gated via configuration/env.
   - Open: delegated trust model, trust store, revocation, and key rotation.

4) Quarantine and recovery
   - Done: crash/timeout-driven quarantine behavior with lifecycle coordination.
   - Partial: recovery/backoff exists but can be expanded operationally.
   - Open: broader automated recovery policy and richer audit/reporting outputs.

5) Observability
   - Done: structured logging and correlation IDs in host/API paths.
   - Open: full metrics surface (latency/cache/resource dashboards).
   - Open: distributed tracing for host -> plugin calls.

## Validation Status

- Done: security/lifecycle tests cover signature checks, quarantine, and
   plugin-host behavior under failure scenarios.
- In progress: expanded chaos scenarios for resource pressure and recovery.
- Open: observability completeness tests for metrics/traces.
- Open: stress/load suites that validate strict resource enforcement.

## Dependencies

- Plugin host sandbox scaffolding from Milestone 2.
- Policy evaluator and request context propagation.

## Open questions

- Preferred signing algorithm and trust store format.
- Minimum telemetry set for production operation.

## Status

In progress.
