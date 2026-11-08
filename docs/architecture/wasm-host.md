# The Wasm host

How EMMA runs plugins. The contract is in [contract.md](contract.md), trust and installation in
[plugin-trust.md](plugin-trust.md).

## Pieces

| Piece | Where | Job |
| --- | --- | --- |
| `emma-wasm` | `native/emma-wasm/` (Rust, wasmtime) | loads components, runs one call at a time in a fresh instance, enforces limits, does the network |
| `EMMA.WasmRuntime` | `src/EMMA.WasmRuntime/` | the C# binding: P/Invoke, JSON wire types, typed `WasmPlugin` calls |
| `PluginLoader`, `WasmPluginGateway` | `src/EMMA.PluginHost/Runtime/` | verify an installed plugin, open it, serve `IPluginGateway`, forward logs, quarantine |
| `WasmPluginTransport`, `WasmPluginInstance` | `src/EMMA.PluginHost/Runtime/` | the Wasm implementation of the transport-neutral seam in [plugin-instance.md](plugin-instance.md) |
| fixture plugins | `native/plugins/fixture/` | a Rust component that exercises every contract path; built by `native/build-plugins.sh` |

Nothing links LLVM, wasi-sdk or Docker. Rust plugins compile straight to components with the
`wasm32-wasip2` target. Bindings for the host (`bindgen!`) and for plugins (`wit-bindgen`) are
generated from `src/EMMA.Contracts/wit`.

## A call

1. `WasmPluginGateway` loads the plugin on first use: `PluginLoader` re-parses the stored manifest,
   re-verifies its signature, re-hashes the entrypoint on disk against the signed digest (a
   mismatch quarantines the plugin), opens it in the native host with the manifest's limits and
   permissions plus the user's settings, and checks the manifest's `kinds` against `describe()`.
2. Each call creates a fresh store and instance, so calls are independent and may run
   concurrently. Requests and results cross the C ABI as JSON (`read` returns its bytes in a
   separate binary buffer).
3. The result is an envelope: `ok` or `error` (a plugin failure is data, not a crash), `trapped`
   (the plugin crashed or was stopped), and the plugin's logs. `WasmPluginGateway` forwards the
   logs to `IPluginLogPort` and counts consecutive traps; five in a row quarantine the plugin.

## Limits

| Limit | How |
| --- | --- |
| CPU per call | epoch interruption, 10 ms ticks; time inside host calls is not counted |
| memory | wasmtime `StoreLimits`, from `limits.memoryMb` |
| wall clock | `limits.callTimeoutMs` (default 60 s), host waits included |
| network | the manifest's allow-list: exact hosts and `*.suffix`, never IP literals; checked again on every redirect; credentials stripped when a redirect changes host; response size and time limits; resolved addresses must be public (SSRF) |
| filesystem, sockets, environment | none: WASI is given no preopens, no sockets and no variables |

A call cannot be interrupted from C# once it has started; the limits above bound it, and the
cancellation token is honoured before it starts.

## Building and testing

```sh
rustup target add wasm32-wasip2
sh native/build-plugins.sh   # artifacts/native/plugins/*.wasm
sh native/build-host.sh      # artifacts/native/lib/libemma_wasm.*
dotnet test -c Release
```

`scripts/development/check.sh` does all of it. The .NET integration tests fail the build when
those artifacts are missing rather than skipping. `EMMA_WASM_LIBRARY` names an explicit library
file for development.

The network allow-list is implemented twice (what the manifest grants in `CapabilityPolicy`,
what is enforced in Rust). Both read `native/emma-wasm/tests/vectors/network-policy.json`.

## Open items

- TODO(rewrite): iOS needs `pulley64` (no JIT). The runtime option and the precompile path exist
  and are tested on the desktop host; nothing has run on a device.
- TODO(rewrite): a `.cwasm` cache. Manifests may not carry a `.cwasm` (machine code the signature
  cannot vouch for), and the host recompiles on every open. A cache needs an integrity story first.
- TODO(rewrite): the loader hashes the entrypoint and the host then opens it by path, a small race
  if the install directory is writable by others. An open-from-bytes call would close it.
- TODO(rewrite): the binary lane exists only for `read`. If `inline` assets or other results grow
  large, move them there too.
- TODO(rewrite): `host.cache` is implemented in Rust and duplicates `IPluginCachePort`. Decide
  which owns plugin cache storage when the Storage layer meets the host.
- TODO(rewrite): a plugin-author test harness (run a component against canned HTTP responses
  without the whole app). The Rust tests have the pieces.
- TODO(rewrite): the user-settings store and secret encryption behind `IPluginSettingsPort`.
- TODO(rewrite): the Windows and macOS legs of CI build the native host but have never run.
