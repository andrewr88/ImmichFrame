# 002 — Step 3: `profile` query parameter on the controllers, and the regenerated client

## Context

Branch `custom`. Feature: a client picks one of several named settings configs at
connect time. Steps 1 and 2 are committed (`f5ace18`, `3f15b92`).

The server side already works end to end. `CurrentProfile`
(`ImmichFrame.WebApi/Helpers/Profiles/CurrentProfile.cs`) reads the `profile` query
parameter straight off `IHttpContextAccessor`, and `Program.cs` resolves every settings
interface and service through `ProfileRegistry` from it. `?profile=kitchen` is honoured
today by every endpoint, and `ProfileResolutionTests` proves it.

What is missing is that the parameter is **invisible to Swagger**, so
`immichFrame.Web/src/lib/immichFrameApi.ts` — generated from `openApi/swagger.json` by
oazapfts (`immichFrame.Web/package.json:14`) and marked DO NOT MODIFY — has no way to
send it. Step 4 needs it there.

## Objective

Declare the parameter on the controller actions so Swashbuckle emits it, then
regenerate the schema and the TypeScript client.

### A. Controllers

Add `string profile = ""` alongside the existing `clientIdentifier` parameter on the
**ten** actions that already take `clientIdentifier`:

- `AssetController` — `GetAssets`, `GetAssetInfo`, `GetAssetFaces`, `GetAlbumInfo`,
  `GetImage`, `GetAsset`, `GetRandomImageAndInfo` (7)
- `ConfigController` — `GetConfig` (1)
- `CalendarController` — `GetAppointments` (1)
- `WeatherController` — `GetWeather` (1)

`ConfigController.GetVersion` (`ConfigController.cs:28`) does **not** get one — it
returns the assembly version, which no profile can vary.

**The parameter is for code generation only.** Do not read it, do not pass it to a
service, do not sanitize and log it the way `clientIdentifier` is handled. DI has
already resolved the profile from the query string before the action body runs, and a
second resolution path here would be one that can silently diverge from the real one.
Add a short comment saying so — once per controller is enough, not once per action.

**Trap:** `AssetController.GetImage` calls `GetAsset(id, clientIdentifier,
AssetTypeEnum.IMAGE)` positionally (`AssetController.cs:78`). If `profile` is inserted
before `assetType` in `GetAsset`'s signature, that call silently rebinds
`AssetTypeEnum.IMAGE` onto the wrong parameter and stops compiling — or worse, does not.
Check every internal call site of a signature you change.

### B. Regenerate

```sh
tmux new -d -s dev 'make dev'     # serves :5217
# wait for the port to answer
make api                          # curls swagger.json, runs oazapfts
tmux kill-session -t dev
```

`make api` (`Makefile:16-18`) rewrites both `openApi/swagger.json` and
`immichFrame.Web/src/lib/immichFrameApi.ts`; both are tracked. Commit them as generated
output — **do not hand-edit either**. If the regenerated diff contains changes unrelated
to `profile` (reordering, version bumps, unrelated schema churn), say so in your report
rather than reverting them by hand.

Confirm afterwards that the generated client's functions actually accept `profile` —
grep for it in `immichFrameApi.ts`.

## Scope

- `ImmichFrame.WebApi/Controllers/{Asset,Config,Calendar,Weather}Controller.cs`
- `openApi/swagger.json` (regenerated)
- `immichFrame.Web/src/lib/immichFrameApi.ts` (regenerated)

## Non-goals

- No frontend wiring. `immichFrame.Web/src/lib/index.ts` (`getAssetStreamUrl`),
  `home-page.svelte` and the `[config]` route are step 4, a separate task.
- No new tests. The behaviour is already covered by `ProfileResolutionTests`; these
  parameters add no behaviour to test.
- Do not touch `Program.cs`, `ProfileRegistry`, `CurrentProfile` or the settings
  interfaces.
- Do not add `profile` to `GetVersion`.

## Constraints / caveats

- **Shared working tree.** Never run `git stash`, `git reset`, `git restore`,
  `git checkout --`, or `git clean`. Never `git add -A` — stage explicit paths only.
  Do not commit; the architect commits. Do not revert or flag changes you did not author.
- The tree is clean at `3f15b92` as this task starts. Anything you find modified that
  is not yours is someone else's work.
- Kill the tmux session when you are done; do not leave `make dev` holding :5217.

## Acceptance criteria

- `make test-webapi` and `make test-core` still green (68 and 80 at `3f15b92`).
- `grep -c profile immichFrame.Web/src/lib/immichFrameApi.ts` is non-zero, and the ten
  operations above each accept it.
- `openApi/swagger.json` lists `profile` as a query parameter on those ten operations
  and on no others.

---

## Addendum — review round 1

Both reviewers approved. The adversarial reviewer independently regenerated both files
from a stub Immich server and got byte-identical output, so the generated artifacts are
confirmed pure regeneration, not hand-edited.

Corrected above: the brief said "nine" actions while enumerating **ten** (AssetController
7 + Config 1 + Calendar 1 + Weather 1). The implementation always matched the list; only
the prose and the acceptance criteria were wrong, and are now fixed. Nothing to change in
code — do not remove a `profile` parameter to make it nine.

Two small changes, then this task is done.

### 1. eslint override for the generated client

Regeneration dropped the leading `/* eslint-disable @typescript-eslint/no-explicit-any */`
that the committed `immichFrameApi.ts` carried — oazapfts 7.4.2 no longer emits it. The
file still has 11 `[key: string]: any` index signatures, so eslint now reports 11 errors
there. Re-adding the pragma by hand is not an option: the file is marked DO NOT MODIFY
and the next `make api` would wipe it.

Add a scoped override to `immichFrame.Web/eslint.config.js`:

```js
{
    files: ['src/lib/immichFrameApi.ts'],
    rules: {
        '@typescript-eslint/no-explicit-any': 'off'
    }
}
```

A targeted rule override, **not** an `ignores` entry — the rest of the rules should keep
applying, so a genuinely broken generation still surfaces. Match the file's existing
formatting (tabs, single quotes). Add a brief comment saying the pragma used to live in
the generated file and cannot survive regeneration.

Note `npm run lint` is red at `3f15b92` for unrelated reasons — 9 eslint errors across
five `.svelte` components plus a repo-wide `prettier --check` failure over 21 files. You
are not expected to fix those. Verify only that the 11 `immichFrameApi.ts` errors are
gone; do not touch anything else the linter complains about.

### 2. Comment placement in `ConfigController`

Both reviewers flagged it: the shared comment at `ConfigController.cs:20-23` says
"the actions below", and `GetVersion` sits below it without a `profile` parameter — a
future reader could read the comment as licence to add one. Either narrow the wording to
the singular or move the comment onto `GetConfig`. The other three controllers are fine
as they are.

## Constraints (unchanged)

Shared working tree: no `git stash`/`reset`/`restore`/`checkout --`/`clean`, no
`git add -A`, do not commit. Do not re-run `make api` — the generated files are correct
and verified; regenerating risks unrelated churn.
