# 003 — Step 4: the `[config]` route and threading the profile through the client

## Context

Branch `custom`. Steps 1-3 are committed (`f5ace18`, `3f15b92`, `2eda02c`). The API
honours `?profile=kitchen` on ten endpoints and the generated client
(`immichFrame.Web/src/lib/immichFrameApi.ts`) can send it — every generated function now
takes an optional `profile`.

Nothing sends it yet. This task makes `https://frame.example.com/kitchen` serve the
`kitchen` profile.

## Objective

### A. A `profileStore`

The profile has to reach four files' worth of call sites, including two leaf components
(`clock.svelte`, `appointments.svelte`) that no prop could reach without drilling
through the whole tree. Mirror the existing `clientIdentifierStore` pattern
(`src/lib/stores/persist.store.ts:25`) and add a store.

**It must be a plain `writable`, not `persistStore`.** The profile comes from the URL on
every visit. Persisting it to localStorage would make a later visit to `/` serve
whichever profile was last opened — a silent wrong-config bug.

### B. The route

- `src/routes/[config]/+page.ts` — set `profileStore` from `params.config`, then load
  the config for that profile. Mirror `src/routes/+page.ts`, which does the same for the
  default and also honours the `?client=` param.
- `src/routes/[config]/+page.svelte` — just `<HomePage />` and a `<svelte:head>` title,
  same as `src/routes/+page.svelte`.
- `export const prerender = false;` on the route. `src/routes/+layout.ts:1` sets
  `prerender = true`, and adapter-static cannot prerender a dynamic route without an
  entries list. The SPA fallback (`fallback: 'index.html'` in `svelte.config.js`, and
  `MapFallbackToFile` on the backend) is what serves `/kitchen`.
- `src/routes/+page.ts` must **reset** `profileStore` to the default. Client-side
  navigation from `/kitchen` back to `/` does not reload the module, so a stale value
  would otherwise persist into the default route.

**Unknown profile → SvelteKit's 404 error page.** The API 404s an unknown profile via
`UnknownProfileMiddleware`, so the `getConfig` call in the route's load will reject.
Catch that and call SvelteKit's `error(404, ...)` with a message naming the profile.
Do not fall back to the default config — a typo'd frame URL that silently shows the
wrong photos is worse than an error. Check how the generated client surfaces a 404
(oazapfts returns a discriminated union on status rather than throwing for declared
responses) before assuming it throws.

### C. Thread `profile` through the call sites

Nine in total. Add `profile: get(profileStore)` / `profile: $profileStore` alongside the
existing `clientIdentifier` at each:

- `src/routes/+page.ts:14` — `getConfig` (the default route, so the default profile)
- `src/lib/components/home-page/home-page.svelte` — `:135` `getAssets`, `:360`
  `getAssetStreamUrl`, `:367` `getAsset`, `:379` `getAlbumInfo`, `:387` `getAssetInfo`,
  `:395` `getAssetFaces`
- `src/lib/components/elements/appointments.svelte:39` — `getAppointments`
- `src/lib/components/elements/clock.svelte:49` — `getWeather`

`getAssetStreamUrl` (`src/lib/index.ts:55`) is hand-written and builds the URL itself, so
it needs a `profile` parameter of its own and a `params.set('profile', ...)` guarded the
same way `clientIdentifier` already is.

**The service worker needs no change.** `static/pwa-service-worker.js:72` filters on
`url.pathname`, which excludes the query string, and rebuilds the request from
`url.href`, which preserves it. Do not touch that file.

### D. Per-profile auth secret

`AuthenticationSecret` is per-profile, but `authSecretStore`
(`src/lib/stores/persist.store.ts:26`) persists a single secret under one localStorage
key. As it stands, visiting `/kitchen` then `/bedroom` sends kitchen's secret to
bedroom and 401s; the auth prompt (`error-element.svelte:11`) then overwrites the one
slot, breaking `/kitchen` in turn.

Key the stored secret by profile: `authSecret:<profile>`, and **plain `authSecret` for
the default profile** so existing installations keep working without re-authenticating.

Constraints on the implementation:

- The store must follow `profileStore`, so reads and writes after a client-side
  navigation hit the right key.
- `src/lib/index.ts` reads it in two places that must see the current profile's value:
  `setBearer()` (`:50`) and the service-worker `postMessage` (`:19`). `api.init()` is
  called from `home-page.svelte:27` and `:81`, both after the route's load has set the
  profile, so ordering works — but verify it rather than assume it.
- `error-element.svelte:12` calls `location.reload()` after setting a secret, so the
  full-reload path is the easy one; the SPA-navigation path is the one to actually test.

## Scope

- `src/lib/stores/persist.store.ts` (or a new store module, your call)
- `src/routes/[config]/+page.ts`, `src/routes/[config]/+page.svelte` (new)
- `src/routes/+page.ts`
- `src/lib/index.ts`
- `src/lib/components/home-page/home-page.svelte`
- `src/lib/components/elements/{appointments,clock}.svelte`

## Non-goals

- Do not touch `static/pwa-service-worker.js` — see above.
- Do not touch any C# file, `openApi/swagger.json`, or
  `src/lib/immichFrameApi.ts` (generated, DO NOT MODIFY). If you believe the API needs a
  change, stop and tell me instead.
- No docs and no `docker/Settings.example.yml` — that is step 5.
- Do not add a profile switcher, a profile list endpoint, or any UI for choosing a
  profile. The URL is the only selector.
- Do not fix the pre-existing lint failures (9 eslint errors across five `.svelte`
  components, `prettier --check` over 21 files). Do not let your own changes add to them.

## Constraints / caveats

- **Shared working tree.** Never run `git stash`, `git reset`, `git restore`,
  `git checkout --`, or `git clean`. Never `git add -A` — stage explicit paths only.
  Do not commit. The tree is clean at `2eda02c` as this task starts.
- `make dev` will not start unless every configured account's Immich server answers
  `server/version` with major ≥ 3 — `ImmichServerVersionChecker` calls
  `Environment.Exit(1)` otherwise. Point `IMMICHFRAME_CONFIG_PATH` at a scratchpad
  `Settings.json` backed by a stub server if you need the backend running. Note
  `prerelease` must be present in the stub's JSON response; it is `Required.AllowNull`
  in the NSwag DTO.
- Backward compatibility is the whole point: `/` with no profile must behave exactly as
  it does today, and an existing install must not be logged out.

## Acceptance criteria

- `npm --prefix immichFrame.Web run check` (svelte-check) is clean.
- `npm --prefix immichFrame.Web run build` succeeds — this is what proves the
  `prerender = false` claim, since adapter-static fails the build otherwise.
- Every one of the nine call sites sends `profile`; verify by grep, and state the count
  you actually observe rather than repeating the number above.
- Manually exercise `/` and `/kitchen` against a running backend with a two-profile
  config and confirm they render different settings. Report what you observed.
