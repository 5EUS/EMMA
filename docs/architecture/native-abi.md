# Native library ABI

`EMMA.Native` is the in-process C interface for apps that embed EMMA: Flutter through `dart:ffi`, or any C caller. It is a
thin shell over the same pipelines the HTTP API uses (`EmmaRuntime`), so an embedded app and a server behave identically.
The header is [src/EMMA.Native/include/emma_native.h](../../src/EMMA.Native/include/emma_native.h); this note explains
the design and lists every operation.

## Design

- **One call, many operations.** The surface is `emma_abi_version`, `emma_runtime_create`, `emma_runtime_destroy`,
  `emma_call`, `emma_call_async` and `emma_buffer_free`. A capability is an *operation name* passed to `emma_call` with a
  JSON request, so adding one never adds an export. `main` grew `_v2` and `_v3` exports and one per media type; this
  cannot, because the ABI contains no media types and no operation-specific functions.
- **Same JSON as REST.** Results use the HTTP API's shapes (`EMMA.Api.Contracts`), including error codes, so a client
  models the data once. Requests are the REST parameters moved into a JSON object. Unknown request fields are refused.
- **Envelope.** Every answer is `{"ok": result}` or `{"error": {"code", "message", "retry_after_ms"}}`. A status of `0`
  means the call ran (read the envelope). A non-zero status means it never reached an operation: `1` bad argument,
  `2` the runtime could not start, `3` an unexpected failure caught at the boundary. Nothing throws into the caller.
- **Handles are numbers,** never pointers, and are never reused. A closed or invented handle returns
  `invalid_handle`; closing twice is harmless. Closing cancels calls in flight and waits for them.
- **Ownership.** Every `EmmaBuffer` returned is the caller's; release it with `emma_buffer_free(buffer.data)`. Requests
  are copied before the call returns, so the caller may free its memory at once.
- **Blocking and async.** `emma_call` blocks the calling (foreign) thread; use it from a background isolate or thread.
  `emma_call_async` returns at once and delivers the answer to a callback, once, on another thread. The callback
  receives the JSON and the binary content as `(pointer, length)` pairs by value, pointing at heap memory it now owns and
  frees with `emma_buffer_free`; nothing points at the library's stack, so the values can be handed to another thread
  (a Dart `NativeCallable.listener` runs later on the event loop and needs exactly that). In Dart, the async form pairs with `NativeCallable.listener`.
- **One user per handle.** A handle belongs to the `user` in its configuration, and progress and library are always that
  user's. There is no way to name another.
- **The caller is trusted.** Unlike the HTTP API, the library exposes plugin installation, switching and removal
  (`plugins.install` and friends): the caller is the app that embeds EMMA, in its own process, and already has every
  authority these grant. Signature checks still apply to what is installed.

## Configuration

```json
{
  "data_dir": "/path/where/emma/keeps/state",
  "user": "local",
  "native_dir": "/path/containing/libemma_wasm-and-libe_sqlite3",
  "trust": {
    "require_signed": true,
    "repositories": [{ "id": "main", "root_keys": { "key-1": "-----BEGIN PUBLIC KEY-----..." } }]
  }
}
```

`data_dir` is required. `user` defaults to `local`. `trust` defaults to `require_signed` with no repositories, which
means nothing can be installed until a repository is trusted. `require_signed: false` is honoured because the embedding
app writes this document; do not build it from user-editable input.

`native_dir` matters because a library loaded into someone else's process is not next to that process's executable,
where native dependencies are normally found. Pass the directory the app put `libemma_wasm` and `libe_sqlite3` in. On
Linux `libe_sqlite3` also expects `libm` to be loaded in the process, as it is in any real application.

## Operations

Requests are JSON objects; an empty request means `{}`. `ref` is a node reference, `plugin.id:local-id`.

| Operation | Request | Result |
|---|---|---|
| `plugins.list` | none | `{plugins: [{id, version, status, signer}]}` |
| `plugins.get` | `plugin` | plugin detail with its declared kinds, filters, sorts, settings |
| `plugins.install` | `package_path`, `repository` | the installed plugin |
| `plugins.set_status` | `plugin`, `status` (`enabled`, `disabled`, `quarantined`) | `{}` |
| `plugins.uninstall` | `plugin` | `{}` |
| `catalog.search` | `plugin`, `query` (`text`, `kinds`, `filters`, `sort`, `cursor`, `limit`) | a node page |
| `catalog.browse` | `plugin`, `cursor`, `limit` | a node page |
| `catalog.children` | `ref`, `cursor`, `limit` | a node page |
| `catalog.get` | `refs` | `{nodes: [...]}` |
| `assets.list` | `ref`, `role` | `{assets: [{index, mime, size, delivery, props}]}` |
| `assets.content` | `ref`, `role`, `index`, `range` (`start`, `length`) | `{mime, length, range_start, total, accepts_ranges}`, with the bytes in the blob |
| `progress.set` | `ref`, `unit`, `value`, `total`, `completed` | the stored progress |
| `progress.get` | `ref` | the caller's progress, or `not_found` |
| `progress.continue` | `ref` | the latest progress beneath a container |
| `progress.recent` | `limit` | `{items: [...]}`, newest first |
| `library.list` | none | `{entries: [{node, added_at}]}` |
| `library.add` | `ref` | `{}`; the node must have been served by a search or listing |
| `library.remove` | `ref` | `{}` |

`assets.content` honours `range` only for assets the plugin serves itself (proxied); other assets are read whole. A range
that starts past the end is the error `range_not_satisfiable`. As over HTTP, asset listings never reveal where an asset
lives: ask for one by `index`.

A test fails if an operation exists that is not in this table.

## Building

`sh scripts/publish-native.sh [rid]` publishes the library with NativeAOT for a desktop target and names it
`libemma_native.so`, `libemma_native.dylib` or `emma_native.dll`. `sh scripts/development/native-smoke.sh` publishes it and
drives it from a C program (`tests/EMMA.Native.Harness/harness.c`) that loads it with `dlopen`: the one check that
proves the real shared library exports what the header says. The managed tests (`EMMA.Native.Tests`) cover behaviour
against a real Wasm plugin through the same code the exports call.

## Deferred

- TODO(rewrite): iOS and Android are not built. `main` had scripts for an iOS static library and an Android NDK build;
  they cannot be validated without a Mac and the NDK, so they were not ported. The library name, `native_dir` and
  `libm` notes above are desktop-only; mobile loaders differ.
- TODO(rewrite): plugin log lines are discarded in the native library. A log callback (`emma_set_log_callback`) would let
  an app show plugin logs; add it when a client needs it.
- TODO(rewrite): calls cannot be cancelled individually. Closing the handle cancels everything in flight; per-call
  cancellation needs a request id returned by `emma_call_async`.
- TODO(rewrite): the library is only built and smoke-tested on the host's own target. CI should run the smoke test on
  Linux, macOS and Windows runners (Windows needs the harness's `LoadLibrary` path exercised; it is written but untested).
