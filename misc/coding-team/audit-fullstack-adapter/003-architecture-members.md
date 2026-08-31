# 003 — fullstack adapter: architecture members

## Context

Tasks 001 (`aebc290`) and 002 (`3c70773`) delivered Phases 1 and 2 across both halves of the
stack: 114 files, 31 packages, 287 findings, six scan drivers. Phase 3 is still stubbed —
`audit_arch_preflight` currently prints a refusal and returns 1, because `audit/arch.sh:45`
calls it unguarded and the core's placeholder mechanism covers only the report sections.

This task replaces that stub with the real arch members and lights up `arch-sweep`.

Read `audit/ADAPTERS.md` ("Architecture report") for the contract and `audit/arch.sh` for how
the core drives it — the call order is `audit_arch_preflight` (line 45) → `resolve_target` →
`audit_arch_build_graph` → sections A, B, C (core's), D, E. `audit/adapters/go.sh` implements
all of them and is the worked reference.

State the core defines before calling your functions: `DB`, `REPO_ROOT`, `TMP_DIR`,
`TARGET_SCOPE`, `TARGET_DIR`, `TARGET_FILE_LIKE`. `TARGET_FILE_LIKE` is `%` in whole-repo mode
and `<subtree>/%` when scoped — use it to scope SQL against the `files` table.

## Objective

Implement the architecture members in `audit/adapters/fullstack.sh` so `audit/audit.sh
arch-sweep` and `make audit-arch` produce a real report, whole-repo and scoped, covering both
the C# projects and the SvelteKit frontend.

## Scope

Modify `audit/adapters/fullstack.sh` and `CLAUDE.md` (drop the "arch unavailable" wording, and
the note that `make audit-arch` refuses). Also remove the now-obsolete `AUDIT_ADAPTER`-keyed
guard in the `audit-arch` Makefile target — it exists only because the members were missing.

### Members

| Member | Responsibility |
| --- | --- |
| `audit_arch_preflight` | **Replaces the stub.** Verify the toolchain, set `AUDIT_ARCH_MODULE` (printed in the report header — something identifying this repo and both stacks), stash any graph inputs under `TMP_DIR`. Non-zero on a genuine precondition failure only. |
| `audit_arch_resolve_nondir <arg>` | Map a non-directory target to a repo-relative directory, or print nothing. Handle a C# namespace (`ImmichFrame.Core.Logic.Pool` → `ImmichFrame.Core/Logic/Pool`) and the SvelteKit `$lib` alias (`$lib/stores` → `immichFrame.Web/src/lib/stores`). Called only after the core's own `[ -d ]` check fails. |
| `audit_arch_has_sources <dir>` | Zero if the directory holds tracked C# or frontend sources. |
| `audit_arch_build_graph` | Build both import graphs scoped to `TARGET_SCOPE`/`TARGET_DIR`, writing artefacts under `TMP_DIR` for the sections to read. |
| `audit_arch_section_a/_b/_d/_e` | Emit each section body including its own `=== Section X: … ===` header and surrounding blank lines. |

Section C (largest files) is the core's — do not implement it.

### Section A — import graph

Emit fan-in / fan-out per package (directory), **as two clearly-labelled subgraphs**, C# and
frontend. Do not attempt to join them into one graph; see Non-goals.

- **C#**: build a namespace→directory map by reading each tracked `.cs` file's `namespace`
  declaration, then resolve each `using ImmichFrame.*;` through that map to a directory edge.
  Deriving the map from actual declarations rather than assuming namespace mirrors path is the
  robust route — `ARCHITECTURE.md` says they track each other, but do not depend on it.
  Ignore `using` of framework/third-party namespaces; internal edges only.
- **Frontend**: resolve relative (`./`, `../`) and `$lib/…` imports to files, then to their
  directory. Ignore bare package imports (`svelte`, `@sveltejs/kit`).

### Section B — complexity hotspots

Use the branch-keyword heuristic: per function, count `if`, `else if`, `while`, `for`,
`foreach`, `case`, `catch`, `&&`, `||`, `??`, `?.` and the ternary `?`. Report functions over a
threshold of 10, highest first, with `file:line` and the score.

**Label the section explicitly as approximate.** This is not cyclomatic complexity — Roslynator
has no complexity command and `Microsoft.CodeAnalysis.Metrics` was rejected in 001 because the
audit tool must not alter the build. A reader must not mistake these numbers for real CC.
Function-boundary detection in `awk` is inherently fuzzy for both C# and Svelte; aim for useful
ranking, not precision, and say so.

### Section D — public API surface

Per package: count exported/public declarations. C# — `public` type and member declarations.
Frontend — `export` declarations. Plain `grep`/`awk` counting is fine; **do not** take a
dependency on `roslynator list-symbols` for this, since Roslynator is an optional tool and arch
must work without it.

### Section E — layer violations

Report each with `file:line`. These are this repo's actual rules, from `ARCHITECTURE.md`:

| Rule | Detection |
| --- | --- |
| `core-to-webapi` | `using ImmichFrame.WebApi…` under `ImmichFrame.Core/` — the dependency direction is one-way, `WebApi → Core` |
| `aspnet-in-core` | `using Microsoft.AspNetCore…` under `ImmichFrame.Core/` — Core knows nothing about ASP.NET |
| `test-in-prod` | `using NUnit…` / `using Moq…` outside the two `*.Tests` projects |
| `profile-leak` | `IConfigCatalog` or `ProfileRegistry` referenced under `ImmichFrame.WebApi/Controllers/` — no controller may know profiles exist |
| `raw-fetch` | `fetch(` under `immichFrame.Web/src` outside the generated `immichFrameApi.ts` — all API access goes through the generated client |

An empty result for a rule is a pass; print it as such rather than omitting the rule.

## Non-goals

- **No cross-language graph edge.** Joining the Svelte call sites to C# controller routes is a
  separate, much larger piece of work; `raw-fetch` in Section E covers the drift that actually
  matters. Keep the two subgraphs side by side.
- No changes to Phase 1 or 2 behaviour. Discovery, priority, the comment regex and all six scan
  drivers must be untouched, and their counts must not move.
- No new tool dependencies. Everything here is `git`, `grep`, `awk`, `sed`, `sqlite3`, `jq`.
- Do not implement Section C.
- Do not fix any violation the report finds.

## Constraints

- **POSIX `sh`, no bashisms.** Verify with `dash -n`. The adapter is sourced by a `#!/bin/sh`
  core.
- Prefix any new function-local variable to avoid colliding with the sourced core's namespace,
  as `_af_` already does — `arch.sh` uses `letter`, `fn`, `arg`, `resolved` at minimum.
- **A section that cannot compute its data must say so, not print an empty table.** This task
  has bitten us four times now in a single shape: an analyzer or guard reporting success with
  zero results, indistinguishable from a genuinely clean run. Specifically: `jq -e` **exits 0
  on empty input**, so it can only tell you parsing didn't fail, never that a document arrived
  — assert presence (`jq -e -s 'length == 1 and …'`) wherever you gate on a tool having
  produced output. Apply the same standard to every new command in this task.
- `audit_arch_build_graph` returning non-zero exits the whole core, so reserve it for genuine
  toolchain failures — an empty scope is not one.
- The working tree holds in-flight C# work owned by someone else (`ImmichFrame.WebApi/Program.cs`,
  `.../Helpers/ImmichServerVersionChecker.cs`, two files under `ImmichFrame.WebApi.Tests/
  Controllers/`, untracked `ImmichFrame.WebApi/Helpers/Profiles/`). Do not edit, revert, judge
  or commit them. No `git add -A`, `git stash`, `git commit`.
- Do not modify any upstream kit file (`audit/*.sh` other than the adapter, `audit/schema.sql`,
  `audit/*.md`, `audit/manifest.json`, `.claude/`). All 21 must keep checksumming clean.
- Raise privileged operations rather than performing them.

## Acceptance criteria

Run each and report actual output:

1. `sh -n` and `dash -n` pass.
2. `make audit-arch` produces a report — it must no longer refuse. Sections A, B, C, D and E
   all carry real data; **none may show `(not available for adapter 'fullstack')`**.
3. The report header names a non-empty `Module:`.
4. Scoped mode works: `make audit-arch PKG=ImmichFrame.Core/Logic` and
   `make audit-arch PKG=immichFrame.Web/src/lib`, each scoping every section.
5. `audit_arch_resolve_nondir` maps `ImmichFrame.Core.Logic.Pool` and `$lib/stores` to their
   directories; `make audit-arch PKG=ImmichFrame.Core.Logic.Pool` works end to end.
6. Section E: report what each of the five rules found. State plainly whether the repo is clean
   — do not present an empty result as though rules had failed to run.
7. Phase 1 and 2 are unperturbed: `sync` still reports **114 files**, `packages` **31**, a scan
   still totals **287 findings** with C# at 47/0/218, and a second scan seeds **0 new** on all
   six tools.
8. Manifest: all 21 upstream files still match.
9. `make audit` exits 0.
10. A bad target still fails cleanly: `make audit-arch PKG=does/not/exist`.

Report the full whole-repo report body, and say which Section B scores you would treat as real
signal versus heuristic noise.
