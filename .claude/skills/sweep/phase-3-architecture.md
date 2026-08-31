# Phase 3: Architecture sweeps

Do this when packages have been deep-swept and the user wants to look at coupling, complexity, or the public API surface — not for routine fixing. Run `audit/audit.sh arch-sweep [pkg]` (or `make audit-arch [PKG=internal/web]`). It generates a report with fan-in/fan-out, cyclomatic hotspots, largest files, exported-symbol counts, and layer violations, then emits a prompt that embeds the same report.

The report already partitions the work by section, so for a large report the orchestrator **MAY** fan out: spawn up to **3** analysis-only finder subagents aligned to those sections — e.g. layer-violations + coupling / complexity-hotspots + largest-files / public-API-surface — in a single batch (make all the `Agent` calls together so they run concurrently). Each finder returns its candidate list as TEXT and must NOT edit any file or write the DB. Then **merge → dedupe (barrier) → the shared Verify → ONE developer Fix → persist** — wait for ALL finders to return before merging, and dedupe on same `file:line` + same underlying issue (keep the highest-severity phrasing). Fan-out is optional and scaled to report size: for a small report, just work the numbers directly in this loop (no finders). The same cost-skips apply — empty merged list skips Verify+Fix; zero confirmed skips Fix.

Work off the numbers in the report: fix layer violations directly if safe, narrow public APIs, inline trivial wrappers, extract small helpers to remove duplication. Record findings in the `findings` table against each finding's relevant `file_id` (category usually `refactoring`), and `auto_fixes`, keyed off the verdicts:

- **confirmed + fixed** → `fixed` (+ `auto_fixes` row) — and if it's a new recurring, generalizable class, append it to `audit/THREAT_MODEL.md` (following the file's existing description/tell/safe format) and include the append in the fixes commit.
- **confirmed but needs a design decision**, OR **confirmed but the Fix subagent couldn't complete it** → `open` (with a `resolution_note` for the failed-fix case).
- **refuted, `source='static-tool'`** → `wontfix`, `resolution_note='refuted: <reason>'` (stops re-seeding on the next scan).
- **refuted, `source='llm'`** → drop (do not persist).

Leave design-level findings `open` only if they need user input.

After the review, in order: **commit the fixes**, then **re-stamp** `audit_coverage.commit_hash` for every file this sweep modified to its post-commit `git log -1 --format='%h' -- <file>` value, then **record the sweep**:

```
sqlite3 audit/audit.db "PRAGMA foreign_keys=ON; INSERT INTO sweeps (scope, sweep_type, finished_at, findings_count, autofixes_count, notes) VALUES ('<scope>', 'architecture', strftime('%Y-%m-%dT%H:%M:%SZ','now'), <count>, <fixes>, '<summary>');"
```
