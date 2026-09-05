# 003 — Admin configuration read/write API

## Context

`7c4a328` made the catalog replaceable (`SwappableConfigCatalog.Swap`, which replaces the
configuration and drops `ProfileRegistry`'s cache in one step). `3c9c8e6` and `3ed1c9a` put an
OIDC-authenticated, allowlisted admin surface under `/api/admin`, with a startup guard that
refuses to boot if an endpoint there is not behind the `AdminOnly` policy.

This task is what both were built for: the endpoints that read and write the configuration file.
It is also the **first real caller of `Swap`**.

## Objective

`GET` and `PUT` on `/api/admin/config`, editing the default configuration and its profiles, with
saves validated before the file is touched and applied without a restart.

## Authorization — non-negotiable

Both actions carry `[Authorize(Policy = AdminAuthentication.AdminOnlyPolicy)]`.

**Do not copy `AdminSessionController.Logout`'s pattern.** It uses
`[Authorize(AuthenticationSchemes = CookieScheme)]` with `[AdminEndpoint(AllowlistNotRequired =
true)]`, which authenticates *without* consulting the allowlist — correct for signing out, and
catastrophic for an endpoint that reads and rewrites Immich API keys. `AdminEndpointGuard` will
refuse to start the host if you get this wrong; do not reach for the waiver to silence it.

## The hard part: sparse profile overrides

`IConfigDocument.Bind(profileName)` returns the **fully merged** result. The admin API needs
something it does not currently expose: the raw set of keys a profile actually declares.

Two consequences, and the second is the one that will bite:

1. **Read.** The editor must show which values a profile overrides and which it inherits, so the
   response needs per-profile provenance, not just merged values.
2. **Write.** Saving a profile back as its merged result would turn every inherited value into an
   explicit override. The file would still load and every profile would still behave identically
   *that day* — and then the next edit to the default configuration would silently stop reaching
   any profile. Round-tripping a profile unchanged must leave its declared key set unchanged.

`IConfigDocument`'s own doc comment explains why merging happens at document level: bound settings
cannot distinguish "profile said nothing" from "profile said `false`". The same reasoning applies
to writing. Settle the shape — most likely exposing the raw profile subtree alongside `Bind` — and
tell me what you chose and why.

## Required behaviour

**Secrets never round-trip.** `GET` masks `ApiKey`, `AuthenticationSecret`, `Webhook` and
`WeatherApiKey`: omit the value, report presence (`hasApiKey: true`). `PUT` treats an absent or
sentinel value as "keep what is stored" and only writes a secret the admin actually typed. A save
must never blank a secret because the browser never had it.

`ConfigControllerTests` asserts these never reach the *client* DTO. That is a different surface;
those tests must keep passing untouched.

**Save order: bind → validate → write → swap.** Validation is local — `ServerSettings.Validate()`
calls `IAccountSettings.ValidateAndInitialize()`, which reads `ApiKeyFile` from disk and throws if
neither `ApiKey` nor `ApiKeyFile` is set. It never contacts Immich. So a bad configuration is
rejected with a readable error **before the file is touched**. There is no state in which the
editor leaves a broken file on disk. Note `ValidateAndInitialize` mutates `ApiKey` as a side
effect — validate a copy, not the instance you are about to serialise.

**Atomic write, with a backup.** Write to a temp file in the same directory and rename over the
original; keep a timestamped backup of the previous contents. Preserve the format that was loaded
— a `Settings.yml` installation stays YAML, a `Settings.json` one stays JSON. Comments are not
preserved; that is accepted and documented in task 006.

**Refuse, clearly, in three cases.** Each is a 409 with a message an operator can act on, never a
500 and never a silent success:
- Configuration came from environment variables — there is no file to write
  (`ConfigLoader.LoadConfigFromDictionary`).
- The file loaded through the v1 fallback (`ServerSettingsV1Adapter`). Saving would silently
  rewrite it in the current schema. Require explicit conversion consent in the request.
- The file changed on disk since the `GET` that produced this edit. Carry a version token
  (content hash is simplest) and reject a stale one — two admins, or one admin and a hand edit,
  must not silently overwrite each other.

Also handle an unwritable or read-only config directory as a clear error. The container runs as
UID 1000 and a read-only mount is a normal deployment.

**Profiles.** Editing existing profiles and creating new ones is in scope, as is deleting one.
Name validation must route through `ConfigCatalog`'s existing rules — the `^[A-Za-z0-9_-]{1,64}$`
pattern, case-insensitive duplicate rejection, and the reserved list (`api`, `static`, `swagger`,
`admin`, `default`) — rather than being reimplemented.

**Renaming a profile is out of scope.** A profile name is a frame's URL path and the key its
browser stores an auth secret under (`authSecret:<profile>` in `persist.store.ts`), so a rename
silently logs out every frame on that profile and points it at a 404. If the UI wants it later it
needs its own design.

**Close the deferred 500.** `7c4a328` documented this and left it: a request admitted by
`UnknownProfileMiddleware` whose profile a swap then removes throws `ProfileNotFoundException`
with nothing in the pipeline to map it, so the client sees a 500. This task makes swaps reachable,
so it is now live. Map `ProfileNotFoundException` to a 404. Keep it narrow — a small mapping for
this exception, not a general problem-details layer.

## Scope

- New `ImmichFrame.WebApi/Controllers/AdminConfigController.cs`.
- New DTOs under `ImmichFrame.WebApi/Models/` for the admin view of settings.
- `ImmichFrame.WebApi/Helpers/Config/` — the raw-override accessor, and a writer.
- Whatever `ConfigLoader`/`IConfigCatalog` need to report their source (file path, format,
  v1-ness) so the refusal cases above can be decided.
- The `ProfileNotFoundException` mapping.
- Tests in `ImmichFrame.WebApi.Tests/`.

## Non-goals

- No Immich album/person/tag proxy (task 004).
- No Svelte page (task 005); no regeneration of `openApi/swagger.json` or `immichFrameApi.ts`
  (task 005 does both in one pass). Never hand-edit either.
- No prose documentation (task 006).
- No editing of the OIDC settings — they are environment-only by design.
- No comment preservation, no profile rename, no config history or rollback beyond the single
  backup file.
- Do not change how frames authenticate, or the client `ClientSettingsDto`.

## Acceptance criteria

- `GET` returns the default configuration and every profile, secrets masked, with each profile's
  declared keys distinguishable from inherited ones.
- A `PUT` that changes one value in one profile leaves every other profile's declared key set
  byte-identical in the file.
- A `PUT` with an invalid configuration returns a readable error and the file on disk is unchanged.
- A `PUT` that omits a secret keeps the stored one; a `PUT` that supplies one replaces it.
- After a successful `PUT`, a subsequent request served through the scoped settings sees the new
  values with no restart.
- Stale version token, env-var-sourced config, and v1-without-consent each return 409.
- Reserved, malformed and case-insensitively duplicate profile names are rejected.
- `ProfileNotFoundException` surfaces as 404, not 500.
- Existing suites pass **unmodified** — `ConfigControllerTests`, `AssetControllerTests`,
  `ProfileResolutionTests`, `ConfigCatalogTest`, `ConfigLoaderTest`, `ProfileReloadTests` and the
  admin suites. If one needs changing, stop and tell me why.
- `dotnet build ImmichFrame.sln` clean; `make test-core` and `make test-webapi` green.

## Working agreements

- Shared working tree. Never run `git stash`, `git reset`, `git restore`, `git checkout --` or
  `git clean`; never `git add -A`. Stage explicit paths only, and only files in Scope above.
  Any change you did not author is another developer's in-flight work — leave it alone.
- `CLAUDE.md` and `.claude/settings.local.json` show as modified/untracked. They are not yours.
- Commit messages must **not** carry a `Co-Authored-By` trailer (`CLAUDE.md`, Git conventions).
  Do not commit unless asked.
- No version attribute on `<PackageReference>`.
- Follow existing style: file-scoped namespaces, primary constructors, structured log templates
  with named placeholders, `SanitizeString()` on client-supplied strings before logging, domain
  exceptions from `ImmichFrame.Core/Exceptions/`, and XML doc comments explaining *why*.
- **Verify claims about what returns 401 against `ImmichFrameAuthenticationHandler.cs:33` rather
  than assuming.** It succeeds anonymously for any endpoint carrying no `[Authorize]`, which has
  already invalidated four confident assertions in this plan, three of them mine.
- If you use a mutation script to check that a test bites, verify the mutation actually applied.
  A previous one failed at `import shutil` — blocked in this sandbox — and scored every run green.
