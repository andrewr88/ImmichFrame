# ImmichFrame – Claude Instructions

## Release Writeup Rule

When asked to create a release writeup or release notes, follow this process and format:

### Process

1. Run `git log --oneline <prev-tag>..HEAD` to get all commits since the last release.
2. Run `git diff <prev-tag>..HEAD --stat` to understand the scope of changes.
3. Fetch the GitHub release page if a URL is provided to cross-reference the auto-generated changelog.
4. Combine the raw git history with the GitHub changelog to produce a human-friendly writeup.

### Output Format

Use the template at `templates/release-template.md`. Key rules:

- **Title**: `# 📦 ImmichFrame Release vX.X.X.X – <Date>`
- **Intro**: One sentence summarising the release highlights (no heading).
- **Sections**: Follow the category order from `.github/release.yml` — Breaking Changes, New Features, Fixes, Documentation, Maintenance, Other Changes.
- **Each entry**:
  - H4 heading with emoji + feature name
  - Bold `**PR [#NNN](url) by @author**` attribution line
  - 2–4 sentences describing *what* changed and *why it matters* to the user
  - Include a code block if a config snippet helps illustrate usage
  - Separate entries with `---`
- **New Contributors**: Call out first-time contributors with 🎉
- **Footer**: Always end with the full changelog comparison URL.

### Tone

- Write for end users, not developers. Avoid internal refactor jargon unless it has a user-visible effect.
- Keep descriptions concise — 2–4 sentences per entry is enough.
- Use "you" / "your" to address users directly.

## Audits

Codebase sweep system in `audit/` (see `audit/README.md`). Three phases: file
sweeps (Phase 1), package deep sweeps (Phase 2), architecture sweeps (Phase 3).
Static analyzers seed findings into `audit/audit.db` for triage on the next sweep.

**Drive it through `make`.** The targets export `AUDIT_ADAPTER`; a bare
`audit/audit.sh ...` does not, and `audit.sh` defaults the variable to `go`
(`audit.sh:11`). Since `audit/adapters/go.sh` exists, a bare invocation silently
resolves to the Go adapter and runs against the C# database — bare `sweep`
hands the agent `go build ./...` / `go test ./...`, and bare `scan` finds no Go
analyzers and aborts.

```sh
make audit-tools    # install roslynator + its RCS analyzer assemblies (one-time)
make audit-init     # create the database (one-time)
make audit-scan     # run analyzers and seed findings
make audit          # coverage, findings, package progress
make audit-sweep    # generate a prompt for the next file batch
```

Other targets: `audit-next`, `audit-findings`. (`audit-arch` exists but refuses
— see below.)

For a subcommand with no target of its own, set the adapter explicitly:

```sh
AUDIT_ADAPTER=fullstack audit/audit.sh packages
AUDIT_ADAPTER=fullstack audit/audit.sh deep-sweep ImmichFrame.Core/Logic
AUDIT_ADAPTER=fullstack audit/audit.sh fix 3
```

The core is language-agnostic; source discovery, priority, build/test commands,
the analyzer catalog, and the arch report live in `audit/adapters/<lang>.sh`,
selected by `AUDIT_ADAPTER`. This repo's is `audit/adapters/fullstack.sh` (the
Makefile default) — one composite adapter for both halves of the stack, since
the core sources exactly one file. It currently covers git-tracked C# files
under the four project directories plus `Directory.Packages.props`; the
TypeScript/Svelte seams are still to be written.

**Phase 3 (`arch-sweep`) is not implemented for this repo.** The adapter defines
no `audit_arch_*` members, and the core's placeholder substitution only covers
the optional Section A/B/D/E bodies — `audit/arch.sh` calls
`audit_arch_preflight` unguarded. The adapter therefore ships a stub preflight
that refuses with an explanatory message, so every entry point (`make
audit-arch`, a bare `audit/audit.sh arch-sweep`, and the automatic hand-off
after Phases 1 and 2 complete) exits non-zero cleanly rather than dying on an
undefined function. Phases 1 and 2 are unaffected. `audit/adapters/go.sh` is the
upstream reference and `audit/ADAPTERS.md` the contract. `audit/THREAT_MODEL.md` is written around Go
tells; its issue classes carry over to C# but its code patterns do not.

Requires `sqlite3`, `jq`, `curl` and `unzip` on PATH, plus the .NET 8 SDK.
`make audit-tools` needs `curl`/`unzip` to fetch `roslynator` and the
`Roslynator.Analyzers` assemblies — the CLI package ships none, so without them
Roslynator's own `RCS*` rules never load and the scan reports only the
analyzers the projects already reference.

## Agent skills

Seven skills under `.claude/skills/`, each an invokable slash command: six
orchestration skills — `architect`, `developer`, `code-reviewer`,
`code-reviewerer`, `repo-scout`, `diff-summarizer` — plus `sweep`, the entry
point to the audit system above. `repo-scout` reads and writes `ARCHITECTURE.md`
at the repo root.

Provenance: replicated from https://onedev.sharpspoon.io/agent-kit; the file
list and SHA-256s it was verified against are in `audit/manifest.json`.
