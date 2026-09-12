# 003 — An optional Label on an Immich account

## Context

The configuration editor is being reworked so that Immich accounts appear once, in a section at the
top of `/admin`, and a profile then assigns accounts rather than re-describing them (tasks 004 and
005). That presentation needs a stable answer to "are these two entries' accounts the same account?"

The settings file cannot answer it today. `AdminAccountSettingsDto`'s own doc comment
(`ImmichFrame.WebApi/Models/AdminConfigDto.cs:239-242`) says why: *"Nor is `ImmichServerUrl` an
identity: two accounts may legitimately be two users on the same Immich server."* That is a real
setup — two family members with separate Immich logins on one server — and since API keys are never
sent to the browser, the editor cannot tell such accounts apart on its own.

The account handle (`AdminConfigService.AccountId`) is not the answer either: it is scoped to one
read of one file version, by design, and says nothing across a reload.

## Objective

`ServerAccountSettings` gains an optional `Label` — a human name for an account — which the admin
API reports and accepts, and which the settings file carries. Nothing at runtime behaves differently
because of it.

## Scope

- `ImmichFrame.WebApi/Models/ServerSettings.cs` — the `Label` property
- `ImmichFrame.WebApi/Models/AdminConfigDto.cs` — `Label` on `AdminAccountSettingsDto`
- `ImmichFrame.WebApi/Helpers/Config/AdminConfigService.cs` — populate it on the read; refuse
  duplicates (see below)
- `ImmichFrame.WebApi.Tests/Controllers/AdminConfigControllerTests.cs` — tests
- `openApi/swagger.json` and `immichFrame.Web/src/lib/immichFrameApi.ts` — regenerated, not
  hand-edited
- `docs/docs/getting-started/configuration.md` — the settings-reference entry for `Label`

### Reading it back

`AdminAccountSettingsDto`'s constructor takes `IAccountSettings`, which is a **Core** interface and
must not learn about `Label` — the editor's notion of identity has no business in Core, and nothing
in `ImmichFrame.Core` would ever read it.

`ServerSettings.AccountsImpl` is already typed `IEnumerable<ServerAccountSettings>`
(`ServerSettings.cs:16`), so `AdminConfigService.Entry(...)` can read the concrete type directly.
Watch the second path: `NotFromAFile(...)` builds its accounts from `_catalog.Default`, which is an
`IServerSettings` and exposes only `IEnumerable<IAccountSettings>`. Handle that case honestly —
a pattern match on the concrete type, reporting no label when it is not one — rather than widening
the Core interface to make one call site tidier.

### Duplicate labels, refused

Two accounts **within one entry's account list** carrying the same non-empty label (compared
case-insensitively, after trimming) must be refused on save, in `AdminConfigService`, with a message
in the style of the file's existing refusals. Such a file would make two distinct credentials look
like one account in the editor, and editing that single row would then write one account's server
URL or key over the other's — the same class of silent credential misattribution that
`Accounts(...)`'s no-positional-fallback rule (`AdminConfigService.cs:495-501`) exists to prevent.

Labels are **not** required to be unique across entries — the opposite: the same label in the
default configuration and in a profile is exactly how those two entries say "this is the same
account". Do not add a cross-entry check.

An absent label, an empty one, and one that is only whitespace all mean the same thing: this account
has no label. Normalise on that.

### Writing it out

`AdminConfigService.AccountProperties` is built by reflection over `ServerAccountSettings`
(`:44-45`), so `Account(...)` will pick `Label` up and write it sparsely with no further work — it
is written when set and omitted when null, like every other account setting. Confirm that rather
than assuming it.

### Regenerating the clients

`openApi/swagger.json` is produced by `make api`, which curls a **running** app:

```
curl http://localhost:5217/swagger/v1/swagger.json -o ./openApi/swagger.json
npm --prefix immichFrame.Web run api
```

Swagger is only served in Development (`Program.cs:253-258`). `make dev` runs the app with the
launch profile, and `ImmichFrame.WebApi/Properties/launchSettings.json` already points
`IMMICHFRAME_CONFIG_PATH` at `/workspace/docker`, where a `Settings.yml` exists. **That file is
another developer's uncommitted work — use it, do not modify it, and do not stage it.**

The admin endpoints are in the generated spec regardless of whether admin OIDC is configured:
Swashbuckle reads ApiExplorer metadata, while `AdminSurfaceMiddleware` only 404s at request time.

**Read the regenerated diff before accepting it.** The checked-in `swagger.json` was last written by
an earlier commit, so a regeneration can pick up unrelated drift. If anything beyond the `Label`
additions appears in either generated file, stop and report it rather than committing it — the
question of whether that drift is correct is not this task's to answer.

## Non-goals

- No change to `ImmichFrame.Core` — not `IAccountSettings`, not the pools, not anything else.
  `Label` is editor metadata that happens to live in the settings file.
- No runtime behaviour keyed off `Label`: nothing selects, logs, or routes by it.
- **No auto-seeding of labels.** Deciding what an unlabelled account is called, and writing one in,
  belongs with the identity logic in `admin-config.ts` — task 004. This task only makes the field
  exist.
- No frontend component changes. Regenerating `immichFrameApi.ts` is not a component change; that
  file is marked DO NOT MODIFY and must only be produced by `npm run api`.
- No uniqueness rule across entries, and no length cap.
- No change to the v1 schema or `ServerSettingsV1Adapter` — a v1 file simply has no labels, and
  converting one produces accounts without them.

## Constraints

- Shared working tree. Read `.claude/skills/_shared/concurrency.md`. Never `git stash`, `git reset`,
  `git restore`, `git checkout --`, or `git clean`; never `git add -A`; stage explicit paths only.
  `.gitignore`, `Makefile` and `ImmichFrame.WebApi/Properties/launchSettings.json` are another
  developer's in-flight changes — leave all three alone. Do not commit.
- C# conventions per `ARCHITECTURE.md`; XML doc comments that explain *why*, matching the density of
  the files you are editing.
- Tests: NUnit 4, `Method_Scenario_ExpectedResult`, Arrange/Act/Assert comments, integration style
  over `WebApplicationFactory<Program>` as the existing admin tests do. Fixtures in this test class
  are inline raw-string consts.
- Kill the dev server when you are done with it.

## Validation

- `dotnet build ImmichFrame.sln`
- `make test-webapi` and `make test-core` — both must pass.
- From `immichFrame.Web`: `npm run check` must pass. Note `npm run lint` fails on ~21 pre-existing
  prettier offenders and 9 pre-existing eslint errors in committed files — that is not yours, do not
  fix it, and do not let `npm run api`'s output be reformatted to satisfy it.

## Acceptance criteria

- A settings file whose account carries `Label: "Mum's photos"` loads, and the admin read reports
  that label on that account.
- An account with no `Label` reports none, and round-tripping it does not introduce one.
- Saving an account with a label writes `Label` into the settings file; saving one without it writes
  no `Label` key at all.
- Two accounts in one entry with the same label, differing only in case or surrounding whitespace,
  are refused with a message naming the duplicate.
- The same label in the default configuration and in a profile is accepted.
- `swagger.json` and `immichFrameApi.ts` differ from their committed versions only by the `Label`
  addition.
- A v1 file still converts, and the accounts it produces carry no labels.
