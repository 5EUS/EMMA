# Plugin SDK Authoring Surface

This document describes the current authoring surface for simple EMMA plugins
after the first round of SDK extraction work.

For the frozen `v0.7.0` release target, see
`plugin-sdk-v0.7.0-release-contract.md`. This document describes the current
surface; the release-contract document defines which parts of that surface must
be stabilized before the SDK is considered ready.

The goal is not to hide plugin behavior behind magic. The goal is to move
repeatable transport/bootstrap glue into documented framework surfaces so plugin
authors spend time in provider logic instead of assembly code.

## Design rules

- Keep provider-specific business logic in the plugin repository.
- Move transport setup, standard operation exports, and JSON registration into
  the SDK when the pattern is mechanical.
- Keep the authoring model declarative enough that examples, docs, and future
  tooling can all describe the same surface.

## Current SDK surfaces

### 1. ASP.NET bootstrap defaults

Use `PluginBuilder.CreateWithDefaults(...)` when a plugin wants the standard
ASP.NET host bootstrap path with manifest-driven budgets and permissions.

What it owns:

- manifest default loading via `PluginManifestDefaultsProvider`
- default host port and root message configuration
- default control service registration
- applying manifest CPU, memory, domain, and path defaults

What the plugin still owns:

- transport-specific service registrations
- capability labels beyond the default health/capability entries
- any plugin-specific control metadata such as the ready message

### 2. Control option configuration

Use `PluginBuilder.ConfigureDefaultControl(...)` for plugin-specific control
metadata after `CreateWithDefaults(...)` has already registered the default
control service.

Use `PluginSdkControlOptions.ApplyManifestDefaults(...)` when a plugin needs to
apply manifest budgets and permissions without open-coding domain/path loops.

## 3. WASM export generation

Apply `PluginWasmExportsAttribute` to the plugin `Program` type for the standard
WASM export surface.

The generator currently emits:

- the `OperationHost` field
- the standard `WasmDispatch` table for handshake/capabilities/search/chapters/
  page/pages/invoke
- the public export wrappers used by typed WIT bridges
- a partial `JsonSerializerContext` with `JsonSerializable` attributes for the
  standard return types plus plugin-supplied extra serializable types

This keeps the mechanical WASM entrypoint surface aligned with the actual
operation host signatures.

## 4. WASM host presets

Use `PluginBasicPagedWasmOperationHost<TChapterOperationItem>` when a plugin
needs the standard paged-media WASM path, and use
`PluginBasicPagedVideoWasmOperationHost<TChapterOperationItem>` when a plugin
needs the standard paged + video path.

These shared hosts own:

- standard health operations
- standard paged-media CLI operations
- standard paged-media invoke operations
- standard video CLI and invoke operations for the paged + video host
- capability-profile driven default capability declarations

Plugins still own:

- provider-specific payload fetching and mapping
- any custom invoke handlers beyond the standard operation surface
- custom benchmark/diagnostic behavior when the defaults are insufficient

## 5. JSON context generation

The standard WASM serializable types should come from the generator, not from a
manually curated attribute list.

Plugins only need to supply extra serializable types that are not implied by
the standard operation signatures, such as custom chapter-operation wire items
or benchmark payload types.

## 6. Payload mapper helpers

`PluginPayloadMapperBase` now includes collection-level helpers intended for the
common provider-mapper shape:

- `ParseStructArray(...)`
- `ParseObjectMetadataByKey(...)`

Use these when the plugin is translating JSON arrays or object maps into domain
records and metadata dictionaries. The helpers are intentionally small. They are
meant to remove repeated loop scaffolding, not to replace provider-specific
mapping decisions.

## Sample plugin path

`emma-test-plugin` now demonstrates the intended local authoring path:

- ASP.NET startup uses `CreateWithDefaults(...)`
- WASM exports are generator-backed when the local EMMA workspace is present
- WASM JSON context attributes are generator-backed on the same path
- payload mapping uses the new collection helpers for repeated parse loops
- the WASM host now uses the shared paged + video abstraction rather than a
  plugin-local paged-only path

`emma-video-test` now demonstrates the intended reusable video-focused WASM
path:

- the plugin uses the shared paged + video WASM host abstraction
- the plugin keeps fixture-specific search and benchmark behavior in plugin code
- the plugin no longer needs a plugin-local custom host builder just to wire the
  standard video operations

The sample project still carries compatibility shims for non-local SDK use so
the repository can transition incrementally while package publishing catches up.

## What remains out of scope

- typed WIT export generation
- packaging the source generator into the published SDK package
- a single declarative plugin-definition object that replaces all remaining
  transport composition

Those are the next logical steps once the current surface has been exercised
across more than one sample plugin, but they are not part of the `v0.7.0`
release contract unless that contract is updated explicitly.