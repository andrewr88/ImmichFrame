# Audit language adapters

The audit core (`audit/audit.sh`, `audit/scan.sh`, `audit/arch.sh`) is
language-agnostic. Every language-specific decision — which files to track, how
to prioritise them, the build/test commands embedded in generated prompts, the
catalog of static analyzers, and the architecture-report analysis — lives in a
single adapter file at `audit/adapters/<lang>.sh`.

`audit/adapters/go.sh` is the reference adapter; read it alongside this document.
To support another language, copy it to `audit/adapters/<lang>.sh` and
reimplement the contract below. When adopting a non-Go adapter, also review
`audit/THREAT_MODEL.md` and translate/replace its Go tells and safe patterns
with your language's idioms — the classes carry over; the code patterns don't.

## Selection

The three core scripts each source exactly one adapter, resolved relative to the
script's own location (`$(dirname "$0")`), so the adapter is found regardless of
the caller's working directory:

```sh
AUDIT_ADAPTER="${AUDIT_ADAPTER:-go}"
ADAPTER_FILE="$(dirname "$0")/adapters/${AUDIT_ADAPTER}.sh"
. "$ADAPTER_FILE"
```

`AUDIT_ADAPTER` defaults to `go`. A missing adapter file is a hard error. Select
a different adapter per invocation, e.g. `AUDIT_ADAPTER=python audit/audit.sh scan`.

The adapter is sourced (not executed) into the core's shell, so the functions and
variables below run with the core's state in scope. Plain assignments in adapter
functions (e.g. `AUDIT_ARCH_MODULE=...`) are visible to the core without
exporting.

## Contract

An adapter MUST define the discovery, priority, verify, and scan members. The
architecture members are needed only for `arch-sweep`; the core substitutes a
placeholder for any arch section an adapter omits.

### Discovery & priority — used by `audit/audit.sh sync`

| Member | Kind | Responsibility |
| --- | --- | --- |
| `audit_discover_sources` | function | Emit repo-relative paths of source files to track, one per line. No leading `./` (the core strips it defensively, but the contract is repo-relative). The core sorts and iterates the list. |
| `audit_priority_score <path>` | function | Print the integer priority score (0–100) for one repo-relative path. Higher = swept sooner. |
| `audit_trivial_comment_regex` | function (optional) | Print an ERE matching comment-only lines, e.g. the Go reference returns `^[[:space:]]*//([[:space:]]|$)`. The regex must NOT match semantically meaningful comment-shaped lines — the Go one requires whitespace/EOL after `//` precisely so toolchain/linter directives (`//go:build`, `//go:generate`, `//go:embed`, `//nolint`, `//export`, ...) classify as non-trivial and force a re-audit. When a file's coverage goes stale, `sync` runs `git diff --quiet -w --ignore-blank-lines -I"<regex>" <old> <new> -- <path>`; exit 0 means the change is trivial (comments/whitespace only) and the coverage is re-stamped to the new commit instead of expired. Any `git diff` failure counts as non-trivial (expire — never re-stamp on doubt). If the adapter omits this hook, the core skips classification entirely and every stale coverage row is expired, exactly as before the hook existed. Worst-case sync cost is one `git diff` per changed covered file after a large rebase — a one-time cost per HEAD move, since each row is then re-stamped or expired rather than re-checked. |

### Verify commands — embedded in generated prompts

| Member | Kind | Responsibility |
| --- | --- | --- |
| `audit_verify_build_cmd` | function | Print the build command string (no trailing newline), e.g. `go build ./...`. |
| `audit_verify_test_cmd` | function | Print the test command string (no trailing newline), e.g. `go test ./...`. |

### Scan tool catalog — used by `audit/scan.sh`

The core owns the generic harness (file-id cache, the SQL batch helpers, the
"all analyzers missing" check, the total-new tally, and the summary table). The
adapter owns the tools:

| Member | Kind | Responsibility |
| --- | --- | --- |
| `AUDIT_SCAN_TOOLS` | variable | Space-separated, ordered list of tool ids. The core iterates it for running drivers, the all-missing check, the new tally, and the per-tool summary order. |
| `scan_<id>` | function | One driver per id. Runs the analyzer, parses its output, and writes finding INSERTs via the core's batch helpers. Sets the per-tool counter/state variables. |
| `audit_scan_label <id>` | function | Print the display name for the summary table. |
| `audit_scan_state <id>` | function | Print `ran` or `missing` for one id. |
| `audit_scan_new <id>` | function | Print the new-findings count for one id. |
| `audit_scan_dup <id>` | function | Print the duplicate (already-seen) count for one id. |
| `audit_scan_skip <id>` | function | Print the skipped (untracked-path) count for one id. |

A driver runs with these **core-provided variables** in scope: `DB`,
`REPO_ROOT`, `TMP_DIR`, `FILES_CACHE` (a `path<TAB>id` map of tracked files).

A driver may call these **core-provided helper functions**:

| Helper | Purpose |
| --- | --- |
| `lookup_fid <rel-path>` | File id for a tracked repo-relative path; empty if untracked. |
| `sql_escape <str>` | Escape a string for inlining into a SQL literal. |
| `rel_path <abs-or-rel>` | Normalise an analyzer-emitted path to repo-relative. |
| `truncate_str <max> <str>` | Truncate a string to `<max>` characters. |
| `count_lines <file>` | Line count of a temp file (0 if absent). |
| `apply_batch <id> <sqlfile> <attempted>` | Apply a batch; prints `"<new> <dup>"`. |
| `write_batch_header <sqlfile>` / `write_batch_footer <sqlfile>` | Wrap the batch in a transaction. |

The findings table columns a driver writes: `file_id, line, tool, source,
severity, category, title, description, status`. Use `source='static-tool'`,
`status='open'`. `INSERT OR IGNORE` plus the `idx_findings_dedup` partial unique
index in `audit/schema.sql` make repeated scans idempotent.

### Architecture report — used by `audit/arch.sh arch-sweep`

The core owns generic directory target resolution (`TARGET_SCOPE`, `TARGET_DIR`,
`TARGET_FILE_LIKE`), Section C (largest files, a pure files-table query), report
assembly, and the Claude-prompt skeleton. The adapter owns the
language/project-specific analysis:

| Member | Kind | Responsibility |
| --- | --- | --- |
| `audit_arch_preflight` | function (required for arch) | Verify the toolchain/preconditions and make module/graph state available. On success set any state (e.g. `AUDIT_ARCH_MODULE`) via plain assignment and stash files under `TMP_DIR`; on failure print to stderr and return non-zero (core exits 1). |
| `audit_arch_resolve_nondir <arg>` | function (optional) | Given a target arg that is NOT an existing directory, print a repo-relative directory for it, or nothing. Go maps an import path to its subtree by stripping the module prefix. Called only after the core's own `[ -d ]` check fails. |
| `audit_arch_has_sources <dir>` | function (optional) | Return zero if `<dir>` (already validated to exist) holds language source files, non-zero if it is devoid of them. If omitted, the core skips the check entirely. |
| `audit_arch_build_graph` | function (required for arch) | Build the package + internal-import graph scoped to `TARGET_SCOPE`/`TARGET_DIR`, writing the temp artefacts the section functions read. May return non-zero on a fatal toolchain error (core exits 1). |
| `audit_arch_section_a` / `_b` / `_d` / `_e` | functions (optional) | Emit the body of Sections A (import graph fan-in/out), B (complexity hotspots), D (public API surface), E (layer violations), including their `=== Section X: ... ===` headers and surrounding blank lines. Section C is core's. An omitted section gets a core placeholder. |

Arch state the core defines before calling the arch functions: `DB`,
`REPO_ROOT`, `TMP_DIR`, `TARGET_SCOPE`, `TARGET_DIR`, `TARGET_FILE_LIKE`.

## Worked reference

`audit/adapters/go.sh` is the complete, working adapter. It discovers `*.go`
files (excluding `vendor`, `node_modules`, `gen`), scores auth/crypto/handler
paths highest, runs `go build`/`go test` in prompts, drives
`gosec staticcheck govulncheck errcheck ineffassign`, and builds the arch report
from `go list` package data. Read it before authoring a new adapter — the
contract above is exactly what it implements.

## Sketches for other languages

These are illustrative seam-to-tool mappings, not full implementations. Copy
`go.sh` and fill each seam in.

### Python

| Seam | Suggested tool / approach |
| --- | --- |
| `audit_discover_sources` | `find . -name '*.py'` excluding `.venv`, `__pycache__`, `build`. |
| `audit_priority_score` | Score by path (e.g. `*/auth/*`, `*/security/*` high; `*_test.py`/`tests/` low). |
| `audit_verify_build_cmd` / `_test_cmd` | `python -m compileall .` (or `mypy .`) / `pytest -q`. |
| `AUDIT_SCAN_TOOLS` + `scan_<id>` | `ruff` (lint), `bandit` (security, `-f json`), `mypy` (types). |
| arch members | `audit_arch_build_graph` from `import`/`from` parsing or `grimp`/`pydeps`; sections from the same graph. |

### JavaScript / TypeScript

| Seam | Suggested tool / approach |
| --- | --- |
| `audit_discover_sources` | `find . -name '*.ts' -o -name '*.js'` excluding `node_modules`, `dist`, `build`. |
| `audit_priority_score` | Score by path (auth/middleware high; `*.test.*`/`*.spec.*` low). |
| `audit_verify_build_cmd` / `_test_cmd` | `tsc --noEmit` (or `npm run build`) / `npm test`. |
| `AUDIT_SCAN_TOOLS` + `scan_<id>` | `eslint` (`-f json`), `semgrep` (`--json`), `tsc` (types). |
| arch members | `audit_arch_build_graph` from `madge`/`dependency-cruiser` JSON; sections from that graph. |
