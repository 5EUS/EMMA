# ADR-0001: Hybrid plugin runtime for iOS post-deploy pluggability

## Status

Accepted

## Date

2026-02-28

## Context

EMMA must support:

- iOS as a first-class target.
- Internal hosting and remote hosting on all targets.
- Post-deploy plugin pluggability.

The existing process-spawned plugin model works well on desktop/server but is not a reliable foundation for post-deploy dynamic plugin execution on iOS. At the same time, EMMA should keep its core/runtime architecture in C# and preserve existing remote C# plugin investments.

## Decision

Adopt a hybrid plugin runtime strategy behind a single host contract:

1) Internal lane (on-device):
- Use WASM for post-deploy dynamic plugin execution.
- Enforce capability policy, deadlines, and budgets in host code.

2) External lane (remote):
- Continue supporting remote C# plugins via gRPC.
- Use remote execution for complex/high-cost workloads or centralized operations.

3) Desktop/server local process lane:
- Keep existing process-based C# plugin execution where supported and beneficial.

Manifest policy:
- Runtime kind is declared in manifest metadata.
- Runtime entrypoint is auto-resolved by trusted host rules.
- Host rejects/ignores manifest-specified entrypoint overrides.

## Rationale

- Meets hard post-deploy pluggability requirements for iOS.
- Preserves internal and remote host modes across targets.
- Avoids unnecessary rewrite of core C# architecture.
- Minimizes manifest attack surface via host entrypoint resolution.

## Consequences

Positive:
- Unified app-facing contracts across execution lanes.
- Stronger security posture from host-controlled runtime resolution.
- Operational flexibility: internal-first with remote fallback.

Tradeoffs:
- Additional host/runtime complexity to support multiple lanes.
- Need ABI versioning, conformance tests, and packaging tooling for WASM plugins.

## Implementation notes

- Detailed migration plan: `ios-viable-targeting-solution.md`.
- Phase 0 scope is complete when:
  - Architecture docs reflect iOS internal + remote support.
  - Post-deploy policy is explicit (`iOS internal dynamic => WASM lane`).
  - This ADR is accepted and referenced by planning docs.
