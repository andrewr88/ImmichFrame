# ImmichFrame Architecture

A digital-photo-frame front end for an [Immich](https://immich.app/) server: an ASP.NET Core 8
Web API that talks to one or more Immich accounts, plus a SvelteKit single-page client that the
API serves as static files from `wwwroot`. Everything ships as one container image.

> Scope: this file describes stack, layout, conventions, and commands. It is a scout report, not a
> spec — see `docs/` (Docusaurus, published to https://immichframe.dev) for user documentation.

## Detected stack

| Area | Choice | Evidence |
| --- | --- | --- |
| Backend | C# / .NET 8 (`net8.0`), nullable enabled, implicit usings | `Directory.Build.props`, every `*.csproj` |
| Backend web | ASP.NET Core (`Microsoft.NET.Sdk.Web`), controllers + Swashbuckle | `ImmichFrame.WebApi/ImmichFrame.WebApi.csproj`, `ImmichFrame.WebApi/Program.cs` |
| NuGet | Central Package Management (versions pinned in one file, `PackageReference` carries no version) | `Directory.Packages.props` |
| Frontend | SvelteKit 2 / Svelte 5, TypeScript, Vite 7, Tailwind 3, `adapter-static` (SPA, `ssr = false`, `prerender = true`) | `immichFrame.Web/package.json`, `svelte.config.js`, `src/routes/+layout.ts` |
| Docs site | Docusaurus 3 + React 19 | `docs/package.json` |
| Containers | Multi-stage `Dockerfile` (dotnet sdk → publish → node build → `aspnet:8.0-jammy` final, non-root UID 1000) | `/Dockerfile` |
| Compose | Prod and dev compose files | `docker/docker-compose.yml`, `docker/docker-compose.dev.yml` |
| CI | GitHub Actions: tests on every push/PR, CodeQL, multi-arch image publish on `v*` tags, GH Pages docs deploy | `.github/workflows/*` |
| Test stack | NUnit 4 + NUnit.Analyzers + Moq; `AwesomeAssertions` and `Microsoft.AspNetCore.Mvc.Testing` in the WebApi tests only | test `*.csproj` files |

### Solution layout

```
ImmichFrame.sln
├─ ImmichFrame.Core          class library — Immich API client, asset pools, settings interfaces, services
├─ ImmichFrame.WebApi        ASP.NET Core host — controllers, config loading, profiles, auth
├─ ImmichFrame.Core.Tests    NUnit + Moq unit tests (pools, account selection)
└─ ImmichFrame.WebApi.Tests  NUnit + WebApplicationFactory integration tests (controllers, config)

immichFrame.Web             SvelteKit SPA (built into wwwroot by the Dockerfile)
docs                        Docusaurus site
docker                      compose files, example Settings.json / Settings.yml, example.env
openApi/swagger.json        ImmichFrame's own OpenAPI spec, checked in; source for the TS client
audit                       codebase-audit kit driven by the `audit-*` Make targets
templates/release-template.md  release-notes template referenced by CLAUDE.md
```

Dependency direction is one-way: `WebApi → Core`. `Core` knows nothing about ASP.NET; it depends
on interfaces (`IServerSettings`, `IAccountSettings`, `IGeneralSettings`) that `WebApi` implements
with its config models.

## How the pieces fit

### Two generated API clients, in opposite directions

1. **Immich → Core.** `ImmichFrame.Core/OpenAPIs/immich-openapi-specs.json` is fed to NSwag at
   build time (`<OpenApiReference>` in `ImmichFrame.Core.csproj`) producing the `ImmichApi` class
   in namespace `ImmichFrame.Core.Api`. `ImmichFrame.Core/Api/ImmichApi.cs` is a small hand-written
   `partial` that adds the constructor and a range-request video method — the rest is generated,
   so never edit generated members.
2. **ImmichFrame → web client.** `make api` curls the running app's Swagger doc into
   `openApi/swagger.json`, then `npm run api` (oazapfts) regenerates
   `immichFrame.Web/src/lib/immichFrameApi.ts`. That file is marked `DO NOT MODIFY`.

### Configuration profiles

Config is loaded once at startup from `Settings.json` / `Settings.yml` (path from
`IMMICHFRAME_CONFIG_PATH`, else a `Config` directory next to the binary). `ConfigLoader` tries the
current schema first, then falls back to the v1 schema via `ServerSettingsV1Adapter`, for both JSON
and YAML. The result is an `IConfigCatalog`: a default configuration plus named profiles.

A request selects a profile with the `?profile=` query parameter (query, not header — asset URLs
are consumed bare by `<img src>`/`<video src>`). The chain is:

- `UnknownProfileMiddleware` — 404s an unknown profile before anything downstream resolves it.
- `ICurrentProfile` / `CurrentProfile` (scoped) — reads the query parameter.
- `ProfileRegistry` (singleton) — `ConcurrentDictionary<string, Lazy<ProfileServices>>`, builds each
  profile's object graph on first use and caches it until the configuration is swapped
  (`SwappableConfigCatalog.Swap` replaces the catalog and drops the cache in one step).
- `ProfileServices` — the expensive per-profile pieces: HTTP clients, asset pools, API caches, and a
  per-profile `BloomFilterAssetAccountTracker`.

`Program.cs` registers `ProfileServices` itself as **scoped**, resolved from the registry once per
request, and then registers `IServerSettings`, `IGeneralSettings`, `IClientSettings`,
`IServerBehaviorSettings`, `IWeatherService`, `ICalendarService`, and `IImmichFrameLogic` as
**scoped** delegates reading from that one pinned instance rather than each asking the registry
again — so a configuration swap landing mid-request cannot split a request across two
configurations, and controllers keep injecting the same interfaces they always have, unaware of
profiles entirely.

Profile names are validated in `ConfigCatalog`: `^[A-Za-z0-9_-]{1,64}$`, case-insensitive, with
`api`, `static`, `swagger`, `admin`, and `default` reserved.

### Asset selection: the pool hierarchy

`PooledImmichFrameLogic` builds an `IAssetPool` per account from that account's settings
(`BuildPool`): an `AllAssetsPool` when nothing is narrowed, otherwise a `MultiAssetPool` composing
`FavoriteAssetsPool`, `MemoryAssetsPool`, `AlbumAssetsPool`, `PersonAssetsPool`, `TagAssetsPool`.
`AggregatingAssetPool` / `QueuingAssetPool` / `CachingApiAssetsPool` are the shared machinery;
`MultiAssetPool` picks a delegate weighted by asset count. Across accounts,
`MultiImmichFrameLogicDelegate` plus `TotalAccountImagesSelectionStrategy` and
`BloomFilterAssetAccountTracker` decide which account serves a given asset id.

This is the busiest area of the codebase — see hotspots below.

### Auth

Single shared secret. `ImmichFrameAuthenticationHandler` (scheme `ImmichFrameScheme`) succeeds
anonymously when no `AuthenticationSecret` is configured or the endpoint carries no `[Authorize]`;
otherwise it compares a `Bearer` token. `CustomAuthenticationMiddleware` runs the scheme for every
request and short-circuits with a 401 and the failure message.

## Conventions

### C#

- **DI**: constructor injection throughout; controllers take `ILogger<T>` plus interfaces. Newer
  files use C# 12 primary constructors with `_name` parameters (`ConfigLoader`, `ProfileRegistry`,
  `UnknownProfileMiddleware`); older files use classic constructors. Both are current in the tree.
- **File-scoped namespaces** in newer code (`namespace ImmichFrame.Core.Logic.Pool;`), block-scoped
  in older files. No enforcement either way — there is no `.editorconfig` and no `dotnet format` in
  CI.
- **Async**: `Task`-returning methods with `CancellationToken ct = default` on pool APIs.
- **Logging**: `ILogger<T>` with structured message templates and named placeholders —
  `_logger.LogDebug("Assets requested by '{sanitizedClientIdentifier}'", …)`. Logging is configured
  in `Program.cs` from the `LOG_LEVEL` env var, single-line console with a `yy-MM-dd HH:mm:ss`
  timestamp, with ASP.NET Core and SpaProxy noise filtered to Warning.
- **Untrusted input into logs is sanitized** with the `SanitizeString()` extension before it is
  logged. Every controller does this for `clientIdentifier`.
- **Errors**: a domain exception hierarchy in `ImmichFrame.Core/Exceptions/ImmichFrameExceptions.cs`,
  all deriving from `ImmichFrameException` (`AssetNotFoundException`, `ProfileNotFoundException`,
  `SettingsNotValidException`, …). Controllers throw these rather than returning error results;
  `ApiException` from the generated client is caught narrowly where a status code maps to a
  response (e.g. `416` in `AssetController.GetAsset`).
- **Comments explain *why***, not what — the profile classes are the model to follow
  (`ProfileRegistry`, `ProfileServices`, `CurrentProfile` all document the reasoning behind the
  design choice in XML doc comments).
- `InternalsVisibleTo` exposes `ImmichFrame.WebApi` internals to `ImmichFrame.WebApi.Tests`.

### Tests

- NUnit: `[TestFixture]` class, `[SetUp]` method, `[Test]` methods.
- Naming: `Method_Scenario_ExpectedResult` (`LoadAssets_NoIncludedAlbums_ReturnsEmpty`).
- Arrange / Act / Assert marked with comments.
- Moq for collaborators, including mocking the generated `ImmichApi` (`new Mock<ImmichApi>("", null)`)
  — its methods are virtual, so no wrapper interface is used.
- Assertions: `Assert.That(x, Is.EqualTo(y))` in `ImmichFrame.Core.Tests`; the WebApi tests also
  have `AwesomeAssertions` available.
- WebApi tests are integration tests over `WebApplicationFactory<Program>` (`Program.cs` ends with
  `public partial class Program { }` to enable this), with services swapped in `ConfigureTestServices`
  and JSON/YAML fixtures under `ImmichFrame.WebApi.Tests/Resources/` copied to output via the csproj.

### Frontend

- Prettier with **tabs**, single quotes, no trailing commas, print width 100
  (`immichFrame.Web/.prettierrc`). ESLint flat config layering `js.configs.recommended`,
  `typescript-eslint`, `eslint-plugin-svelte`, and `eslint-config-prettier`.
- Type checking via `svelte-check` (`npm run check`), not `tsc` directly.
- API access goes exclusively through the generated `$lib/immichFrameApi`; state lives in small
  hand-rolled Svelte stores (`src/lib/stores/*.store.ts`) that wrap `writable`.
- Dev server proxies `/api` and `/static` to `http://localhost:5217` (`vite.config.ts`).

## Lint and test commands

There is no single do-everything command. The minimal set:

```bash
# Backend
dotnet build ImmichFrame.sln
make test-core        # dotnet test ImmichFrame.Core.Tests/ImmichFrame.Core.Tests.csproj
make test-webapi      # dotnet test ImmichFrame.WebApi.Tests/ImmichFrame.WebApi.Tests.csproj
make dev              # dotnet run --project ./ImmichFrame.WebApi   (port 5217)

# Frontend (cwd: immichFrame.Web)
npm run lint          # prettier --check . && eslint .
npm run format        # prettier --write .
npm run check         # svelte-kit sync && svelte-check
npm run dev           # vite dev

# Docs
make docs             # npm --prefix docs run start

# Regenerate the TypeScript API client (app must be running)
make api

# Containers
make docker-build-prod
make docker-prod
```

CI (`.github/workflows/test.yml`) runs exactly the two `dotnet test` invocations on .NET 8.0.x for
every push and PR on every branch. **Frontend lint/check is not run in CI** — run it locally.

## Project structure hotspots

Entry points:

- `ImmichFrame.WebApi/Program.cs` — the whole composition root: logging, config, profile DI, auth,
  middleware order, static files, SPA fallback, startup version check.
- `immichFrame.Web/src/routes/+page.ts` / `+page.svelte` — SPA entry; loads config before render.
- `Dockerfile` — the only place the two halves are combined (`build-node` output → `wwwroot`).

Highest churn over the last 200 commits (change count → why it moves):

| File | Commits | Why |
| --- | --- | --- |
| `immichFrame.Web/src/lib/components/home-page/home-page.svelte` | 35 | The slideshow itself — every UI feature lands here |
| `ImmichFrame.Core/Logic/PooledImmichFrameLogic.cs` | 23 | Per-account orchestration; pool composition and asset fetching |
| `ImmichFrame.WebApi/Controllers/AssetController.cs` | 15 | The main API surface: assets, ranges, webhooks |
| `immichFrame.Web/src/lib/components/elements/asset.svelte` | 13 | Asset rendering (image/video, transitions) |
| `ImmichFrame.Core/Logic/Pool/AllAssetsPool.cs` (+ its tests) | 11 each | Default selection path; filter changes land here |
| `ImmichFrame.Core/Logic/MultiImmichFrameLogicDelegate.cs` | 9 | Multi-account fan-out |
| `docs/docs/getting-started/configuration.md` | 9 | Every new setting needs documenting |

Rule of thumb: a new user-visible setting touches `IClientSettings`/`IGeneralSettings` in Core, the
`ServerSettings`/`ClientSettingsDto` models in WebApi, the regenerated `immichFrameApi.ts`, the
Svelte component that consumes it, and `docs/docs/getting-started/configuration.md`.

## Do and don't

**Do**

- Pin NuGet versions in `Directory.Packages.props` only; leave `<PackageReference>` version-less
  (`ImmichFrame.Core.csproj`).
- Resolve per-request settings through the scoped interfaces registered in `Program.cs`; let
  `ProfileRegistry` own profile lookup (`ImmichFrame.WebApi/Helpers/Profiles/ProfileRegistry.cs`).
- Sanitize client-supplied strings with `SanitizeString()` before logging
  (`ImmichFrame.WebApi/Controllers/AssetController.cs`, `UnknownProfileMiddleware.cs`).
- Throw domain exceptions from `ImmichFrame.Core/Exceptions/ImmichFrameExceptions.cs`.
- Extend the generated `ImmichApi` through the hand-written `partial` in
  `ImmichFrame.Core/Api/ImmichApi.cs`.
- Write structured log templates with named placeholders, never string interpolation.
- Compose behaviour by wrapping `IAssetPool` implementations rather than branching inside one pool
  (`ImmichFrame.Core/Logic/Pool/`).
- Document *why* a design choice was made in XML doc comments when it is non-obvious
  (`ProfileServices.cs` on the per-profile bloom filter is the canonical example).

**Don't**

- Don't hand-edit `immichFrame.Web/src/lib/immichFrameApi.ts` or the NSwag-generated `ImmichApi`
  members — both are regenerated (`make api`; the `<OpenApiReference>` build step).
- Don't add a version attribute to a `<PackageReference>` — central package management will reject it.
- Don't inject `IConfigCatalog` or `ProfileRegistry` into controllers; no controller in the tree
  knows profiles exist.
- Don't add an `appsettings` binding for user configuration — user config comes from
  `Settings.json`/`Settings.yml` through `ConfigLoader`, and both the current and v1 schemas must
  keep loading.
- Don't leak server-only settings into `ClientSettingsDto`; `ConfigControllerTests` explicitly
  asserts that keys, webhooks, calendars, and the auth secret never reach the client.
- Don't reach for a new assertion library — NUnit constraint syntax (plus AwesomeAssertions in the
  WebApi tests) is what is here.
- Don't rely on CI to catch frontend problems; `test.yml` only runs `dotnet test`.

## Open questions

- No `.editorconfig` and no `dotnet format` step, so C# style (file-scoped namespaces, primary
  constructors, brace placement) is inconsistent between older and newer files. Worth settling if
  formatting churn becomes a nuisance.
- `audit/` ships a Go adapter only (`audit/adapters/go.sh`); the Makefile defaults
  `AUDIT_ADAPTER=dotnet`, so the `audit-*` targets stay inert until `audit/adapters/dotnet.sh`
  exists.
- Frontend `npm run lint` / `npm run check` are not wired into any workflow.
