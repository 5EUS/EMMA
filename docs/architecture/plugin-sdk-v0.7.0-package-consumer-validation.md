# Plugin SDK v0.7.0 Package-Consumer Validation

This document records the package-mode validation state for EMMA Plugin SDK
`v0.7.0`.

The goal is not to restate the release contract. The goal is to capture exactly
what has been verified for package consumers, what has only been aligned in the
repository, and what remains blocked on package publication.

Use this document together with:

- `plugin-sdk-v0.7.0-author-workflow.md`
- `plugin-sdk-v0.7.0-phase-5-signoff.md`

## Validation target

For `v0.7.0`, package-consumer compatibility means all of the following are
true:

1. Template and sample repositories can switch from local project references to
   package references without changing code structure.
2. The package-reference path requests version `0.7.0` consistently.
3. The canonical workflow commands are written so they can run in package mode.
4. Any missing package-feed prerequisite is recorded as a release blocker, not
   silently ignored.

## Verified in repository

The following package-mode preconditions are now verified in-repo:

1. `EMMA/templates/plugin/EMMA.TemplatePlugin.Shared.props` defaults
   `EmmaSdkVersion` to `0.7.0`.
2. `emma-test-plugin/EMMA.TestPlugin.Shared.props` defaults
   `EmmaSdkVersion` to `0.7.0`.
3. Both template and sample use the same `UseLocalEmmaSdk` switch shape:
   sibling-workspace project references when local EMMA source exists,
   package references otherwise.
4. The canonical author workflow explicitly documents `UseLocalEmmaSdk=false`
   and `EMMA_SDK_VERSION=0.7.0` for build and pack commands.

## Executable verification status

The current package-mode status is:

- Source-backed workflow: verified and documented.
- Package-reference graph shape: verified in repository files.
- End-to-end package-consumer execution: blocked pending package publication or
  staging of the `0.7.0` SDK packages on a configured feed.

This means package-consumer compatibility is structurally prepared, but final
execution cannot be certified until the feed exposes:

- `EMMA.Plugin.Common` `0.7.0`
- `EMMA.Plugin.Common.Generators` `0.7.0`
- `EMMA.Contracts` `0.7.0`
- `EMMA.Plugin.AspNetCore` `0.7.0`

## Commands to run once packages are staged

From a plugin repository root:

```bash
UseLocalEmmaSdk=false EMMA_SDK_VERSION=0.7.0 \
dotnet run --project ../EMMA/src/EMMA.Cli/EMMA.Cli.csproj -- build

UseLocalEmmaSdk=false EMMA_SDK_VERSION=0.7.0 \
dotnet run --project ../EMMA/src/EMMA.Cli/EMMA.Cli.csproj -- scenario paged-smoke naruto

UseLocalEmmaSdk=false EMMA_SDK_VERSION=0.7.0 \
./scripts/build-pack-plugin.sh ./YourPlugin.plugin.json

UseLocalEmmaSdk=false EMMA_SDK_VERSION=0.7.0 TARGETS="linux-x64" \
./scripts/build-pack-plugin-aspnet.sh ./YourPlugin.plugin.json
```

## Current release implication

Package-consumer validation has been reduced to one external gate:

1. publish or stage the `0.7.0` SDK packages on a feed reachable from the
   sample/template repositories

Until that happens, the repository can certify readiness of the package-mode
shape, but not a successful end-to-end package-consumer execution.