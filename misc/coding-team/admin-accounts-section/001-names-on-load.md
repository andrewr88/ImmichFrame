# 001 — Resolve Immich names on load

## Context

The admin configuration editor renders each configured album / person / tag as a row in
`immichFrame.Web/src/lib/components/admin/immich-picker.svelte`. The row's text is
`item?.label ?? value`, where `item` comes from `known`, which is built from `list`.

`list` starts null and is only ever populated by `load()`, and `load()` is only called from
`toggle()` — the *Choose albums from Immich* button — and from the "Read {noun}" button inside an
open panel. So on a fresh page load every configured entry renders as its raw identifier (a GUID
for albums and people, a tag path for tags). Clicking *Choose albums from Immich* is what makes the
names appear, which is the reported bug.

Everything needed to resolve names already exists: `loadPickerList(kind, source)` in
`immich-picker.ts` reads one list off the picker proxy and returns `{ok:true, list}` or
`{ok:false, message}` — it never throws.

## Objective

Configured entries show their Immich names on load, without the administrator opening any panel.

## Scope

Frontend only. Two files, plus one new one:

- **New** `immichFrame.Web/src/lib/components/admin/picker-cache.ts`
- `immichFrame.Web/src/lib/components/admin/immich-picker.svelte`
- `immichFrame.Web/src/lib/components/admin/immich-picker.ts` (only if something genuinely belongs
  there rather than in the new module)

### The cache module

A module-scoped cache keyed by `` `${sourceKey(source)}\n${kind}` ``, holding the in-flight promise
so that concurrent callers for the same key share one request rather than issuing one each. This
matters now and matters more after task 005: `albums` and `excludedAlbums` are two pickers on the
same account reading the same list, and a profile that overrides accounts renders a second set of
pickers against the same credentials.

Export at least:

- a function that returns the cached `PickerResult` for a key, fetching it once if absent;
- a function that returns what is already cached for a key without fetching, for the synchronous
  first render;
- a way to drop a key, used by the existing *Reload* button so it still re-reads rather than
  handing back the cached list.

`sourceKey` already exists in `immich-picker.ts` and already returns a distinct string for a
`blocked` source — a blocked source must never be fetched, cached or retried.

### Priming on mount

Prime only where there is something to name: a picker with `values.length === 0` has no row to
label and must issue no request. A picker whose `source.kind === 'blocked'` must issue no request
either — there are no credentials that describe that account, which is the whole point of
`pickerSource`'s refusals.

Priming must not disturb the panel's own state machine:

- it must not set `manual = true` on failure. `load()` does that deliberately, because a failed
  read *at the moment the administrator asked to browse* has to leave a way to edit the field. A
  background resolve that quietly opened the text box on every picker on the page would be noise.
- it must not write `message`. A page that cannot reach Immich should render ids as it does today,
  not a wall of amber text above every field.
- it must not open the panel, and must not touch `loading` — that spinner belongs to the panel.

A primed list *should* populate `list`/`listSource` so the rows outside the panel are labelled and
so `unmatched` / `partial` behave exactly as they do today once a list is in hand. Note this makes
the "unmatched" badge and the amber `unmatched.length > 0` paragraph appear on load where before
they appeared only after browsing — that is correct and is the point.

### Interaction with the existing invalidation

`immich-picker.svelte` has an `$effect` that drops `list` when `sourceKey` changes, so a list read
from one server is never shown as another's. That must keep working. Deciding whether a credential
change re-primes automatically is yours, but note the comment already in that effect: the server
URL is bound on `input`, so re-reading on every change would be one request per keystroke. Not
re-priming — leaving the rows as bare ids until the panel is opened — is acceptable and is the
safer default.

The cache is keyed by `sourceKey`, which already encodes the credentials, so a stale entry for old
credentials can never be served for new ones. Whether entries for superseded keys are evicted is a
judgement call; the page is short-lived and the entries are small.

## Non-goals

- No change to `AdminImmichController` or any backend file. The three list endpoints are enough.
- No new API endpoint for resolving ids to names in bulk.
- No caching across page loads (`localStorage` or similar). `AdminImmichController`'s class comment
  states nothing is cached on purpose — a stale album list is worse than a slow one. A cache that
  lives for one page load is within that; one that outlives it is not.
- No change to the person-thumbnail path, to `personThumbnailUrl`, or to the accounts/profile
  layout — that is tasks 004 and 005.
- Do not pre-empt the top-level accounts section. This task changes where a list gets requested,
  not who owns the account.

## Constraints

- Shared working tree. Read `.claude/skills/_shared/concurrency.md`. Never `git stash`, `git reset`,
  `git restore`, `git checkout --`, or `git clean`; never `git add -A`; stage explicit paths only.
  Scope edits to the files above and do not revert or flag hunks you did not author.
- Do not commit.
- Svelte 5 runes, matching the surrounding code (`$state`, `$derived`, `$effect`, `untrack`).
- Prettier with tabs, single quotes, no trailing commas, width 100 (`immichFrame.Web/.prettierrc`).
- `immichFrame.Web/src/lib/immichFrameApi.ts` is generated and marked DO NOT MODIFY.
- Comments explain *why*, not what — match the density and tone of the file you are editing, which
  is unusually heavily commented for a reason.

## Validation

From `immichFrame.Web`: `npm run lint` and `npm run check` must both pass. Neither runs in CI
(`ARCHITECTURE.md`, "Lint and test commands"), so run them locally. No dotnet build is needed —
nothing here touches C#.

## Acceptance criteria

- On loading `/admin` against a configuration with albums, people or tags configured, the rows
  render Immich names without any panel being opened.
- A picker with no configured values issues no request on load.
- A picker whose source is `blocked` issues no request on load and shows no message it did not show
  before.
- `albums` and `excludedAlbums` on one account cause one albums request between them, not two.
- Opening the panel still works; *Reload* still re-reads from Immich rather than replaying the
  cached list.
- With Immich unreachable, the page still renders, the fields are still editable, and no picker has
  silently switched itself to text entry.
