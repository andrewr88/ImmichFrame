# 007 — Document the accounts section

## Context

`docs/docs/getting-started/admin-editor.md` (182 lines) describes the configuration editor as it was
before tasks 001-005. Two of its sections are now wrong:

- **"What you can edit"** (`:102`) says *"Immich accounts, including per-account filters"*, describing
  accounts as something edited inside the configuration you have selected. They are now edited once,
  in a section above the profile tabs, and a profile assigns them.
- **"What the editor will not do"** (`:112`) is a list of deliberate refusals. It is missing the new
  ones, and those are exactly the kind a user hits and cannot explain.

`docs/docs/getting-started/configuration.md` already documents the `Label` setting itself — task 003
did that, including that `/admin` refuses to save a list whose labels collide while ImmichFrame
itself still starts. Do not duplicate that reference entry; link to it if it helps.

## Objective

An operator reading the admin-editor page understands where accounts live, how a profile uses one
without retyping its API key, and what the editor will refuse and why.

## Scope

`docs/docs/getting-started/admin-editor.md` only.

### What changed, as the user experiences it

- **Accounts are a section of their own, above the profile tabs.** One row per account across the
  whole settings file: its label, Immich server URL, API key and API key file. Editing a row changes
  that account everywhere it is used — which is the point, since which server an account is, is a
  property of the account and not of the configuration mentioning it.
- **A profile assigns accounts and says what to show from each.** The inherit/override choice is
  unchanged: a profile that inherits stays sparse and keeps following later edits to the default
  configuration. Overriding reveals a tick per account, and the albums/people/tags/rating/date
  filters for each ticked one.
- **Assigning an account does not ask for its API key.** The key is copied on the server; the browser
  is never given it. This is the friction the whole change exists to remove — say so plainly.
- **Configured albums, people and tags now show their Immich names as soon as the page loads**,
  rather than only after opening a picker. Worth one sentence; it is the first thing a user notices.
- **`Label` is how two accounts on one Immich server stay distinct.** Two accounts are the same
  account when their labels match; unlabelled accounts are matched on server URL instead. The editor
  never invents a label.

### New refusals for "What the editor will not do"

Each of these is a deliberate stop, and each needs the *why*, in the voice the existing entries use
(bold lead sentence, then the reasoning):

- **Guess which account is which when identity is ambiguous.** Two unlabelled accounts on one server
  in the same configuration, or two whose labels differ only in case or surrounding spaces. The
  editor shows both, marks the collision, and asks for distinct labels rather than merging them —
  merging would mean editing one row wrote one account's credential over the other's.
- **Save an account that one configuration stores inline and another reads from an API key file.**
  The two describe one account two different ways, and whichever is written would leave the other
  configuration with no usable credential. The editor names the configuration and the path and asks
  you to make them agree. Mention both ways out.
- **Remove the last account from the default configuration.** Every profile inherits from it and
  ImmichFrame cannot serve an image without one.

Removal is also worth a sentence under the accounts section: it takes the account out of every
configuration that used it, asks first, and says how many profiles are affected — including profiles
that were inheriting it rather than declaring it.

### Style

Match the page: `##` sections, second person, bold lead sentence for each refusal, no screenshots
(the page has none). Keep it tight — this is a reference page an operator scans, not a tutorial.
Read the whole file before editing so the new prose sounds like the old.

## Non-goals

- **No changes to `configuration.md`.** `Label` is documented there already.
- No documentation of this fork's CI or container image. `docs/` is the upstream project's published
  site (immichframe.dev); the `custom`-branch image is a fork-local arrangement and does not belong
  in it.
- No screenshots, no new pages, no changes to `_category_.json` or the sidebar.
- No code changes of any kind. If the documentation you are writing turns out to describe something
  the code does not actually do, **stop and report it** — that is a bug worth more than the sentence.
- Do not document the vitest suite; it is developer tooling, not operator documentation, and
  `ARCHITECTURE.md` is where commands live. Mentioning `npm test` there is out of scope for this task
  but worth flagging to the architect if you notice the omission.

## Constraints

- Shared working tree. Read `.claude/skills/_shared/concurrency.md`. Never `git stash`, `git reset`,
  `git restore`, `git checkout --`, or `git clean`; never `git add -A`; stage explicit paths only.
  `.gitignore`, `Makefile` and `ImmichFrame.WebApi/Properties/launchSettings.json` are another
  developer's in-flight work — leave them alone. Do not commit.
- Prose only. Do not reformat the parts of the file you are not changing.

## Validation

- `npm --prefix docs run build` must succeed — Docusaurus fails a build on a broken internal link,
  which is the realistic way to get this wrong.
- Re-read your own additions against the committed behaviour in
  `immichFrame.Web/src/lib/components/admin/` rather than against this brief. This brief is a
  summary; the code is what users will meet.

## Acceptance criteria

- "What you can edit" describes the accounts section, profile assignment, and that assigning an
  account does not ask for its key.
- "What the editor will not do" gains the three refusals above, each with its reason.
- The `Label` rule and the ambiguity behaviour are explained where a user would look for them.
- Nothing in the page still describes accounts as edited per-configuration.
- The docs site builds.
