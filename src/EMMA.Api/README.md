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
| `Emma:DataDirectory` | local app data `/emma` | database, installed plugins, delegation files, asset cache |
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
| `GET /v1/nodes?ref=…&ref=…` | Fetch up to 500 nodes by reference, in the order asked. Unknown ones are omitted. |
| `GET /v1/nodes/children?ref=…` | Children of a node in natural order (chapters, pages, episodes). `cursor`, `limit`. |
| `GET /v1/assets?ref=…&role=…` | What a node offers for a role (`image`, `stream`, `subtitle`, …): `index`, `mime`, `size`, `delivery`, `props`. |
| `GET /v1/assets/content?ref=…&role=…&index=0` | The bytes of one asset. A proxied asset also honours a single `Range: bytes=start-end` header. |
| `PUT /v1/progress?ref=…` | Record the caller's position. Body: `unit` (`page`, `ms`, …), `value`, `total`, `completed`. |
| `GET /v1/progress?ref=…` | The caller's progress in a node. |
| `GET /v1/progress/continue?ref=…` | The latest progress anywhere beneath a container: "continue reading". |
| `GET /v1/progress/recent?limit=…` | The caller's recent progress, newest first (1 to 200). |
| `GET /v1/library` | The caller's saved nodes, newest first. |
| `PUT /v1/library?ref=…` | Save a node the host has already served. |
| `DELETE /v1/library?ref=…` | Remove a saved node. |

Progress and the library always belong to the authenticated client; there is no parameter to name another user.

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
- **Plugin settings and downloads** have no endpoints yet: the settings store and the download executor are not built.
