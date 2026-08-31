# Phase 1: File sweeps

Each file runs through three roles: **Find** (you, the orchestrator in this main loop) → **Verify** (a fresh, analysis-only `Agent` subagent) → **Fix** (a `developer` `Agent` subagent). You own ALL git and ALL DB writes; the subagents only read code and return text. Don't stop after one file — do 3–5 per session.

**Per file:**

### Find (orchestrator / main loop)

1. Get the next target: `audit/audit.sh next` (use the `file_id` and `path` from the first row). The `audit/audit.sh sweep` prompt also includes a `state` column:
   - `state=first-pass`: the file has never been audited — review the whole file with the full protocol below.
   - `state=re-audit`: the file was audited before but changed. **Audit ONLY the change**: the diff hunks since `last_run` (`git log --since='<last_run>' -p -- <file>` — the `last_run` column is the timestamp) plus their blast radius — callers/callees of changed functions, and the test file. Walk only the `audit/THREAT_MODEL.md` classes the diff plausibly touches; do NOT re-review unchanged code paths. The audit is scoped, not skipped — all 5 categories still get coverage UPSERTs in *Persist*. The sweep prompt may include the prior run's notes (`refuted: ...`); if it doesn't, query them directly (`sqlite3 audit/audit.db "SELECT notes FROM runs WHERE file_id=<id> AND finished_at IS NOT NULL ORDER BY finished_at DESC LIMIT 1;"`). Do not re-propose a previously-refuted candidate unless the code it concerns changed.
2. Start a run:
   ```
   sqlite3 audit/audit.db "PRAGMA foreign_keys=ON; INSERT INTO runs (file_id, categories, trigger_type) VALUES (<file_id>, 'security,bugs,tests,refactoring,enhancements', 'manual') RETURNING id;"
   ```
3. Read the file and related files (same package, test file, models it uses).
4. Audit all 5 categories: security, bugs, tests, refactoring, enhancements. For **security**, walk `audit/THREAT_MODEL.md` and check each applicable class against this file. (On a re-audit, scope all of this to the diff + blast radius per step 1.)
5. Produce an in-session **candidate list** of findings. Do NOT fix anything yet. For each candidate record: title, category, severity, `file:line`, and a one-line description. (Security candidates: note whether the claim is exploitability or non-exploit hardening.)
6. **Cost skip:** if the candidate list is empty, skip Verify and Fix entirely — jump straight to *Persist* (coverage + complete run). Clean files stay cheap.

### Verify

7. Run the candidate list through the shared **Verify** role in `SKILL.md`: ONE batched, analysis-only `Agent` subagent, judging each candidate independently, default-to-refuted.
8. **Cost skip:** if Verify confirms zero candidates, skip Fix — go to *Persist* (record refutations, coverage, complete run).

### Fix

9. Hand the confirmed findings to the shared **Fix** role in `SKILL.md`: ONE `developer` subagent — the only role that mutates the tree.

### Persist (orchestrator)

10. **Commit the fixes FIRST** — stage explicit paths only (the audited file, its tests, anything else the Fix subagent reported touching); skip the commit if Fix was skipped or made no changes. Committing before the coverage UPSERT is what keeps the `commit_hash` stamp valid (step 12). If a confirmed finding represents a new *recurring, generalizable* class (not a one-off), append it to `audit/THREAT_MODEL.md` — following the file's existing description/tell/safe format — and include that in the same commit.
11. Record findings in the `findings` table, keyed off the verdicts:
    - **confirmed + fixed** → status `fixed`, add an `auto_fixes` row — and if it's a new recurring, generalizable class, append it to `audit/THREAT_MODEL.md` per step 10.
    - **confirmed but needs a design decision** → status `open` (these should be rare).
    - **confirmed but the Fix subagent could not implement it (failed/partial fix)** → status `open`, `resolution_note` briefly noting it's a verified issue the automated fix couldn't complete (so it surfaces for human attention rather than vanishing).
    - **refuted, `source='static-tool'`** → status `wontfix`, `resolution_note='refuted: <reason>'`. This is essential: the static-tool dedup index keys on `open|wontfix`, so persisting the refutation stops the analyzer false-positive from being re-seeded on the next scan.
    - **refuted, `source='llm'`** → do NOT persist (drop it), to avoid bloating the findings table with per-sweep noise.
12. UPSERT `audit_coverage` for each of the 5 categories. **Set `commit_hash` to `git log -1 --format='%h' -- <file>` run AFTER the commit** (correct whether or not a fix landed on the file; if no commit was made, this still returns the file's existing last commit — record it all the same) **and `run_id` to the run just opened** — otherwise the next `sync` (which runs at the start of every `sweep`/`scan`) will delete the row as stale, because `audit_coverage.commit_hash != files.last_commit` is its expiry predicate. Stamping the file's *pre*-commit hash after a fix touched it creates exactly that mismatch and triggers a pointless full re-audit.
13. Complete the run: UPDATE runs with `finished_at`, `findings_count`, `autofixes_count`, and `notes` set to a compact list of refuted candidate titles (`refuted: <title>; <title>`; empty if none) so the next re-audit can skip re-litigating them.
14. Move to the next file.
