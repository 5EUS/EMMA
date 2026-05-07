# Milestone 7 - Plugin developer experience

## Scope

- Make plugin development fast and consistent across WASM, Linux, and Windows
  targets.
- Build on the existing `EMMA.Cli` interactive flow instead of replacing it.
- Bring plugin build scripts and packaging utilities under the same developer
  experience plan so running, building, and packing stop feeling like separate
  systems.
- Introduce a stable development-session architecture that can back CLI,
  browser UI, and future editor integrations.
- Cover the common development cases first while keeping the model extensible
  for future plugin types, media types, and execution environments.

## Current Status (2026-05-07)

Planned. `EMMA.Cli` already provides an interactive command loop over embedded
API calls, which makes it a good starting point for a broader plugin
developer-experience milestone. What is missing is a stable session model,
runtime abstraction, discovery/config flow, diagnostics, and a UI-ready local
API.

## Goals

1) Broad usability
- Support the most common plugin workflows with minimal setup.
- Work when developers point at a project, a build output directory, or a
  packaged plugin artifact.
- Prefer discovery and profiles over long command lines and environment-only
  setup.
- Make build, run, package, and smoke-test flows feel like one toolchain
  instead of a loose collection of scripts.

2) Stable architecture
- Keep CLI, web UI, and future tooling clients on top of the same session
  orchestration layer.
- Model runtime differences as adapter capabilities instead of scattering
  target-specific logic across commands.
- Separate development-time configuration from production plugin manifests.

3) Expandability
- Keep the design ready for future media types beyond paged and video.
- Keep the runtime surface ready for future execution modes such as container,
  remote, or mobile-hosted plugin testing.
- Provide explicit extension points for richer diagnostics, scenario runners,
  and UI panels.

## Non-goals

- Replacing the existing plugin host production lifecycle with a separate
  parallel runtime model.
- Building a large custom IDE before the session orchestration and local API
  exist.
- Solving every packaging and deployment concern inside the developer tool.

## Target user journeys

1) Local project development
- Developer selects a plugin project and runs a default development profile.
- The tool discovers the best runnable target, validates prerequisites, starts
  a session, and streams logs.

2) Artifact validation
- Developer points at existing WASM or native build outputs.
- The tool validates compatibility, starts the correct runtime adapter, and
  runs sample scenarios.

3) Packaged plugin smoke test
- Developer points at a packaged plugin bundle before publication.
- The tool verifies manifest/config consistency and runs smoke scenarios.

4) Host-bridge validation
- Developer runs a plugin against mock host services first, then re-runs the
  same scenarios against a real host-bridge mode when needed.

5) Cross-platform build preparation
- Developer asks for a WASM or native-target development run.
- The tool resolves the required build path, detects whether local toolchains
  are sufficient, and falls back to documented alternatives such as Docker on
  macOS when LLVM-backed native code generation is required.

## Architecture direction

The north star is a reusable local plugin development platform with multiple
clients:

- `EMMA.Cli` becomes a thin client over a session application layer.
- A local session API serves the same operations for CLI and future web UI.
- Runtime adapters isolate WASM/Linux/Windows execution differences.
- Host services are exposed through capability-driven contracts.

This milestone should not produce a CLI-only solution that has to be rewritten
once a browser or editor surface is added.

## Core model

### Plugin development session

Introduce a `PluginDevSession` application model with at least:

- plugin identity and source location
- selected profile
- runtime target and execution mode
- resolved artifact or entrypoint
- host mode: mock or bridge
- capability requirements and resolved capability set
- lifecycle state: discovered, prepared, starting, running, reloading,
  stopped, failed
- diagnostics, logs, and scenario results

This is the primary stability boundary for the milestone.

### Configuration split

Use two configuration layers:

- Production plugin manifest
  - identity, capabilities, permissions, signing, packaging metadata
- Development session config such as `plugin.dev.json`
  - profiles, artifact hints, watch globs, scenario defaults, local-only
    overrides, and future UI hints

The development tool must still work with inference when the development config
is absent.

### Profiles

Add named execution profiles so developers can run common cases without
remembering runtime-specific flags:

- `wasm-dev`
- `linux-dev`
- `windows-dev`
- `packaged-smoke`
- `host-bridge`

Each profile resolves runtime, artifact source, host mode, watch behavior, and
scenario defaults.

Profiles should also be able to resolve the build strategy used to produce the
artifacts they run, including script/tool entrypoints, packaging mode, and
required environment prerequisites.

## Work items

1) Session orchestration
- Extract a reusable application layer from the current CLI flow.
- Move session lifecycle, state, logs, and diagnostics into shared services.
- Keep the current interactive result loop as one client of that layer.

2) Discovery and resolution
- Discover plugins from project roots, manifests, or packaged artifacts.
- Detect likely runtime targets from project outputs and metadata.
- Infer runnable profiles and expose a `doctor` path when inference fails.

3) Runtime adapters
- Add a WASM runtime adapter for component-style plugin execution.
- Add Linux and Windows native runtime adapters for local process execution.
- Keep adapter contracts consistent for start, stop, reload, invoke, and log
  streaming.

4) Host capability layer
- Define shared development-host contracts for logging, storage, HTTP,
  lifecycle, diagnostics, and media operations.
- Support both mock-host mode and real host-bridge mode through the same
  capability model.
- Keep the design ready for future audio/text/video capability expansion.

5) Scenario engine
- Add reusable named scenarios for common plugin operations such as search,
  chapters, pages, stream discovery, and custom payload-driven invocations.
- Allow scenarios to run from CLI, future UI, and CI using the same session
  APIs.

6) Watch and reload
- Add artifact and config watching with clear runtime-specific behavior.
- Support in-process refresh when possible and restart-on-change when not.
- Expose reload capabilities explicitly instead of promising generic hot reload
  on every target.

7) Diagnostics and doctor flow
- Detect missing SDKs, incompatible artifacts, capability mismatches, invalid
  config, and unsupported profile selections.
- Detect when a target requires a containerized build path instead of a native
  local build path.
- Make diagnostics available before launch and during session execution.

8) Build and packaging tooling normalization
- Replace janky one-off plugin scripts with a smaller set of shared,
  composable build/pack entrypoints that the CLI can invoke consistently.
- Standardize profile-to-build resolution so `run`, `watch`, `pack`, and smoke
  test commands all use the same build metadata.
- Support both direct local builds and delegated/containerized builds without
  changing the higher-level workflow.

9) Local session API
- Expose session creation, lifecycle operations, logs, diagnostics, scenario
  execution, and result inspection through a local API.
- Use this API as the sole backend for future browser UI work.

10) Web UI foundation
- Add a minimal browser UI after the session API exists.
- Cover status, logs, profile selection, scenario execution, and diagnostics
  first.
- Keep richer panel/plugin UI experiments out of the first implementation.

11) CI smoke coverage
- Reuse the scenario engine to run per-target smoke checks in CI.
- Validate packaged and unpacked flows where practical.

## Milestone phases

### Phase 1 - Core session model
- Define `PluginDevSession`, session state transitions, config loading, and
  profile resolution.
- Refactor `EMMA.Cli` to use session services without breaking the current
  interactive workflow.

### Phase 2 - Discovery and diagnostics
- Implement plugin/project/artifact discovery.
- Add doctor-style validation before runtime launch.
- Add inferred default profiles for the common targets.
- Start surfacing build prerequisites and script resolution in diagnostics so
  the tool can explain how a profile is expected to produce runnable artifacts.

### Phase 3 - WASM first end-to-end
- Ship the WASM runtime adapter first.
- Validate against the existing WASM-oriented test plugin flow.
- Wire scenarios, logs, and reload semantics for the WASM path.
- Introduce the first normalized build/pack invocation path for the WASM
  profile so the CLI is not relying on bespoke script knowledge.

### Phase 4 - Native parity
- Add Linux native runtime execution.
- Add Windows native runtime execution.
- Normalize diagnostics and lifecycle behavior across all three runtime types.
- Define explicit platform fallbacks where native local builds are not practical
  or supported.

### Phase 5 - Session API and UI foundation
- Stand up the local session API.
- Keep CLI on the same backend.
- Add a lightweight browser UI for logs, profile selection, and scenario
  execution.

### Phase 6 - CI and operational polish
- Add smoke scenarios to CI.
- Improve reliability of watch/reload and diagnostics.
- Expand documentation and sample configurations.
- Consolidate remaining build/package scripts behind shared tooling entrypoints
  where possible.

## Deliverables

- A reusable plugin development session application layer.
- A CLI flow that can run plugins across WASM, Linux, and Windows through
  profiles.
- A `plugin.dev.json`-style configuration model with inference fallback.
- A normalized build/profile mapping that lets the CLI resolve how a plugin is
  built, packed, and launched.
- Shared runtime adapter contracts.
- Shared host capability contracts for mock and bridge modes.
- Scenario definitions usable by CLI, UI, and CI.
- A local session API that future UI work can consume.

## Validation status

- Open: define the session model and profile resolution contract.
- Open: validate discovery against existing plugin repositories.
- Open: validate WASM, Linux, and Windows runner parity against test plugins.
- Open: validate that build and pack workflows can be invoked through shared
  profile metadata instead of repository-specific script knowledge.
- Open: add CI smoke checks based on declarative scenarios.

## Dependencies

- Existing `EMMA.Cli` interactive command flow.
- Embedded runtime and plugin host APIs already used by the CLI.
- Plugin host lifecycle and sandboxing work from Milestones 2 and 6.
- Current paged and video plugin contracts.
- Existing plugin build/pack scripts in the plugin repositories until shared
  tooling replaces or wraps them.
- Docker availability on macOS for workflows that require
  `-p:NativeCodeGen=llvm`, because those LLVM-backed native codegen paths are
  currently not something the plan should pretend can always run locally.

## Open questions

- Should the local session API live inside `EMMA.Cli`, `EMMA.PluginHost`, or a
  new tooling-focused project?
- How much of the session API should be reusable in automated integration
  tests?
- Which parts of development config should remain inference-only versus being
  made explicit in `plugin.dev.json`?
- How far should the first implementation go in wrapping or replacing existing
  shell scripts versus standardizing around them temporarily?
- Should build strategy be declared directly in profiles or inferred from the
  target and repository layout first?
- For macOS, do we want Docker to remain the canonical fallback for
  `-p:NativeCodeGen=llvm` builds, or do we want to explicitly scope those
  profiles as container-backed from the start?
- What is the minimum useful browser UI for the first release?

## Status

Planned.