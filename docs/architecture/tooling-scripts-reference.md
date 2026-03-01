# Tooling scripts reference

This document is the canonical reference for script-based workflows in:

- `scripts/` (repo root)
- `templates/plugin/scripts/` (plugin template)

It focuses on real behavior from the current scripts, including platform constraints and required environment variables.

## Prerequisites

- .NET SDK 10
- Bash
- `python3` (manifest parsing/signing helpers)
- `zip` (plugin pack script)
- macOS only:
  - `codesign`

Suggested structure:

- `EMMA` repo at `.../EMMA`
- Flutter app repo at `.../emmaui` (default expected by sync scripts)

## Root scripts (`scripts/`)

### `publish-native-aot.sh`

Usage:

```bash
./scripts/publish-native-aot.sh <rid> [output-dir]
```

Behavior:

- Publishes `src/EMMA.Native/EMMA.Native.csproj` with NativeAOT.
- Uses static lib mode for iOS RIDs (`ios-*`, `iossimulator-*`) and shared lib mode otherwise.
- Normalizes output names when needed:
  - macOS: `libemma_native.dylib`
  - Linux/Android: `libemma_native.so`
  - Windows: `emma_native.dll`
  - iOS: `libemma_native.a`

Default output:

- `artifacts/aot/EMMA.Native/<rid>`

### `publish-plugin-host.sh`

Usage:

```bash
./scripts/publish-plugin-host.sh <rid> [output-dir]
```

Behavior:

- Publishes `src/EMMA.PluginHost/EMMA.PluginHost.csproj` self-contained with apphost.
- If `artifacts/wasm-runtime-native/<rid>` contains a matching `emma_wasm_runtime` library, bundles it into the publish output.
- Warns when native runtime library is missing for the requested RID.

Default output:

- `artifacts/pluginhost/<rid>`

Notes:

- Emits reminder to set `EMMA_PLUGIN_HOST_EXECUTABLE` to the published host binary.
- Build native runtime first for WASM component execution: `./scripts/build-wasm-runtime-native.sh <rid>`.

### `build-wasm-runtime-native.sh`

Usage:

```bash
./scripts/build-wasm-runtime-native.sh <rid> [output-dir]
```

Behavior:

- Builds native in-process runtime from `src/EMMA.WasmRuntime.Native` using Cargo.
- RID→target mapping is built in for: `linux-x64`, `osx-arm64`, `osx-x64`, `win-x64`, `ios-arm64`, `iossimulator-arm64`, `iossimulator-x64`.
- Writes runtime artifact to `artifacts/wasm-runtime-native/<rid>` by default.

### `publish-test-plugin.sh`

Usage:

```bash
./scripts/publish-test-plugin.sh <rid>
```

Behavior:

- For `osx-*`: delegates to `src/EMMA.TestPlugin/scripts/build-plugin-macos-app.sh`
- For `linux-*`: delegates to `src/EMMA.TestPlugin/scripts/build-plugin-linux-bundle.sh`
- Other RIDs: exits with unsupported message.

### `sync-native-aot-to-emmaui.sh`

Usage:

```bash
./scripts/sync-native-aot-to-emmaui.sh <rid> [aot-output-dir] [emmaui-dir]
```

Behavior:

- Copies native library artifact into target app platform directories.
- Destination mapping:
  - macOS: `emmaui/macos/Runner/Frameworks`
  - iOS/iOS simulator: `emmaui/ios/Runner/Frameworks`
  - Android: `emmaui/android/app/src/main/jniLibs/<abi>`
  - Linux: `emmaui/linux/runner`
  - Windows: `emmaui/build/windows/<arch>/runner/{Debug,Profile,Release}`

### `sync-plugin-host-to-emmaui.sh`

Usage:

```bash
./scripts/sync-plugin-host-to-emmaui.sh <rid> [pluginhost-output-dir] [emmaui-dir]
```

Behavior:

- Copies full published PluginHost runtime directory into app targets.
- Supported RIDs: `osx-*`, `linux-*`, `win-*`.
- Host runtime is copied under `<target>/EMMA.PluginHost.runtime`.

### `sync-test-plugin-to-emmaui.sh`

Usage:

```bash
./scripts/sync-test-plugin-to-emmaui.sh <rid> [plugin-output-dir] [emmaui-dir]
```

Behavior:

- Syncs seeded test plugin manifest + runtime bundle into app-readable plugin locations.
- Supported RIDs: `osx-*`, `linux-*`.
- macOS:
  - Writes to `macos/Runner/Resources/EMMA.Plugins/...`
  - Removes stale `EMMA.Plugins` from built `.app` Frameworks directories.
- Linux:
  - Writes to `${XDG_DATA_HOME:-$HOME/.local/share}/com.example.emmaui/emmaui`

### `publish-and-sync-all.sh`

Usage:

```bash
./scripts/publish-and-sync-all.sh <rid> [emmaui-dir]
```

Behavior:

- For `osx-*` and `linux-*`: runs publish+sync for Native, PluginHost, and TestPlugin.
- For other RIDs: runs Native + PluginHost only, skips TestPlugin by design.

### `run-plugin-host-with-test-plugin.sh`

Usage:

```bash
./scripts/run-plugin-host-with-test-plugin.sh
```

Behavior:

- Builds solution.
- Runs `EMMA.TestPlugin` on localhost:5005 and waits for port readiness.
- Starts `EMMA.PluginHost` with `PluginHost__ManifestDirectory` set to `src/EMMA.PluginHost/plugins`.
- Captures plugin stdout/stderr at `.tmp/test-plugin.log`.

### `validate-aot.sh`

Usage:

```bash
./scripts/validate-aot.sh
```

Behavior:

- NativeAOT-publishes `src/EMMA.Cli/EMMA.Cli.csproj` for `osx-arm64`.
- Intended as quick AOT viability check.

## Plugin template scripts (`templates/plugin/scripts/`)

### `build-plugin.sh`

Usage:

```bash
templates/plugin/scripts/build-plugin.sh
```

Behavior:

- Release publish of template plugin to `templates/plugin/artifacts/publish`.

### `build-plugin-macos-app.sh`

Usage:

```bash
templates/plugin/scripts/build-plugin-macos-app.sh
```

Behavior:

- Builds self-contained macOS app bundle at `templates/plugin/artifacts/EMMA.PluginTemplate.app`.
- Ad-hoc signs app bundle with template entitlements.

### `build-pack-plugin.sh`

Usage:

```bash
TARGETS="osx-arm64 linux-x64 wasm" templates/plugin/scripts/build-pack-plugin.sh [manifest-path]
```

Behavior:

- Parses manifest (`id`, `name`, `version`) via `python3`.
- Supports targets:
  - `osx-*`: builds apphost and packages `<pluginId>.app/`.
  - `linux-*`: builds apphost and packages `<pluginId>/` publish output.
  - `wasm*`: packages `plugin.wasm` (WebAssembly component) from `WASM_MODULE_PATH` (default `templates/plugin/artifacts/wasm/plugin.wasm`).
- Uses a consistent package layout for all targets:
  - `manifest/<plugin-id>.plugin.json`
  - `<plugin-id>/...runtime payload...`
- Creates zip per target:
  - `<pluginId>_<version>_<target>.zip`
- Optional manifest signing when `EMMA_HMAC_KEY_BASE64` is set.

Examples:

```bash
TARGETS="osx-arm64 linux-x64" templates/plugin/scripts/build-pack-plugin.sh
```

```bash
TARGETS="wasm" WASM_MODULE_PATH="/absolute/path/plugin.wasm" templates/plugin/scripts/build-pack-plugin.sh
```

## Test plugin scripts (`src/EMMA.TestPlugin/scripts/`)

### `build-pack-plugin.sh`

Usage:

```bash
TARGETS="osx-arm64 linux-x64 wasm" src/EMMA.TestPlugin/scripts/build-pack-plugin.sh [manifest-path]
```

Behavior:

- Mirrors template pack script behavior for the real test plugin.
- Supports `osx-*`, `linux-*`, and `wasm*` targets with the same package layout and zip naming.
- Uses `WASM_MODULE_PATH` for `wasm*` targets (default `src/EMMA.TestPlugin/artifacts/wasm/plugin.wasm`, expected to be a WebAssembly component).
- Signs manifest when `EMMA_HMAC_KEY_BASE64` is set.

### `sign-plugin.sh`

Usage:

```bash
EMMA_HMAC_KEY_BASE64=<base64-key> templates/plugin/scripts/sign-plugin.sh <manifest-path>
```

Behavior:

- Computes HMAC-SHA256 over `id|version|protocol`.
- Updates `signature.algorithm` and `signature.value` in manifest JSON.

### `generate-hmac-key.sh`

Usage:

```bash
templates/plugin/scripts/generate-hmac-key.sh [bytes]
```

Behavior:

- Prints a random base64 key (default 32 bytes).

### `sign-plugin-macos-app.sh`

Usage:

```bash
templates/plugin/scripts/sign-plugin-macos-app.sh <app-bundle-path>
```

Behavior:

- Re-signs given app bundle using template entitlements.

## iOS bring-up script path (current)

For fast device validation of native core + host plumbing:

1) Publish AOT native lib for iOS/simulator:

```bash
./scripts/publish-native-aot.sh ios-arm64
./scripts/sync-native-aot-to-emmaui.sh ios-arm64
```

2) PluginHost/TestPlugin scripts are currently desktop-centric; iOS plugin runtime execution should use the internal WASM lane in host/runtime integration.

3) Keep signing/integrity checks in dev mode explicit while Phase 3 hardening is in progress.

## Known constraints

- `publish-plugin-host.sh` does not currently include iOS-specific packaging pipeline.
- `publish-test-plugin.sh` supports only macOS and Linux.
- `publish-test-plugin.sh` does not yet dispatch to a wasm pack flow; wasm packaging is available through plugin pack scripts directly.
- External WASM hoster/runner execution paths are disabled; WASM activation must use an in-process runtime lane.
