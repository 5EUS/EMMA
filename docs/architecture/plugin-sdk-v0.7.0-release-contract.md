# Plugin SDK v0.7.0 Release Contract

This document freezes the intended contract for EMMA Plugin SDK `v0.7.0`.

The goal of `v0.7.0` is to make the SDK close to production-worthy by
stabilizing the structure, core abstractions, transport expectations, and the
plugin-author workflow used by the EMMA ecosystem.

This is a contract document, not an aspirational wishlist. Anything described
here as required should be treated as a release gate for `v0.7.0` unless it is
explicitly moved into the deferred list.

For the current-state baseline and Phase 1 sign-off against this contract, see:

- `plugin-sdk-v0.7.0-phase-1-signoff.md`
- `plugin-sdk-v0.7.0-transport-parity-audit.md`

## Release objective

`v0.7.0` should be the release that finalizes the current generation of EMMA
plugin authoring:

- the SDK project structure is stable enough for package consumers
- the transport abstraction boundaries are stable enough for templates and docs
- the common plugin-author workflow is stable enough to document as the
  canonical path
- plugin authors can rely on one coherent mental model instead of transport-
  specific exceptions for common operations

## Production-worthy standard for this release

For `v0.7.0`, "close to production-worthy" means all of the following are true:

1. A plugin author can scaffold, implement, run, inspect, package, and validate
   a plugin using documented SDK-first workflows.
2. The public SDK surface used by package consumers matches the surface used by
   the EMMA workspace samples closely enough that there are no source-only
   authoring assumptions.
3. WASM and ASP.NET expose the same practical feature set whenever the host and
   runtime model make parity feasible.
4. Transport differences are implementation details, not authoring-model forks,
   for the standard paged and video plugin flows.
5. Templates, sample plugins, plugin-dev tooling, and packaging guidance all
   describe the same supported workflow.

## Core release rules

### 1. Transport parity rule

WASM should have total parity with ASP.NET whenever practical, and ASP.NET
should not lag WASM-only features either.

For `v0.7.0`, parity is evaluated by plugin-author experience end to end, not
only by whether low-level runtime primitives exist somewhere in the codebase.

### 2. Stable authoring model rule

The supported way to build a plugin should be understandable as one model:

- provider logic stays in plugin code
- standard transport/bootstrap glue lives in SDK abstractions
- templates, docs, and tooling all point to the same abstractions

### 3. Package-consumer rule

An SDK surface is not considered finalized until it works when consumed through
published packages, not only through local project references.

### 4. Workflow-completeness rule

The SDK is not ready if a feature works in a host/runtime path but remains
unsupported or undocumented in the default plugin-dev workflow.

## Required v0.7.0 support matrix

The matrix below defines what `v0.7.0` is expected to support at release time.

| Area | ASP.NET target | WASM target | Release expectation |
| --- | --- | --- | --- |
| Bootstrap defaults | First-class | First-class | Stable, documented, package-consumable |
| Handshake/capabilities | Required | Required | Same practical behavior |
| Search | Required | Required | Same authoring model |
| Search suggestions | Required | Required | Same authoring model |
| Search metadata enrichment | Required | Required | Same authoring model |
| Chapters/page/pages | Required | Required | Same authoring model |
| Video streams/segment | Required | Required when practical | No manual-only WASM path for standard video plugins |
| Manifest/resource defaults | Required | Required | Derived from one coherent rule set |
| Plugin-dev runtime inspection | Required | Required | Common workflow across supported transports |
| Template support | Required | Required | Generated projects reflect supported path |
| Package-mode consumption | Required | Required | Verified outside local workspace references |
| Packaging/signing guidance | Required | Required | Deterministic and documented |

## In scope for v0.7.0

The following are in scope and should be treated as release-gating work:

1. Freeze the public SDK structure used by plugin authors.
2. Finish transport parity for the standard paged and video authoring flows.
3. Finish the plugin-dev workflow for the finalized transport/media surfaces.
4. Normalize capability declarations, manifest defaults, and operation-routing
   behavior so transports cannot drift semantically.
5. Remove or absorb transitional compatibility shims that would otherwise keep
   the sample/template path different from the package-consumer path.
6. Finalize the template and sample plugin as the reference path for the
   release.
7. Add enough regression/scenario coverage to support the contract claims.
8. Publish migration and author guidance for the new stable shape.

## Explicitly deferred from v0.7.0

The following items are not required to ship `v0.7.0` unless later promoted:

1. A single fully declarative plugin-definition object that replaces all
   transport composition.
2. Typed WIT export generation beyond the current standard export surface.
3. Rich browser-first plugin development UI beyond what is needed to keep the
   shared session/tooling architecture viable.
4. Broad audio-specific parity work beyond preserving the architecture needed to
   support it later.
5. Solving every future packaging/tooling abstraction in one release if the
   stable current path can already be documented and validated.

Deferred does not mean ignored. It means these items must not block the release
contract defined here.

## Known release-critical gaps at Phase 1 close

These are the major gaps currently visible against the contract:

1. ASP.NET has a higher-level default-provider path for video than the current
   standardized WASM authoring path.
2. Plugin-dev still has transport-specific unsupported paths for some video
   inspection flows.
3. Template and sample paths still reflect transitional asymmetry rather than a
   fully frozen parity-first story.
4. Package-consumer validation still needs to prove that the finalized public
   surface works without local workspace references or shims.

WASM video itself is not absent from the codebase. The dedicated
`emma-video-test` plugin proves that WASM video operations can work, and the
current known-good validation set includes Linux, macOS, and iOS. The current
understanding is that Windows is not working yet and Android is still
unconfirmed. The remaining release gap is that this capability is not yet
expressed as the same first-class, standardized SDK/default path that ASP.NET
currently has.

## Critical path after Phase 1

The remaining work should be executed in this order:

1. Close parity-breaking abstraction gaps.
2. Close plugin-dev workflow gaps for finalized features.
3. Align template/sample/plugin package-consumer behavior with the finalized
   abstractions.
4. Harden validation, packaging, and signing around the stabilized public
   surface.
5. Publish migration/docs and run the release-readiness pass.

In practical terms, this means the critical path is:

1. WASM video host/default-provider abstraction.
2. Plugin-dev video parity.
3. Capability/default normalization.
4. Template and sample-plugin alignment.
5. Package-consumer validation.

## Phase 1 completion criteria

Phase 1 is complete when all of the following are true:

1. There is one written definition of the `v0.7.0` SDK contract.
2. The required support matrix is explicit.
3. The deferred list is explicit.
4. The critical path is explicit.
5. The current-state parity baseline is captured in a detailed audit document.
6. Subsequent implementation work can be judged against this contract instead
   of relying on chat context or implied goals.

This document is the Phase 1 output.