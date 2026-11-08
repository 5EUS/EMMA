# emma, the command-line tool

Manage the plugins an EMMA host serves, and develop new ones. Built with NativeAOT:

```sh
dotnet publish src/EMMA.Cli -c Release -r linux-x64 --self-contained    # produces ./emma
```

Run `emma --help` for the full list. In short:

| Command | Does |
|---|---|
| `emma plugin list` / `info <id>` | What is installed, and what a plugin declares |
| `emma plugin install <zip> --repository <id>` | Install a package from a trusted repository (signature checked) |
| `emma plugin enable` / `disable` / `remove <id>` | Change what is served |
| `emma plugin new <dir> --id com.me.thing` | Scaffold a Rust plugin from the template, with the contract |
| `emma plugin pack <dir> [--sign-key ...]` | Fill in the component's digest, validate, optionally sign, and zip |
| `emma plugin run <dir\|zip> <op> ...` | Try a plugin in a throwaway host, with the same checks the real one applies |

`--json` prints the same shapes as the HTTP API (`EMMA.Api.Contracts`). `--data-dir` picks the state directory; by
default it is the one the HTTP API uses, so what you install here is what the API serves after it restarts.

## Trust

`emma plugin install` verifies signatures exactly as the host does. Which repositories are trusted comes from
`<data-dir>/config.json`, in the same shape the native library takes (the `data_dir` may be left out):

```json
{ "trust": { "repositories": [ { "id": "main", "root_keys": { "key-1": "-----BEGIN PUBLIC KEY-----..." } } ] } }
```

and the repository's delegation file goes in `<data-dir>/delegations/<repository>.delegations.json`. With no trusted
repository nothing can be installed: there is no way to switch signature checking off from the command line.
`emma plugin run` is the exception that exists for authors: it installs your unsigned work into a directory it deletes
afterwards, and never touches your real data directory.

## Exit codes

`0` done, `1` the command ran and could not do what was asked (the reason is on standard error), `2` the command line
is wrong.

See [docs/plugins/authoring.md](../../docs/plugins/authoring.md) for writing a plugin.
