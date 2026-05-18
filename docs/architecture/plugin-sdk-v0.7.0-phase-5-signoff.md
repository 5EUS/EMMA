# Plugin SDK v0.7.0 Phase 5 Sign-Off

This document completes Phase 5 for the EMMA Plugin SDK `v0.7.0` work.

Phase 5 converts the implementation work into a release candidate by freezing
the canonical author workflow, running a release-readiness pass, and locking the
final deferred-items list.

For the release contract, see `plugin-sdk-v0.7.0-release-contract.md`. For the
canonical plugin-author path, see `plugin-sdk-v0.7.0-author-workflow.md`.
For migration and package-mode validation artifacts, see
`plugin-sdk-v0.7.0-migration-guide.md` and
`plugin-sdk-v0.7.0-package-consumer-validation.md`.

## Phase 5 decisions

The following decisions are now locked for `v0.7.0`:

1. The canonical plugin-author workflow is the scaffold -> core implementation
   -> CLI build/scenario validation -> explicit package-mode validation ->
   shared packaging/signing flow described in
   `plugin-sdk-v0.7.0-author-workflow.md`.
2. The template and sample plugin are part of the release surface and must tell
   the same story as the SDK docs.
3. Package-consumer mode is a release gate, not an optional extra check.
4. Remaining gaps are only acceptable if they are explicit deferred items or
   explicit readiness blockers.

## Release-readiness pass

Phase 5 readiness was evaluated across the following surfaces:

1. template documentation and scaffold shape
2. sample plugin documentation and pack/sign flow
3. package-mode validation path
4. CLI scenario path
5. shared tests and diagnostics

### Readiness findings

At Phase 5 close, the release candidate has the following status:

1. The repository now contains one canonical plugin-author workflow document.
2. The template and sample plugin both describe the same paged-first,
   package-consumable authoring model.
3. The migration path from the earlier sample/template shape is explicitly
   documented for package consumers and source-workspace users.
4. Phase 4 package/sign validation hardening remains in place and is part of
   the certified workflow.
5. The local source-backed readiness path has executable evidence:
   the sample WASM build completed, the Linux direct build completed, the
   metadata-enrichment scenario passed, and the WASM pack/sign/validate flow
   completed with `WASI_SDK_PATH` configured.
6. Explicit package-consumer mode remains a release gate because the shared
   sample/template props now correctly request `EMMA` packages at version
   `0.7.0`, but the configured feeds only expose `0.6.8` today. This is the
   expected result until the `0.7.0` packages are published or staged on a
   release-candidate feed.

### What Phase 5 certifies now

Phase 5 certifies the documented release candidate shape:

1. The supported author path is explicit and repository-backed.
2. The package-consumer requirement is explicit and no longer implied.
3. The remaining non-goals are explicit.
4. Any unresolved issues found during readiness are called out as blockers
   rather than being silently omitted from the release story.

## Active release gates

The following item remains an explicit release gate at Phase 5 close:

1. Publish or stage the `0.7.0` SDK packages (`EMMA.Plugin.Common`,
   `EMMA.Plugin.Common.Generators`, `EMMA.Contracts`,
   `EMMA.Plugin.AspNetCore`) so the documented package-consumer workflow can be
   executed end to end with `UseLocalEmmaSdk=false`.

## Final deferred-items list

The following items are explicitly deferred from `v0.7.0` and are not accidental
omissions:

1. A single fully declarative plugin-definition object that replaces remaining
   transport composition.
2. Typed WIT export generation beyond the current standard export surface.
3. Rich browser-first plugin development UI beyond the session/tooling support
   needed for the canonical workflow.
4. Broad audio-specific parity work beyond preserving the architecture needed
   for later support.
5. Solving every future packaging/tooling abstraction in one release when the
   stable current path is already documented and validated.
6. Dedicated reproducible-archive determinism assertions for final package zip
   outputs.
7. A fully uniform error-contract rewrite across every remaining CLI exception
   and diagnostic path.

## Phase 5 completion checklist

Phase 5 is complete when all of the following are true:

1. The canonical author workflow is written down in the repository.
2. The release-readiness pass is recorded.
3. The final deferred-items list is frozen.
4. Any unresolved release issues are recorded as blockers instead of being left
   implicit.

This document is the Phase 5 output.