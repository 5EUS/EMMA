# Plugin SDK v0.7.0 Canonical Author Workflow

This document defines the single supported plugin-author path for EMMA Plugin
SDK `v0.7.0`.

It is intentionally opinionated. The goal for `v0.7.0` is not to describe every
possible repository shape or bespoke script combination. The goal is to freeze
one workflow that plugin authors can follow from scaffold to packaged artifact
without depending on local EMMA source-only assumptions.

For the release contract behind this workflow, see
`plugin-sdk-v0.7.0-release-contract.md`.

## Supported golden path

The canonical `v0.7.0` plugin-author workflow is:

1. Scaffold from `templates/plugin/`.
2. Implement provider logic in the transport-agnostic core project.
3. Use the normalized `plugin.dev.json` + `EMMA.Cli` workflow for build,
   scenario validation, session inspection, and optional local UI/watch flows.
4. Validate package-consumer mode explicitly with `UseLocalEmmaSdk=false`.
5. Package with the shared pack/sign/validate scripts.

Anything outside this path may still work, but it is not the canonical release
story for `v0.7.0`.

## Repository shape

The stable scaffold shape is the split-transport repository used by the template
and sample plugin:

- `*.Core.csproj`: provider logic, mapping, and reusable domain code.
- `*.csproj`: ASP.NET transport host.
- `*.Wasm.csproj`: WASM transport host.
- `plugin.dev.sample.json`: canonical local development config.
- `scripts/build-pack-plugin.sh`: canonical WASM pack flow.
- `scripts/build-pack-plugin-aspnet.sh`: canonical native ASP.NET pack flow.

Provider-specific behavior should stay in the plugin repository. Standard
transport/bootstrap glue should stay in SDK abstractions.

## Step 1: Scaffold a plugin

Start from the template repository in `EMMA/templates/plugin`.

Immediately customize:

1. `*.plugin.json`: plugin id, name, version, permissions, and budgets.
2. `plugin.dev.sample.json`: local sync destinations and per-profile defaults.
3. signing metadata and CI secrets for your repository.

The template intentionally ships as a paged-first plugin that builds and returns
empty results before a provider is integrated.

## Step 2: Implement provider logic

The default implementation surface is the core project.

Expected customization points:

- Replace the stub provider client/domain logic in `Core/`.
- Keep URL construction and provider-specific mapping in one place.
- Keep ASP.NET and WASM adapters thin.
- Reuse SDK helpers such as batch metadata loaders, payload resolvers, paging
  helpers, and query enrichers instead of re-implementing boilerplate.

For `v0.7.0`, the template/sample story is intentionally paged-first. Dedicated
video-focused validation remains in `emma-video-test`.

## Step 3: Configure plugin-dev

Use `plugin.dev.sample.json` as the starting point for local development.

The expected profile model is:

- `wasm-dev` for direct WASM component execution.
- `linux-dev` and `windows-dev` for direct native-process execution when the
  local host can run the selected target.

The canonical CLI entrypoint is `EMMA.Cli` from the main EMMA repository.

Recommended commands from the plugin repository root:

```bash
dotnet run --project ../EMMA/src/EMMA.Cli/EMMA.Cli.csproj -- session
dotnet run --project ../EMMA/src/EMMA.Cli/EMMA.Cli.csproj -- doctor
```

When using a non-default profile or config file, keep it explicit:

```bash
EMMA_PLUGIN_DEV_CONFIG=plugin.dev.sample.json \
EMMA_PLUGIN_PROFILE=linux-dev \
dotnet run --project ../EMMA/src/EMMA.Cli/EMMA.Cli.csproj -- session
```

## Step 4: Build through the normalized CLI flow

Use the CLI build surface before falling back to bespoke scripts.

Examples:

```bash
EMMA_PLUGIN_DEV_CONFIG=plugin.dev.sample.json \
EMMA_PLUGIN_PROFILE=linux-dev \
dotnet run --project ../EMMA/src/EMMA.Cli/EMMA.Cli.csproj -- build

EMMA_PLUGIN_DEV_CONFIG=plugin.dev.sample.json \
EMMA_PLUGIN_PROFILE=wasm-dev \
dotnet run --project ../EMMA/src/EMMA.Cli/EMMA.Cli.csproj -- build
```

Use `build all` when you want the normalized cross-profile pass:

```bash
dotnet run --project ../EMMA/src/EMMA.Cli/EMMA.Cli.csproj -- build all
```

## Step 5: Validate scenarios

Use built-in or config-defined scenarios to validate the finalized workflow
surface.

The minimum release-readiness scenario path for `v0.7.0` is:

1. `paged-smoke`
2. one metadata-enrichment scenario
3. one secondary-result metadata scenario

Example:

```bash
EMMA_PLUGIN_DEV_CONFIG=plugin.dev.sample.json \
EMMA_PLUGIN_PROFILE=linux-dev \
dotnet run --project ../EMMA/src/EMMA.Cli/EMMA.Cli.csproj -- scenario paged-smoke naruto

EMMA_PLUGIN_DEV_CONFIG=plugin.dev.sample.json \
EMMA_PLUGIN_PROFILE=linux-dev \
dotnet run --project ../EMMA/src/EMMA.Cli/EMMA.Cli.csproj -- scenario enriched-rating-smoke naruto

EMMA_PLUGIN_DEV_CONFIG=plugin.dev.sample.json \
EMMA_PLUGIN_PROFILE=linux-dev \
dotnet run --project ../EMMA/src/EMMA.Cli/EMMA.Cli.csproj -- scenario second-result-metadata-smoke "one piece"
```

For `v0.7.0`, these scenario checks should run against package-consumer mode as
well, not only against local EMMA workspace references.

## Step 6: Validate package-consumer mode explicitly

`v0.7.0` is not considered ready if the workflow only works through a checked
out sibling `EMMA/` repository.

Before release, explicitly force package mode:

```bash
UseLocalEmmaSdk=false EMMA_SDK_VERSION=0.7.0 \
dotnet run --project ../EMMA/src/EMMA.Cli/EMMA.Cli.csproj -- build
```

This check assumes the `0.7.0` SDK packages already exist on the configured
feed, or that a local release-candidate feed is configured with the same
version. Until the `0.7.0` packages are published, this step remains a release
gate rather than a locally passable validation.

And for packaging:

```bash
UseLocalEmmaSdk=false EMMA_SDK_VERSION=0.7.0 \
./scripts/build-pack-plugin.sh ./YourPlugin.plugin.json

UseLocalEmmaSdk=false EMMA_SDK_VERSION=0.7.0 TARGETS="linux-x64" \
./scripts/build-pack-plugin-aspnet.sh ./YourPlugin.plugin.json
```

## Step 7: Package, sign, and validate

The canonical pack flow is the shared repository scripts, not ad hoc publish
commands.

Required properties of the release path:

- the source manifest is validated before packaging
- the staged manifest is validated again after signing
- delegated signature metadata is present and well-formed
- package mode can be forced explicitly

Use the WASM and ASP.NET pack scripts from the repository root:

```bash
./scripts/build-pack-plugin.sh ./YourPlugin.plugin.json
TARGETS="linux-x64" ./scripts/build-pack-plugin-aspnet.sh ./YourPlugin.plugin.json
```

## Step 8: Optional local UI/watch flow

`serve` and `watch` are supported workflow helpers, not a separate authoring
model.

Use them only after the same profile already works through `session`, `doctor`,
`build`, and `scenario`.

Examples:

```bash
EMMA_PLUGIN_DEV_CONFIG=plugin.dev.sample.json \
EMMA_PLUGIN_PROFILE=linux-dev \
dotnet run --project ../EMMA/src/EMMA.Cli/EMMA.Cli.csproj -- serve 5071

EMMA_PLUGIN_DEV_CONFIG=plugin.dev.sample.json \
EMMA_PLUGIN_PROFILE=linux-dev \
dotnet run --project ../EMMA/src/EMMA.Cli/EMMA.Cli.csproj -- watch start
```

## Release-readiness checklist

A plugin is on the canonical `v0.7.0` path when all of the following are true:

1. The scaffold still builds cleanly before provider integration.
2. The provider implementation lives in the core project rather than transport
   glue.
3. `session` and `doctor` resolve the intended profile and artifact locations.
4. `build` works for the intended transport profiles.
5. Scenario validation works through the CLI workflow.
6. Package mode is exercised explicitly with `UseLocalEmmaSdk=false`.
7. Packaging/signing uses the shared scripts and staged-manifest validation.

That is the path EMMA certifies for `v0.7.0`.