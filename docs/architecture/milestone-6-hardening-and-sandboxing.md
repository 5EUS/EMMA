# Milestone 6 - Hardening and sandboxing

## Scope

- Enforce plugin resource limits and sandbox policies across OS targets.
- Add plugin signing and trust verification.
- Implement quarantine and recovery flow for misbehaving plugins.
- Ship an observability stack with metrics, tracing, and diagnostics.

## Work items

1) Resource enforcement
   - Apply per-plugin CPU, memory, and IO budgets.
   - Enforce filesystem and network access policies.
   - Add per-call timeouts and global circuit breakers.
   - Capture resource usage metrics for policy audits.

2) Sandboxing
   - Finalize OS-specific sandbox implementations and configuration.
   - Add sandbox feature detection and fallback modes.
   - Provide a local dev mode with explicit warning banners.

3) Plugin signing and trust
   - Add manifest signing and signature verification on load.
   - Maintain a trust store and revocation list.
   - Prevent unsigned plugins from running in production mode.

4) Quarantine and recovery
   - Detect repeated crashes, timeouts, and policy violations.
   - Quarantine plugins with a backoff schedule and manual override.
   - Provide recovery workflow and audit logs.

5) Observability
   - Emit structured logs with correlation IDs across host and plugins.
   - Add metrics for latency, cache hit rate, and resource usage.
   - Wire distributed tracing for host -> plugin calls.

## Validation plan

- Chaos tests with misbehaving plugins (timeouts, crashes, leaks).
- Security review of sandboxing and signing flows.
- Observability tests to ensure metrics and traces are complete.
- Load tests to validate resource enforcement under stress.

## Dependencies

- Plugin host sandbox scaffolding from Milestone 2.
- Policy evaluator and request context propagation.

## Open questions

- Preferred signing algorithm and trust store format.
- Minimum telemetry set for production operation.

## Status

Not started.
