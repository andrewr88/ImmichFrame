# 002 — Resolve account API-key handles across the whole document

## Context

`AdminConfigService` hands the editor an opaque handle per stored account —
`AccountId(version, profileName, index)` (`ImmichFrame.WebApi/Helpers/Config/AdminConfigService.cs:337`),
a truncated SHA-256 of `version \n profile \n index`. The editor echoes it back on save, and
`Accounts(...)` (`:502`) uses it to put the masked API key back on the account it came from, so an
administrator never retypes a key they did not change.

`Accounts(...)` builds its lookup from **one entry's** stored accounts only:

```csharp
storedById[AccountId(version, profileName ?? ConfigCatalog.DefaultProfileName, index)] = account;
```

where `stored` is that entry's own declared `Accounts` array — `BuildEntry`'s `declared` argument,
which comes from `StoredSecrets(...)`'s `document.DeclaredOverrides(profileName)` (`:214-229`).

A profile that has never declared its own accounts therefore has `stored == null`, and any account
it sends with a masked key hits the refusal at `:548`:

> Saving this makes configuration profile 'kitchen' declare its own account list instead of
> inheriting one, so it cannot keep an API key it never had of its own. Enter the API key for
> '…' and save again.

That refusal is the single obstacle to the feature this plan exists for: assigning an existing
account to a profile without retyping its credentials. Tasks 004 and 005 depend on this task.

The handle is already understood as a copy mechanism, not an integrity control — `:326-333` says so
outright: *"an administrator who echoes a legitimately issued handle against a server URL of their
choosing gets that account's stored key written there, and is anyway free to type the key out. What
the handle prevents is accidental misattribution."* This task widens where a handle may be
resolved from; it does not change what a handle is for or who may use one.

## Objective

An account handle issued by the read resolves against **every entry in the document** — the default
configuration and every profile — rather than only the entry being saved. A profile that adopts an
account it did not previously declare keeps that account's stored API key, server-side, with the key
never sent to the browser.

## Scope

- `ImmichFrame.WebApi/Helpers/Config/AdminConfigService.cs`
- `ImmichFrame.WebApi.Tests/Controllers/AdminConfigControllerTests.cs` (and a new fixture under
  `ImmichFrame.WebApi.Tests/Resources/` if an existing one does not fit)

### The lookup

Build the id → stored-account map once per save, over the whole document: `DeclaredOverrides(null)`
for the default configuration plus `DeclaredOverrides(name)` for each of `document.ProfileNames`,
keying each account by `AccountId(version, <that entry's name>, index)` exactly as today. The
default configuration's name in a handle is `ConfigCatalog.DefaultProfileName`, which is what
`Accounts(...)` already substitutes for a null `profileName`.

The id already encodes which entry it came from, so entries cannot collide and nothing is guessed.

`StoredSecrets(...)` (`:191`) already creates the document and closes over it; that is the natural
place to build this from, but how you thread it down to `Accounts(...)` through `BuildDocument` /
`BuildEntry` is your call — prefer the change that keeps those signatures honest over the one that
adds the fewest characters.

The legacy-schema branch of `StoredSecrets` synthesises its stored accounts from the running catalog
and has no profiles at all (a v1 file is flat). Its map is therefore just the default's accounts,
and a conversion save must keep working exactly as it does now.

### Two different questions, two different lookups

`Accounts(...)` currently uses one matched `JsonObject` for two unrelated purposes. Keep them apart:

1. **Which stored API key is this?** — now answered by the document-wide map.
2. **What did *this entry* already spell out?** — the `declared` argument threaded into `Account(...)`
   (`:583`), where `Declares(declared, property.Name)` decides whether a property whose value equals
   the built-in default is still written. That question must keep being answered from **this entry's
   own** stored account, and must be null when this entry had no such account.

If (2) were also answered cross-entry, a profile adopting the default's account would write out every
property the *default* spelled out, needlessly de-sparsifying the profile. Sparseness is the property
`BuildEntry`'s doc comment (`:413-415`) and `Account(...)`'s (`:570-578`) both exist to protect.

### Ambiguity, which stays refused

`claimed` is per-`Accounts(...)` call, i.e. per entry. That is correct and must stay:

- Two entries both claiming the default's account #0 is **legitimate** — each writes its own copy of
  that key into its own `Accounts` array. This is the whole point of the task.
- Two accounts **within one entry** claiming the same stored account is still the ambiguity that
  must not be resolved by guessing. Keep refusing it.

Rewrite the two refusal messages at `:548` and `:555` so they describe what is now true. The `:548`
message ("it cannot keep an API key it never had of its own") is wrong after this change and must go;
what remains is the genuinely unresolvable case — a handle that matches nothing anywhere in the
document, or one already claimed within this same entry — and a brand-new account with no handle and
no typed key.

## Non-goals

- **No new server-side URL check.** Do not refuse a save because the adopting account's
  `ImmichServerUrl` differs from the stored account's. The editor already guards this for browsing
  (`immich-picker.ts`'s `URL_CHANGED`), `:326-333` explicitly accepts the server-side case, and an
  administrator moving their Immich server to a new address is a legitimate edit that such a check
  would break.
- No change to `AccountId`'s inputs or hash — handles stay wire-compatible, and the stale-version
  refusal at `:113-117` still fires before any handle is looked up.
- No change to `Label`, the accounts/profile UI, or any frontend file. `Label` is task 003.
- No change to `ApiKeyFile` handling, to the three general secrets, or to `StoredSecrets`'s
  secrets-per-entry behaviour — an inherited *general* secret must still not be written into a
  profile as an override of its own (`:176-190`).
- Do not relax the "default configuration must declare at least one account" refusal (`:380`).

## Constraints

- Shared working tree. Read `.claude/skills/_shared/concurrency.md`. Never `git stash`, `git reset`,
  `git restore`, `git checkout --`, or `git clean`; never `git add -A`; stage explicit paths only.
  Scope edits to the files above; do not revert or flag hunks you did not author. Do not commit.
- `.gitignore`, `Makefile` and `ImmichFrame.WebApi/Properties/launchSettings.json` are modified in
  the working tree by someone else. Leave them alone.
- C# conventions per `ARCHITECTURE.md`: XML doc comments that explain *why*, matching the density of
  the file you are editing, which is heavily commented deliberately.
- Tests: NUnit 4, `Method_Scenario_ExpectedResult` naming, Arrange/Act/Assert comments, integration
  style over `WebApplicationFactory<Program>` as the existing admin tests do.

## Validation

- `dotnet build ImmichFrame.sln`
- `make test-webapi` and `make test-core` — both are what CI runs
  (`.github/workflows/test.yml`), and both must pass.

Note: `npm run lint` in `immichFrame.Web` fails on ~21 pre-existing prettier offenders and 9
pre-existing eslint errors in committed files. That is not yours and nothing here touches the
frontend — do not attempt to fix it.

## Acceptance criteria

- A profile that declares no `Accounts` of its own, saved with an account carrying the **default's**
  handle and a masked key, writes that account into the profile with the default's stored API key —
  and the key is never present in any response to the browser.
- The same works between two profiles, not only default → profile.
- Two profiles in one save both adopting the same stored account each get the key.
- Two accounts in **one** entry carrying the same handle is still refused, by a message naming the
  ambiguity.
- A handle matching nothing in the document, with a masked key, is still refused.
- A brand-new account with no handle and no typed key is still refused.
- A stale `Version` is still refused before any of the above is reached.
- Converting a v1 file still carries its API keys across.
- Round-tripping an unchanged configuration still produces a byte-identical file, and profiles stay
  as sparse as they were.
