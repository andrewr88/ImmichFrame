# 004 — Per-profile auth secrets in the service worker

## Context

Branch `custom`. Steps 1-4 of multi-config profiles are committed (`0f0be55`,
`3f15b92`, `2eda02c`, `e210768`). Every profile may carry its own
`AuthenticationSecret`, and step 4 keyed the browser's persisted secret by profile
(`authSecret:<profile>`, plain `authSecret` for the default) so the main thread always
signs with the right one.

`immichFrame.Web/static/pwa-service-worker.js` did not get the same treatment, and both
reviewers flagged it independently. It holds **one** module-global `authSecret` (`:5`)
shared by every client it controls, and `getAuthSecret()` short-circuits on it (`:40`).
Two tabs open on different profiles therefore sign video-stream requests with whichever
secret was posted last — exactly the failure step 4 fixed everywhere else.

Read `CLAUDE.md`'s audit section first: this file is called out as carrying the highest
priority on the frontend side precisely because it caches a bearer secret and signs
outbound requests with it. Treat it accordingly.

## Objective

### A. Key the service worker's cached secret by profile

- Replace the `authSecret` scalar with a map keyed by profile. Normalise the default
  profile to a single key (`''`); `getAssetStreamUrl` omits the parameter entirely for
  the default, so `url.searchParams.get('profile')` is `null` there.
- In the `fetch` handler, read the profile off the intercepted request's URL and look up
  that profile's secret. **Keep the existing origin check** (`:72`) exactly as it is —
  its comment explains it stops the secret being attached to any cross-origin host that
  happens to serve the same path, and the most recent commit on `main` was written to
  add it.
- `authSecretResolvers` (`:6`) is a flat list and must become per-profile too, or a
  waiter for one profile will be resolved by another profile's secret arriving. The
  2-second timeout behaviour should be preserved per waiter.
- The `SET_AUTH_SECRET` message (`:30`) must carry which profile its secret belongs to.
  A client only knows its own profile, so on `REQUEST_AUTH_SECRET` each client answers
  for itself; the worker stores each reply under that reply's profile and resolves only
  the waiters for it. A request for a profile no client can answer must still time out
  and fall through to `fetch(event.request)` unsigned, as it does today (`:76-79`).

### B. The sender side

`immichFrame.Web/src/lib/index.ts` posts the message (`:16-21`) and must include the
profile. Read it from `profileStore` at call time, not at module scope.

### C. The same staleness on the images path

Both reviewers also noted `setBearer()` (`:50`) sets
`defaults.headers['Authorization']` once at `api.init()` and never follows
`profileStore`. This is the same defect as A on the non-video path. It is currently
masked because the app has no client-side navigation that reuses the component — the
adversarial reviewer verified there is no `goto` and no internal `<a href>` — so every
profile change today is a full document load. Close it anyway: it is a trap that springs
the moment anyone adds a `/kitchen` → `/bedroom` link, and that is not a discoverable
failure.

Make the bearer and the service-worker message follow `profileStore` rather than relying
on `HomePage` being remounted. Keep it simple — a subscription in `index.ts` that re-runs
the existing `init()` work on change is enough; do not restructure the module.

## Scope

- `immichFrame.Web/static/pwa-service-worker.js`
- `immichFrame.Web/src/lib/index.ts`

## Non-goals

- Do not change the wire protocol beyond adding the profile to the existing messages.
  No versioning, no handshake, no new message types unless one is genuinely unavoidable
  — say so first if you think it is.
- Do not touch the C# side, the generated client, the routes, or the stores. Step 4's
  `profileKeyedPersistStore` is correct and reviewed; read it, don't change it.
- No caching-strategy changes. The network-first branch at `:100-103` is out of scope.
- Do not fix the pre-existing lint failures, and do not add to them.
- No docs — that is task 005.

## Constraints / caveats

- **Shared working tree.** Never run `git stash`, `git reset`, `git restore`,
  `git checkout --`, or `git clean`. Never `git add -A` — stage explicit paths only.
  Do not commit. The tree is clean at `e210768` as this task starts.
- A service worker persists across reloads and updates on its own schedule. Bump
  `CACHE_NAME` (`:2`) only if you actually need the activate handler's cache sweep; say
  which you did and why.
- `static/` is copied verbatim into the build — this file is not bundled, has no build
  step, and cannot use imports or TypeScript. Plain browser JS only.

## Acceptance criteria

- `npm --prefix immichFrame.Web run check` clean and `run build` succeeds.
- **Demonstrate the fix, don't assert it.** Two tabs on two profiles with different
  secrets, both requesting a video asset: each request must carry its own profile's
  secret. Report what you actually observed. The previous task in this series drove
  headless Chromium against a stub backend for this; the same approach works here, and
  a service worker needs the page served over http://localhost (a secure context) to
  register at all.
- A single-profile install, and an install with no `AuthenticationSecret` at all, both
  still play video. This is the regression that matters most — do not let the
  per-profile keying break the common case.
