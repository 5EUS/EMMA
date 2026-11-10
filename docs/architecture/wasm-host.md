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

## The plugin cache

`host.cache-get` and `host.cache-put` are owned by the Rust host (`native/emma-wasm/src/cache.rs`) and nothing else.
The cache is in memory, private to one opened plugin, bounded (256 entries, 8 MiB in total, 1 MiB a value) and
expiring, so a lookup never crosses the FFI boundary and a plugin cannot fill the user's disk. It does not survive the
instance: closing or reloading a plugin empties it, which is also how a user clears it. There is deliberately no
.NET-side port, because a second implementation would have to enforce the same limits to be interchangeable. Anything
that must outlive a restart is a node prop or a setting, not a cache entry.

## Exported names

Every export of `libemma_wasm` starts with `emma_wasm_`. The prefix is load-bearing: on iOS the app links `libemma_native.a`
and `libemma_wasm.a` statically into one binary, so a name both libraries export is a single symbol and one library's
call lands in the other's function. (`emma_buffer_free`, `emma_runtime_create` and `emma_runtime_destroy` were once shared
that way: freeing a buffer then passed a stack address to `NativeMemory.Free`, and the app aborted on its first plugin
load.) Never export a name without the prefix from this library, and never reuse an `emma_wasm_` name in `EMMA.Native`.

## Open items

- TODO(rewrite): iOS runs plugins on the Pulley interpreter (`pulley64`, chosen in `LazyWasmTransport` for iOS and
  tvOS since there is no JIT). The runtime option and the precompile path are tested on the desktop host; the iOS
  path itself has not been confirmed on a device yet.
- TODO(rewrite): a `.cwasm` cache. Manifests may not carry a `.cwasm` (machine code the signature
  cannot vouch for), and the host recompiles on every open. A cache needs an integrity story first.
- TODO(rewrite): the loader hashes the entrypoint and the host then opens it by path, a small race
  if the install directory is writable by others. An open-from-bytes call would close it.
- TODO(rewrite): the binary lane exists only for `read`. If `inline` assets or other results grow
  large, move them there too.
- TODO(rewrite): canned HTTP responses for a plugin author (`emma plugin run --canned <file>`), so a plugin that calls
  `host.http-fetch` can be tried without its real upstream. `--settings` already gives a plugin its settings. What is
  missing is a `testing.canned` list in the Rust plugin config that `Network::fetch` answers from (after the usual
  allow-list check, so the manifest is still tested), carried through `WasmTestingOptions`, `PluginLaunch` and
  `EmmaRuntimeOptions` to the CLI. Remote asset bytes go through the .NET fetcher, which would need the same list.
- TODO(rewrite): the Windows and macOS legs of CI build the native host but have never run.
