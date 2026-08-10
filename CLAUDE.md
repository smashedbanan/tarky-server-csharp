# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
scripts/decompress-assets.sh                        # REQUIRED before first build (or .ps1 on Windows) - unpacks looseLoot.7z
dotnet build                                        # solution is server-csharp.slnx (README's .sln path is stale)
dotnet test                                         # all tests (Testing/UnitTests, NUnit)
dotnet test --filter "FullyQualifiedName~MongoIdTests"   # single fixture
dotnet test --filter "Name=EveryRegisteredServiceCanBeResolved"   # single test
csharpier format .                                  # run before opening a PR
```

The server refuses to start unless the working directory contains `sptLogger.json`/`sptLogger.Development.json`, so run
the built `SPT.Server` executable from its output directory (`SPTarkov.Server/bin/<Config>/net10.0`), not via
`dotnet run` from the repo root. IDE profiles for this are in `SPTarkov.Server/Properties/launchSettings.json`.

Publish flags (`dotnet publish`) feed the generated `ProgramStatics` class: `-p:SptVersion=`, `-p:SptCommit=`,
`-p:SptBuildTime=`, `-p:SptBuildType=` (`LOCAL`/`DEBUG`/`RELEASE`/`BLEEDINGEDGE`/`BLEEDINGEDGEMODS`). Defaults live in
`Build.props`.

This fork has no CI: `.github/` (workflows, CODEOWNERS, issue templates, funding config) was removed. Formatting,
tests, and build checks are local-only — nothing enforces them on push. Upstream formats JSON under `SPT_Data` with
Biome; there is no committed `biome.json` to reproduce that locally.

Upstream stores its large database JSON files (`looseLoot.json`, `items.json`) via a custom LFS server and rejects PRs
that touch them — open an issue instead. This fork instead bundles those files as a plain 7z archive
(`Libraries/SPTarkov.Server.Assets/looseLoot.7z`, extracted by `scripts/decompress-assets.sh`/`.ps1`), so no LFS
setup is needed here and normal PRs touching them are fine.

## Architecture

Five projects matter: `SPTarkov.Server` (host/entry point + mod loading), `Libraries/SPTarkov.Server.Core` (all game
logic), `Libraries/SPTarkov.Server.Web` (Blazor admin panel), `Libraries/SPTarkov.DI` (the attribute-driven container),
`Libraries/SPTarkov.Server.Assets` (SPT_Data: configs, JSON database, images; the largest JSON files ship as
`looseLoot.7z`, see Commands above).

### Request pipeline

The server is ASP.NET Core but does **not** use MVC controllers or attribute routing. `Program.cs` installs one
catch-all middleware into `HttpServer.HandleRequestAsync`, which picks an `IHttpListener` (normally `SptHttpListener`).
From there:

```
HttpRouter → StaticRouter (exact URL match) or DynamicRouter (substring match)
           → *Callbacks (deserialize/serialize, HttpResponseUtil.GetBody)
           → *Controller (orchestration)
           → Services / Helpers / Generators (logic)  ←→ Database tables (DI singletons)
```

Routers are declarative: a router subclass passes a list of `RouteAction<TRequest>` records to its base constructor
(see `Routers/Static/WeatherStaticRouter.cs`). Adding an endpoint means touching the router, the callback, and usually
a controller — not adding an `[HttpGet]`.

`/client/game/profile/items/moving` fans out through `ItemEventRouter` subclasses in `Routers/ItemEvents/` instead,
keyed by the action name in the body. `SaveLoadRouter` subclasses run on profile load to migrate/patch saved data.

### Dependency injection

Classes are registered by putting `[Injectable]` on them; `DependencyInjectionHandler` scans assemblies and registers
each type against itself, its interfaces, and its base types. `InjectionType` selects Singleton/Transient/Scoped/
HostedService. `[Injectable(TypePriority = ...)]` controls both registration order and load order.

`ProgramHelpers.RegisterSptServicesAsync` is the single place every service gets registered — `DependencyInjectionValidationTests`
builds the exact same container (with mods on and off) so a broken registration fails the test run rather than a
launch. Keep new registrations there.

Lifecycle interfaces in `Core/DI/`:
- `IOnLoad` — startup work, ordered by `OnLoadOrder` constants (`Watermark` → `Preload` → `GameCallbacks` → … →
  `PostLoad`). Anything below `GameCallbacks` runs pre-web-start via `RunPreSptLoadCallbacks` (this is what lets mods
  mutate `HttpConfig` before Kestrel binds); the rest runs in `SPTStartupHostedService`.
- `IOnUpdate` — polled every 5s by `SPTStartupHostedService`.
- `IOnDIConstruct` — static hook letting a mod add its own registrations.

### Startup order

`ProgramStatics.Initialize` → early logger → `ConfigLoader` (maps `SPT_Data/configs/*.json` to `BaseConfig` types via
the `ConfigTypes` enum) → throwaway "early" provider → `ModLoader` (validate, prepatch, load assemblies) →
`DatabaseImporter` (hash-verified outside DEBUG) → real `WebApplicationBuilder` with database tables registered as
singletons → pre-SPT-load callbacks → Kestrel on HTTPS with a self-generated cert.

The mod-loading split in `Program.StartServerAfterModLoading` is deliberate: merging it back breaks prepatching by
forcing types into context too early.

### Mods

A mod DLL in `user/mods/` implements exactly one `IModMetadata` (GUID, semver, `SptVersion` range, dependencies,
incompatibilities) plus any number of `[Injectable]` classes. `Testing/TestMod` is the reference implementation.
`HasPrepatcher = true` opts into enum prepatching from `user/patchers/{ModGuid}`. Runtime method patching uses
`SPTarkov.Reflection` (`AbstractPatch`/`PatchManager`).

### Build-time code generation

Two non-obvious steps run during build, both in `SPTarkov.Server.Core.csproj`:
- `GenerateProgramStatics` writes `Utils/ProgramStatics.Generated.cs` from MSBuild properties. Never edit it.
- On Release/publish, `Tools/Ceciler` rewrites the compiled `SPTarkov.Server.Core.dll` with Mono.Cecil, injecting a
  `[JsonExtensionData]` property into every model type under `Models` so unknown client JSON round-trips instead of
  being dropped. This means Release binaries differ structurally from Debug ones; `PrepatchIsolationTests` guards it.

`SPTarkov.Server.Assets` hashes SPT_Data into `checks.dat` on Release builds, which `DatabaseImporter` verifies at
startup.

## Style

CSharpier plus `.editorconfig` handle formatting. The rules a formatter can't catch, from CONTRIBUTING.md:

- Always brace single-line bodies.
- File-scoped namespaces; `using` directives outside the namespace, `System.*` first.
- No `this.` qualification. Private/internal fields are `_camelCase`; consts are `PascalCase`.
- Language keywords over BCL types (`string`, not `String`).
- Block bodies for methods/constructors/properties/accessors — no expression-bodied members (lambdas are fine).

AI-generated code is permitted in this fork until a new policy is drafted (CONTRIBUTING.md).
