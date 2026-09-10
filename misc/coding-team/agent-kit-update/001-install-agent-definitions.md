# 001 — Install the upstream kit's agent definitions

## Context

`.claude/skills/` and `audit/` are replicated from the `omnispoon-agent-kit`
at https://onedev.sharpspoon.io/agent-kit. `audit/manifest.json` is the record
of what upstream shipped; `CLAUDE.md:173-189` documents the provenance and the
verification command.

Upstream has advanced from kit version `46284224…` to `4fa477a2…`, 21 files to
26. The delta, established by diffing the two manifests:

- **New (5)** — `.claude/agents/{code-reviewer,code-reviewerer,developer,diff-summarizer,repo-scout}.md`
- **Changed (1)** — `audit/INTEGRATION.md` (+8 lines, documenting those five)
- **Unchanged (20)** — including `audit/THREAT_MODEL.md`: upstream's sha still
  equals this repo's stale manifest entry, so upstream has *not* revised it and
  our C#/TypeScript rewrite stands.

The five new files register the existing skills as spawnable subagents, pinning
model and effort in frontmatter. This repo has no `.claude/agents/` directory,
yet `.claude/skills/architect/SKILL.md:61` already branches on
`.claude/agents/developer.md` existing — so the gap is functional, not cosmetic.

The six upstream files are already fetched and verified byte-for-byte against
the upstream manifest at:

    /tmp/claude-1000/-workspace/0f6a28ea-a3b7-46a3-b510-1c5f265024af/scratchpad/kit/
    /tmp/claude-1000/-workspace/0f6a28ea-a3b7-46a3-b510-1c5f265024af/scratchpad/upstream-manifest.json

Copy from there. Do not re-fetch over the network — the verified bytes are on disk.

## Objective

Bring the replicated kit to upstream version `4fa477a2…`, and update this repo's
own documentation of it.

## Scope

1. Copy the five `.claude/agents/*.md` files from the scratchpad `kit/` tree to
   the same relative paths under `/workspace`. Verbatim — byte-for-byte, no
   reformatting, no reflowing, no local edits to the `model:`/`effort:` pins.
   Create `.claude/agents/`; it is not gitignored (`git check-ignore` confirms).
2. Replace `audit/INTEGRATION.md` with the scratchpad copy. Wholesale replace,
   do not hand-merge — it is an upstream-owned file.
3. Replace `audit/manifest.json` with `scratchpad/upstream-manifest.json`
   wholesale. `CLAUDE.md:181` says not to "fix" this file; installing a genuine
   new upstream manifest is not a fix and is the intended path. The
   `audit/THREAT_MODEL.md` entry must keep upstream's sha — do not reconcile it
   to the local file.
4. Update `CLAUDE.md`'s "Agent skills" section (starts `CLAUDE.md:165`):
   - the count sentence at line 176, `20 of 21` → `25 of 26`
   - the closing sentence at line 189, `20 \`OK\`` → `25 \`OK\``
   - the opening paragraph (167-171) to say the seven skills are accompanied by
     five agent definitions under `.claude/agents/`, which register them as
     spawnable subagents and pin model/effort; note there is no `architect`
     definition because it runs as the main session.
   Keep the existing voice and the surrounding THREAT_MODEL.md rationale intact.
5. Amend the `.gitignore:464` comment — it reads "`.claude/skills/` is shared and
   tracked" and should now name `.claude/agents/` too. Comment text only; do not
   add, remove, or reorder any ignore rule.

## Non-goals

- No edits to `audit/THREAT_MODEL.md`.
- No edits to any `.claude/skills/**` file — all 11 are unchanged upstream.
- No edits to `audit/adapters/fullstack.sh` or any repo-owned audit script.
- No new dependencies, no `.dockerignore` change, no commit.

## Constraints

- The working tree is shared with parallel developers. Read
  `.claude/skills/_shared/concurrency.md` and obey it: never `git stash`,
  `git reset`, `git restore`, `git checkout --` or `git clean`; never
  `git add -A`; stage explicit paths only; leave any hunk you did not author
  alone and do not report it as scope leak.
- Do not commit.

## Acceptance criteria

Kit verification reports exactly 25 `OK` and one `FAILED` on
`audit/THREAT_MODEL.md`:

```sh
jq -r '.files[] | "\(.sha256)  \(.path)"' audit/manifest.json | sha256sum -c -
```

Run it and paste the output in your report. Any other `FAILED` means a file was
altered in transit and must be re-copied, not patched.
