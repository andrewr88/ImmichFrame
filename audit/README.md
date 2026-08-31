# Codebase Audit

Three-phase sweep system. Phase 1 audits individual files. Phase 2 does cross-cutting reviews of whole packages. Phase 3 reviews architecture with coupling and complexity data. All phases fix aggressively — only genuine design questions stay as open findings.

An automated scan pipeline runs alongside the phases: static analyzers seed findings into the DB for Claude to triage on the next sweep.

## Fresh setup

```bash
make audit-tools              # Install pinned analyzers (one-time)
audit/audit.sh init           # Create the database
make audit-scan               # Seed findings from static analyzers
audit/audit.sh sweep          # Generate a prompt for the next file batch
```

## Usage

**Sweep files** (phase 1):
> Sweep the codebase

**Deep sweep a package** (phase 2, after its files are covered):
> Deep sweep internal/web

**Architecture sweep** (phase 3):
> Arch sweep internal/web

**Sustained sweeping**:
> /loop Sweep the codebase

### Check progress

```bash
audit/audit.sh status         # Dashboard: coverage, findings, package progress
audit/audit.sh packages       # Package-level coverage + sweep status
audit/audit.sh findings       # Open findings needing your input
audit/audit.sh history        # Recent runs and sweeps
```

### Generate prompts

```bash
audit/audit.sh sweep          # Prompt for next batch of file sweeps
audit/audit.sh deep-sweep     # Prompt for next package deep sweep
audit/audit.sh deep-sweep internal/db  # Prompt for a specific package
audit/audit.sh arch-sweep     # Architecture report + prompt (whole repo)
audit/audit.sh arch-sweep internal/web # Scoped to a package
audit/audit.sh fix 3          # Prompt for a specific finding
audit/audit.sh fix-all        # Prompt for all findings in the worst file
```

`sweep` and `deep-sweep` (with no package arg) auto-progress when their phase is complete: `sweep` advances to `deep-sweep` once every file is covered, and `deep-sweep` advances to `arch-sweep` once every eligible package has been deep-swept. Swept packages are not done forever: a package re-qualifies for another deep sweep once ≥30% of its files (minimum 3, capped at the package size) have been re-audited since its last sweep. If `deep-sweep` is run before any package has full file coverage, it falls back to `sweep` instead. You can keep calling `sweep` until the audit reaches `arch-sweep`.

Arch is non-terminal and the cycle is **convergent**: when both phases are complete, `sweep` runs arch once, records the HEAD it ran at, and arms a re-scan; the next `sweep` tick re-scans and loops back to Phase 1 from fresh findings + expired coverage. If nothing has changed since arch (and no findings are open), `sweep` idles instead of re-running arch — so `/loop 1m /sweep` self-repeats on new commits and rests on a quiet repo. A direct `arch-sweep` invocation stays pure (report + prompt only, no re-scan).

### Manage findings

```bash
audit/audit.sh finding 3      # Details for finding #3
audit/audit.sh triage         # Prompt to triage every open finding interactively
audit/audit.sh resolve 3      # Mark as fixed
audit/audit.sh wontfix 3      # Mark as won't fix
```

`triage` emits a pasteable prompt for an interactive session that walks through every open finding in severity-then-age order: re-read the cited code (it may have been fixed since), get a one-line recommendation (`fix now` / `wontfix` / `keep open`), decide, and have the decision recorded before moving on. It is a human-in-the-loop session, not part of the autonomous sweep loop.

### Maintenance

```bash
audit/audit.sh sync           # Refresh file list; expire stale coverage
audit/audit.sh scan           # Run static analyzers and seed findings
audit/audit.sh stale          # Files changed since last audit
audit/audit.sh next           # Next files in the queue
audit/audit.sh init           # Rebuild database from scratch
```

Equivalent `make` targets: `make audit`, `audit-findings`, `audit-next`, `audit-sweep`, `audit-scan`, `audit-arch [PKG=internal/web]`, `audit-tools`.

## Phase 1: File sweeps

Each file is audited across 5 categories (security, bugs, tests, refactoring, enhancements). Issues are fixed directly. Tests are added where they have high ROI — meaningful boundaries, risky logic, edge cases. Low-value tests are skipped.

## Phase 2: Package deep sweeps

After a package's files are individually covered, a deep sweep reads ALL files together looking for: consistency issues, dead code, duplicated patterns, integration bugs, and feature-level test gaps.

A package qualifies when all its files are covered and it has never been swept — or when it has churned since its last sweep: ≥30% of its files (minimum 3, never more than the package size) re-audited after `last_swept`. Re-audits only happen when a file changed, so completed post-sweep runs are the change signal; `v_next_package_sweep` exposes the count as `changed_since_sweep`.

Run `audit/audit.sh packages` to see which are ready.

## Phase 3: Architecture sweeps

After packages have been deep-swept, `make audit-arch` (or `audit/audit.sh arch-sweep [pkg]`) generates a report covering:

- **Import graph**: fan-in / fan-out per package.
- **Complexity hotspots**: functions over cyclomatic 10.
- **Largest files**.
- **Public API surface**: exported symbol and function counts.
- **Layer violations**: models-leak, db-to-web, crypto-leak, test-in-prod.

The command prints the report followed by a Claude prompt that embeds the same report. Completed sweeps are recorded with `sweeps.sweep_type='architecture'`.

## Automated scanning

`make audit-scan` (or `audit/audit.sh scan`) runs gosec, staticcheck, govulncheck, errcheck, and ineffassign. Findings are inserted with `source='static-tool'` and the correct `tool` name. The scan syncs files first and dedups via a partial unique index — repeated runs on an unchanged tree insert zero rows. The next `sweep` picks seeded findings up naturally via the audit target view and finding triage.

## Stale re-audits

`audit/audit.sh sync` compares each file's current `last_commit` against the commit recorded in `audit_coverage`. Mismatches are expired, which surfaces the file in `v_next_audit_target` again. `audit/audit.sh sweep` tags each target with a `state` column (`first-pass` or `re-audit`) and a `last_run` timestamp so Claude can focus on what changed. `make audit-scan` calls `sync` first, so scanning a changed repo automatically triggers re-audit queueing.

## What stays open

Almost nothing. The sweep fixes error handling, nil checks, cleanup, small refactors, logging, and adds tests. Only genuine design questions that need your input remain as open findings.

## Dependencies

- `sqlite3` and `jq` on PATH (system packages).
- Go analyzers installed by `make audit-tools`: `gosec`, `staticcheck`, `govulncheck`, `errcheck`, `ineffassign`, `gocyclo`.

## Database

- Schema: `audit/schema.sql` (in git)
- Data: `audit/audit.db` (gitignored)
- Rebuild: `audit/audit.sh init && audit/audit.sh sync`
- Upgrade in place: `audit/migrate.sh`
