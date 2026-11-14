# Template Plugin

A working EMMA plugin in Rust. It serves two made-up kinds from memory, so it builds and runs with no network.
Replace the sample data with calls to your source.

## Build and try it

You need Rust with the Wasm component target, and the `emma` command-line tool:

```sh
rustup target add wasm32-wasip2
sh build.sh --no-pack               # cargo build for wasm32-wasip2, then copy the result to plugin.wasm

emma plugin run . describe          # what the plugin declares
emma plugin run . search "collection 2"
emma plugin run . children c1
emma plugin run . content c1-e2 text
```

`emma plugin run` installs your plugin unsigned into a throwaway directory and calls it through the same pipelines
EMMA uses, so it reports everything EMMA would: kinds you did not declare, ids that are too long, assets outside
your network permissions.

## What to change

1. `plugin.json`: the `id` (never change it once published), `name`, `version`, `kinds` (must equal what `describe`
   returns), `permissions.network` (the hosts you may fetch, e.g. `"api.example.com"` or `"*.cdn.example.com"`) and `limits`.
2. `src/lib.rs`: the sample data. `search`, `children` and `get` return nodes; `resolve` returns the bytes (or a URL
   or token) for a node and role. Network calls go through `host::http_fetch`.
3. The kinds and roles. They are yours to name; EMMA never switches on them. If your content follows an existing
   client profile (paged images, video, audio), name it in `KindInfo::profile`.

## Package and install

```sh
sh build.sh                                          # build, then pack an unsigned package into dist/
emma plugin install dist/com.example.template-0.1.0.zip --repository dev
```

`build.sh` passes any other arguments to `emma plugin pack`, so a signed package for distribution is
`sh build.sh --sign-key key.pem --key-id my-key --repository my-repo`. It finds the crate name in `Cargo.toml`, and
the `EMMA` environment variable names the `emma` tool if it is not on the PATH. See
`docs/plugins/authoring.md` in the EMMA repository for signing and repositories.

The `wit` directory is the EMMA contract, copied here by `emma plugin new`. Do not edit it; update it by creating a
fresh template when EMMA's contract version changes.
