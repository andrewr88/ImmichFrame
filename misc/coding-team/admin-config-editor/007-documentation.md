# 007 — Document the admin configuration editor

## Context

Final task. Eight commits built an OIDC-authenticated editor at `/admin`:
`7c4a328` swappable catalog and the `admin` reserved name, `3c9c8e6`/`3ed1c9a` the OIDC surface and
its startup authorization guard, `3b1de22` the config read/write API, `3daaf1f` the Immich picker
proxy, `627703e` a blank-`AuthenticationSecret` fix, `023e975` the page, `fe9772a` the pickers.

None of it is documented beyond commented entries in `docker/example.env`.

## Objective

An operator can enable the editor, understand what it will and will not do to their settings file,
and recognise the two failure modes that will otherwise cost them an evening.

## Rule: every claim must be checked against the code

This plan produced repeated documentation-versus-behaviour mismatches — a line in
`docker/example.env` claimed a verification guarantee the code deliberately did not provide, and
four code comments were accurate about behaviour while wrong about why. Do not write from this
brief. For each statement, find where it is true in the tree and confirm it. If something here
contradicts the code, the code wins and you tell me.

## New page

`docs/docs/getting-started/admin-editor.md`, Docusaurus, matching the voice of
`configuration.md` — direct, second person, short sections.

**Enabling it.** Five environment variables (`AdminOidcOptions`):
`IMMICHFRAME_OIDC_AUTHORITY`, `_CLIENT_ID`, `_CLIENT_SECRET`, `_ADMINS`,
`_TRUST_PROXY_HEADERS`. Be explicit that the surface stays **off** unless the first three *and* a
non-empty allowlist are all present — an empty `_ADMINS` does not mean "everyone", it means the
page is not there. `/admin` 404s when it is off, deliberately.

These are environment-only and cannot be edited from the editor. Say why: it must not be able to
break the way in to itself, and a client secret has no business in the file the editor rewrites.

**Two failure modes worth their own callouts.**

1. `AddOpenIdConnect` does not set `RequireHttpsMetadata = false`, so an `http://` authority makes
   **every request** to ImmichFrame return 500 — not just the login. Anyone testing against a local
   identity provider over plain HTTP will hit this and have no idea why the frames stopped.
2. Behind a TLS-terminating reverse proxy without `_TRUST_PROXY_HEADERS=true`, ImmichFrame builds
   an `http://` `redirect_uri` and the provider rejects it. Say what to set, and warn that trusting
   those headers when *not* behind a proxy lets a client forge them.

**Who is an administrator.** Each `_ADMINS` entry is matched against both the `sub` and `email`
claims; either matches. State the real rule on verification, which is narrower than it sounds: an
email entry is refused only when the provider explicitly reports `email_verified: false`. A
provider that omits the claim cannot be distinguished from one confirming the address, so on a
provider with open registration an email entry can be claimed by a stranger. Recommend `sub` —
the provider assigns it and a user cannot choose it. `docker/example.env` already words this
correctly; match it rather than reinventing.

**What the editor will not do**, each with its reason:

- Configuration from environment variables is read-only — there is no file to write.
- A v1-schema file needs explicit conversion consent, and converting rewrites it in the current
  schema.
- A settings file using YAML anchors or aliases is read-only in the editor. An alias resolves to
  the anchored node, so saving would silently expand it and turn an inherited value into an
  explicit override.
- Comments are not preserved when the editor rewrites a file.
- A key the current schema does not model is dropped on save.
- Profiles cannot be renamed. A profile name is a frame's URL path and the key its browser stores
  that profile's auth secret under, so a rename logs every frame on it out and points it at a 404.
  They can be created and deleted.

**Saving.** Validated before the file is touched, so a rejected configuration never lands on disk.
Written atomically with a timestamped backup kept beside it, pruned to the five most recent.
Changes apply without a restart. The config directory must be **writable by UID 1000** — the
container runs non-root, and a read-only mount gives a clear refusal rather than a broken save.

**Secrets.** Never shown, only reported as set or not set. An untouched secret survives a save. An
account whose key comes from `ApiKeyFile` is shown as such. A profile that newly takes over the
account list has to be given its API key, because the browser never had it.

Cross-reference `configuration.md:235` for a profile setting `AuthenticationSecret` to null: the
editor can express it, and it is how one frame stays reachable without a prompt while the rest stay
secured.

## Existing pages to update

- `docs/docs/getting-started/configuration.md` — link to the new page from the security and
  profiles sections. **Also document `627703e`:** an empty or whitespace `AuthenticationSecret` now
  means no authentication at all. Previously it was enforced, which locked out every real client
  while admitting anyone sending an empty bearer token. Behaviour change, worth stating plainly.
- `docs/docs/getting-started/installation/docker.md` — the writable config volume, if that page is
  where a volume is described. Check before adding.

Add the new page to whatever controls docs navigation — check how `getting-started` and `features`
order themselves (`_category_.json`) rather than assuming a sidebar file exists.

## Non-goals

- **No code changes.** If you find a defect, report it; do not fix it here.
- No changes to `audit/`, `CLAUDE.md`, or `ARCHITECTURE.md`.
- Do not rewrite unrelated parts of `configuration.md`.
- No release notes — `CLAUDE.md`'s release rule is for releases, and this is not one.

## Acceptance criteria

- A new operator can go from nothing to a working `/admin` using only the new page.
- Both failure-mode callouts are present and say what to do.
- The `email_verified` wording matches `AdminOidcOptions` and `docker/example.env` — no stronger.
- Every "the editor will not do X" item states its reason.
- The blank-`AuthenticationSecret` change is documented.
- `npm --prefix docs run build` succeeds, and no link is broken.

## Working agreements

- Shared working tree. Never run `git stash`, `git reset`, `git restore`, `git checkout --` or
  `git clean`; never `git add -A`. Stage explicit paths only.
- `CLAUDE.md` and `.claude/settings.local.json` are not yours.
- Commit messages must **not** carry a `Co-Authored-By` trailer. Do not commit unless asked.
- Write for operators, not developers — `CLAUDE.md`'s release-notes guidance on tone applies here
  too: "you"/"your", concise, no internal refactor jargon.
- `json` and `shutil` are blocked in this sandbox's python; `jq` works.
