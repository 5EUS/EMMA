# Plugin SDK Authoring Surface

This document describes the current authoring surface for simple EMMA plugins
after the first round of SDK extraction work.

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

Use `PluginWasmHostBuilderPresets` to register standard CLI operations instead
of repeating the same `AddCliJson(...)` and `AddCliHandler(...)` calls in every
plugin host.

The current presets cover:

- standard health operations
- standard paged-media CLI operations

Plugins still own:

- invoke dispatcher policy
- custom operations such as benchmark or diagnostics
- media-type specific mapping logic

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

The sample project still carries compatibility shims for non-local SDK use so
the repository can transition incrementally while package publishing catches up.

## What remains out of scope

- typed WIT export generation
- packaging the source generator into the published SDK package
- a single declarative plugin-definition object that replaces all remaining
  transport composition
- richer preset coverage for video/audio-specific WASM hosts

Those are the next logical steps once the current surface has been exercised
across more than one sample plugin.