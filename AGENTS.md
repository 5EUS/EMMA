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
- TODO(rewrite): this file is still the generic template text. Replace it with
  EMMA-specific guidance (hexagonal layering, the `emma:core` WIT contract, the
  `IPluginInstance` seam) once phase 1 settles the architecture. Its template line
  `dotnet publish src/*/*.csproj` was wrong for a multi-project repo and is fixed above.
- TODO(rewrite): `src/EMMA.Api/README.md` has an empty `## Endpoints` heading left
  over from the skeleton; fill it in or delete it in phase 7.
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
  inject a clock abstraction.
- **Hashing:** no MD5 or SHA1.
- **Style:** StyleCop is enforced. Public and internal members need XML docs;
  `using` directives go outside the namespace, `System` first; files end with a newline.
  Namespaces and type names are PascalCase.
- **Dependencies:** add a `PackageVersion` to `Directory.Packages.props` and a
  versionless `PackageReference` in the project. Prefer no new dependency.
- **Tests:** add or update a test for behavior you change. Test classes must be
  `public` (xUnit cannot discover internal ones).
