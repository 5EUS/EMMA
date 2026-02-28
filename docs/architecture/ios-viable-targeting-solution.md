# iOS-viable targeting solution (post-deploy pluggability)

## Objective

Deliver a plugin architecture that:

- Supports iOS as a first-class target.
- Preserves internal hosting and remote hosting across all targets.
- Satisfies hard post-deploy pluggability requirements.
- Keeps core/runtime architecture in C#.

## Problem statement

Current desktop-oriented plugin flows assume process-spawned plugin executables. iOS constraints make that model unreliable for post-deploy dynamic plugin code. A viable target model needs a portable plugin runtime that does not require shipping new native/.NET executable artifacts after app release.

## Target architecture

Use a hybrid plugin runtime model behind one host contract:

1) Internal host lane (on-device)
   - Supports iOS, Android, desktop.
   - Executes post-deploy plugins via a constrained interpreter/runtime lane (WASM).
   - Enforces capabilities, deadlines, budgets, and allowlists in host policy.

2) External host lane (remote)
   - Supports all targets including iOS.
   - Runs full C# plugins remotely behind existing gRPC contracts.
   - Provides centralized rollout, revocation, and operations.

3) Existing local process lane (desktop/server)
   - Kept where process isolation is available and beneficial.

## Similar target model for C# plugins

`remote-csharp` and `local-process-csharp` should be treated as similar targets with the same plugin contract surface.

- Shared implementation: plugin business logic, contracts, and capability declarations are the same.
- Shared packaging intent: one plugin identity/version can be deployed to either lane.
- Main differences are operational: host location, startup mechanics, sandbox boundary, and network latency.
- Runtime routing selects host lane at activation time; plugin behavior remains contract-compatible.

## Non-goals

- Rewriting core runtime or API host out of C#.
- Forcing all plugins to WASM.
- Removing remote host support.

## Design principles

- Single manifest schema, multiple runtimes (`wasm`, `remote-csharp`, optional `local-process-csharp`).
- Runtime entrypoints are host-autoresolved from trusted package layout/registry (not caller-provided in manifest).
- Single capability policy engine enforced by host, independent of plugin runtime.
- Stable app-facing contracts (search/paged/video) independent of execution lane.
- Fail closed on signature, runtime mismatch, or policy violations.

Decision record:
- ADR-0001: `adr-0001-hybrid-plugin-runtime-for-ios-post-deploy-pluggability.md`

## Phased migration plan

### Phase 0 - Alignment and guardrails

Status: Completed (2026-02-28)

Steps:
- Freeze runtime assumptions in docs (iOS supports internal + remote modes).
- Define policy for post-deploy code: iOS internal dynamic plugins use WASM lane.
- Add architecture decision record summarizing why hybrid runtime is required.

Acceptance criteria:
- Docs consistently describe iOS as internal-capable and remote-capable.
- Team agrees on runtime matrix by platform.

### Phase 1 - Manifest and resolver evolution

Steps:
- Extend plugin manifest with runtime metadata:
  - `runtime.kind`: `wasm | csharp`
  - `runtime.minHostVersion` for compatibility gates.
- Update plugin resolution service to route by runtime kind and auto-resolve runtime entrypoints from trusted host rules.
- Keep backward compatibility by defaulting legacy manifests to existing behavior.

Acceptance criteria:
- Host resolves each plugin to a deterministic runtime lane.
- Legacy manifests continue to load without breakage.

### Phase 2 - Internal WASM runtime host (minimum ABI)

Steps:
- Embed a WASM runtime in PluginHost internal mode.
- Define minimal ABI and bridge calls for:
  - Health and capabilities.
  - Search (`query -> media summaries`).
  - Paged retrieval (`chapters`, `page by index`).
- Route outbound HTTP, filesystem, and cache access through host-provided adapters only.

Acceptance criteria:
- A sample WASM plugin can pass handshake + search + paged flows on iOS simulator/device.
- No direct plugin network/file access bypassing host adapters.

### Phase 3 - Security and trust hardening

Steps:
- Sign plugin packages and validate signatures before activation.
- Add per-plugin integrity hash checks for runtime payloads.
- Enforce capability policies for both WASM and remote lanes:
  - Domain allowlist.
  - Storage scope.
  - CPU/memory/time budgets.
- Add quarantine and backoff behavior parity across lanes.

Acceptance criteria:
- Unsigned/tampered plugins fail activation.
- Policy violations produce deterministic host-side denial and audit logs.

### Phase 4 - Developer toolchain and packaging

Steps:
- Create plugin packaging format that can carry either WASM module or remote descriptor.
- Add build scripts/templates for WASM plugin authors.
- Update plugin validation CLI to check ABI compatibility and manifest/runtime consistency.

Acceptance criteria:
- Plugin authors can build, package, validate, and load a WASM plugin locally.
- CI can reject invalid plugin artifacts before distribution.

### Phase 5 - Runtime routing and fallback strategy

Steps:
- Add runtime selection policy:
  - Prefer internal WASM on iOS when available.
  - Fall back to remote when plugin is remote-only or local runtime unsupported.
- Add explicit errors (no silent empty-result fallbacks).
- Expose runtime lane and failure reason in diagnostics endpoints.

Acceptance criteria:
- Runtime choice is visible and explainable per request.
- Offline behavior is deterministic and user-facing errors are actionable.

### Phase 6 - Test matrix and rollout

Steps:
- Add parity tests for each plugin capability across lanes:
  - `internal wasm` vs `external remote-csharp`.
- Add chaos tests for timeout, crash, malformed responses, and policy violations.
- Roll out in stages:
  - Dev builds -> internal beta -> partial production cohort -> full rollout.

Acceptance criteria:
- Parity SLO met for search/paged correctness.
- No critical policy bypass findings in security review.

## Platform/runtime matrix

- iOS
  - Internal host: supported.
  - Post-deploy dynamic plugin execution: WASM lane.
  - Remote host: supported.

- Android
  - Internal host: supported.
  - WASM lane: supported.
  - Remote host: supported.

- Desktop/server
  - Internal process lane: supported.
  - WASM lane: optional.
  - Remote host: supported.

## Contract and schema changes (minimum)

Manifest additions (outline):

```json
{
  "runtime": {
    "kind": "wasm",
    "minHostVersion": "1.2.0"
  }
}
```

Host requirements:
- Reject unknown `runtime.kind`.
- Verify `minHostVersion` before activation.
- Auto-resolve runtime entrypoint from package/registry metadata (ignore/reject manifest-specified entrypoints).
- Resolve runtime lane before handshake.

## Operational concerns

- Telemetry: tag every request with runtime lane (`wasm`, `remote-csharp`, `local-process-csharp`).
- Incident handling: quarantine state must include runtime lane + policy reason.
- Rollback: remote lane acts as immediate operational fallback for failed WASM deployments.

## Risks and mitigations

1) ABI drift between host and WASM plugins
- Mitigation: versioned ABI + compatibility tests in CI.

2) Performance regressions in interpreted lane
- Mitigation: budget instrumentation + hotspot profiling + optional remote routing.

3) Policy inconsistencies across lanes
- Mitigation: single host-side policy engine + shared conformance tests.

4) Tooling friction for plugin authors
- Mitigation: starter template + package validator + reference plugin.

## Immediate next actions (2-3 sprints)

Sprint 1:
- Implement manifest `runtime` fields and resolver routing.
- Add diagnostics output for selected runtime lane.

Sprint 2:
- Deliver minimal internal WASM host with search + paged ABI.
- Port `EMMA.TestPlugin` behavior into a WASM reference plugin.

Sprint 3:
- Add signing + integrity verification for runtime payloads.
- Execute iOS device parity tests (`internal wasm` vs `external remote-csharp`).
