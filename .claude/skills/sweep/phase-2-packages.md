# Phase 2: Package deep sweeps

After a package's files are individually covered, do a cross-cutting pass reading ALL files in the package together — the cross-file issues invisible in single-file reviews. Unlike Phase 1 (one small file, where fanning out would pay the read cost N times), a package is a LARGE surface: the read cost amortizes and depth-per-lens pays off, so here the orchestrator **fans out** parallel finders, merges + dedupes their candidates, then runs the SAME independent Verify→Fix gate defined in the core `SKILL.md`.

Run `audit/audit.sh packages` to see which packages are ready, then `audit/audit.sh deep-sweep [pkg]` for the chosen package's info and prompt context.

### Fan-out find (orchestrator spawns 2–4 analysis-only finder subagents, in parallel)

Spawn **2–4** `Agent` finder subagents in a single batch (make all the `Agent` calls together so they run concurrently). Give each the WHOLE package but exactly ONE grouped lens — grouped so overlapping categories aren't split across finders, which would duplicate findings. Each finder is **analysis-only**: it returns a candidate list as TEXT and must NOT edit any file or write the DB (this is what keeps parallel finders safe — no parallel tree-mutation).

- **Lens A — Security:** walk `audit/THREAT_MODEL.md` across the package; check each applicable class. Watch for sibling-inconsistency — one call site hardened, a neighbour not.
- **Lens B — Bugs & integration:** correctness bugs + caller/callee contract mismatches, wrong return-value assumptions, cross-file integration errors.
- **Lens C — Dead code, duplication & refactor:** unused funcs/constants, helpers called from only one place, duplicated logic to extract, and consistency (error handling / logging / validation done the same way everywhere).
- **Lens D — Test gaps:** missing feature-level / end-to-end coverage of user-facing flows.

Each finder returns, per candidate: title, category, severity, `file:line`, one-line description. (Security candidates: note whether the claim is exploitability or non-exploit hardening.)

**Sizing:** scale the finder count to the package. For a small package, use fewer lenses — merge B+C into one finder, or skip D if there are no user-facing flows. **Never exceed 4 finders.**

### Merge + dedupe (orchestrator — barrier)

Wait for ALL finders to return first — this is a barrier. Collect every finder's candidates into one list and drop duplicates: same `file:line` + same underlying issue is one candidate (keep the highest-severity phrasing). The result is the deduped candidate list for the package.

**Cost skip:** if the merged candidate list is empty, skip Verify and Fix entirely — jump straight to *Persist* (record coverage / the sweep).

### Verify (reuse the shared Verify role)

Run the deduped candidate list through the shared **Verify** role in `SKILL.md`: ONE batched, analysis-only `Agent` subagent that judges each candidate independently, default-to-refuted, with bugs and exploitability claims repro-gated (concrete reproducing test OR precise code-cited execution path). See `SKILL.md` for the full rules — don't restate them. It returns a per-candidate `confirmed`/`refuted` verdict and does not touch the tree or DB.

**Cost skip:** if Verify confirms zero candidates, skip Fix — go to *Persist* (record refutations, coverage, the sweep).

### Fix (reuse the shared Fix role — ONE developer subagent)

Hand the confirmed findings to ONE `Agent` subagent with `subagent_type: developer` — the only role that mutates the tree, under the same constraints as the shared **Fix** role in `SKILL.md`: test-first for confirmed bugs (red→green), apply the other confirmed fixes, run the project's build and test commands (embedded in the `audit/audit.sh` prompt), report back as text. Scope edits strictly to the package's files plus their tests — **no `git stash`, no `git add -A`, do not revert others' work, do not commit.** It does not write the DB.

### Persist (orchestrator)

Record findings in the `findings` table against each finding's relevant `file_id`, keyed off the verdicts:

- **confirmed + fixed** → `fixed` (+ `auto_fixes` row) — and if it's a new recurring, generalizable class, append it to `audit/THREAT_MODEL.md` (following the file's existing description/tell/safe format) and include the append in the fixes commit.
- **confirmed but needs a design decision**, OR **confirmed but the Fix subagent couldn't complete it** → `open` (with a `resolution_note` for the failed-fix case).
- **refuted, `source='static-tool'`** → `wontfix`, `resolution_note='refuted: <reason>'` (stops re-seeding on the next scan).
- **refuted, `source='llm'`** → drop (do not persist).

Then, in order:

1. **Commit the sweep's fixes** — stage explicit paths only.
2. **Re-stamp `audit_coverage.commit_hash` for every file the sweep itself modified** to that file's post-commit `git log -1 --format='%h' -- <file>` value — otherwise the deep sweep's own fixes expire those files' coverage and trigger pointless Phase 1 re-audits.
3. **Record the deep sweep:**

```
sqlite3 audit/audit.db "PRAGMA foreign_keys=ON; INSERT INTO sweeps (scope, sweep_type, finished_at, findings_count, autofixes_count, notes) VALUES ('<package>', 'package', strftime('%Y-%m-%dT%H:%M:%SZ','now'), <count>, <fixes>, '<summary>');"
```
