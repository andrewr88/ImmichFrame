# 004 — rewrite THREAT_MODEL.md for C# and TypeScript

## Context

`audit/THREAT_MODEL.md` is the security lens for every sweep phase — `phase-1-files.md:17`
("walk `audit/THREAT_MODEL.md` and check each applicable class against this file"),
`phase-2-packages.md:11` (Lens A), and `phase-3-architecture.md:9`. It is not reference
material; it is the checklist an agent walks on every single file it audits.

It currently ships the kit's Go content. Its own preamble says the classes are
language-neutral but the tells and safe patterns are Go, that consumers "should translate them
to their language's idioms (and may drop Go-only classes)", and that after install the file is
**owned by the consuming repo and expected to diverge**. `audit/ADAPTERS.md:12` says the same.
So this is a sanctioned edit, not a violation of the don't-touch-upstream rule that governed
tasks 001–003.

**Manifest handling.** Because sweeps append to this file routinely (`phase-1-files.md:10`),
its checksum will drift permanently from the moment the first sweep runs. Do not try to keep it
matching. Leave `audit/manifest.json` untouched — it is the record of what upstream shipped —
and update `CLAUDE.md` to say that kit verification is **20 of 21 files**, with
`THREAT_MODEL.md` the known repo-owned exception, noting that its manifest entry is still
useful for spotting a genuine upstream revision.

## Objective

Replace the Go tells and safe patterns with C# and TypeScript/Svelte ones, drop classes that do
not apply here, and add the classes that are real and recurring in *this* codebase.

## Scope

Modify `audit/THREAT_MODEL.md` and `CLAUDE.md`. **No code changes of any kind.**

### Format — preserve exactly

Numbered list; each entry is a bold class name, a description, a **tell** (how to spot it while
reading) and a **safe** (the fix pattern). Sweeps append in this format, so it has to stay
mechanically consistent.

Keep the preamble's structure and its two load-bearing points: that the list is walked
explicitly rather than from memory, and that **sibling-inconsistency is the strongest tell** —
one call site hardened and its neighbour not. Both are language-neutral and both are the most
useful things in the file. Update only the paragraph explaining the language.

This repo is two languages now, so mark each class with which it applies to (C#, frontend, or
both) — an agent auditing a `.svelte` file should be able to skip the C#-only classes quickly.

### Translate

Work through the existing classes and keep the ones that are real here, rewritten with C#/TS
idioms. Some have direct analogues worth getting right rather than transliterating:

- **Path traversal** — C#'s failure mode differs from Go's. `Path.Combine` does not clean `..`,
  and it *discards the base entirely* if the second argument is rooted. Get the tell right for
  C#, don't just swap the function name.
- **Mid-rune truncation** — the C# equivalent splits surrogate pairs, not UTF-8 runes.
  `SanitizeString` is the place this would bite.
- **Unbounded reads** — the C# shape is uncapped `ReadAsByteArrayAsync`/`MemoryStream` sized
  from request input; this app streams images and video.
- **SSRF** — highly applicable: the app fetches from a user-configured Immich server URL and a
  weather API.
- **URL path injection** — applicable to the hand-written `ImmichApi` partial and anywhere a
  value is interpolated into an outbound path rather than escaped.
- **NaN/Inf floats** — applicable to config parsing.

Drop anything with no analogue or no surface here rather than padding — a class that can't
occur is noise in a checklist walked on every file.

### Add — repo-specific classes

These come from conventions `ARCHITECTURE.md` documents and tests already enforce. **Cite real
`file:line` evidence for each**; do not add a class you cannot ground in this codebase.

- **Unsanitized client input reaching the log.** The codebase runs client-supplied strings
  through `SanitizeString()` before logging, without exception. A missing call is a log-injection
  finding and the sibling-inconsistency tell applies directly.
- **Server-only settings leaking into the client DTO.** `ClientSettingsDto` must never carry
  API keys, webhooks, calendar URLs, or the auth secret — `ConfigControllerTests` asserts this
  explicitly. Adding a setting to the wrong DTO is exactly the kind of recurring mistake this
  list exists for.
- **Profile isolation.** A request selects a profile via `?profile=` (a query parameter, not a
  header, because asset URLs are consumed bare by `<img src>`), and `UnknownProfileMiddleware`
  404s unknown names before auth. Anything resolving a profile outside the registry, or letting
  one profile's data reach another, belongs here.
- **The service worker's bearer secret.** `immichFrame.Web/static/pwa-service-worker.js` caches
  a secret received over `postMessage` and injects `Authorization: Bearer` onto intercepted
  requests. Unvalidated `event.origin` on a `postMessage` handler, and the secret's storage
  lifetime, are both real classes — this is the highest-value frontend file in the repo.
- **Frontend XSS.** Svelte escapes by default, so the tell is the escape hatch: `{@html}`, and
  any direct DOM sink.

Add others if you can ground them, but the bar is *recurring and generalizable* — the same
standard the sweep applies when it appends a class.

## Non-goals

- No code changes. Not the adapter, not the app, not the tests.
- Do not edit the sweep skill files under `.claude/skills/` — they reference this file by path
  and that reference stays correct.
- Do not edit `audit/manifest.json`.
- Do not fix any vulnerability you notice while writing this. If you find something real,
  report it to me separately and leave the code alone — that is a sweep's job, not this task's.
- No generic OWASP checklist material. Every class must be justified against this repo.

## Constraints

- Cite `file:line` for repo-specific classes and verify each citation resolves.
- Keep it walkable. This is read on every file audit; length is a real cost. If it grows much
  past its current size, you are adding filler.
- The working tree has in-flight C# work owned by someone else (`ImmichFrame.WebApi/Program.cs`,
  `.../Helpers/ImmichServerVersionChecker.cs`, two files under
  `ImmichFrame.WebApi.Tests/Controllers/`, untracked `ImmichFrame.WebApi/Helpers/Profiles/`).
  Do not edit, revert, judge or commit them. No `git add -A`, `git stash`, `git commit`.
- Raise privileged operations rather than performing them.

## Acceptance criteria

1. No Go syntax or Go-stdlib references remain (`filepath.Join`, `io.ReadAll`, `strconv.`,
   `fmt.Sprintf`, `math.IsNaN`, `[]rune`, …). Grep and show the result.
2. Every class carries a description, a **tell** and a **safe**, and is marked C# / frontend /
   both.
3. Every repo-specific class cites at least one `file:line` that resolves — list them and
   confirm each.
4. The preamble still states the walk-explicitly instruction and the sibling-inconsistency tell.
5. `CLAUDE.md` records the 20-of-21 verification position and why.
6. `git status` shows only `audit/THREAT_MODEL.md` and `CLAUDE.md` modified — no code files.
7. The other 20 kit files still checksum clean against the manifest.
8. Report the final class count against the original, and say which classes you dropped and why.
