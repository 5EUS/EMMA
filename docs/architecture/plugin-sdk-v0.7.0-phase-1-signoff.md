# Plugin SDK v0.7.0 Phase 1 Sign-Off

This document completes Phase 1 for the EMMA Plugin SDK `v0.7.0` work.

Phase 1 was defined as the point where the SDK release contract, current-state
assessment, required support matrix, deferred list, and critical path are all
written down in the repository so later work can be judged against a stable
baseline.

Use `plugin-sdk-v0.7.0-release-contract.md` as the target contract, and use
`plugin-sdk-v0.7.0-transport-parity-audit.md` as the detailed transport
baseline behind this sign-off.

## Phase 1 decisions

The following decisions are now locked unless explicitly revised:

1. `v0.7.0` is the release that stabilizes the current generation of EMMA SDK
   project structure, core abstractions, and plugin-author workflow.
2. WASM and ASP.NET should have total parity whenever practical, and parity is
   judged by plugin-author experience rather than by low-level runtime
   primitives alone.
3. The SDK is not considered ready if a feature exists in runtime/host code but
   is missing from the standard plugin-dev workflow or package-consumer path.
4. Templates and sample plugins are part of the release surface for this
   version, not optional examples that may lag behind the SDK.
5. `v0.7.0` may defer broader future abstractions, but it may not defer the
   standard paged and video authoring workflow if those features are treated as
   first-class in the SDK story.

## Current-state parity baseline

The detailed parity matrix is now maintained in
`plugin-sdk-v0.7.0-transport-parity-audit.md`.

## What Phase 1 proves

Phase 1 establishes the following as facts about the current codebase:

1. The SDK already contains meaningful authoring-surface reduction for ASP.NET
   bootstrap, standard WASM exports, and shared helper logic.
2. Paged-media authoring is much closer to stable parity than video authoring.
3. The remaining transport asymmetry is no longer primarily about missing
   runtime primitives; it is about missing high-level abstractions and missing
   workflow completion.
4. The sample plugin and template are good enough to validate the direction of
   the SDK, but they still expose the remaining asymmetry clearly.

## Release-gating gaps confirmed at Phase 1 close

The following gaps are confirmed release gates for `v0.7.0`.

### 1. WASM video authoring is not yet first-class

The sample ASP.NET transport registers `IPluginVideoRuntime` and wires a
default video provider path, while the WASM sample host is still based on
`PluginBasicPagedWasmOperationHost<T>` with `PluginCapabilityProfile.PagedOnly`.
At the same time, the shared SDK already contains `PluginWasmVideoOperationScaffold`
and `PluginCapabilityProfile.PagedAndVideo`.

Separately, the dedicated `emma-video-test` plugin demonstrates that WASM video
operations themselves do work when wired manually through a custom WASM host and
typed exports. The current known-good platform set for that path is Linux,
macOS, and iOS. Windows is currently not working, and Android remains
unconfirmed.

Conclusion: the low-level pieces exist, but the first-class authoring surface
does not yet match ASP.NET.

### 2. Plugin-dev does not yet provide complete video parity

`PluginDevRuntimeAdapter` defines video inspection methods and some runtime
adapters implement them, but the host-bridge path still throws explicit
`NotSupportedException` messages for video stream and segment inspection.

Conclusion: plugin-dev still falls short of the workflow-completeness rule.

### 3. Template and sample still reflect transitional asymmetry

The template explicitly documents a paged-only default and the current WASM
template host advertises `PagedOnly` capability. That is valid only if the
release contract keeps the template intentionally paged-first. It is not valid
if the release message is that standard paged+video plugin authoring is now
equally first-class in both transports.

Conclusion: the final `v0.7.0` release message must either narrow scope or the
template/sample must be upgraded.

### 4. Package-mode validation is still a release gate

The current documentation still references transition-period compatibility
shims and package-publishing catch-up work.

Conclusion: the SDK public surface is not fully finalized until package-based
consumption proves it.

## Required follow-on work from Phase 1

The following work is now justified by documented evidence rather than only by
directional goals:

1. Use the completed transport parity audit as the baseline for implementation
   planning and code changes.
2. Design and implement the missing first-class WASM video host/default path.
3. Close plugin-dev video inspection parity.
4. Normalize capability/default declarations across transports.
5. Align the template and sample plugin with the final release story.
6. Prove package-consumer compatibility with the finalized public surface.

## Critical path ordering

The Phase 1 baseline confirms this order as the shortest valid path to
`v0.7.0` readiness:

1. WASM video host/default-provider abstraction.
2. Plugin-dev video parity.
3. Capability/default normalization.
4. Template and sample alignment.
5. Package-consumer validation.
6. Error-contract hardening, packaging/signing stabilization, and broader
   regression coverage.
7. Migration/docs and final release-readiness pass.

## Phase 1 completion checklist

Phase 1 is now considered complete because the repository contains all of the
following:

1. The release contract: `plugin-sdk-v0.7.0-release-contract.md`.
2. The current-state sign-off: this document.
3. The detailed baseline parity audit:
   `plugin-sdk-v0.7.0-transport-parity-audit.md`.
4. The required support matrix, deferred list, and critical path.
5. A written set of release-gating gaps that explains why the next work items
   exist.

This means later phases can now be evaluated against stable repository docs,
not only against chat context.