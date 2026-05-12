# Plugin SDK v0.7.0 Migration Guide

This document describes how to migrate an existing plugin repository onto the
stable EMMA Plugin SDK `v0.7.0` authoring surface.

Use this guide together with:

- `plugin-sdk-v0.7.0-release-contract.md`
- `plugin-sdk-v0.7.0-author-workflow.md`
- `plugin-sdk-v0.7.0-package-consumer-validation.md`

## Who should use this guide

Use this guide when a plugin repository currently depends on one or more of the
following older shapes:

- property-switched transport builds instead of fixed transport projects
- plugin-local WASM entrypoint boilerplate that duplicates SDK behavior
- source-only EMMA project references with no explicit package-mode check
- older signing env vars or pack flows that predate delegated RSA signing

## Stable v0.7.0 target shape

The supported repository shape for `v0.7.0` is:

- `*.Core.csproj` for provider/domain logic
- `*.csproj` for the ASP.NET transport host
- `*.Wasm.csproj` for the WASM transport host
- SDK-first bootstrap via `PluginBuilder.CreateWithDefaults(...)`
- generator-backed WASM exports via `PluginWasmExportsAttribute`
- explicit package-mode validation with `UseLocalEmmaSdk=false`
- shared pack/sign/validate scripts instead of ad hoc publish commands

## Required migration steps

### 1. Normalize project structure

Move transport-specific code into fixed transport projects.

Expected end state:

- ASP.NET startup lives in `Program.cs` under the ASP.NET compile path
- WASM host glue lives in `WASM/`
- provider logic lives in `Core/`

If a repository still relies on one project with changing MSBuild properties to
select transport behavior, split it before treating the SDK surface as stable.

### 2. Move ASP.NET startup onto SDK defaults

Use `PluginBuilder.CreateWithDefaults(...)` and
`PluginBuilder.ConfigureDefaultControl(...)` instead of open-coding manifest
loading, host-port rules, or capability defaults.

The default path should own:

- manifest-derived CPU and memory budgets
- domain and path permissions
- default host port resolution
- the default control service registration

### 3. Move WASM entrypoints onto the generator-backed path

Use `PluginWasmExportsAttribute` on the plugin `Program` type and let the
generator own the standard export surface and standard JSON context types.

Plugin repositories should only keep explicit extra serializable types that are
not already implied by the standard operation signatures.

### 4. Choose the right WASM host abstraction

For paged-media plugins, use:

- `PluginBasicPagedWasmOperationHost<TChapterOperationItem>`

For standardized paged + video plugins, use:

- `PluginBasicPagedVideoWasmOperationHost<TChapterOperationItem>`

For `v0.7.0`, the reference sample/template remain intentionally paged-first,
and the dedicated video reference path lives in `emma-video-test`.

### 5. Normalize package-mode references

Keep package mode explicit in shared props:

- `EmmaSdkVersion` defaults to `0.7.0`
- `UseLocalEmmaSdk` resolves to `true` only when a sibling EMMA workspace is
  actually present
- package references are used whenever `UseLocalEmmaSdk != true`

The release contract treats package mode as part of the supported authoring
surface, not as an optional afterthought.

### 6. Normalize plugin-dev configuration

The canonical profile model is:

- `wasm-dev`
- `linux-dev`
- `windows-dev`

`plugin.dev.json` is the auto-discovered local config. `plugin.dev.sample.json`
is the committed baseline when a repository wants the config to remain opt-in.

The stable workflow is driven through `EMMA.Cli` using `session`, `doctor`,
`build`, `scenario`, `serve`, and `watch`.

### 7. Move packaging and signing onto the delegated RSA flow

Use the shared pack scripts and delegated RSA signing variables:

- `EMMA_PLUGIN_SIGNING_KEY_ID`
- `EMMA_PLUGIN_REPOSITORY_ID`
- `EMMA_PLUGIN_SIGNING_PRIVATE_KEY_BASE64` or
  `EMMA_PLUGIN_SIGNING_PRIVATE_KEY_PEM`

The old `EMMA_HMAC_KEY_BASE64` alias may still exist for compatibility, but the
documented `v0.7.0` path is delegated RSA signing.

## Reference repository roles in v0.7.0

- `EMMA/templates/plugin`: canonical scaffold and empty-result paged baseline
- `emma-test-plugin`: canonical paged sample with enrichment/scenario coverage
- `emma-video-test`: dedicated video validation path and reusable video-host
  proof path

Do not mix these roles when describing the supported `v0.7.0` story.

## Migration completion checklist

A repository is migrated onto the `v0.7.0` surface when all of the following
are true:

1. The repository uses fixed transport projects and a shared core project.
2. ASP.NET startup uses the SDK default bootstrap path.
3. WASM exports use `PluginWasmExportsAttribute`.
4. The repository declares or documents whether it is paged-first or paged +
   video.
5. Package mode can be forced with `UseLocalEmmaSdk=false`.
6. Packaging uses the shared pack/sign/validate scripts.
7. The repository follows the canonical `EMMA.Cli` workflow.