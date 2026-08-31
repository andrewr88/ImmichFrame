---
name: sweep
description: Sweep the codebase — run the next audit phase (file, package, or architecture)
user-invocable: true
allowed-tools: Bash Read Edit Write Grep Glob Agent
---

You are running the codebase audit. The audit database lives at `audit/audit.db`. The system has three phases — pick one based on the user's intent in `$ARGUMENTS`, then execute it end-to-end.

$ARGUMENTS

## Dispatch

- No args, or wording like "sweep", "audit", "fix things" → **Phase 1** (file sweeps). Run `audit/audit.sh sweep [N]` to get the next file queue and prompt context; protocol in `phase-1-files.md`.
- "deep sweep", "sweep package [pkg]" → **Phase 2** (package deep sweep). Run `audit/audit.sh deep-sweep [pkg]` to get the package info and prompt context; protocol in `phase-2-packages.md`.
- "arch sweep", "architecture review" → **Phase 3** (architecture sweep). Run `audit/audit.sh arch-sweep [pkg]` to generate the report; protocol in `phase-3-architecture.md`.

Each `audit/audit.sh <cmd>` prints a phase-specific prompt with the current queue/package/report embedded. Read the output, then follow the protocol in that phase's file (a sibling of this file in `.claude/skills/sweep/`).

Goal: make the code better each pass. **Find → verify → fix, then report rarely.** Phase 1 stages every candidate through an independent verifier before any code is touched, so raising discovery doesn't raise false positives. Only leave confirmed findings `open` if they need a design decision.

**Fan-out is for Phase 2/3 ONLY** — Phase 1 sweeps one small file, where parallel finders would pay the read cost N times for no depth gain, so its Find stays a single-orchestrator loop. Phases 2 and 3 work a LARGE surface where the read cost amortizes, so they fan out grouped-lens finders (capped at 4 / 3 respectively, scaled down for small packages/reports) before the shared Verify→Fix gate. The cost-skips carry across all phases: an empty (merged) candidate list skips Verify + Fix; zero confirmed candidates skips Fix.

## Shared roles: Verify and Fix

The phases gate candidates through the same two roles, defined once here and referenced by name from the phase files.

### Verify (fresh `Agent` subagent — analysis only, default-to-refuted)

Spawn ONE `Agent` subagent and batch ALL candidates into that single call (cost control), but instruct it to judge each candidate independently. Give it ONLY the candidate *claims* (title, category, severity, `file:line`, one-line description) plus the file path(s) — **never your finder reasoning.** It re-derives each candidate from the code itself. Prompt it to be adversarial: **default to `refuted` unless it can demonstrate the issue is real.**

- For `bugs` candidates, and `security` candidates claiming exploitability: confirmation REQUIRES a concrete reproducing test OR a precise, code-cited execution path that triggers the issue. If it can produce neither → `refuted`.
- For `refactoring` / `enhancements` / `tests` / non-exploit security hardening: confirm on a sound, code-cited justification (no repro needed).
- It returns, per candidate: `confirmed` | `refuted`, a one-line reason, and (for confirmed bugs) a sketch of the reproducing test. It does NOT touch the working tree or the DB.

### Fix (developer `Agent` subagent — the only role that mutates the tree)

Spawn ONE `Agent` subagent with `subagent_type: developer`, given ONLY the confirmed findings. Instruct it to: for each confirmed `bug`, add the reproducing test FIRST (red), then fix (green); apply the other confirmed fixes; run the project's build and test commands (the concrete commands arrive embedded in the `audit/audit.sh` prompt); report what was fixed. Scope edits strictly to the files under audit plus their tests / closely-related files — **no `git stash`, no `git add -A`, do not revert others' work, do not commit.** It reports text back to you; it does not write the DB.

## Automated scanning

`make audit-scan` (or `audit/audit.sh scan`) runs the language adapter's static analyzers and seeds findings with `source='static-tool'` and the relevant `tool` name. It calls `sync` first and dedups via a partial unique index, so it is idempotent. These findings appear in `v_next_audit_target` and `v_open_findings` like any other — the next `sweep` naturally triages them alongside LLM-found issues. You don't need to re-run the scan mid-sweep; rely on whatever is already seeded.

For CLI details, see `audit/README.md`.

## Testing philosophy

- **Add tests with high ROI**: boundaries that matter, logic that's easy to get wrong, edge cases that have bitten before.
- **Skip low-value tests**: trivial getters, simple delegation wrappers, obvious happy paths.
- **Push back on coverage for coverage's sake.** A test that validates a real invariant is worth ten boilerplate tests.
- **Table-driven tests** are preferred when there are multiple cases to cover.

## Fix verified, report rarely

The goal is to make the code better each pass, not build a backlog. In Phase 1 only confirmed (verified) findings reach the Fix step; only leave a confirmed finding `open` if it genuinely needs a design decision — "should we restructure this?" or "which approach do you prefer?"

## Status check

Run `audit/audit.sh status` at the start of a session to see where things stand.

## Database rules

- Always `PRAGMA foreign_keys=ON;` before writes.
- Severity: `critical`, `high`, `medium`, `low`.
- Categories: `security`, `bugs`, `tests`, `refactoring`, `enhancements`.
