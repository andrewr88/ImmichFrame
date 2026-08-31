# 001 — Step 2: profile resolution tests + auth handler secret read

## Context

Branch `custom`. Feature: a client picks one of several named settings configs at
connect time (`?profile=kitchen` on the API). Step 1 (`IConfigCatalog` + document-level
merge) is committed as `f5ace18`. Step 2 (`ProfileRegistry` + scoped DI) is written but
**uncommitted and untested** — that is what this task closes out.

Read first, they are the subject under test:

- `ImmichFrame.WebApi/Helpers/Profiles/ProfileRegistry.cs`
- `ImmichFrame.WebApi/Helpers/Profiles/ProfileServices.cs`
- `ImmichFrame.WebApi/Helpers/Profiles/CurrentProfile.cs`
- `ImmichFrame.WebApi/Helpers/Profiles/UnknownProfileMiddleware.cs`
- `ImmichFrame.WebApi/Helpers/Config/ConfigCatalog.cs`
- `ImmichFrame.WebApi/Program.cs` (the scoped re-registration block)

`ConfigCatalog`'s ctor is
`ConfigCatalog(IServerSettings defaultSettings, IEnumerable<KeyValuePair<string, IServerSettings>>? profiles = null)`
(`ConfigCatalog.cs:30`). `ProfileNotFoundException` lives at
`ImmichFrame.Core/Exceptions/ImmichFrameExceptions.cs:35`. The concrete settings models
(`ServerSettings`, `GeneralSettings`, `ServerAccountSettings`) are in
`ImmichFrame.WebApi/Models/ServerSettings.cs`.

## Objective

Two independent pieces of work.

### A. `ImmichFrame.WebApi.Tests/Helpers/Profiles/ProfileResolutionTests.cs` (new file)

Unit tests over `ProfileRegistry`, built on a minimal `ServiceProvider`
(`new ServiceCollection().AddLogging().AddHttpClient().BuildServiceProvider()` is
enough — no constructor in the per-profile graph performs network I/O at build time):

1. `For("x")` called twice returns the same `ProfileServices` instance.
2. Two different profile names yield different `ProfileServices` **and** different
   `.Logic` instances.
3. `For(null)`, `For("")` and `For("default")` all return the one same instance.
4. An unknown profile name throws `ProfileNotFoundException`.
5. Each profile's `GeneralSettings` reflects that profile's own config (give the
   profiles distinguishable values, e.g. different `Interval`).

Then the end-to-end test — **this is the one that matters**, it is the only thing that
proves the scoped DI in `Program.cs` actually works:

6. `WebApplicationFactory<Program>` seeded via `ConfigureTestServices` with
   `services.AddSingleton<IConfigCatalog>(new ConfigCatalog(defaultSettings, [new("kitchen", kitchenSettings)]))`,
   where `kitchen` differs from the default by `Interval`. Assert:
   - `GET /api/Config` returns the default's `Interval`.
   - `GET /api/Config?profile=kitchen` returns kitchen's `Interval`.
   - `GET /api/Config?profile=bogus` returns 404.

   You **must** also call
   `services.UseMockHandler(new Mock<HttpMessageHandler>().WithServerVersion())`
   (`ImmichFrame.WebApi.Tests/Mocks/ImmichApiMock.cs`) or startup's version gate calls
   `Environment.Exit(1)` and the test host dies.

Seed `IConfigCatalog`, never `IServerSettings` — the per-profile services are built
from the catalog, so a settings override alone never reaches them.

### B. `ImmichFrame.WebApi/Helpers/ImmichFrameAuthenticationHandler.cs`

The handler reads `settings.GeneralSettings.AuthenticationSecret` in its constructor
(`:20`). It happens to work — the handler is built from request services — but reading
per-request state at construction time is a trap. Defer the **read**, not the
injection: keep `IServerSettings` as a constructor-injected field and move the
`AuthenticationSecret` read into `HandleAuthenticateAsync`.

## Scope

- `ImmichFrame.WebApi.Tests/Helpers/Profiles/ProfileResolutionTests.cs` (new)
- `ImmichFrame.WebApi/Helpers/ImmichFrameAuthenticationHandler.cs`

## Non-goals

- Do **not** modify `ImmichFrame.WebApi.Tests/Controllers/AssetControllerTests.cs` or
  `ConfigControllerTests.cs`. Their current uncommitted edits are correct as they
  stand; this task deliberately duplicates a little catalog-seeding boilerplate rather
  than refactoring them onto a shared fixture.
- No extraction of a shared test fixture / builder helper.
- Do not touch `Program.cs`, the registry, or any step-3/4/5 surface (controllers,
  swagger, the TypeScript client, Svelte routes, docs).
- Do not add validation preventing a profile from nulling out `AuthenticationSecret`.
  A profile with a null secret is **intentionally** unauthenticated — the handler
  grants access when the secret is null, and that is the designed behaviour.

## Constraints / caveats

- **Shared working tree.** Other developers have in-flight work here. Never run
  `git stash`, `git reset`, `git restore`, `git checkout --`, or `git clean`. Never
  `git add -A` — stage explicit paths only. Do not commit; the architect commits.
  Do not flag or revert changes you did not author.
- The uncommitted step-2 diff (`Program.cs`, `ImmichServerVersionChecker.cs`, the two
  controller test files, `Helpers/Profiles/`) is **this feature's own prior work**,
  not someone else's — read it freely, just do not edit outside your scope.
- Test framework is NUnit (`[TestFixture]`, `[Test]`, `Assert.That`) with Moq; match
  the style of `ImmichFrame.WebApi.Tests/Controllers/ConfigControllerTests.cs`.
- Match surrounding comment density: this codebase comments the *why* behind
  non-obvious choices and nothing else.

## Acceptance criteria

- `make test-webapi` and `make test-core` are green. Report the pass counts; they were
  58 and 80 before this task, so webapi must be 58 + the number of tests you added.
- Every one of the six behaviours above has a test that would actually fail if the
  behaviour regressed. In particular, test 6 must fail if `IServerSettings` in
  `Program.cs` is reverted to singleton.

---

## Addendum — review round 1

Both reviewers approved. Three changes, all inside
`ImmichFrame.WebApi.Tests/Helpers/Profiles/ProfileResolutionTests.cs`. Do not touch
any other file.

### 1. `DefaultInterval` is non-discriminating (blocking)

`DefaultInterval = 45` (`:21`) is byte-for-byte the model's own default —
`public int Interval { get; set; } = 45;` at
`ImmichFrame.WebApi/Models/ServerSettings.cs:44`. So the default-profile assertions at
`:91` and `:115` would still pass if the default profile's settings object were never
consulted at all, e.g. if `ProfileRegistry.For(null)` handed back a freshly constructed
`ServerSettings`. Pick a default interval that is not 45.

### 2. Overclaiming comment (`:62-64`)

The comment says the assertion guards against "sharing pools between profiles", but the
assertion only compares `Logic` references — it would not detect a shared
`IAssetAccountTracker` or pool beneath two distinct `Logic` instances. Reword it to say
what it actually checks. Do not strengthen the assertion; different `.Logic` instances
is what the brief asked for.

### 3. New test: a profile's secret does not authenticate another profile

Scope addition, deliberate. This feature makes `AuthenticationSecret` per-profile, and a
profile may legitimately null its own out — so "a token minted for one profile is not
accepted for another" is a security property this feature newly introduces, and nothing
currently asserts it. The reviewers confirmed the whole suite never exercises the
`token == authenticationSecret` success branch at all.

Add one end-to-end test on the existing `WebApplicationFactory` setup:

- Default profile secret `"default-secret"`; the `kitchen` profile a different one,
  `"kitchen-secret"`.
- Probe an endpoint that actually carries `[Authorize]` — `/api/Calendar`
  (`CalendarController.cs:9`) is the cheap one: with an empty `Webcalendars` list
  `IcalCalendarService.GetAppointments` returns an empty list without any network I/O.
  (`/api/Config` is deliberately unauthenticated, so it cannot show this.)
- Assert: `Bearer default-secret` against `?profile=kitchen` → 401, and
  `Bearer kitchen-secret` against `?profile=kitchen` → not 401. Cover the mirror
  direction too if it costs nothing.

This is the test that would have caught the constructor-time secret read had it ever
resolved against the wrong profile, so it is worth its keep.

## Constraints (unchanged)

Shared working tree: no `git stash`/`reset`/`restore`/`checkout --`/`clean`, no
`git add -A`, do not commit, stage nothing. Scope stays inside the one test file.
Re-run `make test-webapi` and `make test-core` and report the counts.
