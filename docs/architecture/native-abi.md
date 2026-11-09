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
- **One user per handle.** A handle belongs to the `user` in its configuration, and progress, libraries and remembered
  choices are always that user's. There is no way to name another.
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

`data_dir` is required: the library keeps everything under it (the database is `emma.db`), and there is no default
location. `user` defaults to `local`. `trust` defaults to `require_signed` with no repositories, which
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
| `plugins.settings_get` | `plugin` | `{settings: [{key, is_set, values, missing}]}`, one per declared setting; a secret is `is_set` with no `values` |
| `plugins.settings_set` | `plugin`, `changes` (`[{key, values}]`; empty `values` clears) | the same as `plugins.settings_get`, after the change. All or none is applied; the plugin reopens on its next call |
| `plugins.install` | `package_path`, `repository` | the installed plugin |
| `plugins.set_status` | `plugin`, `status` (`enabled`, `disabled`, `quarantined`) | `{}` |
| `plugins.uninstall` | `plugin` | `{}` |
| `catalog.search` | `plugin`, `query` (`text`, `kinds`, `filters`, `sort`, `cursor`, `limit`) | a node page |
| `catalog.browse` | `plugin`, `cursor`, `limit` | a node page |
| `catalog.children` | `ref`, `cursor`, `limit` | a node page |
| `catalog.get` | `refs` | `{nodes: [...]}` |
| `assets.list` | `ref`, `role`, `hint` (optional) | `{assets: [{index, mime, size, delivery, props}]}`. `hint` is a quality such as `1080p`, passed to the plugin as `emma.quality`; a plugin may ignore it |
| `assets.content` | `ref`, `role`, `index`, `range` (`start`, `length`), `hint` (optional) | `{mime, length, range_start, total, accepts_ranges}`, with the bytes in the blob. A leaf that was downloaded (index 0) is served from its stored file before the plugin is asked, and then honours `range` whatever it is |
| `progress.set` | `ref`, `unit`, `value`, `total`, `completed` | the stored progress |
| `progress.get` | `ref` | the caller's progress, or `not_found` |
| `progress.continue` | `ref` | the latest progress beneath a container |
| `progress.recent` | `limit` | `{items: [...]}`, newest first |
| `progress.history` | `limit` | `{groups: [{series, latest, completed, in_progress}]}`: one entry per series (the topmost node the host has cached above the progress), the one with the latest activity first, `latest` being its most recently updated progress |
| `progress.series` | `ref` | `{container, completed, in_progress, latest_activity, items}`: everything beneath the container with progress, newest first (at most 10,000), and the counts. Never an error: empty when there is nothing |
| `progress.delete` | `refs` | `{removed}`: deletes the progress of each node and of everything beneath it (a chapter, or a whole series); zero is not an error |
| `progress.clear` | none | `{removed}`: deletes all of the user's progress |
| `progress.complete` | `refs` | `{items: [...]}`: marks nodes finished. A node with progress keeps its unit and extent and its position moves to the end; one without is recorded as unit `item`, 1 of 1 |
| `progress.retention_get` | none | `{days}`: how long history is kept, `0` for ever (the default) |
| `progress.retention_set` | `days` (0 to 3650) | `{days, removed}`: sets the retention and deletes what is older now. It is applied again whenever progress is recorded |
| `library.list` | `library` (optional) | `{entries: [{node, library, added_at}]}`, newest first: one library, or every library when `library` is left out; `not_found` for a library that does not exist |
| `library.search` | `text`, `library` (optional), `limit` | the same shape: saved nodes whose title contains `text`, from the node cache and without asking any plugin (ASCII case is ignored). Each entry names the library it came from |
| `library.add` | `ref`, `library` (optional) | `{}`; the node must have been served by a search or listing. `library` defaults to the default library and must exist. A node that is already saved stays where it is (use `library.move`) |
| `library.remove` | `ref` | `{}`; removes the node from whichever library holds it |
| `library.move` | `ref`, `library` | `{}`; moves a saved node to another library, keeping its date. `not_found` when the node is not saved or the library does not exist |
| `library.libraries` | none | `{libraries: [{name, created_at, count, is_default}]}`, the default library first, then by name |
| `library.create` | `name` | the new library; names are 1 to 64 characters, unique ignoring case |
| `library.delete` | `name` | `{}`; deletes the library and the entries in it (progress and the nodes are untouched). The default library cannot be deleted |
| `downloads.enqueue` | `ref`, `role`, `hint` (optional) | the download `{id, ref, role, hint, state, items_completed, items_total, bytes_downloaded, error, created_at, updated_at, started_at, completed_at}`. The node (a chapter, a season, a whole series or one leaf) must have been served; the executor walks its children down to the nodes whose kind declares `role`, resolves each (passing `hint`) and keeps the first asset. A queued, running, paused or completed download for the same node and role is returned as it is; a failed or cancelled one is queued again |
| `downloads.list` | `states` (optional, any of `queued`, `running`, `paused`, `completed`, `failed`, `canceled`), `limit` (default 200, at most 1000) | `{items: [download]}`, the most recently changed first |
| `downloads.get` | `id` | `{download, items: [{ref, position, stored, mime, bytes, error}]}`: the leaves in the order the plugin lists them, empty until the download has walked its node. A client reads a download offline by asking `assets.content` for each stored leaf (`role` of the download, index 0) |
| `downloads.pause` | `id` | the download. A queued or running download stops after the leaf in flight and keeps what is stored |
| `downloads.resume` | `id` | the download, queued again; only a paused one |
| `downloads.retry` | `id` | the download, queued again; only a failed or cancelled one. Leaves that are stored are not fetched again |
| `downloads.cancel` | `id` | the download; only a queued, running or paused one. What is stored is kept until the download is deleted |
| `downloads.delete` | `id` | `{}`; deletes the download and the stored files no other download's leaves name |
| `downloads.clear` | `states` (at least one) | `{count}`: deletes every download in those states, with their files |
| `downloads.retry_failed` | none | `{count}`: queues every failed download again |
| `downloads.settings_get` | none | `{parallel}`: how many downloads run at once (default 2) |
| `downloads.settings_set` | `parallel` (1 to 8) | `{parallel}`; applied before the executor starts the next download |
| `app_settings.get` | `scope` | `{scope, values}`: the small text choices the app remembered under a scope, such as `series:<ref>` |
| `app_settings.set` | `scope`, `changes` (`[{key, value}]`; no `value` forgets the key) | the same as `app_settings.get`, after the change. All or none is applied. Keys are 1 to 64 of `a-z 0-9 . _ -`, values at most 4096 characters, a scope holds at most 100 keys, and scopes starting with `emma.` belong to the host and are refused |

Downloads belong to the host, not to the handle's user: the queue, the stored files and the parallel limit are the same
for everyone who uses the data directory. The executor starts when the handle is created and stops when it is closed; a
download that was running when the process died is queued again at the next start. `emma downloads run` does the same
work from the command line.

`assets.content` honours `range` for a stored download and for assets the plugin serves itself (proxied); other assets are read whole. A range
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
