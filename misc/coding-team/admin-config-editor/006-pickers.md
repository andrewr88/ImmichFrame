# 006 — Album, person and tag pickers

## Context

`3daaf1f` built the proxy; `023e975` built the page, deliberately leaving album, excluded-album,
person and tag fields as raw text entry. This task replaces that entry with name-based pickers.

The generated client already carries the endpoints (regenerated in `023e975`, do not regenerate
again unless the backend changes):

- `getAdminImmichAlbums(AdminImmichAccountRefDto)` → `{ id, albumName, assetCount }[]`
- `getAdminImmichPeople(AdminImmichAccountRefDto)` → people plus `total` and `truncated`
- `getAdminImmichTags(AdminImmichAccountRefDto)` → `{ id, value, name }[]`
- `getAdminImmichPersonThumbnail(id, { accountId, version, profile })`

`AdminImmichAccountRefDto` is `{ profile, accountId, version }` **or** `{ serverUrl, apiKey }`.

## Objective

Pick albums, excluded albums, people and tags by name, against a saved account or credentials
being typed, without ever silently changing what the configuration selects.

## The invariant that matters most

**A configured id that Immich no longer returns must survive.** The config may hold an album or
person id that was deleted, or a tag value that was renamed — and the account being edited may not
even be the account that id came from. A picker that renders only what the server returned, and
saves only what is ticked, silently drops those ids: the save succeeds, validation passes, and the
frame quietly stops showing a set of photos.

That is the same class of failure as the sparse-override drift the backend guards against, and it
is invisible for the same reason. Show an unmatched id as still configured, marked unknown, and
keep it unless the admin removes it deliberately.

**Tags are submitted by `value`, never `id` or `name`.** `TagAssetsPool.cs:22` keys
`allTags.ToDictionary(t => t.Value)`. Submitting the wrong field yields a configuration that loads,
validates, and selects nothing.

## Which credentials a picker uses

The account being edited decides. Pin these rules explicitly — they are the fiddly part:

- Account came from the file, URL and key untouched → **saved** ref (`profile`, `accountId`,
  `version` from the loaded config).
- Account is new, or the admin typed an API key → **inline** ref with the typed values.
- Account came from the file, key still masked, but the **URL was changed** → neither is correct.
  The saved ref would browse the old server. Refuse to open the picker and say why: save first, or
  enter the API key for the new server.

`version` is the loaded configuration's token even when there are unsaved edits — the picker is
asking about what is on disk, which is what the handle resolves against.

## Required behaviour

**Person thumbnails need a saved account** (`3daaf1f`): an `<img src>` cannot POST, so inline
credentials cannot reach the thumbnail endpoint. With inline credentials, show names only.

**Immich people frequently have no name at all.** Without a name and without a thumbnail an entry
is unidentifiable, so with inline credentials say so rather than rendering a list of blanks —
"unnamed person" plus a short id fragment, and a note that saving the account first enables faces.

**People can be truncated.** The response carries `total` and `truncated`. Say so when it is cut,
with the total — an admin who cannot find someone needs to know the list was capped, not conclude
the person does not exist.

**Failures must not block editing.** A rejected API key is a 400 and an unreachable server a 502,
both with operator-readable messages. Show the message and fall back to manual entry for that
field. Losing the ability to edit a config because Immich is down would be worse than the GUIDs.

**Search.** Album, person and tag lists can be long. Client-side filtering over the fetched list
is enough; do not add server-side search or paging beyond what the proxy already does.

## Scope

- `immichFrame.Web/src/lib/components/admin/` — the picker component(s) and their wiring into
  `setting-field.svelte` / `account-editor.svelte`.
- Nothing under `ImmichFrame.WebApi/` unless you find a genuine backend defect — if you do, report
  it rather than fixing it here.

## Non-goals

- No documentation (task 007).
- Do not regenerate `openApi/swagger.json` or `immichFrameApi.ts`; `023e975` already did.
- Do not change the declared-key/override semantics in `admin-config.ts`. Pickers change how a
  value is chosen, never which keys a save declares.
- No caching of picker results beyond the lifetime of an open picker.
- No new npm dependency without telling me first and why.
- No change to the slideshow or anything the frames render.

## Acceptance criteria

- All four fields are pickable by name, and tags submit `value`.
- An id in the configuration that Immich does not return is shown as unknown and survives a save
  untouched. This is the one to write a check for first.
- A truncated person list says so, with the total.
- A rejected key and an unreachable server each show their message and leave the field editable.
- Thumbnails render for a saved account; an unnamed person is still identifiable without one.
- Changing an account's URL while its key is masked refuses the picker with an explanation rather
  than browsing the old server.
- A no-edit round trip still sends the same declared key set as before this task — the 005
  regression check.
- `npm run lint` and `npm run check` pass for your files; `dotnet build ImmichFrame.sln` clean and
  both backend suites green.

## Working agreements

- Shared working tree. Never run `git stash`, `git reset`, `git restore`, `git checkout --` or
  `git clean`; never `git add -A`. Stage explicit paths only.
- `CLAUDE.md` and `.claude/settings.local.json` are not yours.
- Commit messages must **not** carry a `Co-Authored-By` trailer. Do not commit unless asked.
- Prettier with tabs, single quotes, no trailing commas, width 100. `npm run check`, not `tsc`.
  `npm run lint` fails on `main` with 21 pre-existing prettier files and 9 eslint errors — none
  yours, do not reformat them; just keep your own files clean.
- **CI runs only `dotnet test`.** Frontend checks are local-only.
- There is no browser automation in this environment. Say plainly what you could not see rather
  than implying you exercised the UI.
- Assert what distinguishes the case, not a substring a neighbouring outcome would also satisfy.
  Six test-validity problems have surfaced in this plan; three were caught by the developer before
  review, which is where you want them caught.
