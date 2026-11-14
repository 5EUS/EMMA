# Writing an EMMA plugin

A plugin is a WebAssembly component that answers questions about a tree of nodes. EMMA owns everything else: the
database, caching, retries, the network, settings, progress, signatures and sandboxing. You write the part that knows
one source (a site, an API, a folder of files) and nothing about any media type: a manga reader, a video player and a
recipe app all use the same three interfaces.

Rust is the first-class language: it compiles straight to a component with no extra tooling. Any language that can
produce a component for the [WIT](https://component-model.bytecodealliance.org/design/wit.html) contract in
`src/EMMA.Contracts/wit` works, because the host only checks the component's real exports (see the end of this page for
C#).

## Start

```sh
rustup target add wasm32-wasip2
emma plugin new notes --id com.me.notes --name "Notes"
cd notes
sh build.sh --no-pack          # builds the component and copies it to plugin.wasm
emma plugin run . describe
emma plugin run . search "collection 2"
```

`emma plugin new` writes a working plugin (it serves two made-up kinds from memory), the contract it builds against
in `wit/`, a README and `build.sh`, which builds the component and packs it (`sh build.sh`). The template is built and linted in EMMA's own CI against the current contract, so it always
compiles.

## The model

Everything is a **node**: `{id, kind, title, parent, thumbnail, props}`. The `kind` is a string *you* choose and declare;
EMMA never switches on it. A manga plugin declares `manga`, `manga.chapter`, `manga.page`; a video plugin `show`,
`season`, `episode`; a recipe plugin `recipe`, `recipe.step`. Each kind says which kinds can be its children and which
**roles** of asset it can resolve (`image`, `stream`, `subtitle`, `audio`, `text`, anything). That declaration is the
schema generic clients navigate by. A kind may name a **profile** (`emma.profile.paged@1`) to tell clients it follows a
known vocabulary so they can offer a typed experience without EMMA changing.

You implement three interfaces (`wit/meta.wit`, `catalog.wit`, `assets.wit`):

| Call | Answers |
|---|---|
| `meta.describe` | The kinds, filters, sorts and user settings you offer. Called once and cached. |
| `meta.health` | Is the source reachable? Cheap. |
| `catalog.search` | Nodes for a query (`text`, `kinds`, `filters`, `sort`, `cursor`, `limit`). |
| `catalog.children` | The children of a node in natural order (chapters, pages, episodes, tracks); `none` is your browse root. |
| `catalog.get` | Nodes by id, in order; unknown ids are left out. |
| `assets.resolve` | The assets of a node for a role: inline bytes, a remote URL (EMMA fetches it), or a proxied token (you serve it). |
| `assets.read` | Bytes for a proxied token, optionally a range. |

Pages are cursor-based: return `next-cursor` and EMMA hands it back. Failures are `plugin-error` with a class
(`not-found`, `unauthorized`, `rate-limited` with `retry-after-ms`, `unavailable`, `invalid-argument`, `unsupported`,
`cancelled`, `internal`); EMMA retries the transient ones for you. Props are typed (text, integer, number, flag,
timestamp, url, text-list), never JSON blobs. The optional `search-assist` interface adds lazy enrichment (`enrich`) and completion for lookup filters (`suggest(filter-id, text, limit)`, returning choices). Give each filter a `role` such as `emma.filter.tag` or `emma.filter.language` when it means one of the well-known things: clients type-ahead and group by role, so "english" can offer your language filter. Declare a filter with the `lookup` type and a `lookup-spec` (minimum length, maximum results, debounce) when its values are too many to list in `choices`; the client then calls `suggest`.

### Video and audio

A plugin for video or audio needs no extra interface: declare the profile (`emma.profile.video@1` or
`emma.profile.audio@1`) on your kinds, give the playable kind the roles it resolves, and describe what each asset is with
typed props. A player then picks a quality, a subtitle or an audio track, shows chapters and plays from a local address
EMMA serves, so a stream that needs your `Referer` or cookie, or that only you can read, plays like any other.

```text
episode roles [stream, subtitle, audio, chapters]
resolve(ep, "stream", hints{emma.quality "1080p"}) -> [asset application/vnd.apple.mpegurl remote(url, headers)
                                                         props{emma.quality "1080p", emma.width 1920, emma.height 1080}]
resolve(ep, "subtitle")                            -> [asset text/vtt remote(..) props{emma.language "en", emma.label "English", emma.default true}, ...]
resolve(ep, "chapters")                            -> [asset text/plain inline() props{emma.start-ms 0, emma.end-ms 90000, emma.label "Opening"}, ...]
```

The props are checked: `emma.width` as text, or a negative `emma.duration-ms`, is rejected as your bug. Mark a protected
stream with `emma.drm.scheme` and `emma.drm.license-url`, a live one with `emma.live`. Adaptive manifests (HLS, DASH) may
point at segments on any host your manifest permits, relative or absolute; EMMA rewrites them. A queue is just your
`children` in order. Try it with `emma playback plan <ref>` and `emma playback serve <ref>` (then `mpv <address>`), after
a search has made EMMA see the node. All of it is in `contract.md` section 13.

## What EMMA gives you (`wit/host.wit`)

`host.http-fetch` (HTTP, restricted to the hosts in your manifest, redirects included), `host.cache-get/put` (if you ask
for it), `host.setting` / `setting-values` (user settings you declared; secrets are returned in clear to you only),
`host.log` and `host.now-ms`. There is no filesystem, no sockets and no ambient clock. CPU and memory are metered per
call, with the ceilings you declare in the manifest.

## The manifest (`plugin.json`)

The schema is `src/EMMA.Contracts/plugin-manifest.schema.json`. The parts that matter:

- `id`: lower-case reverse-DNS, never changes once published. EMMA namespaces every node by it.
- `kinds`: must equal what `describe` returns; the host refuses a mismatch at install and on every load.
- `contract.required` / `optional`: the interfaces you implement, such as `emma:core/search-assist@1`.
- `permissions.network`: exact hosts or `*.example.com`; empty means no network. `permissions.cache`: whether you use the cache.
  For a service the user hosts (an Invidious instance), list the text setting that holds its base URL in
  `permissions.networkSettings`: the host of that value joins your allow-list for that user only. A setting's declared
  default is not counted, so put a default host in `network` as well. A plugin that does this may declare the boolean setting
  `emma.allow-local-network` (write the warning in its `help`); only when the user turns it on may the plugin reach loopback
  and private addresses, which stay refused otherwise.
- `limits`: `cpuBudgetMs` per call and `memoryMb`.
- `entrypoint`: `path` (a `.wasm`) and `sha256`. You do not fill the digest in: `emma plugin pack` computes it.

## Try it before you ship it

`emma plugin run <dir|zip> <operation>` installs your plugin, unsigned, into a directory it throws away, and calls it
through the *same pipelines the host uses*. So it reports what the host would refuse: a node of a kind you did not
declare, an id that is too long, a cursor that never ends, an asset on a host your manifest does not allow, a
manifest that disagrees with the component. Operations: `describe`, `search [text]`, `children [node]`, `get <node>...`,
`assets <node> <role>`, `content <node> <role> [--out file]`. Nodes may be given as the plugin's own id or the full
`plugin.id:local-id`. `--json` output is the same JSON the HTTP API serves.

If your plugin declares settings (an account name, an API token), `--settings settings.json` gives it values the way a
user's screen would: a JSON object of setting key to text, number, boolean or (for a multi-choice) a list of text. The
values are checked against your declaration like a user's, so this also tests the declaration itself.

For automated tests, a plugin is a function from requests to responses: unit-test it natively in Rust, and use `emma
plugin run` in CI as an end-to-end check against the real host.

## Package and distribute

```sh
sh build.sh                                       # build, then pack unsigned into dist/: for development
sh build.sh --sign-key signer.pem --key-id release-2026 --repository my-repo --valid-days 30
emma plugin pack . --out dist/                    # what build.sh runs after the build, if you build another way
emma plugin install dist/com.me.notes-0.1.0.zip --repository my-repo
```

A host accepts a package only if its signature chains to a repository the host trusts: the repository's offline *root
key* signs a *delegation file* listing the keys allowed to sign plugins (with the plugin ids each may sign, a validity
window and a status), and a listed key signs your manifest, which carries the component's digest. The full scheme and
threat model are in [docs/architecture/plugin-trust.md](../architecture/plugin-trust.md). Generate a signing key with:

```sh
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out signer.pem
openssl pkey -in signer.pem -pubout -out signer.pub
```

TODO(rewrite): the repository operator's tooling is not built. Creating a root key, writing and signing a delegation
file, and publishing a catalog are done today with `PluginSigner` from code (see `tests/EMMA.TestSupport/TestPki.cs` for
a worked example). A `emma repo` command group belongs with the repository catalog client.

## C# and other languages (optional)

The core build needs no .NET-to-Wasm toolchain, and EMMA does not support one. If you would rather write C#,
[componentize-dotnet](https://github.com/bytecodealliance/componentize-dotnet) can produce a component from the same
`wit/` directory; it is experimental and depends on NativeAOT-LLVM and the WASI SDK. Whatever produces it, the component
must export the `plugin` world, and `emma plugin run` is the way to find out whether it does.
