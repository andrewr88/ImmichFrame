# Integration snippet

Paste the section below into your repo's `CLAUDE.md` (or equivalent
agent-instructions file) so agents know the audit system and skills exist and how
to drive them. Adjust paths if you placed the kit somewhere other than the repo
root.

---

## Audits

Codebase sweep system in `audit/`. Three phases: file sweeps (Phase 1),
package deep sweeps (Phase 2), architecture sweeps (Phase 3). Static analyzers
seed findings into `audit/audit.db` for triage on the next sweep.

```sh
audit/audit.sh init     # create the database (one-time)
audit/audit.sh scan     # run analyzers and seed findings
audit/audit.sh status   # coverage, findings, package progress
audit/audit.sh sweep    # generate a prompt for the next file batch
```

The core is language-agnostic; the language-specific logic (source discovery,
priority, build/test commands, analyzer catalog, arch report) lives in
`audit/adapters/<lang>.sh`, selected by the `AUDIT_ADAPTER` env var (default
`go`). To audit a non-Go repo, author an adapter following `audit/ADAPTERS.md`
and run with `AUDIT_ADAPTER=<lang>`. Install the adapter's analyzers before
`scan`. Add `audit/audit.db`, `audit/audit.db-wal`, and `audit/audit.db-shm` to
`.gitignore`.

## Agent skills

Seven skills live under `.claude/skills/` and work as Claude Code skills with no
further setup: six orchestration skills — `architect`, `developer`,
`code-reviewer`, `code-reviewerer`, `repo-scout`, `diff-summarizer` — plus the
`sweep` skill, which is the entry point to the audit system above. Each is an
invokable slash command (e.g. `/developer`, `/sweep`); `/sweep` drives the audit
phases (`audit/audit.sh sweep` etc.).

---
