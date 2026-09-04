# 002 — OIDC authentication and admin authorization

## Context

Task 001 (`7c4a328`) made the config catalog replaceable and reserved the profile name
`admin`. This task adds the authentication that will guard the editor; task 003 adds the
endpoints that actually read and write config.

Today there is one authentication scheme and no user concept.
`ImmichFrameAuthenticationHandler` (`ImmichFrame.WebApi/Helpers/ImmichFrameAuthenticationHandler.cs`)
compares a `Bearer` token against `AuthenticationSecret`, succeeding anonymously when no secret is
configured or the endpoint carries no `[Authorize]`.

**The trap this task exists to clear.** `CustomAuthenticationMiddleware`
(registered `Program.cs:132`) calls `AuthenticateAsync("ImmichFrameScheme")` on *every* request
and short-circuits with a 401 on failure. On an installation with `AuthenticationSecret` set,
that middleware would reject an admin request — and the `/admin` page's own HTML — long before
any OIDC scheme ran. `MapFallbackToFile("/index.html")` (`Program.cs:139`) means the SPA route
`/admin` is an ordinary request through the same pipeline.

## Objective

A working OIDC login that produces an authenticated cookie session, an `AdminOnly` authorization
policy gated on an allowlist, and a pipeline in which admin paths reach it — with frame
authentication provably unchanged.

## Configuration (environment variables only)

Follow the existing `IMMICHFRAME_` prefix (`Program.cs:56` is the only current example).

- `IMMICHFRAME_OIDC_AUTHORITY`
- `IMMICHFRAME_OIDC_CLIENT_ID`
- `IMMICHFRAME_OIDC_CLIENT_SECRET`
- `IMMICHFRAME_OIDC_ADMINS` — comma-separated `sub` or `email` values
- `IMMICHFRAME_OIDC_TRUST_PROXY_HEADERS` — optional, see below

Deliberately **not** in `Settings.json`/`Settings.yml`: the editor must not be able to break the
way in to itself, and the client secret stays out of the config file. Do not add these to
`ServerSettings`, `GeneralSettings` or `IConfigCatalog`.

## Required behaviour

**Fail closed, in both directions.**
- Authority, client id or client secret missing → the admin surface is **off**: every admin
  endpoint 404s. Not 500, not an unprotected endpoint.
- OIDC configured but `IMMICHFRAME_OIDC_ADMINS` empty or absent → also off. An empty allowlist
  must never mean "everyone"; that is the single worst failure this task could ship.

**One always-reachable status endpoint.** `GET /api/admin/session` responds without
authentication and reports whether admin is configured and, if the caller has a session, who they
are. The SPA (task 005) needs this to decide between showing a login button, showing the editor,
or explaining that admin is unconfigured. Every *other* admin endpoint requires the policy.

**Allowlist matching.** Match the token's `sub` (ordinal) or `email` (ordinal
case-insensitive) against the configured list. A match on either is sufficient. Trim whitespace
around entries; ignore empty entries.

**Scheme separation.** `AddAuthentication("ImmichFrameScheme")` currently sets the default scheme
(`Program.cs:101`). Admin endpoints must authenticate against the **cookie** scheme explicitly
rather than inheriting the default, and frame endpoints must keep using `ImmichFrameScheme`. Do
not change the default scheme.

**Middleware bypass — narrow and explicit.** `CustomAuthenticationMiddleware` must skip exactly:
`/api/admin` (prefix), the OIDC callback path, the sign-in and sign-out paths, and the `/admin`
SPA route. Everything else keeps running the frame scheme exactly as now. Prefix matching must
not let `/api/adminfoo` or `/admins` through — and must not accidentally exempt any existing
endpoint. This is the highest-risk edit in the task; a mistake here silently unauthenticates the
whole API.

**Reverse-proxy scheme.** ImmichFrame is typically run behind a TLS-terminating proxy. Without
honouring `X-Forwarded-Proto` the OIDC `redirect_uri` is built as `http://` and the IdP rejects
it. Support `ForwardedHeaders` for proto and host, **opt-in** via
`IMMICHFRAME_OIDC_TRUST_PROXY_HEADERS`, defaulting to off. Note in an XML comment that ASP.NET
ignores forwarded headers unless `KnownProxies`/`KnownNetworks` is configured or cleared, and
that trusting these headers when *not* behind a proxy lets a client forge them.

## Scope

- `Directory.Packages.props` — `Microsoft.AspNetCore.Authentication.OpenIdConnect`, version here only.
- `ImmichFrame.WebApi/ImmichFrame.WebApi.csproj` — versionless `<PackageReference>`.
- New files under `ImmichFrame.WebApi/Helpers/Admin/` — options binding from env, the allowlist
  requirement/handler, and whatever small pieces the policy needs.
- New `ImmichFrame.WebApi/Controllers/AdminSessionController.cs` — the session endpoint plus
  login-challenge and sign-out actions. Keep it thin; it is the only admin surface this task adds.
- `ImmichFrame.WebApi/Helpers/CustomAuthenticationMiddleware.cs` — the bypass.
- `ImmichFrame.WebApi/Program.cs` — schemes, policy, forwarded headers, registration.
- `docker/example.env` — the new variables, commented, with a one-line note each.
- Tests in `ImmichFrame.WebApi.Tests/`.

## Non-goals

- No config reading or writing, and no `Swap` caller (task 003).
- No Immich album/person/tag proxy (task 004).
- No Svelte page or route (task 005).
- No prose documentation beyond `docker/example.env` (task 006).
- Do not regenerate `openApi/swagger.json` or `immichFrameApi.ts` — task 005 does that in one
  pass. Never hand-edit either.
- Do not change `ImmichFrameAuthenticationHandler`'s behaviour, or how frames authenticate.
- Do not add persistent DataProtection key storage. Admin sessions not surviving a container
  restart is acceptable here; just do not be surprised by it.

## Acceptance criteria

- Admin endpoint, no session → challenge or 401, **not** the frame scheme's failure message.
- Admin endpoint, authenticated session whose `sub`/`email` is not allowlisted → **403**.
- Admin endpoint, allowlisted session → **200**.
- OIDC env vars absent → admin endpoints 404 and `/api/admin/session` reports unconfigured.
- OIDC configured, allowlist empty → admin endpoints refuse access (fail closed).
- **Regression, mandatory:** with `AuthenticationSecret` set, every existing frame endpoint still
  401s without the bearer token and still succeeds with it. The existing suites
  (`AssetControllerTests`, `ConfigControllerTests`, `ProfileResolutionTests`) must pass unmodified —
  if any needs changing, stop and tell me why rather than editing it.
- A test proving the bypass does not exempt a non-admin path (`/api/Config` with a
  secret configured still 401s unauthenticated).
- `dotnet build ImmichFrame.sln` clean; `make test-core` and `make test-webapi` green.

## Testing note

Do not stand up a real IdP. Register a test authentication scheme in `ConfigureTestServices` that
mints a cookie-equivalent principal with chosen claims, and test the policy, the fail-closed
paths and the middleware bypass against it. The OIDC handshake itself is framework code; the
allowlist, the bypass and the fail-closed behaviour are what must be covered.

## Working agreements

- Shared working tree. Never run `git stash`, `git reset`, `git restore`, `git checkout --` or
  `git clean`; never `git add -A`. Stage explicit paths only, and only files in Scope above.
  Any change you did not author is another developer's in-flight work — leave it alone.
- `CLAUDE.md` and `.claude/settings.local.json` currently show as modified/untracked. They are not
  yours; do not stage, edit or revert them.
- Commit messages must **not** carry a `Co-Authored-By` trailer (`CLAUDE.md`, Git conventions).
  Do not commit at all unless asked.
- No version attribute on `<PackageReference>` — central package management rejects it.
- Follow existing style: file-scoped namespaces and primary constructors in new files, structured
  log templates with named placeholders, `SanitizeString()` on any client-supplied string before
  logging, and XML doc comments explaining *why* for non-obvious decisions.
