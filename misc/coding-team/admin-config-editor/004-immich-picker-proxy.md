# 004 — Immich picker proxy

## Context

`3b1de22` gave the admin surface a config API. Album, person and tag fields in it are raw
identifiers — `List<Guid> Albums`, `ExcludedAlbums`, `People`, and `List<string> Tags` on
`ServerAccountSettings`. This task adds the endpoints that let the editor show names instead, so
an admin picks "Holidays 2024" rather than pasting a GUID.

Backend only. The page that consumes this is task 005.

## Two facts to build on, both verified

**Tags are matched by `value`, not by id or name.** `TagAssetsPool.cs:22` builds
`allTags.ToDictionary(t => t.Value)` and looks up each configured string in it. A picker that
submits a tag's `name` or `id` produces a configuration that loads cleanly, validates, and then
selects no assets at all. Return and submit `value`.

**People are paginated; albums and tags are not.** The generated signatures are:

- `GetAllAlbumsAsync(Guid? assetId, Guid? id, bool? isOwned, bool? isShared, string name, CancellationToken)` → `ICollection<AlbumResponseDto>`
- `GetAllPeopleAsync(Guid? closestAssetId, Guid? closestPersonId, long? page, int? size, bool? withHidden, CancellationToken)` → `PeopleResponseDto { hasNextPage, hidden, people, total }`
- `GetAllTagsAsync(CancellationToken)` → `ICollection<TagResponseDto>`
- `GetPersonThumbnailAsync(Guid id, CancellationToken)` → `FileResponse`

A large library has thousands of people. Page through to completion behind a hard cap, and report
truncation honestly rather than silently returning a short list — an admin who cannot find a
person needs to know the list was cut, not conclude the person does not exist.

## Objective

Admin-only endpoints returning trimmed album, person and tag lists from an Immich account, usable
both against an account already in the configuration and against credentials the admin is still
typing.

## Authorization

`[Authorize(Policy = AdminAuthentication.AdminOnlyPolicy)]`. No `AdminEndpoint` waiver — sign-out's
scheme-only pattern is not a model for anything else, and `AdminEndpointGuard` will refuse to boot
if you reach for it.

## Credentials, and why POST

Two ways to name an account:

1. **Saved** — the opaque per-account handle `GET /api/admin/config` already issues
   (`AdminAccountSettingsDto.Id`), plus its version token and profile. Reuse that scheme rather
   than inventing a second one; the client holds these already.
2. **Inline** — `{ serverUrl, apiKey }` for an account being typed. This is what makes the picker
   useful during first-run setup, which is when GUIDs hurt most.

**These are POST endpoints, not GET.** An inline API key must never appear in a URL, where it
lands in access logs, proxy logs and browser history. This is deliberate and worth a comment.

Accepting an arbitrary `serverUrl` from an authenticated admin is a deliberate server-side fetch
surface — the user chose it knowingly. Bound it: an explicit timeout, no automatic redirect
following, a cap on response size, and the target host logged via `SanitizeString()`. Never echo a
response body from the target back to the caller verbatim; map failures to your own messages.

## Required behaviour

**Trimmed DTOs, not raw Immich objects.** Albums: `id`, `albumName`, `assetCount`. People: `id`,
`name`. Tags: `id`, `value`, `name`. Nothing else — these lists can be long, and the browser needs
none of it.

**Readable failures, never a 500.** Immich answering 401 means "that API key was rejected"; a
connection failure or timeout means "could not reach that server". Both are ordinary outcomes of
an admin typing a URL or key wrong, and both must arrive as an actionable message.

**Person thumbnails are for saved accounts only.** `GET /api/admin/immich/people/{id}/thumbnail`
referencing a saved account handle, streaming the bytes. An `<img src>` cannot POST, so inline
credentials have no way to reach a thumbnail endpoint without either putting a key in a URL or
building a server-side credential cache — neither is worth it here. During first-run setup the
picker shows names without faces. Note in the response or a comment that Immich people frequently
have no name at all, so task 005 has to render an unnamed person sensibly.

## Scope

- New controller under `ImmichFrame.WebApi/Controllers/` for the three lists plus the thumbnail.
- New trimmed DTOs under `ImmichFrame.WebApi/Models/`.
- Whatever small piece resolves a request to an `ImmichApi` instance — saved handle or inline
  credentials — built through `IHttpClientFactory`, as `ProfileServices` already does.
- Tests in `ImmichFrame.WebApi.Tests/`, using the existing `ImmichApiMock` where it fits.

## Non-goals

- No caching. Config editing is rare; do not reach for `IApiCache` or add a cache of your own.
- No album or asset thumbnails — person thumbnails only.
- No Svelte (task 005); no regeneration of `openApi/swagger.json` or `immichFrameApi.ts`
  (task 005 does both in one pass). Never hand-edit either.
- No docs (task 006).
- Do not change `AdminConfigService`, the config DTOs, or anything about how config is saved.
  If you need something from the handle machinery, read it — do not reshape it.
- Do not add a server-side store of inline credentials.

## Acceptance criteria

- Each of the three lists returns from a saved account and from inline credentials.
- Tag entries carry `value`, and a test pins that it is `value` — not `name`, not `id` — since
  submitting the wrong one fails silently at runtime.
- A library with more people than one page returns all of them, and one larger than the cap
  reports that it was truncated.
- A rejected API key and an unreachable host each produce a distinct, readable error, not a 500.
- An unknown or unresolvable account handle is refused, and no request is made.
- Person thumbnails stream for a saved account.
- Endpoints are unreachable without an allowlisted admin session; a valid **frame** bearer token
  does not open them. Verify that against `ImmichFrameAuthenticationHandler.cs:33` rather than
  assuming — it succeeds anonymously for endpoints carrying no `[Authorize]`, which has already
  invalidated several confident claims in this plan.
- Existing suites pass unmodified. If one needs changing, stop and tell me why.
- `dotnet build ImmichFrame.sln` clean, no new warnings; `make test-core` and `make test-webapi` green.

## Working agreements

- Shared working tree. Never run `git stash`, `git reset`, `git restore`, `git checkout --` or
  `git clean`; never `git add -A`. Stage explicit paths only, and only files in Scope above.
- `CLAUDE.md` and `.claude/settings.local.json` are not yours; leave them alone.
- Commit messages must **not** carry a `Co-Authored-By` trailer. Do not commit unless asked.
- No version attribute on `<PackageReference>`.
- Follow existing style: file-scoped namespaces, primary constructors, structured log templates
  with named placeholders, `SanitizeString()` on client-supplied strings before logging, domain
  exceptions from `ImmichFrame.Core/Exceptions/`, XML doc comments explaining *why*.
- **Assert the thing that distinguishes the branch**, not a substring a neighbouring outcome would
  also satisfy. Three tests in task 003 passed vacuously that way — one on an environment default,
  two on a message fragment shared with the fallback path.
- If you use a mutation check, verify the mutation actually applied. A previous script failed at
  `import shutil` — blocked in this sandbox — and scored every run green. `json` is blocked too;
  `jq` works.
