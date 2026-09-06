# 005 — Admin page: session gate and configuration form

## Context

The backend is complete. Five commits: `7c4a328` swappable catalog, `3c9c8e6`/`3ed1c9a` OIDC admin
surface and its startup authorization guard, `3b1de22` the config read/write API, `3daaf1f` the
Immich picker proxy.

This task builds the page. **Pickers are task 006** — album, person and tag fields stay as raw
text entry here. Splitting them off keeps this task reviewable; do not start on them.

## Objective

An `/admin` route that authenticates through OIDC, loads the configuration, edits the default and
its profiles, and saves — with override-versus-inherit visible and secrets never mishandled.

## First: regenerate the API client

`immichFrame.Web/src/lib/immichFrameApi.ts` is generated and marked **DO NOT MODIFY**. Regenerate
it; do not hand-write the admin calls.

```sh
make dev          # port 5217; Swagger is Development-only (Program.cs)
make api          # curls swagger.json, then oazapfts regenerates the client
```

This pulls in every admin endpoint at once. The diff on that file will be large — that is expected
and is not scope creep.

## Route

`immichFrame.Web/src/routes/admin/+page.svelte` and `+page.ts`, with `export const prerender =
false` — `+layout.ts` sets `prerender = true` globally and `adapter-static` cannot prerender this.
`src/routes/[config]/+page.ts` is the precedent and says why.

`admin` is already a reserved profile name (`ConfigCatalog.ReservedProfileNames`) and
`CustomAuthenticationMiddleware` already steps aside for `/admin`, so the route is reachable on an
installation with an `AuthenticationSecret` set.

## Session states

`GET /api/admin/session` answers without authentication. Render four states distinctly:

1. **Admin not configured** — the OIDC environment variables are absent. Say so and point at the
   documentation; do not show a login button that cannot work.
2. **Not signed in** — a sign-in button that performs a **full-page navigation** to
   `/api/admin/login?returnUrl=/admin`. An OIDC challenge is a 302 to the identity provider and
   cannot be followed by `fetch`; an XHR here fails silently or CORS-errors.
3. **Signed in, not an administrator** — a 403 from the config API. Say that the account is
   authenticated but not on the allowlist, and offer sign-out. Sign-out requires authentication
   only, not the allowlist, so it works from this state — that is deliberate (`3ed1c9a`).
4. **Signed in as an administrator** — the editor.

A 401 from any admin call at any time returns to state 2 rather than showing a broken form.

## The form

`GET /api/admin/config` returns the default configuration and every profile. Each entry carries
`DeclaredKeys` — the keys that entry actually declares, e.g. `General.ShowClock`, `Accounts`.

**Override versus inherit is the heart of this page.** For a profile, every field is either
declared by that profile or inherited from the default. Show which, and let the admin add or
remove an override per field. A field's value being *equal* to the default is not the same as
inheriting it — send back exactly the keys the admin intends to declare. Writing back a merged
profile would look right today and silently stop the next default change from propagating; that
is the failure `3b1de22` exists to prevent, and this UI is the other half of it.

The default configuration has no inherit state — it declares everything it sets.

**Secrets.** `WeatherApiKey`, `Webhook`, `AuthenticationSecret` and each account's `ApiKey` come
back absent with a `Has…` flag. Show "set" or "not set", never a fake value. Send a secret only
when the admin typed a new one; otherwise send the placeholder back unchanged so the server keeps
what it has. Do not invent your own sentinel — use exactly what the API documents.

**Accounts need care, and the API already tells you what it needs:**

- Echo each account's `Id` for any account whose key stays masked. It is how the server matches a
  kept key to its account; without it the save is refused. Do not reorder or synthesise these.
- `ApiKeyFromFile` means the key comes from `ApiKeyFile` on disk. Render that as its own state —
  not as "no key set", which would invite an admin to type a key the server cannot accept
  alongside the file.
- A profile that *newly* declares `Accounts` cannot inherit a secret the browser never had. Prompt
  for the API key at the moment the admin adds that override, not by letting the save fail.

**Saving.** `PUT /api/admin/config` with the `version` token from the `GET`. On 409, say what
happened — stale token means the file changed underneath, and the admin needs to reload and redo.
Surface the server's message; it is written for an operator. A 400 carries a validation message
worth showing verbatim.

The API also reports when configuration is not editable at all — sourced from environment
variables, or a v1-schema file needing explicit conversion consent. Render those as a read-only
view with the reason, not as a form that fails on save.

## Scope

- `immichFrame.Web/src/lib/immichFrameApi.ts` — regenerated, never hand-edited.
- `openApi/swagger.json` — regenerated by `make api`.
- `immichFrame.Web/src/routes/admin/` — the route.
- `immichFrame.Web/src/lib/components/admin/` — components for the form.
- `immichFrame.Web/src/lib/stores/` — a store if one is warranted; follow the existing hand-rolled
  `writable` wrapper pattern rather than reaching for a state library.

## Non-goals

- **No pickers** (task 006). Albums, excluded albums, people and tags stay as raw text entry.
- No documentation (task 007).
- No profile rename — a profile name is a frame's URL path and its localStorage auth-secret key
  (`authSecret:<profile>` in `persist.store.ts`), so renaming logs every frame on it out.
- No editing of OIDC settings; they are environment-only by design.
- No change to the slideshow, `home-page.svelte`, or anything the frames render.
- No new npm dependency without telling me first and why.

## Acceptance criteria

- All four session states render distinctly, and sign-in is a full-page navigation.
- A profile shows which fields it declares and which it inherits; toggling an override changes
  what the save sends, and a round-trip with no edits sends back the same declared key set.
- Secrets show as set/not-set, are never displayed, and an untouched secret survives a save.
- An `ApiKeyFile` account is visibly distinct from one with no key.
- Adding an `Accounts` override to a profile prompts for the API key before saving.
- A stale version token, an env-var-sourced configuration and a v1 file each produce a clear
  explanation rather than a failed save.
- `npm run lint` and `npm run check` pass in `immichFrame.Web`.
- `dotnet build ImmichFrame.sln` clean; `make test-core` and `make test-webapi` still green — the
  regenerated swagger must not change backend behaviour.

## Working agreements

- Shared working tree. Never run `git stash`, `git reset`, `git restore`, `git checkout --` or
  `git clean`; never `git add -A`. Stage explicit paths only.
- `CLAUDE.md` and `.claude/settings.local.json` are not yours; leave them alone.
- Commit messages must **not** carry a `Co-Authored-By` trailer. Do not commit unless asked.
- Prettier with **tabs**, single quotes, no trailing commas, print width 100
  (`immichFrame.Web/.prettierrc`). Type-check with `svelte-check` (`npm run check`), not `tsc`.
- **CI runs only `dotnet test`** — frontend lint and check are not in any workflow, so running them
  locally is the only thing standing between a broken frontend and `main`.
- API access goes exclusively through `$lib/immichFrameApi`.
- Assert the thing that distinguishes the case, not a substring a neighbouring outcome would also
  satisfy. Five test-validity problems surfaced in tasks 002-004 — three vacuous assertions, a
  fixture that mimicked the behaviour under test, and a test asserting the right value in the
  wrong place.
- `json` and `shutil` are blocked in this sandbox's python; `jq` works.
