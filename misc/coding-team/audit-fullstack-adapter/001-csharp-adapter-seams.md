# 001 — fullstack adapter: C# seams

## Context

`audit/` holds a portable audit kit replicated from https://onedev.sharpspoon.io/agent-kit.
Its core (`audit/audit.sh`, `audit/scan.sh`, `audit/arch.sh`) is language-agnostic and
sources exactly one adapter, resolved from `AUDIT_ADAPTER` — see `audit/audit.sh:11-17`,
`audit/scan.sh:20-23` (sourced at `:117`), `audit/arch.sh:26-32`. The kit ships only
`audit/adapters/go.sh` (924 lines, 23 contract functions), so every `audit-*` target in the
Makefile is currently inert.

**`audit/ADAPTERS.md` is the authoritative contract. Read it fully before writing code, and
read `audit/adapters/go.sh` alongside it as the worked reference** — it is exactly what the
contract describes, and its structure, comment density, and error handling are the house
style to match.

Decisions already made (do not relitigate):
- One composite adapter, not one per language. This task does C# only; TS/Svelte seams are
  task 002 into the same file. Hence the name `fullstack.sh`.
- A "package" is a directory containing source files. This is already how the core works
  (`v_packages` in `audit/schema.sql:268-290` computes `dirname(path)` in pure SQL), so
  there is nothing to implement for it.
- Zero changes to any upstream kit file. `audit/manifest.json` records SHA-256 for all 21
  of them and must keep verifying.

## Objective

Create `audit/adapters/fullstack.sh` implementing the discovery, priority, verify, and scan
members of the contract for C#, so that Phase 1 (`sweep`) and Phase 2 (`deep-sweep`) work
end-to-end on this repo.

## Scope

Create:
- `audit/adapters/fullstack.sh`

Modify:
- `Makefile` — change `AUDIT_ADAPTER ?= dotnet` to `fullstack`; add an `audit-tools` target
  running `dotnet tool install -g roslynator.dotnet.cli` (the README documents `make
  audit-tools`, and no such target exists yet).
- `CLAUDE.md` — the "Audits" section says the adapter does not exist and names
  `audit/adapters/dotnet.sh`. Correct it to `fullstack.sh` and drop the "inert" warning.

### Members to implement

Exact names from the contract. Anything not listed is out of scope.

| Member | Behaviour |
| --- | --- |
| `audit_discover_sources` | Emit repo-relative paths, one per line, no leading `./`. All `*.cs` under the four project directories, **excluding `obj/` and `bin/`** (87 files), **plus `Directory.Packages.props`** (see below). 88 total. |
| `audit_priority_score <path>` | Print an integer 0–100 per the table below. |
| `audit_trivial_comment_regex` | Print `^[[:space:]]*//([[:space:]]|$)` |
| `audit_verify_build_cmd` | Print `dotnet build ImmichFrame.sln` (no trailing newline) |
| `audit_verify_test_cmd` | Print `dotnet test ImmichFrame.sln` (no trailing newline) |
| `AUDIT_SCAN_TOOLS` | `build vulnerable roslynator` |
| `scan_build`, `scan_vulnerable`, `scan_roslynator` | One driver each, see below. |
| `audit_scan_label/_state/_new/_dup/_skip <id>` | Per-tool metadata, exactly as go.sh does it. |

**Why `Directory.Packages.props` is tracked:** `dotnet list package --vulnerable` reports per
*package*, but a finding row needs a `file_id` from a tracked path, and the core silently
skips untracked ones (that is what the `audit_scan_skip` counter is for). This repo uses
Central Package Management — `Directory.Packages.props` holds every version and is where a
bump is actually made — so it is the correct owner for dependency CVEs.

**Why that comment regex:** C# XML doc comments (`/// <summary>`) are semantically meaningful
and this codebase uses them to record design rationale (`ProfileRegistry.cs`,
`ProfileServices.cs`). The regex requires whitespace-or-EOL after `//`, so `///` does not
match and a doc-comment change correctly forces a re-audit — the same reasoning go.sh applies
to `//go:build` directives.

### Priority scoring

Path-prefix scoring only. No git-churn weighting (keep it simple; the core already surfaces
staleness separately).

| Score | Paths | Rationale |
| --- | --- | --- |
| 95 | `ImmichFrame.WebApi/Helpers/Config/**` | Settings loading; holds auth secret, API keys, webhooks |
| 90 | `ImmichFrame.WebApi/Program.cs` | DI, middleware and auth wiring |
| 85 | `ImmichFrame.WebApi/Helpers/**` (not `Config/`) | Auth middleware, `SanitizeString`, profile registry |
| 80 | `ImmichFrame.WebApi/Controllers/**` | Request handling, client-supplied input |
| 70 | `ImmichFrame.Core/Logic/**` | Asset pools — the busiest and most intricate area |
| 65 | `ImmichFrame.Core/Api/**`, `ImmichFrame.Core/Services/**` | Outbound HTTP, credential handling |
| 55 | `ImmichFrame.WebApi/Models/**`, `ImmichFrame.Core/Models/**` | DTO boundary; client-leak surface |
| 50 | everything else in `ImmichFrame.Core/**`, `Directory.Packages.props` | |
| 25 | `*.Tests/**` | Audited, but last |

### Scan drivers

All three write findings with `source='static-tool'`, `status='open'`, and the correct `tool`
id, using the core's batch helpers (`write_batch_header`, `apply_batch`,
`write_batch_footer`) and `lookup_fid`/`sql_escape`/`rel_path`/`truncate_str` — read how
go.sh's drivers use them and follow the same shape. `INSERT OR IGNORE` plus
`idx_findings_dedup` is what makes re-runs idempotent; do not invent your own dedup.

Valid values: severity `critical|high|medium|low`; category
`security|bugs|tests|refactoring|enhancements`.

**`scan_build`** — `dotnet build ImmichFrame.sln`.
> **Gotcha that will silently break this driver:** an incremental build emits *no* warnings
> for projects it considers up to date, so a second scan would legitimately find zero
> diagnostics and you would wrongly conclude dedup is working. Force a full analysis pass
> (`--no-incremental`, or `-t:Rebuild`) and confirm the warning count is stable across two
> consecutive runs.

Parse `path(line,col): warning CODE: message [project]`. Map `warning` → `medium`, `error` →
`high`. Category: `NUnit*` → `tests`, `CA*`/`IDE*` → `refactoring`, otherwise `bugs`.
Paths in output are absolute — normalise with `rel_path`.

**`scan_vulnerable`** — `dotnet list package --vulnerable --include-transitive --format json`
(the .NET 8 SDK supports `--format json`; verify, and fall back to text parsing if it does
not). Parse with `jq`. Every finding gets `category='security'` and is attributed to
`Directory.Packages.props`; for the line number, grep that file for
`PackageVersion Include="<name>"` and use the match line, or 0 if absent (transitive packages
will not appear there). Map advisory severity Critical/High/Moderate/Low →
`critical/high/medium/low`. Include the advisory URL in the description.

**`scan_roslynator`** — `roslynator analyze ImmichFrame.sln`. Not currently installed;
`~/.dotnet/tools` is on PATH, so `make audit-tools` makes it available. Output is XML with
one `<Diagnostic Id="RCSxxxx" Severity="...">` block per finding carrying `<FilePath>`,
`<Location Line=... />` and `<Message>`. **Parse it with `awk`/`sed`** — accumulate fields
across the block, flush on `</Diagnostic>`. Map Roslynator severity Error/Warning/Info →
`high/medium/low`; category `refactoring`.

## Non-goals

- **No arch members.** `audit_arch_*` is task 003. Omit them entirely — the core substitutes
  placeholders for missing arch sections, which is the intended behaviour.
- **No TS/Svelte anything.** Task 002.
- No `.editorconfig`, no `dotnet format`, no style diagnostics — style in this repo is
  genuinely mixed and format findings would be pure noise.
- Do not modify `*.csproj`, `Directory.Build.props`, or the contents of
  `Directory.Packages.props`.
- Do not fix, triage, or comment on anything the scan reports. Getting the scan to run is the
  deliverable; its findings are a later sweep's job.
- Do not add a `Microsoft.CodeAnalysis.Metrics` package reference (that was considered and
  rejected — the audit tool must not alter the build).

## Constraints

- **POSIX `sh` only, no bashisms.** The core is `#!/bin/sh` and *sources* the adapter, so
  bashisms will break it. No arrays, no `[[`, no `local`, no `${var,,}`, no process
  substitution. Match go.sh.
- **Dependencies are `sqlite3`, `jq`, `git`, `dotnet` only.** `xmllint` is **not installed** —
  do not use it. Do not introduce a `python3` dependency for XML parsing.
- Every driver must degrade gracefully: a missing analyzer reports state `missing` and the
  scan continues. A scan must never fail because a tool is absent.
- Adapter functions set state by plain assignment (they are sourced, not sub-shelled) — but a
  driver invoked in a subshell cannot export counters back, so follow go.sh's exact pattern
  for the per-tool counter variables.
- The working tree has four in-flight files owned by someone else
  (`ImmichFrame.WebApi/Program.cs`, `ImmichFrame.WebApi/Helpers/ImmichServerVersionChecker.cs`,
  `ImmichFrame.WebApi.Tests/Controllers/{Asset,Config}ControllerTests.cs`) plus
  `ImmichFrame.WebApi/Helpers/Profiles/`. Do not edit, revert, judge, or commit them. Scope
  your edits to the files in this brief's Scope section. Do not run `git add -A`, `git stash`,
  or `git commit`.
- Do not modify any upstream kit file: `audit/*.sh`, `audit/adapters/go.sh`,
  `audit/schema.sql`, `audit/*.md`, `audit/manifest.json`.

## Acceptance criteria

Run these and report actual output:

1. `sh -n audit/adapters/fullstack.sh` passes.
2. `AUDIT_ADAPTER=fullstack audit/audit.sh init` then `... sync` reports **88 files**.
3. `AUDIT_ADAPTER=fullstack audit/audit.sh status` and `... packages` run clean and show
   ~21 packages, with `ImmichFrame.Core/Helpers` (12 files) the largest C# one.
4. `AUDIT_ADAPTER=fullstack audit/audit.sh scan` runs. `dotnet build` and `dotnet list
   package --vulnerable` must show state `ran`. If `roslynator` cannot be installed (no
   network), it must show `missing` and the scan must still succeed — say which case you hit.
5. **Run `scan` a second time with no tree changes: new findings must be 0 for every tool.**
6. `AUDIT_ADAPTER=fullstack audit/audit.sh sweep` emits a prompt containing
   `dotnet build ImmichFrame.sln` and `dotnet test ImmichFrame.sln`.
7. All 21 upstream files still match `audit/manifest.json`:
   `sha256sum -c` equivalent, or re-derive and compare.
8. `make audit` works (proving the Makefile default is wired correctly).

Report the scan's summary table verbatim and the total findings seeded.
