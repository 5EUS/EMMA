# Plugin SDK v0.7.0 Transport Parity Audit

This document records the detailed transport parity baseline for the EMMA Plugin
SDK `v0.7.0` release work.

The goal is not to restate the release contract. The goal is to map the current
SDK, CLI, sample-plugin, and template behavior to that contract so parity work
can be tracked against a concrete baseline.

Use this document together with:

- `plugin-sdk-v0.7.0-release-contract.md`
- `plugin-sdk-v0.7.0-phase-1-signoff.md`

## Audit scope

This audit covers the current parity baseline across these surfaces:

1. SDK public abstractions in `EMMA.Plugin.Common` and `EMMA.Plugin.AspNetCore`
2. Plugin-dev workflow behavior in `EMMA.Cli`
3. The reference sample in `emma-test-plugin`
4. The scaffolded authoring path in `EMMA/templates/plugin`

It is intentionally focused on the plugin-author experience, not only on raw
runtime capability.

## Evaluation legend

- `Parity`: both transports expose materially equivalent authoring paths
- `Partial parity`: both transports can do the work, but the authoring model or
  abstraction level is meaningfully different
- `Gap`: one transport still lacks a first-class supported path relative to the
  release contract
- `Open gate`: parity cannot be claimed until validation outside the source
  workspace is completed

## Detailed matrix

| Surface | ASP.NET baseline | WASM baseline | Assessment | Release implication |
| --- | --- | --- | --- | --- |
| Host/bootstrap defaults | `PluginBuilder.CreateWithDefaults(...)` owns manifest-driven host defaults and control setup in practice | WASM has generator-backed exports and reusable paged host scaffolding, but not a matching bootstrap/default-provider story | Partial parity | Structure is improved, but authoring symmetry is incomplete |
| Standard health operations | Standard handshake/capability flow is available through builder/default host path | Standard handshake/capability flow is available through generated exports and paged host scaffolding | Parity | No Phase 1 blocker |
| Search authoring path | Default provider path exists via SDK builder and default search/page providers | Standard paged host provides search path for generated exports/CLI/invoke | Parity for paged flow | Good baseline for v0.7.0 |
| Search suggestions | Implemented in the sample via runtime interface/service registration | Implemented in the sample via custom invoke handler path | Partial parity | Works, but abstraction level differs |
| Metadata enrichment | Implemented in the sample via runtime interface/service registration | Implemented in the sample via custom invoke handler path | Partial parity | Works, but abstraction level differs |
| Chapters/page/pages | Default paged provider path exists | Standard paged WASM host path exists | Parity for paged flow | Good baseline for v0.7.0 |
| Video streams/segment SDK path | `AddDefaultVideoProvider<TRuntime>()` exists as a first-class builder path | `PluginWasmVideoOperationScaffold` exists, but there is no equivalent high-level standard video host/default-provider path | Gap | Major v0.7.0 release gate |
| Capability declaration model | Builder path can register paged and video runtime/provider features directly in sample usage | Capability profiles already contain `PagedAndVideo`, but the standard paged host and template/sample stay `PagedOnly` | Partial parity with drift risk | Needs normalization before release |
| Plugin-dev runtime inspection | Direct runtime adapters expose video operations | Host-bridge adapter now exposes search enrichment and video inspection through authenticated dev endpoints | Parity for plugin-dev workflow | Workflow gap closed in Phase 3 |
| Template default path | Template ASP.NET path uses `CreateWithDefaults(...)` plus default paged providers | Template WASM path is explicitly `PagedOnly` | Intentional paged-first template baseline | Template now reflects the chosen golden path instead of an accidental subset |
| Sample plugin reference path | Sample ASP.NET and WASM paths now stay on the same paged-first default surface | Dedicated video validation lives in `emma-video-test` | Aligned | Sample no longer claims unsupported video parity |
| Package-consumer behavior | Transitional docs still mention compatibility/package catch-up | Same | Open gate | Must be validated before claiming stable SDK surface |

## Evidence map

### ASP.NET-first high-level defaults exist today

- `EMMA/src/EMMA.Plugin.AspNetCore/PluginSdkHost.cs`
  - `AddDefaultPagedProviders<TRuntime>()`
  - `AddDefaultVideoProvider<TRuntime>()`
- `emma-video-test/Program.cs`
  - dedicated sample registers `IPluginVideoRuntime`
  - dedicated sample calls `AddDefaultVideoProvider<AspNetClient>()`

Conclusion: ASP.NET already has a first-class default-provider story for paged
and video, but the dedicated video sample is the correct reference path for
that surface.

### WASM has strong paged scaffolding but not equivalent video scaffolding

- `EMMA/src/EMMA.Plugin.Common/PluginBasicPagedWasmOperationHost.cs`
  - standard reusable host for paged-media plugins
- `EMMA/src/EMMA.Plugin.Common/PluginCapabilityProfiles.cs`
  - already defines `PluginCapabilityProfile.PagedAndVideo`
- `EMMA/src/EMMA.Plugin.Common/PluginWasmVideoOperationScaffold.cs`
  - contains low-level invoke helpers for video stream/segment operations
- `emma-test-plugin/WASM/WasmGlue.cs`
  - sample host stays on the shared paged host path
  - sample host declares `PluginCapabilityProfile.PagedOnly`
- `emma-video-test/Infrastructure/WasmPluginOperationHost.cs`
  - dedicated video-focused sample manually registers paged and video invoke
    operations in a custom WASM host
  - advertises `PluginCapabilityProfile.PagedVideoAudio`
- `emma-video-test/Infrastructure/WasmTypedExports.cs`
  - dedicated typed exports implement `VideoStreams(...)` and
    `VideoSegment(...)`

Conclusion: WASM has enough low-level pieces to support parity, and the
dedicated video sample is the correct parity reference. The remaining release
work is keeping template/sample/default paths explicit about when they are
paged-first versus paged+video.

Platform note at Phase 1 close:

- Known working for the dedicated WASM video sample: Linux, macOS, iOS
- Known not working: Windows
- Not yet confirmed: Android

### Plugin-dev parity is incomplete

- `EMMA/src/EMMA.Cli/PluginDevRuntimeAdapter.cs`
  - runtime interface includes `GetVideoStreamsAsync(...)`
  - runtime interface includes `GetVideoSegmentAsync(...)`
  - host-bridge adapter still throws explicit `NotSupportedException` for both
    operations

Conclusion: plugin-dev still violates the workflow-completeness rule for video.

### Template and sample still encode the asymmetry

- `EMMA/templates/plugin/Program.cs`
  - ASP.NET template path uses `CreateWithDefaults(...)` and
    `AddDefaultPagedProviders<AspNetClient>()`
- `EMMA/templates/plugin/WASM/WasmGlue.cs`
  - template WASM host declares `PluginCapabilityProfile.PagedOnly`
- `EMMA/templates/plugin/README.md`
  - template explicitly states generated plugins default to paged media only
- `emma-test-plugin/Program.cs`
  - sample ASP.NET path includes video registration/default provider path
- `emma-test-plugin/WASM/WasmGlue.cs`
  - sample WASM path remains paged-host-based with video export gap
- `emma-video-test/Infrastructure/WasmPluginOperationHost.cs`
  - dedicated video sample proves WASM video capability through a manual/custom
    path

Conclusion: the docs and examples are internally consistent today, but they are
not yet consistent with a parity-first `v0.7.0` story unless scope is narrowed.
More precisely: the gap is not "WASM video does not work". The gap is that the
parity-first story is not yet embodied in the standardized sample/template/SDK
path.

## Layer-by-layer findings

### 1. SDK abstraction layer

Current state:

- ASP.NET abstractions are further along for the standard paged + video authoring path.
- WASM abstractions are further along for generated export mechanics and standard paged operations.
- WASM video is already proven by the dedicated video sample, but the missing
  piece is a WASM-side video host/default abstraction that closes the gap
  between low-level working capability and the release contract.

Assessment:

- `Paged`: parity is strong enough to stabilize.
- `Video`: raw WASM capability is proven on Linux, macOS, and iOS, but parity
  is not ready to stabilize at the standardized SDK/default level and platform
  coverage is still incomplete.

### 2. Plugin-dev workflow layer

Current state:

- Some runtime adapters already expose video operations.
- The host-bridge path still rejects them.

Assessment:

- The workflow contract is not yet transport-complete.
- This is a release gate because plugin authors will experience it as a broken
  default development path even if the host/runtime internals already support
  video elsewhere.

### 3. Template/sample/reference layer

Current state:

- Template and sample successfully demonstrate the current SDK direction.
- They also expose exactly where parity still breaks.

Assessment:

- They are good evidence for the audit.
- They are not yet valid as the final `v0.7.0` parity reference path.

### 4. Package-consumer layer

Current state:

- Transition notes still exist in the authoring-surface docs.
- Phase 1 cannot claim this layer is closed without package-mode validation.

Assessment:

- This is not a transport-specific gap.
- It is still a release gate for the `v0.7.0` SDK contract.

## Parity decisions at Phase 1 close

Based on the audit, the following decisions are now justified:

1. Paged-media parity is sufficiently mature to be treated as part of the
   stable `v0.7.0` authoring surface.
2. Video parity is not yet mature enough to claim as stable across transports
  at the standard SDK/default authoring level, even though dedicated WASM video
  samples already work on Linux, macOS, and iOS.
3. The highest-value remaining parity work is not new runtime invention; it is
   elevating existing low-level WASM and plugin-dev capability into first-class
   authoring and workflow surfaces.
4. If `v0.7.0` continues to promise practical parity, the release must include:
   - a standard WASM video host/default path
   - plugin-dev video parity
   - template/sample alignment

## Release gates created by this audit

The audit confirms these concrete release gates:

1. Create a first-class WASM video abstraction equivalent in intent to the
   ASP.NET default-provider path.
2. Remove host-bridge video inspection gaps in `EMMA.Cli`.
3. Normalize capability declarations so default/sample/template paths cannot
   silently advertise less than the SDK profiles imply.
4. Decide whether the template remains intentionally paged-first or becomes the
   parity reference for paged + video.
5. Prove package-consumer behavior with the finalized public surface.
6. Decide how Windows and Android fit into the v0.7.0 support statement for the
  WASM video lane.

## Phase 1 output status

This audit completes the missing detailed baseline for Phase 1.

After this document exists, Phase 1 includes all of the following repository
artifacts:

1. The release contract.
2. The Phase 1 sign-off.
3. The detailed transport parity audit.
4. The explicit critical path into implementation work.

That means Phase 1 is fully implemented in-repo and later phases can proceed
against a documented baseline.