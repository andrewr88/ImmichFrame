# 005 — A test runner, and a suite over the admin configuration model

## Context

`immichFrame.Web` has no test runner: `package.json`'s scripts are `dev`, `build`, `preview`,
`prepare`, `check`, `check:watch`, `lint`, `format`, `api` — there is no `test`, and no vitest,
jest or playwright among the dependencies.

Meanwhile `immichFrame.Web/src/lib/components/admin/admin-config.ts` has grown to roughly 880 lines
in which account identity, handle selection, write-through and validation are all load-bearing for
**which Immich API key gets written under which server URL**. A mistake there produces a save that
validates, swaps in, and refreshes the version token exactly like a successful one.

Both reviewers of task 004 found real defects in that file — and both found them by bundling it with
esbuild and writing throwaway harnesses, one of them a 20 000-document fuzz. That work was discarded
after the review. Three defects reached review that a modest suite would have caught first:

1. Untick → re-tick of an account on a profile replaced that profile's photo selection with the
   default configuration's.
2. An account stored inline in one entry and via `ApiKeyFile` in another was sent with neither
   credential, and the save died on the loader's generic *"Either ApiKey or ApiKeyFile must be
   provided."*
3. The removal dialog counted only profiles that *declare* accounts, so inheriting profiles losing
   an account were never named.

The user has approved introducing vitest for this. **CI is explicitly out of scope** — see non-goals.

## Objective

`npm test` runs a suite in `immichFrame.Web` that covers the pure model logic behind the admin
configuration editor, including regression tests for the three defects above.

## Scope

- `immichFrame.Web/package.json` — the `vitest` devDependency and a `test` script
- `immichFrame.Web/package-lock.json` — as produced by npm, not hand-edited
- Whatever vitest configuration the repo needs (see below)
- **New** test file(s) for `admin-config.ts`

### The configuration problem to expect

`admin-config.ts` imports `normalizedServerUrl` from `./immich-picker`, and `immich-picker.ts` does
a runtime `import * as api from '$lib/immichFrameApi'`. So even a test that only touches pure
functions drags the `$lib` alias in, and `$lib` is resolved by SvelteKit rather than by plain vite.

Solve that however is cleanest — a `vitest.config.ts` declaring the alias, or the SvelteKit plugin,
or a test-time stub. Prefer whatever keeps `npm run dev`, `npm run build` and `npm run check`
behaving exactly as they do now; those three are the commands people actually use here, and none of
them may change behaviour because a test runner arrived.

There is also a deliberate import cycle (`admin-config.ts` → `immich-picker.ts` → `admin-config.ts`)
which the production build resolves because no module-level initializer crosses it. If it bites
under vitest, say so rather than restructuring the modules to suit the test runner.

### What to cover

Pure model logic only — `toEditable`, `toUpdate`, `validationErrors`, the roster/identity
construction, handle selection, and the selection helpers. Aim at behaviour that would be wrong in a
way that matters, not at restating what the code says.

At minimum:

- **Identity.** One Immich server used by the default configuration and two profiles collapses to
  one row. Labelled accounts match on the trimmed, case-folded label; unlabelled ones on normalised
  URL plus ordinal; a labelled and an unlabelled account at one URL never merge. Labelled accounts
  must not consume an unlabelled ordinal.
- **Adoption.** A profile that declared no accounts, given one, emits the **default's** handle with
  the masked-key placeholder — and emits no validation error asking for a key.
- **`ApiKeyFile` carry-forward.** An adopted file-backed account sends its path and no key.
- **The no-duplicate-handle invariant.** Two accounts within one entry can never carry the same
  handle; `AdminConfigService` refuses that as ambiguous. This is an invariant over arbitrary
  documents rather than a fact about one, so it wants generated input rather than a single case.

- **The credential-shape grid — and prefer exhaustive enumeration over random generation here.**
  This is a lesson paid for during task 004's review. The borrowed-handle key-file defect was found
  by an *exhaustive* grid over credential shapes; a reviewer's 250 000-document **random** fuzz
  reported zero misses against the same known-buggy code, because the shape is too rare to hit by
  chance. A random generator would have shipped that bug with a green suite.

  Enumerate the combination space instead: for one account, cross the owner entry's credential kind
  (inline key / `ApiKeyFile` / inheriting) against whether the row's key-file box is set or cleared,
  against each entry ticked or not, against adopted or self-declared, against a freshly typed key or
  the kept placeholder. That is a few thousand cases and runs in well under a second.

  Check each against what the server would do. Two rules for that model, both learned the hard way:
  write it from the C# in `AdminConfigService.Accounts(...)`/`Account(...)` and
  `ServerAccountSettings.ValidateAndInitialize()`, **never** from `admin-config.ts` — a model
  derived from the code under test restates its assumptions and cannot see a shared mistake; and
  assert in both directions, since an over-refusal blocking a legitimate save is as much a defect
  as a missed one.
- **Sparseness and round-tripping.** An untouched configuration produces the same handles and
  selections it was read with, and a profile that inherits still declares no `Accounts`.
- **Regressions for the three defects**, each written so it fails against the pre-fix behaviour.
- **The existing validation rules** that predate task 004 and must not have been broken by it:
  a declared-but-empty account list, a missing server URL, a missing API key on a genuinely new
  account, non-numeric numeric settings, and an empty secret box in `set` mode.

Do not test the Svelte components. There is no jsdom or testing-library here and adding them is a
larger dependency decision than this task carries; the defects that mattered were all in the model.

### Conventions

The repo has no frontend test precedent, so you are setting it. Keep it recognisable to someone who
knows the C# side: the tests there are `Method_Scenario_ExpectedResult` with Arrange/Act/Assert
comments (`ARCHITECTURE.md`, "Tests"). Match that spirit in whatever idiom reads naturally in
vitest — do not transliterate NUnit.

Fixtures are `AdminConfigDto`-shaped read payloads. Build them with a helper rather than repeating
large literals; a reader should be able to see what makes each case different.

## Non-goals

- **Do not touch `.github/workflows/test.yml`.** The user decided against wiring this into CI for
  now. CI stays the two `dotnet test` invocations.
- No change to any file under `src/lib/components/admin/` other than what a genuine test-visibility
  need forces — and if something must be exported purely to be tested, say so in your report rather
  than doing it silently.
- No component, DOM, or end-to-end tests; no jsdom, no testing-library, no playwright.
- No backend tests — `make test-webapi` already covers `AdminConfigService`.
- Do not fix anything you find. If the suite surfaces a new defect, **leave it failing or skipped,
  and report it**; the architect decides whether it is this task's business.
- Do not reformat, re-lint or otherwise touch the ~21 pre-existing prettier offenders or 9 eslint
  errors in committed files.

## Constraints

- Shared working tree. Read `.claude/skills/_shared/concurrency.md`. Never `git stash`, `git reset`,
  `git restore`, `git checkout --`, or `git clean`; never `git add -A`; stage explicit paths only.
  `.gitignore`, `Makefile` and `ImmichFrame.WebApi/Properties/launchSettings.json` are another
  developer's in-flight work — leave all three alone. Do not commit.
- Pin the vitest version the way the rest of `devDependencies` is pinned (caret ranges).
- Prettier: tabs, single quotes, no trailing commas, width 100. Test files are not exempt.

## Validation

- `npm test` passes from `immichFrame.Web`.
- `npm run check` still passes.
- `npm run build` still succeeds — proof the runner did not disturb the app build.
- `npx prettier --check` and `npx eslint` clean on the files you added or changed. Do not run
  `npm run lint`.

## Acceptance criteria

- `npm test` runs the suite and passes.
- Each of the three regression tests demonstrably fails against the pre-fix behaviour. Show your
  working: temporarily reintroduce each defect, confirm the test fails, restore. Report the result
  — a regression test that passes against the bug is worse than none.
- The no-duplicate-handle test exercises many generated documents, not one.
- The credential grid is exhaustive over the enumerated shapes, not randomly sampled, and its model
  of the server is written from the C# rather than from `admin-config.ts`. Demonstrate its
  sensitivity the same way: revert `handleOwner` to the pre-fix `entryName` test, confirm the grid
  goes red, restore.
- `npm run dev`, `npm run build` and `npm run check` behave exactly as before.
- No file under `src/lib/components/admin/` changed except test files, or with a reported reason.
