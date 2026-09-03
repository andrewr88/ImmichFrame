# 001 — Swappable config catalog + reserved `admin` name

## Context

The admin config editor (plan: `misc/coding-team/admin-config-editor/`) will save config
and have it take effect without a restart. Two things block that today:

- `Program.cs:61` registers `IConfigCatalog` as a singleton built once from disk, and
  `ProfileRegistry` (`ImmichFrame.WebApi/Helpers/Profiles/ProfileRegistry.cs:16`) captures it
  in its primary constructor. Nothing can replace the catalog after startup.
- `ProfileRegistry` caches a `ProfileServices` per profile for the process lifetime
  (`_byProfile`, `ProfileRegistry.cs:18`). Each one captures its `IServerSettings` at
  construction (`ProfileServices.cs:19`), so a replaced catalog would not reach a cached entry.

Separately, the editor will live at `/admin` in the SPA. `ConfigCatalog.ReservedProfileNames`
(`ConfigCatalog.cs:26`) currently holds `api`, `static`, `swagger`, `default` — a profile named
`admin` would be served at `/admin` and shadow the page.

This task is groundwork only. No admin surface, no HTTP endpoint, no behaviour change for
existing clients.

## Objective

Make the current `IConfigCatalog` replaceable at runtime, give `ProfileRegistry` a way to drop
its cache, and reserve `admin`.

## Scope

- `ImmichFrame.WebApi/Helpers/Config/ConfigCatalog.cs` — add `admin` to `ReservedProfileNames`.
- `ImmichFrame.WebApi/Helpers/Config/` — new holder type(s) for the swappable catalog.
- `ImmichFrame.WebApi/Helpers/Profiles/ProfileRegistry.cs` — cache invalidation.
- `ImmichFrame.WebApi/Program.cs` — registration.
- `ImmichFrame.WebApi.Tests/Helpers/Config/ConfigCatalogTest.cs` — reserved-name coverage.
- `ImmichFrame.WebApi.Tests/Helpers/Profiles/ProfileResolutionTests.cs` — reload coverage,
  or a new test file alongside it if that reads better.

## Design constraints

**Keep `IConfigCatalog` resolvable as a service.** Three existing tests replace it wholesale
via `services.AddSingleton<IConfigCatalog>(...)` in `ConfigureTestServices`
(`AssetControllerTests.cs:80`, `ConfigControllerTests.cs:91`, `ProfileResolutionTests.cs:168`).
Those registrations must keep working unchanged. The suggested shape is a forwarding
implementation of `IConfigCatalog` that delegates `Default`, `ProfileNames`, `TryGet` and `Get`
to a swappable inner catalog — then every existing consumer (`ProfileRegistry`,
`UnknownProfileMiddleware.cs:18`, `ImmichServerVersionChecker.cs:34`) keeps injecting
`IConfigCatalog` and needs no change. If you find a cleaner shape, take it, but the three test
overrides above must still compile and pass untouched.

**Swap and invalidate must not be separable.** A caller that replaces the catalog without
clearing `ProfileRegistry._byProfile` leaves stale `ProfileServices` serving old settings, which
is a silent correctness bug. Expose exactly one entry point that does both. Callers of that entry
point arrive in task 003; for now it is exercised only by tests.

**Seeding stays lazy.** `Program.cs:61` builds the catalog inside a factory delegate, so a test
that overrides `IConfigCatalog` never touches the disk. Preserve that — do not load config
eagerly during service registration.

**In-flight requests keep the catalog they resolved.** That is acceptable and intended: nothing
in `ProfileServices` implements `IDisposable` and its `HttpClient`s come from
`IHttpClientFactory` (`Program.cs:65`), so a superseded graph is simply garbage collected once
the last request holding it completes. Document this in an XML doc comment on the swap entry
point rather than leaving it to be rediscovered.

## Non-goals

- No admin controller, endpoint, authentication or authorization (tasks 002/003).
- No writing of config files, and no reloading from disk (task 003).
- Do not change `ConfigLoader`'s parsing, the v1 fallback, or `IConfigDocument`.
- Do not change how `CurrentProfile` reads the profile from the query string.

## Acceptance criteria

- A profile named `admin` (any casing) is rejected by `ConfigCatalog` with the same
  reserved-name message as `api`/`static`/`swagger`/`default`.
- After a swap, resolving `IServerSettings` through the scoped delegates in `Program.cs:77-83`
  yields the new catalog's settings, and `ProfileRegistry.For(...)` returns a rebuilt
  `ProfileServices` rather than the cached one.
- Swapping to a catalog that no longer declares a profile makes `ProfileRegistry.For(thatName)`
  throw `ProfileNotFoundException`, and `UnknownProfileMiddleware` 404 it.
- `dotnet build ImmichFrame.sln` clean; `make test-core` and `make test-webapi` green, with the
  three existing `IConfigCatalog` test overrides unmodified.

## Working agreements

- Shared working tree. Never run `git stash`, `git reset`, `git restore`, `git checkout --` or
  `git clean`; never `git add -A`. Stage explicit paths only, and only files in Scope above.
  Any change you did not author is another developer's in-flight work — leave it alone.
- Commit messages must **not** carry a `Co-Authored-By` trailer (`CLAUDE.md`, Git conventions).
- No version attribute on any `<PackageReference>` — versions live in
  `Directory.Packages.props` only. This task should need no new package.
- Follow the file's existing style: file-scoped namespaces and primary constructors in the
  profile/config classes, XML doc comments explaining *why* (`ProfileServices.cs` is the model).
