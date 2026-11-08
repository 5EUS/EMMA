# The plugin instance seam

Everything above the plugin transport talks to a running plugin through one interface,
`IPluginInstance` (`src/EMMA.PluginHost/Instances/`). Today the only implementation carries a
WebAssembly component in the native host ([wasm-host.md](wasm-host.md)). A plugin that runs in its own
process (desktop only) is deferred, but the seam is where it plugs in, and the tests that hold the Wasm
host to the contract will hold it to the same one.

```
Application ports  --IPluginGateway-->  WasmPluginGateway   (load, cache, logs, quarantine)
                                              |
                                        PluginLoader          (re-verify signature, hash, kinds)
                                              |
                                        IPluginTransport      (Name, OpenAsync(PluginLaunch))
                                         /            \
                            WasmPluginTransport      (future) process transport
                                  |
                            IPluginInstance  <-- WasmPluginInstance / (future) ProcessPluginInstance
```

## The interface

`IPluginInstance` has the handshake (`DescribeAsync`, `Extensions`), health, the catalog calls (`Search`,
`Children`, `Get`), the asset calls (`Resolve`, `Read`), the search-assist extension calls (`Suggest`,
`Enrich`) and `DisposeAsync`. Every call returns `PluginCall<T>`: the `Outcome<T>`, whether the plugin
**trapped** (crashed or was stopped), and what it logged. Those types live in `EMMA.Domain`, so nothing in
the interface names Wasm, the native host, a process id or a pipe.

Rules an implementation follows (the doc comment on the interface is the source):

- An expected failure is a failed outcome, never an exception. Only a bug in the host throws.
- `Trapped` means the plugin itself failed; the instance stays usable for the next call.
- Calls are independent and may run concurrently.
- Results are raw. The pipelines validate them against the contract limits.
- A token cancelled before a call starts throws; once started, the implementation's own limits bound the call.
- Optional extensions are advertised in `Extensions` exactly when they work, and calling one that is not
  offered returns `unsupported`.

`IPluginTransport.OpenAsync` receives a `PluginLaunch` (the verified manifest, the entrypoint path, the user's
settings). `PluginLoader` has already verified the signature and the entrypoint's digest, so a transport only
runs what it is given. The manifest has no `transport` field beyond `wasm` yet; when a second transport exists,
the loader picks one by that field and the schema gains the new value.

## Conformance

`tests/EMMA.PluginConformance` is an abstract xUnit suite (`PluginInstanceConformance`) over a fixed fixture
catalog (`FixtureCatalog`: series, chapters, pages, a recipe, one proxied asset). A transport's test project
derives from it and supplies `CreateAsync`:

| Subject | Where | Serves the catalog from |
| --- | --- | --- |
| `InMemoryPluginInstance` | `EMMA.PluginHost.Tests` | C# in memory (the reference for the contract) |
| `WasmPluginInstance` | `EMMA.WasmRuntime.Tests` | the real `native/plugins/fixture` component in the native host |

Both pass the same 14 tests: describe consistency, health, cursor paging, typed props, tree walking and
`not-found`, `get` order, every asset delivery kind, ranged reads, error code and retry delay preserved,
crash reported as trapped while the instance survives, extensions advertised exactly when they work,
concurrency, cancellation and idempotent disposal. A new transport adds one derived class and a fixture
component or server that serves the same catalog.

What the suite does not cover is transport-specific: CPU and memory limits, network policy, signature and
hash checks. Those have their own tests (`PluginIsolationTests`, `WasmGatewayTests`).

## Deferred: out-of-process plugins

Nothing below is built, and none of it is designed beyond what is recorded here.

- **Wire format.** How a process plugin and the host exchange `IPluginInstance` calls. gRPC and Protobuf were
  dropped on purpose (the `rewrite` branch has no gRPC); the choice is open. It must carry the same JSON shapes
  as the Wasm lane (`EMMA.WasmRuntime/Wire`) or a documented equivalent, and the same binary lane for `read`.
- **Sandboxes must be honest.** `main`'s sandboxes were not, and were not lifted:
  - Linux: if bubblewrap is used, pass arguments as an argv array, never a quoted string; keep the network off
    unless the manifest grants it (the old one shared the host network); fail closed when the sandbox cannot be set up.
  - Windows: assign the job object at suspended start, before the first instruction runs (the old assignment raced).
  - macOS, iOS, Android: the old sandboxes were no-ops that reported `enforced=true`. A sandbox that does not
    enforce must say `enforced=false`, and those platforms must refuse process plugins rather than run them
    unconfined. `HostPlatformPolicy` gates this and does not exist yet.
- **Permissions.** A process plugin has a filesystem and sockets, which a Wasm plugin does not. The manifest has
  no path permissions on purpose; adding them is a schema change with its own review.
- **Quarantine and limits.** `WasmPluginGateway` counts traps and quarantines. A process transport must report
  crashes, timeouts and out-of-memory kills as `Trapped` so the same policy applies.
- **Not lifted from `main`:** `PluginProcessManager`, the per-OS sandbox managers, the handshake services and
  the macOS and mobile no-op sandboxes.
