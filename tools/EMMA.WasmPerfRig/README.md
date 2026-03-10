# EMMA WASM Perf Rig (temporary)

This is a temporary test rig to time the same execution surfaces used by the app runtime:

- **app-flow**: mirrors the embedded flow (`Native -> PluginHostExports -> WasmPluginRuntimeHost -> Native runtime`).
- **direct-runtime**: calls `emma_wasm_component_invoke` directly to isolate runtime/plugin execution cost.

The rig auto-loads `libemma_wasm_runtime.dylib` from EMMA artifacts on macOS. Override with:

- `--runtime-lib-dir <dir>` or env `EMMA_WASM_RUNTIME_LIB_DIR`

## Build

```bash
cd /Users/zeus/Git/EMMA/tools/EMMA.WasmPerfRig
dotnet build
```

## Mode 1: app-flow (closest to real app path)

Required inputs:

- `--plugin-id <id>`
- `--op <search|chapters|page|pages>`

Operation-specific inputs:

- `search`: `--query <text>`
- `chapters`: `--media-id <id>`
- `page`: `--media-id <id> --chapter-id <id> --page-index <n>`
- `pages`: `--media-id <id> --chapter-id <id> --start-index <n> --count <n>`

By default, this mode resolves directories the same way frontend bootstrap does:

- manifests: `<Application Support>/emmaui/manifests`
- plugins: `<Application Support>/emmaui/plugins`

Optional overrides:

- `--runtime-lib-dir <dir>`
- `--app-support-dir <dir>` (frontend support root; rig uses `<dir>/emmaui/{manifests,plugins}`)
- `--manifests <dir>`
- `--sandbox <dir>` (or `--plugins <dir>`)
- `--cache-bust true|false` (for `search`; appends unique suffix per iteration)

Optional:

- `--iterations <n>` (default: `5`)
- `--warmup <n>` (default: `1`)

Example:

```bash
DYLD_LIBRARY_PATH="/Users/zeus/Git/EMMA/artifacts/wasm-runtime-native/osx-arm64" \
dotnet run -- app-flow \
  --op search \
  --plugin-id emma.plugin.test \
  --query naruto \
  --iterations 8 --warmup 1
```

Each iteration prints total elapsed time, parsed native `totalMs` (when available), computed host-overhead (`elapsed - nativeTotal`), success/failure, and result count.

## Mode 2: direct-runtime (runtime + plugin only)

Required inputs:

- `--component <path-to-plugin.wasm>`
- `--operation <handshake|capabilities|search|...>`

Optional:

- `--runtime-lib-dir <dir>`
- `--op-arg <value>` (repeatable)
- `--timeout-ms <n>` (default: `8000`)
- `--iterations <n>` (default: `5`)
- `--warmup <n>` (default: `1`)

Example:

```bash
DYLD_LIBRARY_PATH="/Users/zeus/Git/EMMA/artifacts/wasm-runtime-native/osx-arm64" \
dotnet run -- direct-runtime \
  --component /tmp/emma-perf/sandbox/emma.plugin.test/wasm/plugin.wasm \
  --operation search \
  --op-arg naruto \
  --timeout-ms 8000 \
  --iterations 8 --warmup 1
```

## Notes

- Keep this project temporary; remove after diagnostics are complete.
- For parity with app behavior, run with same plugin package and manifests used by iOS lane.