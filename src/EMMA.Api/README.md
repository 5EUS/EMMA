# API

`EMMA.Api` is the HTTP host: REST over JSON, nothing else (no gRPC, no reflection). It is the only NativeAOT
project and the composition root, so it is where each port is bound to an implementation. The design and the
security decisions behind it are in [docs/architecture/api.md](../../docs/architecture/api.md).

## Running

```sh
sh native/build-host.sh                               # the native Wasm host, once
dotnet run --project src/EMMA.Api -- --new-api-key alice    # prints a key and its hash; the key is shown once
```

Put the printed hash in configuration (never the key), then start the host:

```sh
dotnet run --project src/EMMA.Api -- \
  --Emma:Api:Keys:0:ClientId=alice \
  --Emma:Api:Keys:0:KeyHash=sha256:<hash>
```

| Setting | Default | Meaning |
|---|---|---|
| `Emma:DataDirectory` | local app data `/emma` | database, installed plugins, delegation files, downloads, asset cache (`cache/assets`) |
| `Emma:Api:Keys:N:ClientId` / `KeyHash` | none | one entry per client; with none, every request except `/health` is refused |
| `Emma:Api:KeyHeader` | `x-api-key` | the header the key is read from |
| `Emma:Api:RateLimit:PerClientPermits` / `PerClientWindowSeconds` | 60 / 60 | requests per client per window |
| `Emma:Api:RateLimit:GlobalConcurrency` / `GlobalQueue` | 64 / 0 | requests in flight across all clients, and how many may wait |
| `Emma:Trust:RequireSigned` | `true` | `false` is only honoured when `ASPNETCORE_ENVIRONMENT=Development` |
| `Emma:Trust:Repositories:N:Id` / `RootKeys:<keyId>` | none | trusted plugin repositories and their root public keys (PEM) |

Listen addresses use the usual `--urls`. The API speaks plain HTTP and the key travels in a header, so put TLS in
front of it (a reverse proxy, or Kestrel's own certificate configuration) before exposing it beyond localhost.
A bad value in any setting stops the host at startup with a message naming it.

## Conventions

- Every path is under `/v1` except `/health`. Every request except `/health` needs the key header.
- Bodies and responses are JSON with `snake_case` names. Absent values are omitted rather than sent as `null`. A request
  body with a field the API does not know is refused.
- Nodes are named by their canonical reference `plugin.id:local-id` in the `ref` query parameter. The local id is
  opaque text a plugin chose, so it may contain slashes, colons or spaces; percent-encode it.
- Pages carry `next_cursor`; pass it back as `cursor`. `limit` is a hint from 1 to 500.
- Errors are always `{"error": {"code": "...", "message": "...", "retry_after_ms": 1500}}`, and a retry hint is also sent
  as `Retry-After` (whole seconds).

## Endpoints

| Method and path | Purpose |
|---|---|
| `GET /health` | Liveness. The only unauthenticated endpoint. |
| `GET /v1/plugins` | Installed plugins: `id`, `version`, `status` (`enabled`, `disabled`, `quarantined`), `signer`. |
| `GET /v1/plugins/{plugin}` | One plugin and, when enabled, what it declares: kinds (the tree schema), filters, sorts, settings. |
| `GET /v1/plugins/{plugin}/settings` | Where each setting the plugin declared stands: `key`, `is_set`, `values` (never for a secret) and `missing` (required, unset, no default). The labels, types and defaults are in the plugin detail. |
| `PUT /v1/plugins/{plugin}/settings` | Change settings, all or none. Body: `changes`, a list of `key` and `values` (an empty list clears the setting). Answers like `GET`. The plugin reopens with the new values on its next call. |
| `GET /v1/plugins/{plugin}/browse` | The plugin's landing content. `cursor`, `limit`. |
| `POST /v1/plugins/{plugin}/search` | Search. Body: `text`, `kinds`, `filters` (`id`, `values`), `sort`, `cursor`, `limit`. All optional. |
| `GET /v1/plugins/{plugin}/suggest?filter=…&text=…&limit=…` | Suggested values for a filter declared with type `lookup`. `filter` is required, `limit` defaults to 10 (at most 50). Answers `suggestions` (`value`, `label`, `description`, `is_default`); `value` is what to send back in the filter's `values`. Text shorter than the filter's `min_query_length` gives none. `501 unsupported` when the plugin does not offer search assist. |
| `GET /v1/nodes?ref=…&ref=…` | Fetch up to 500 nodes by reference, in the order asked. Unknown ones are omitted. |
| `POST /v1/nodes/enrich?ref=…&ref=…` | Fills in expensive props (ratings, counts) of nodes already served, such as search results. The plugin's props replace the stored props with the same key and are remembered; nodes the host has not seen are omitted. `501 unsupported` when the plugin does not offer search assist. |
| `POST /v1/nodes/refresh_cover?ref=…` | Asks the plugin for a node again and stores the answer, so its thumbnail address and details are current, and drops the node's cached thumbnail bytes. Answers the node. `404` when the host has not seen the node or the plugin no longer has it. |
| `GET /v1/nodes/thumbnail?ref=…` | The node's thumbnail image, fetched by the host under the plugin's network permissions and kept in the disk cache. `404` when the host has not seen the node or it has no thumbnail. |
| `POST /v1/cache/clear` | Empties the cached asset bytes, remembered asset resolutions and stored child listings (nodes the host has seen are kept). Answers `cleared`. |
| `GET /v1/nodes/children?ref=…` | Children of a node in natural order (chapters, pages, episodes). `cursor`, `limit`. For a node whose kind declares children the host lists them completely once and serves every page from that stored listing; the `cursor` it returns is its own and only valid for the same node. |
| `GET /v1/assets?ref=…&role=…&hint=…` | What a node offers for a role (`image`, `stream`, `subtitle`, …): `index`, `mime`, `size`, `delivery`, `props`. `hint` (optional) is a quality such as `1080p`, passed to the plugin as `emma.quality`; a plugin may ignore it. |
| `GET /v1/assets/content?ref=…&role=…&index=0&hint=…` | The bytes of one asset. A downloaded leaf (index 0) is served from its stored file before the plugin is asked. A stored or proxied asset also honours a single `Range: bytes=start-end` header. |
| `GET /v1/playback?ref=…&hint=…` | What a player needs for a node: `primary` (`stream` or `audio`), `sources` (qualities or files), `subtitles`, `audio_tracks`, `chapters`, `duration_ms`. Each track has `role`, `index`, `mime`, `label`, `language`, `quality`, `width`, `height`, `bitrate`, `is_default`, `is_live`, and `drm_scheme` and `drm_license_url` when the media is protected. Never says where media lives. `501 unsupported` when the node's kind has nothing to play; `404` when the host has not seen the node. |
| `POST /v1/playback/open?ref=…&role=…&index=0&hint=…` | Opens one asset for playing. Answers `url`, a path on this API (`/v1/stream/{session}/0`), and the `mime` of what it serves first. |
| `GET` or `HEAD /v1/stream/{session}/{part}` | A part of an opened stream, with `Range`. The playlist or manifest comes back rewritten so that every segment is another part of the same session; the host fetches each with the plugin's headers and under its network permissions, and keeps segments in the asset cache. Needs the same API key as any request, so a player must be able to send it (a plain browser `<video>` cannot). Stream parts are not counted against the per-client request allowance. |
| `PUT /v1/progress?ref=…` | Record the caller's position. Body: `unit` (`page`, `ms`, …), `value`, `total`, `completed`. |
| `GET /v1/progress?ref=…` | The caller's progress in a node. |
| `GET /v1/progress/continue?ref=…` | The latest progress anywhere beneath a container: "continue reading". |
| `GET /v1/progress/recent?limit=…` | The caller's recent progress, newest first (1 to 200). |
| `GET /v1/progress/history?limit=…` | Recent activity grouped by series (1 to 200): `series`, `latest` (the most recent progress beneath it), `completed`, `in_progress`. |
| `GET /v1/progress/series?ref=…` | Everything beneath a container: `completed`, `in_progress`, `latest_activity` and every node with progress, newest first. |
| `DELETE /v1/progress?ref=…&ref=…` | Delete the progress of each node and of everything beneath it. Answers `removed`. |
| `POST /v1/progress/clear` | Delete all of the caller's progress. Answers `removed`. |
| `POST /v1/progress/complete?ref=…&ref=…` | Mark nodes finished (positions that exist are kept). Answers `items`. |
| `GET /v1/progress/retention` | How long history is kept: `days`, 0 for ever. |
| `PUT /v1/progress/retention` | Body: `days` (0 to 3650). Deletes older history at once and whenever progress is recorded. Answers `days` and `removed`. |
| `GET /v1/library?library=…` | The caller's saved nodes, newest first, each with its `library`. One library, or all when `library` is left out. |
| `GET /v1/library/search?text=…&library=…&limit=…` | Saved nodes whose title contains `text`, across libraries unless `library` is given, from the node cache (no plugin is called). |
| `PUT /v1/library?ref=…&library=…` | Save a node the host has already served, into `library` (default: the default library). A node already saved stays where it is. |
| `DELETE /v1/library?ref=…` | Remove a saved node from whichever library holds it. |
| `POST /v1/library/move?ref=…&library=…` | Move a saved node to another library. |
| `POST /v1/library/refresh?library=…` | Lists the children of every saved node again (one library, or all) and reports which are new: `checked`, `updated` (`node`, `new_children`) and `failed` (`node`, `error`). A node whose children were never listed only gets a baseline; see `docs/architecture/native-abi.md`. One failing node does not stop the others. |
| `GET /v1/libraries` | The caller's libraries: `name`, `created_at`, `count`, `is_default`. The default library is always first. |
| `POST /v1/libraries` | Create a library. Body: `name` (1 to 64 characters, unique ignoring case). |
| `DELETE /v1/libraries?name=…` | Delete a library and its entries. The default library cannot be deleted. |
| `GET /v1/app-settings?scope=…` | What the app remembered for the caller under a scope: `values`, text by key. |
| `PUT /v1/app-settings?scope=…` | Change it, all or none. Body: `changes`, a list of `key` and `value` (no `value` forgets the key). Scopes starting with `emma.` are the host's. |
| `POST /v1/downloads?ref=…&role=…&hint=…` | Queue a download of a node (a chapter, a season, a series or one leaf) for an asset role; `hint` is optional. Answers the download. An existing download of the same node and role is returned as it is (a failed or cancelled one is queued again). The node must have been served. |
| `GET /v1/downloads?state=…&limit=…` | Downloads, the most recently changed first (default 200, at most 1000). `state` may repeat: `queued`, `running`, `paused`, `completed`, `failed`, `canceled`. |
| `GET /v1/downloads/{id}` | A download and its leaves (`ref`, `position`, `stored`, `mime`, `bytes`, `error`) in the order the plugin lists them. |
| `POST /v1/downloads/{id}/pause`, `/resume`, `/retry`, `/cancel` | Change a download's state; `400` when its state does not allow it. |
| `DELETE /v1/downloads/{id}` | Delete a download and the stored files no other download uses. |
| `DELETE /v1/downloads?state=…` | Delete every download in the given states (at least one is required). Answers `count`. |
| `POST /v1/downloads/retry-failed` | Queue every failed download again. Answers `count`. |
| `GET /v1/downloads/settings`, `PUT /v1/downloads/settings` | How many downloads run at once: `parallel`, 1 to 8 (default 2). |

Progress, libraries and remembered choices always belong to the authenticated client; there is no parameter to name another user.
Downloads belong to the host: every client sees and controls the same queue, which is the same for the CLI (`emma downloads`)
and the native library working on the same data directory. A download runs on its own once queued; the host's executor works
the queue in the background.

Asset listings say how an asset is delivered but never where it lives. A remote URL may carry cookies or referer
rules and a proxied token is private to the plugin, so clients ask for an asset by `index` and the host fetches it
under the plugin's network policy. Content responses carry `X-Content-Type-Options: nosniff` and a
`Content-Security-Policy: default-src 'none'; sandbox` header, because the bytes and their type come from a plugin.

## Errors

| HTTP | `code` | Meaning |
|---|---|---|
| 400 | `invalid_argument` | The request is malformed or breaks the plugin's declaration. |
| 401 | `unauthenticated` | Missing or unknown key. Sent with `WWW-Authenticate: ApiKey`. |
| 404 | `not_found` | Unknown plugin, node, asset index or progress. |
| 416 | `range_not_satisfiable` | A byte range starts past the end of the asset. |
| 424 | `plugin_unauthorized` | The plugin's own upstream wants credentials from the user. Not this API's 401. |
| 429 | `rate_limited` | This API's limit. `Retry-After` says when. |
| 429 | `upstream_rate_limited` | The plugin's upstream asked for slower requests. |
| 501 | `unsupported` | The plugin does not support the request (for example a role its kind lacks). |
| 502 | `plugin_error` | The plugin broke the contract or failed internally. |
| 503 | `unavailable` | The plugin's upstream is down; retrying later may help. |
| 504 | `timeout` | The plugin did not answer in time. |
| 500 | `internal_error` | A bug in the host. The response says nothing more. |

## Not here, on purpose

- **Managing plugins** (install, upgrade, enable, disable, remove) is not an endpoint. Installing code over the network
  is the most dangerous thing the host does, and the keys issued here are for clients. The CLI (phase 8) does it.
- **Plugin repositories** (browsing and installing plugins from a catalog) have no endpoints yet.
