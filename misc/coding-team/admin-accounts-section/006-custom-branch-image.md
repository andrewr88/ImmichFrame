# 006 — Build and publish a container image on pushes to `custom`

## Context

This repository is a fork: `origin` is `andrewr88/ImmichFrame`, `upstream` is
`immichFrame/ImmichFrame` with push disabled. Work lands on the `custom` branch, not `main`.

`.github/workflows/build-docker.yml` already publishes to GHCR, but only on `v*` tags and manual
dispatch. The fork's owner wants an image built and pushed from `custom` so they can run their own
build, without tagging releases.

### Facts that constrain the implementation

- `Dockerfile:24` runs `dotnet publish … -p:AssemblyVersion=$VERSION`. **MSBuild requires
  `AssemblyVersion` to be purely numeric** (`major.minor.build.revision`), so a semver-ish value
  like `0.0.0-custom.abc1234` fails the build. Anything passed as `VERSION` must be numeric.
- `APP_VERSION` is only exported as an environment variable for diagnostics
  (`Dockerfile:11,21,45`); no C# reads it. Verify that with a grep rather than trusting it.
- The .NET stages are `FROM --platform=$BUILDPLATFORM` and cross-compile via
  `--runtime linux-${TARGETARCH}`, so they run natively on the amd64 runner.
- `github.repository` is `andrewr88/ImmichFrame`, which contains an uppercase letter, while GHCR
  requires lowercase image names. `docker/metadata-action` lowercases the image name for you —
  confirm that holds for the version you use rather than assuming it.

## Objective

Every push to `custom` builds the container image for linux/amd64 and linux/arm64 and pushes it to
`ghcr.io/andrewr88/immichframe`, tagged so the owner can either track the branch or pin an exact
commit.

## Scope

- **New** `.github/workflows/build-custom-image.yml`

A **new file**, deliberately not an edit to `build-docker.yml`. That file comes from upstream, and
this fork will keep merging upstream changes into it; a local modification there is a merge conflict
every time it is touched, whereas a separate workflow file merges cleanly forever.

### What it must do

- Trigger on `push` to `custom`, plus `workflow_dispatch` so it can be run by hand.
- Permissions: `contents: read`, `packages: write`. Nothing else — the default token is broad and
  this job needs two things.
- Log in to `ghcr.io` with `GITHUB_TOKEN`; no secret needs to be created.
- Build `target: final` for `linux/amd64,linux/arm64`, as `build-docker.yml` does for its own set.
- **Tags**: `latest` and a short-SHA tag. The owner explicitly does not want version numbers, so do
  not derive semver tags. `latest` tracks the branch; the SHA tag is what makes a bad deploy
  recoverable — without an immutable tag there is no way to say "run the one from before".
- **`VERSION` build-arg**: pass something numeric and monotonic — the run number is the obvious
  source. This is not for the owner's benefit; it is so `AssemblyVersion` stays valid and the
  container does not report an empty version. Do not spend effort making it meaningful.
- `docker/setup-qemu-action` is needed because arm64 is being built. Note `build-docker.yml` omits
  it and builds three platforms anyway — do not "fix" that file, and do not assume its omission
  proves QEMU is unnecessary; GitHub runners' pre-registered binfmt handlers are not a contract.
- Use the GitHub Actions cache (`type=gha`) for build layers. Pushes to a working branch are
  frequent and this is the difference between a tolerable loop and an ignored one.

### Pin actions the way the repository already does

`build-docker.yml` uses major-version tags (`actions/checkout@v4`, `docker/build-push-action@v6`).
Match that convention rather than introducing SHA pinning in one file.

## Non-goals

- **Do not edit `.github/workflows/build-docker.yml`**, `test.yml`, `codeql.yml`, `deploy.yml`, or
  `release.yml`.
- **Do not edit the `Dockerfile`.** Stage 3 (`build-node`) lacks a `--platform=$BUILDPLATFORM` pin
  and so builds under emulation for arm64 even though its output is architecture-independent static
  files. That is a real inefficiency and the architect is raising it separately — it is not this
  task's to fix, and changing the Dockerfile widens the fork's diff from upstream.
- No `linux/arm/v7`. The owner chose amd64 + arm64; arm/v7 is the slow emulated leg and they do not
  deploy 32-bit ARM.
- No build on pull requests, on `main`, or on other branches.
- No release notes, no GitHub Release, no version tagging.
- Do not attempt to change the GHCR package's visibility from the workflow; that is a one-time
  setting in the repository's package settings and is the owner's to make.

## Constraints

- Shared working tree. Read `.claude/skills/_shared/concurrency.md`. Never `git stash`, `git reset`,
  `git restore`, `git checkout --`, or `git clean`; never `git add -A`; stage explicit paths only.
  `.gitignore`, `Makefile`, `ImmichFrame.WebApi/Properties/launchSettings.json` and
  `immichFrame.Web/package-lock.json` carry other people's uncommitted work — leave them alone.
  Another developer is concurrently working under `immichFrame.Web/` and may be adding a test
  runner; stay out of that directory entirely. Do not commit.
- YAML: match the existing workflows' two-space indentation and comment style.

## Validation

You cannot run GitHub Actions here, so do not claim you have. What you can do:

- Parse the file and prove it is valid YAML (`python3 -c "import yaml,sys; yaml.safe_load(open(...))"`
  or equivalent). A workflow that does not parse fails silently on GitHub with no useful message.
- Check the file against the schema of the actions it uses — every `uses:` names a real action and
  every `with:` key is one that action accepts. Getting a key name wrong is the most common way one
  of these is wrong and still parses.
- Confirm by reading that the tag expressions produce what you intend. State in your report the
  exact image references a push to `custom` at commit `abc1234` would produce.

## Acceptance criteria

- A push to `custom` builds linux/amd64 and linux/arm64 and pushes to
  `ghcr.io/andrewr88/immichframe`.
- The resulting references are `:latest` and an immutable short-SHA tag, and you have stated both
  literally in your report.
- The `VERSION` build-arg is numeric, so `-p:AssemblyVersion=` cannot fail the build.
- No other workflow file is modified, and the `Dockerfile` is untouched.
- Pushes to any other branch, and pull requests, build nothing.
