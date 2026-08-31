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

Other targets: `audit-next`, `audit-findings`, `audit-arch` (Phase 3 — see below).

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
the core sources exactly one file. It covers git-tracked C# files under the four
project directories plus `Directory.Packages.props`, and git-tracked
`*.ts`/`*.svelte`/`*.js` under `immichFrame.Web/src` plus
`immichFrame.Web/package.json` and `immichFrame.Web/static/pwa-service-worker.js`
— the last of those is not build configuration but hand-written code that caches
a bearer auth secret and signs outbound video-stream requests with it, so it
carries the highest priority on the frontend side. The oazapfts-generated
`immichFrame.Web/src/lib/immichFrameApi.ts` is deliberately excluded — it is
marked DO NOT MODIFY, so auditing it would queue an agent to hand-edit
generated code.

Six scan drivers: `dotnet build`, `dotnet list package --vulnerable` and
`roslynator` for the C# half; `eslint`, `svelte-check` and `npm audit` for the
frontend. Prettier is deliberately not a driver — formatting diffs are noise in
a findings table, the same reason `dotnet format` is absent. `npm audit` runs a
second pass with `--omit=dev` and seeds advisories that are unreachable from the
production dependency graph one severity level lower, tagged `dev dependency` in
the title, so build tooling cannot crowd out real findings. If that second
pass fails, every advisory is kept at full severity and the driver says so —
so a row that is not tagged `dev dependency` has genuinely been checked.

**Phase 3 (`arch-sweep`) covers both stacks.** `make audit-arch` reports on the
whole repo; `make audit-arch PKG=<target>` scopes it, where `<target>` is a
subtree (`ImmichFrame.Core/Logic`) or a C# namespace
(`ImmichFrame.Core.Logic.Pool`) — the adapter resolves the latter onto a
directory. The SvelteKit `$lib` alias also resolves, but **not through `PKG=`**:
make expands a command-line variable on assignment, so `PKG='$lib/stores'` is
already `ib/stores` before the recipe runs. Use the script for that form:

```sh
AUDIT_ADAPTER=fullstack audit/audit.sh arch-sweep '$lib/stores'
```

Section A prints **two separate import subgraphs**, C# and frontend: the SPA
reaches the API over HTTP, not through a module import, so there is no edge
between them, and Section E's `raw-fetch` rule is what guards that boundary
instead. C# edges come from each file's actual `namespace` declaration rather
than from assuming namespace mirrors path — it does not, the project directories
carry dots — and a `using` is then resolved only into projects the owning
`.csproj` actually references (transitively). That second filter is not
optional: `ImmichFrame.Core.csproj` has no `<ProjectReference>` at all, and two
files under `ImmichFrame.Core/Helpers` declare a `ImmichFrame.WebApi.Helpers`
namespace, so without it the graph invents Core → WebApi edges that cannot
compile. If the reference graph cannot be established at all — a csproj that is
missing, unreadable, truncated, or carries a `<ProjectReference>` with no
readable `Include` — the run says so on stderr, suppresses every cross-project
edge, and makes `core-to-webapi` refuse a verdict rather than print a `pass` it
cannot justify. Section B is a **branch-keyword heuristic, not
cyclomatic complexity**, and says so in its own header: there is no CC tool in
this toolchain (Roslynator's CLI has no complexity command and
`Microsoft.CodeAnalysis.Metrics` would make the audit alter the build), and
function boundaries are pattern-matched rather than parsed. Read it as a
ranking, not a measurement. Section E checks five invariants from
`ARCHITECTURE.md` — `core-to-webapi`, `aspnet-in-core`, `test-in-prod`,
`profile-leak`, `raw-fetch` — plus one hygiene rule,
`namespace-project-mismatch`. Every rule prints a verdict with the number of
files it read, so a genuine `pass` is never confused with a rule that had
nothing in scope (`n/a`); `FAIL` means a layering invariant is broken and `WARN`
means hygiene that still compiles. `core-to-webapi` resolves its `using`s
through the same edge list Section A draws rather than grepping, so the two
sections cannot contradict each other — a bare grep reports two hits here that
are *not* layer inversions, and they surface under the hygiene rule instead. Phase 3 shells out to neither dotnet nor npm: it is
git + grep + awk + sqlite3 over the tree. `audit/adapters/go.sh` is the upstream
reference and `audit/ADAPTERS.md` the contract. `audit/THREAT_MODEL.md` is written around Go
tells; its issue classes carry over to C# but its code patterns do not.

Requires `sqlite3`, `jq`, `curl`, `unzip`, `node` and `npm` on PATH, plus the
.NET 8 SDK. `make audit-tools` needs `curl`/`unzip` to fetch `roslynator` and
the `Roslynator.Analyzers` assemblies — the CLI package ships none, so without
them Roslynator's own `RCS*` rules never load and the scan reports only the
analyzers the projects already reference.

The three frontend drivers run out of `immichFrame.Web/node_modules`, so on a
fresh clone run:

```sh
npm --prefix immichFrame.Web ci    # one-time, before the first audit-scan
```

A scan never installs dependencies itself. Without `node_modules` the three
frontend drivers print that command as a notice and report state `missing`; the
C# half still scans normally. Note the summary table's wording for a missing
tool is the core's, and always reads `not installed (run 'make audit-tools')` —
for the frontend drivers the actual remedy is the `npm ci` above, which is why
each driver prints its own notice as it stands down.

## Agent skills

Seven skills under `.claude/skills/`, each an invokable slash command: six
orchestration skills — `architect`, `developer`, `code-reviewer`,
`code-reviewerer`, `repo-scout`, `diff-summarizer` — plus `sweep`, the entry
point to the audit system above. `repo-scout` reads and writes `ARCHITECTURE.md`
at the repo root.

Provenance: replicated from https://onedev.sharpspoon.io/agent-kit; the file
list and SHA-256s it was verified against are in `audit/manifest.json`.
