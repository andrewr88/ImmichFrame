# 005 — Step 5: document configuration profiles

## Context

Branch `custom`. The multi-config-profiles feature is complete and committed:
`f5ace18` (catalog + document merge), `3f15b92` (registry + scoped DI), `2eda02c`
(query parameter + regenerated client), `e210768` (`[config]` route + threading),
`f482646` (per-profile secrets in the service worker).

Nothing documents it. This task is the last one.

## Objective

### A. `docs/docs/getting-started/configuration.md`

Add a section on configuration profiles. Place it after `### Multiple Immich Accounts`
(around `:186`) — it is the neighbouring "more than one of a thing" concept — or
somewhere better if you see one; say which you chose.

Cover, all of it verifiable in the code rather than from this brief alone:

- **What it does.** One ImmichFrame instance serves several named settings configs. A
  browser picks one by path — `https://frame.example.com/kitchen`; the API picks one
  with `?profile=kitchen`. No profile named means today's behaviour, unchanged.
- **How to declare one.** `Profiles` is a top-level key alongside `General` and
  `Accounts` (`ConfigLoader.cs:122-126`). Each profile names only what it overrides.
  Show a short worked YAML example — a default plus one or two profiles differing in a
  couple of obvious settings. Keep it short; the page already warns against copying
  large configs wholesale.
- **Merge semantics, precisely.** Nested mappings such as `General` merge key by key;
  scalars and **lists replace outright — `Accounts` included**. A profile that names
  `Accounts` replaces the whole list rather than appending to it. This is the single
  most surprising thing about the feature and deserves to be spelled out, not implied.
- **Name rules.** `^[A-Za-z0-9_-]{1,64}$`; `api`, `static`, `swagger` and `default` are
  reserved; lookup is case-insensitive (`ConfigCatalog.cs:20-26`). Say why the four are
  reserved — they are paths the backend already serves, so a profile named after one
  could never be reached at `/{profile}` in a browser.
- **Per-profile `AuthenticationSecret`.** A profile may override it, and may set it to
  `null`, which makes that profile unauthenticated — the handler grants access when the
  secret is null. That is intended and deliberately not validated against, so document
  it as a choice with consequences rather than a quirk. Note the browser stores each
  profile's secret separately, so each profile is authenticated once, independently.
- **An unknown profile is a 404**, not a fallback to the default. Say so — an operator
  debugging a typo'd frame URL should not have to guess.
- **Profiles need a settings file.** Environment-variable configuration cannot express
  them: env vars are flat and can only ever describe one configuration
  (`ConfigLoader.cs:86-88`). An install configured purely by env vars gets the default
  and nothing else.
- **One upgrade note.** A browser tab left open across an ImmichFrame upgrade may play
  no video for up to two seconds per asset until it is reloaded, because the old page
  and the new service worker disagree about which profile a cached secret belongs to. It
  resolves itself on reload. Frames stay open for weeks, so this is worth a line.

### B. `docker/Settings.example.yml` and `docker/Settings.example.json`

Add a commented-out `Profiles` block to both, in each file's own style — the YAML file
uses `#` comments to explain non-obvious keys, the JSON file cannot, so match whatever
that file already does for the same problem. Keep the example small: one profile
overriding two or three settings is enough to show the shape.

Both files must remain valid and loadable. `Settings.example.json` in particular must
stay parseable JSON — if the commented block cannot be expressed there, say so and
leave that file alone rather than breaking it.

## Scope

- `docs/docs/getting-started/configuration.md`
- `docker/Settings.example.yml`
- `docker/Settings.example.json`

## Non-goals

- No code changes at all. If you find a bug while writing the docs, report it to me
  rather than fixing it.
- Do not touch `docs/docs/getting-started/configurationV1.md` — that documents the
  superseded format.
- No new docs pages, no sidebar restructuring, no changes to `docs/` tooling.
- Do not document a profile switcher, a profile-list endpoint or any profile UI. None
  exists; the URL is the only selector.

## Constraints / caveats

- **Shared working tree.** Never run `git stash`, `git reset`, `git restore`,
  `git checkout --`, or `git clean`. Never `git add -A` — stage explicit paths only.
  Do not commit. The tree is clean at `f482646` as this task starts.
- Write for end users, not developers. `CLAUDE.md`'s release-writeup rule sets the house
  tone: address the reader as "you", keep it concise, skip internal refactor jargon.
  Match the surrounding page — it is plain prose plus fenced examples, no heavy
  formatting.
- **Every factual claim you write must be checked against the code**, not against this
  brief. I have been wrong in these briefs before: an earlier one in this series
  miscounted its own list and the developer repeated the wrong number back to me. If
  something here does not match what you find, the code wins — tell me.

## Acceptance criteria

- `docker/Settings.example.json` still parses (`jq . docker/Settings.example.json`), and
  `docker/Settings.example.yml` still parses as YAML.
- Confirm the documented merge behaviour by reading `JsonConfigDocument` /
  `YamlConfigDocument`, and say in your report which file and function you confirmed it
  from. Do not take the brief's word for it.
- If `npm --prefix docs run build` works in this environment, run it and report the
  result; if it does not, say so rather than claiming it passed.
