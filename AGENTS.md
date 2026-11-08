# AGENTS.md

Guidance for AI coding agents working in this repository.

## Commands

```sh
dotnet restore --locked-mode                              # restore; fails if a lock file is stale
dotnet build -c Release --no-restore                      # build; warnings are errors
dotnet test -c Release --no-build                         # run tests
dotnet format whitespace --no-restore                     # apply formatting
sh scripts/development/check.sh                           # everything CI checks
dotnet publish src/EMMA.Api/EMMA.Api.csproj -c Release -r <rid> --self-contained   # NativeAOT publish
```

Run `scripts/development/check.sh` before declaring work finished. After changing
package references, run a plain `dotnet restore` to regenerate `packages.lock.json`
and commit the result.

If the repository has Rust under `native/`, `check.sh` also runs `cargo fmt --check`,
`cargo clippy -D warnings` and `cargo test --locked`, and `cpd.sh` checks Rust for
duplicated code. Do not weaken these either: fix the code. Keep generated Rust in
`generated/` or `*.generated.rs`.

## Layout

- `src/` holds the EMMA libraries plus the `EMMA.Api` host (the only NativeAOT
  project so far); `tests/` will hold xUnit v3 projects, one per `src/` project.
- Layering is hexagonal and enforced by project references: `EMMA.Domain` (no dependencies) <-
  `EMMA.Application` (ports and pipelines) <- `EMMA.Infrastructure`, `EMMA.Storage`, `EMMA.PluginHost`
  <- `EMMA.Api`. `EMMA.WasmRuntime` is the C# binding to the native host and depends only on Domain.
- The plugin contract is the WIT package `emma:core` in `src/EMMA.Contracts/wit`. The core never switches on
  a media type; kinds are open strings the plugin declares. Read `docs/architecture/contract.md` before
  changing the contract, and keep the vendored copies of the WIT (for example in plugin repositories) in step.
- Plugins run behind `IPluginInstance` (`EMMA.PluginHost/Instances`); the Wasm transport is the only one.
  Do not let Wasm or native-host types leak into it. New transports must pass `tests/EMMA.PluginConformance`.
- The native Wasm host (`native/emma-wasm`) and the fixture plugins must be built before the .NET tests:
  `sh native/build-plugins.sh` and `sh native/build-host.sh` (`check.sh` does both; needs the
  `wasm32-wasip2` Rust target). A missing artifact fails the build on purpose.
- `EMMA.Api` is REST plus source-generated JSON only (no gRPC) and the composition root
  (`Composition/EmmaServices.cs`). Read `docs/architecture/api.md` before changing authentication, rate
  limiting or a response shape. Authentication is default-deny and runs before the rate limiter, which is
  keyed by the authenticated client; never key anything on a claim the caller can make up. Every response,
  including errors, is built by `ApiResults`; every wire type lives in `Contracts/` and `ApiJsonContext`.
  Nodes travel as `ref` query parameters, not path segments. `tests/EMMA.Api.Tests` runs the real host on a
  loopback port around a fake gateway (`ApiTestServer`); `WasmEndToEndTests` also needs the native artifacts.
- All build settings live in `Directory.Build.props`; package versions live in
  `Directory.Packages.props`. A `.csproj` should stay nearly empty.
- `BannedSymbols.txt` lists banned APIs; `.editorconfig` sets analyzer severities.

## Rules

- **Never weaken the build.** Do not set `TreatWarningsAsErrors=false`, turn off
  nullable, disable the AOT/trim analyzers, remove lock files, or lower a severity in
  `.editorconfig` to get a build through. `Directory.Build.targets` fails the build
  (STRICT0001-0004) if you try. Fix the code instead.
- **No `#pragma warning disable` or `[SuppressMessage]`** unless there is no
  alternative; include a comment naming the reason. This applies to banned APIs
  (`RS0030`) too.
- **NativeAOT:** no reflection-based serialization (use a `JsonSerializerContext`),
  no `Activator.CreateInstance`, no runtime code generation.
- **Async:** await tasks; never `.Wait()`, `.Result` or `Thread.Sleep`. Use
  `.ConfigureAwait(false)` on library awaits (CA2007).
- **Time:** do not use `DateTime.Now`/`UtcNow` or `DateTimeOffset.Now`/`UtcNow`;
  inject `TimeProvider` (tests use `FakeTimeProvider`).
- **Nulls:** nullable reference types cover non-null parameters (`CA1062` is off in
  `.editorconfig`), so do not write `ArgumentNullException.ThrowIfNull` for them. Check
  only where the compiler cannot see: FFI exports, Wasm and JSON input, and invariants a
  null would silently break (`Outcome.Failure`). Prefer primary constructors; keep an
  explicit constructor only when it transforms its arguments (`CapabilityPolicy`). A
  primary-constructor parameter used in a field initializer *and* in methods trips
  `CS9124`, so inject the finished object instead (see `PluginCallExecutor`).
- **Tests:** use `.ConfigureAwait(true)` in test methods; it satisfies both `CA2007` and
  xUnit's `xUnit1030`.
- **No raw string literals (`"""`) in C#.** PMD's C# lexer cannot parse them: CPD reports
  `Lexical error` and skips the file (exit 5), which silently hides duplicates. Use verbatim
  strings (`@"..."`) for multi-line text.
- **SQL:** statements are constants (never built from input) and values go in as
  parameters. `SqliteDatabase` is the only code that sets `CommandText` (its single `CA2100`
  pragma relies on this). The schema is one script, `src/EMMA.Storage/Sql/001_initial.sql`,
  edited in place until the first release (no deployed database exists). After release,
  never edit a released script: add the next number and register it in `Migrations.cs`.
- **Hashing:** no MD5 or SHA1.
- **Style:** StyleCop is enforced. Public and internal members need XML docs;
  `using` directives go outside the namespace, `System` first; files end with a newline.
  Namespaces and type names are PascalCase.
- **Dependencies:** add a `PackageVersion` to `Directory.Packages.props` and a
  versionless `PackageReference` in the project. Prefer no new dependency.
- **Tests:** add or update a test for behavior you change. Test classes must be
  `public` (xUnit cannot discover internal ones).
