# 004 — Accounts in a section of their own, assigned by profile

## Context

Read `001-names-on-load.md`, `002-cross-entry-key-handles.md` and `003-account-label.md` first —
all three are committed, and this task is what they were for.

Today `/admin` renders one profile at a time. `config-editor.svelte` draws a tab strip
(Default configuration + each profile) and hands the selected entry to `entry-editor.svelte`, which
ends with an "Immich accounts" section; each account is an `account-editor.svelte` carrying **both**
its credentials (server URL, API key, key file) and its photo selection (albums, excluded albums,
people, tags, rating, date filters, show-memories/favorites/archived/videos).

Two consequences the administrator feels:

1. The same Immich account is re-described in every entry that uses it. Three profiles on one server
   means the URL appears four times.
2. A profile either inherits the whole account list or declares its own. Declaring its own used to
   mean retyping every API key — task 002 fixed the server side of that, and this task is what
   finally spends it.

### What the settings file still looks like

Unchanged, and not changing here: `Accounts` is an array on the default configuration and on each
profile, and each element carries credentials *and* selection together
(`ImmichFrame.WebApi/Models/ServerSettings.cs:79-97`). A profile that declares `Accounts` replaces
the inherited array outright. This task changes only how that is *presented* and *edited*.

### Three findings carried from earlier tasks

- **The read hands inherited accounts `id: null`.** `AdminConfigService.Entry(...)` issues
  `Id = index < (stored?.Count ?? 0) ? AccountId(...) : null`, so a profile that declares no
  `Accounts` gets its merged accounts with null handles. The handle a profile needs in order to
  adopt an account therefore has to come from **the default entry's** copy of that account, which
  does carry one. This is the hinge of the whole task.
- **Two assertions that go stale the moment adoption works.** The warning banner at
  `entry-editor.svelte:170-173` ("it cannot keep API keys it never had of its own") and the `Id` doc
  comment at `ImmichFrame.WebApi/Models/AdminConfigDto.cs:245-248` ("Null on an account a profile is
  inheriting … neither has a stored key to keep"). Both are true today and false after this task.
- **`ApiKeyFile` must be carried forward on adoption.** An adopted account whose stored source reads
  its key from a file, sent without `apiKeyFile`, is written with neither a key nor a file and the
  save fails with the loader's generic *"Either ApiKey or ApiKeyFile must be provided."*

## Objective

Immich accounts appear once, in a section above the profile tabs. A profile assigns accounts and
says what to show from each, without re-describing credentials and without retyping an API key.

## Scope

- `immichFrame.Web/src/lib/components/admin/admin-config.ts` — the identity model, write-through,
  assignment, adoption, validation
- **New** `immichFrame.Web/src/lib/components/admin/accounts-section.svelte`
- `immichFrame.Web/src/lib/components/admin/config-editor.svelte` — render the section above the tabs
- `immichFrame.Web/src/lib/components/admin/entry-editor.svelte` — the accounts section becomes
  assignment, not description
- `immichFrame.Web/src/lib/components/admin/account-editor.svelte` — selection only
- `ImmichFrame.WebApi/Models/AdminConfigDto.cs` — the stale `Id` doc comment only

### Account identity

One row per distinct account across the default configuration and every profile.

- **Labelled accounts** match on the label, trimmed and compared case-insensitively — the same
  comparison `AdminConfigService` uses, so the editor and the server agree on what one account is.
- **Unlabelled accounts** match on normalised server URL, and among several unlabelled accounts at
  one URL within an entry, on their order. Reuse `normalizedServerUrl` from `immich-picker.ts:66`
  rather than writing a second normaliser — it already handles the trailing slash and the
  case-insensitivity of scheme and host, and its comment explains why the path is left alone.
- A labelled account and an unlabelled one never match, even at the same URL. A label is an
  assertion of identity; absence of one is not.

**No label is invented.** Do not seed labels from the URL or anything else — the settings file gains
a key only when the administrator types one.

Two ambiguities to surface rather than resolve:

- **Two accounts in one entry normalising to the same label.** `AdminConfigService` refuses to save
  such a file, but it *loads* — labels are normalised on read, so `"Mum"` and `"mum "` in a
  hand-written file both arrive as `Mum`. Do not merge them into one row. Show both, say they
  collide, and add a validation error asking for distinct labels.
- **Two unlabelled accounts at one URL within an entry.** Ordinal matching makes this deterministic
  but fragile — reordering the array by hand re-pairs them. Require labels here, via a validation
  error naming the URL.

### The accounts section

Above the profile tab strip in `config-editor.svelte`, so it reads as belonging to the whole
configuration rather than to whichever tab is selected.

Each row owns everything credential: label, server URL, API key (the existing Set / Replace key /
Keep stored key states from `account-editor.svelte:80-121`, which work and should be reused rather
than reinvented), and API key file. Plus add and remove.

**Editing a row writes through to every entry's copy of that account.** The URL, label, key and key
file are properties of the account, not of the entry that mentions it; a settings file that
disagreed with itself about which server an account is would be a bug, not a feature.

**Removing a row removes the account from every entry.** Refuse a removal that would leave the
default configuration with no accounts — `AdminConfigService` refuses that save by name
(*"The default configuration must declare at least one Immich account"*), so catch it here where it
can be explained. Removal is destructive and not obviously so: it silently changes every profile
that used the account. Confirm it the way `config-editor.svelte:150-163` confirms deleting a
profile, and say how many profiles are affected.

### The profile section

`entry-editor.svelte` keeps its general-settings sections unchanged. Its accounts section becomes:

- **The default configuration** always declares accounts. Its section is which of the accounts it
  uses — at least one — plus the selection for each.
- **A profile** keeps the existing inherit/override choice, and the existing reasoning for it:
  inheriting keeps the profile sparse and tracking later edits to the default. Overriding reveals a
  tick per account in the section above, plus the selection for each ticked one.
- Seeding on first override keeps working as `toggleAccounts` does now — the profile starts from
  what it was inheriting, so overriding and saving without further edits changes nothing but where
  the values are written.

`account-editor.svelte` loses the URL, key and key-file controls and keeps the selection fields. It
is no longer "an account" but "what this profile shows from this account" — name it and title it
accordingly.

### Adoption: the part that must be exactly right

When an entry declares an account it did not previously declare, the DTO it sends must carry:

- the **`id`** of that account as read from an entry that *does* have a handle for it — in practice
  the default configuration's. Task 002's document-wide lookup then copies the stored key
  server-side. Without the id, the save is refused and the administrator is asked for a key they
  should never have to type.
- **`apiKeyFile`** when the account reads its key from a file, or the save fails closed with a
  message about neither being present.

`toAccount(...)` currently discards the handle for an inherited account (`id: declared ? … : null`)
because an inherited account had no stored key of its own. That is exactly what changed. Keep the
handle available for adoption — but keep `hasStoredKey` honest about *this* entry, since it is what
drives the "Set / Replace key" UI and the "enter a key" validation.

Two accounts in one entry must never carry the same handle: `AdminConfigService` refuses that as
ambiguous, and rightly. Identity is per-row and rows are distinct, so this should be impossible by
construction — make sure it is.

### Validation

Extend `validationErrors(config)` (`admin-config.ts:509`), which is the existing pre-save check.
Add: colliding labels within an entry; two unlabelled accounts at one URL within an entry; a
removal that would empty the default. Keep every existing check working — in particular
"enter the API key for …", which must **not** fire for an account being adopted with a handle,
and must still fire for a genuinely new account.

Its doc comment says it is deliberately not a re-implementation of the server's validation. Honour
that: add the cases where the browser already knows, not a mirror of `AdminConfigService`.

## Non-goals

- No settings-schema change. `Accounts` stays per-entry with credentials and selection together.
- No backend change beyond the one stale doc comment. Do not touch `AdminConfigService`,
  `ServerSettings`, or any controller.
- No change to `immichFrameApi.ts` (generated, DO NOT MODIFY), `swagger.json`, or the picker proxy.
- No change to `picker-cache.ts`'s caching rules. The pickers move on screen; where a list is
  requested from may change, what gets requested must not.
- No auto-seeded labels.
- No reordering UI for accounts, no per-account enable/disable beyond assignment, no bulk actions.
- Do not touch `docs/` — that is task 005.

## Constraints

- Shared working tree. Read `.claude/skills/_shared/concurrency.md`. Never `git stash`, `git reset`,
  `git restore`, `git checkout --`, or `git clean`; never `git add -A`; stage explicit paths only.
  `.gitignore`, `Makefile` and `ImmichFrame.WebApi/Properties/launchSettings.json` are another
  developer's in-flight work — leave all three alone. Do not commit.
- Svelte 5 runes throughout, matching the surrounding code.
- Prettier: tabs, single quotes, no trailing commas, width 100.
- Keep the `{#each … (account)}` keying discipline in `entry-editor.svelte:180-183` — keyed by
  object, never by index, so a delete cannot carry one account's half-typed key onto another.
- Comments explain *why*. These files are heavily commented on purpose; match them.

## Validation

- From `immichFrame.Web`: `npm run check` must pass.
- `npx prettier --check src/lib/components/admin/` and `npx eslint src/lib/components/admin/` must
  both be clean. Do **not** run `npm run lint` and try to fix what it reports: it fails on ~21
  pre-existing prettier offenders and 9 pre-existing eslint errors in committed files that are not
  yours.
- `dotnet build ImmichFrame.sln` — only because of the one doc-comment edit.
- Exercise the real screen. `make dev` serves the app with the existing launch profile; `/admin`
  needs admin OIDC configured, so if you cannot reach the page, say so in your report rather than
  claiming behaviour you did not observe.

## Acceptance criteria

- One Immich server used by the default configuration and two profiles appears as **one** row in the
  accounts section.
- Editing that row's URL or label changes it for every entry that uses the account.
- Creating a profile, ticking an existing account, and saving succeeds **with no API key prompt**,
  and the settings file gains that account under the profile with its key.
- The same for an account whose key comes from an `ApiKeyFile`: the path is carried forward and the
  save is not refused.
- A profile that inherits declares no `Accounts` key and still tracks later edits to the default.
- Two accounts on one Immich server with distinct labels stay two rows and never merge.
- A hand-written file with colliding labels in one entry shows both rows, explains the collision,
  and refuses to save.
- Removing an account asks first, names how many profiles it affects, and is refused when it would
  leave the default with none.
- Adding a brand-new account still requires its API key to be typed before saving.
- The selection fields still show Immich names on load (task 001) and still write the same values.
