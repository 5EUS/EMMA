# HTTP API

`EMMA.Api` is a thin REST layer over the Application pipelines. It owns no media logic: a route parses and validates
its request, calls one pipeline, and maps the `Outcome` to JSON. The endpoint reference is
[src/EMMA.Api/README.md](../../src/EMMA.Api/README.md); this note records why it is shaped the way it is.

## Shape

- **REST and source-generated JSON only.** gRPC and Protobuf are gone (decided in the plan), and every DTO is in one
  `JsonSerializerContext`, so the NativeAOT build has no reflection. The DTOs are separate from the Domain types on
  purpose: the wire format is a promise to clients and must not move when a Domain record gains a field.
- **Generic like the core.** There is no `/manga` or `/video`. A client navigates by the `kinds` a plugin declares,
  asks for `assets` by `role`, and records progress in an open `unit`. A new media type adds no route.
- **Nodes travel as `ref` query parameters,** never path segments. A local id is opaque plugin text (it can hold `/`,
  `:` or `%2F`), and reverse proxies and routers disagree about decoded slashes in paths.
- **One result type.** Every response, including every error, is built by `ApiResults`, so a client parses one error
  shape. Plugin failure classes map to fixed statuses (table in the README); a plugin's "needs credentials" is `424`,
  not `401`, because `401` means *this API* did not authenticate the caller.
- **Composition root.** `EmmaServices.AddEmma` is the one place that binds ports to implementations (SQLite stores,
  the Wasm gateway, the HTTP asset fetcher). Tests replace a registration after the defaults, so they run the real
  pipeline, real SQLite and the real middleware around a fake gateway.

## Security decisions

These fix the problems found in `main`'s API (plaintext keys, a non-constant-time comparison, a rate limiter that ran
before authentication and keyed on the *claimed* key, streaming calls that skipped auth).

| Decision | Why |
|---|---|
| Keys are 256 random bits; configuration holds only `sha256:` hashes. | A leaked config file or log does not leak a working key. A fast hash is right for a key with no weak secret to stretch. |
| The comparison runs over every configured hash with `CryptographicOperations.FixedTimeEquals`, no early exit. | Timing reveals nothing about which key is close. |
| A raw key in configuration fails the start. | The common mistake is pasting the key where the hash goes. |
| Authentication is a default-deny step before everything else, and the one exemption (`/health`) is an exact match. | A new route is protected without anyone remembering to protect it; `/Health` or `/health/` is not a loophole. Unknown paths answer `401`, so probing learns nothing. |
| A repeated key header is refused. | Ambiguity is refused, not resolved. |
| Rate limiting runs *after* authentication and is partitioned by the authenticated client id. | A caller cannot get a fresh bucket by inventing a key or a header. A flood of invalid keys is rejected cheaply before the limiter and spends no real client's allowance. |
| A global concurrency limit runs alongside the per-client window. | One slow plugin cannot be used to tie up every worker. |
| Progress, libraries and remembered choices are filed under the authenticated client; no route takes a user. | There is no cross-user access to get wrong. |
| Downloads are the host's, not a client's: every client key sees and controls the whole queue, and a download names only a node and a role. | A download is a copy of a plugin's content on the host's disk, shared by every client of that host. It is not user data, so there is nothing to file under a client. It carries the same "keys have no scopes" gap as plugin settings: any key may delete a download. |
| Asset listings never reveal remote URLs, request headers or proxied tokens. | Those can carry cookies, referer rules and capabilities private to the plugin. The host fetches under the plugin's network policy. |
| Content responses are `nosniff` with `Content-Security-Policy: sandbox`, and the plugin's type is re-parsed. | Bytes and type come from a plugin. Without this, a plugin could serve a page that runs with the API's origin, or inject headers through its `mime`. |
| Request bodies are limited to 64 KiB and unknown fields are refused. | Every body is a small document; a typo should fail, not be ignored. |
| Unhandled exceptions become a generic JSON `500`. | No stack traces or connection strings reach a client. |
| `Emma:Trust:RequireSigned=false` is honoured only in the Development environment. | Environment variables are configuration, so a production host must not be downgradable by one. |
| Plugin log text has control characters removed before it is logged. | Plugin text is untrusted and must not forge log lines. |

## Deferred

- The HTTP API never reloads plugins on request: installing, upgrading or removing one is not an HTTP operation (see the
  API project file), so there is no endpoint to call. A running API picks up plugins that were changed behind its back
  only when it restarts. The native library, whose caller already has every authority, has `plugins.reload`, and an app
  that embeds EMMA uses that. TODO(rewrite): if a deployment needs live reload of a server, add it as an admin
  operation reachable from loopback only, never as a client-key endpoint.
- TODO(rewrite): failed authentication is not rate limited. Invalid keys are cheap to reject, and a 256-bit key cannot
  be guessed, so this is a resource concern only. A limiter keyed by remote address would need a decision about trusted
  proxies (`X-Forwarded-For` is caller-controlled), so it waits for a deployment that needs it.
- TODO(rewrite): TLS is left to the deployment (reverse proxy or Kestrel certificate configuration). The host does not
  refuse plain HTTP on a non-loopback address.
- Playing media. `GET /v1/playback` is the typed view of a node's streams, subtitles, audio tracks and chapters, and
  `POST /v1/playback/open` plus `GET /v1/stream/{session}/{part}` give a player an address for one of them, so a
  stream that needs headers, cookies or the plugin itself plays without the client ever learning where it lives (see
  `contract.md` section 13). A stream is a *session*: an unguessable name that lives six hours past its last use, at most
  32 at once, and that registers up to 50,000 addresses (the segments of the playlists it has served). Only HLS and DASH
  manifests are rewritten; any other file is passed through with byte ranges. The part route is exempt from the per-client
  request allowance (a player asks for a part every few seconds), but not from authentication or the global concurrency
  limit. A part is never served for an address the plugin could not itself reach: every hop of every fetch is checked
  against the plugin's network permissions, and credential headers do not follow a playlist to another host.
- Resolutions are cached. `GET /v1/assets` and `GET /v1/assets/content` reuse what the plugin said a node resolves to
  (per node, role and `hint`) for five minutes, or until shortly before a link says it expires, so reading every page of
  a chapter, or every range of a proxied video, asks the plugin once. Only remote links and proxied tokens are kept
  (inline bytes are not), and only in memory: a resolution can carry cookies, so it is never written to disk. A read
  that fails on a remembered link or token asks the plugin again and retries once if the answer changed. Refreshing a
  node, changing or reloading its plugin, and `POST /v1/cache/clear` forget the matching resolutions. A plugin that
  revokes a token early is covered by that retry; one that rotates content under the same link is not (wait out the
  five minutes, or refresh the node). Downloads resolve each leaf once, so the cache does not matter to them.
- TODO(rewrite): API keys carry no scopes or expiry; every client may use every endpoint. A read-only key is easy to add
  to `ApiClientKey` when a client needs one.
- TODO(rewrite): plugin settings are host-wide, not per client, and every client key may read and change them (a secret
  can be set but never read back). That is the same "keys carry no scopes" gap as above: once keys have scopes, changing
  settings should need one that reading does not.
- Out of scope for the API: plugin management (CLI).
- Progress, libraries and remembered choices have no scopes of their own: any client key may use them, but only its own
  data, which is why the "keys carry no scopes" gap above does not apply to them.
