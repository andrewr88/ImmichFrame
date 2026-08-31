# Threat model checklist

This is the security lens for the sweep. On every file/package review, walk this
list and check each applicable class against the code under review. These are
real, recurring findings in this repo — the same classes keep being
re-discovered one at a time, so check them explicitly rather than from memory.

The kit ships this file written around Go idioms; it has been rewritten for this
repo — an ASP.NET Core 8 API (`ImmichFrame.Core`, `ImmichFrame.WebApi`) plus a
SvelteKit/TypeScript SPA (`immichFrame.Web`) — as `audit/ADAPTERS.md` instructs.
Every class is tagged **(C#)**, **(frontend)** or **(both)**; when auditing a
`.ts`/`.svelte` file, skip the C#-only entries. After install this file is owned
by this repo and is **expected to diverge** from the kit — sweeps append new
classes as they are discovered, so its `audit/manifest.json` entry will not
match (see `CLAUDE.md`).

**Sibling-inconsistency is the strongest tell**: when one call site is hardened
and a neighbouring one isn't (one controller sanitizes its input before logging
and the next logs it raw; one outbound path escapes its segment and the next
interpolates), the un-hardened sibling is almost always a real finding. This
codebase is built out of near-identical sets — four controllers with the same
preamble, JSON and YAML config documents, the current schema and its v1
fallback, one asset pool per selection kind — so this tell fires constantly.

For each class: description, **tell** (how to spot it while reading), **safe**
(the fix pattern). `file:line` references point at the current example, hardened
or not — re-check the line before trusting it.

1. **Endpoint anonymous by default** *(C#)* — `ImmichFrameAuthenticationHandler`
   succeeds anonymously whenever the endpoint carries no `[Authorize]` metadata,
   not only when no secret is configured
   (`ImmichFrame.WebApi/Helpers/ImmichFrameAuthenticationHandler.cs:28`). Auth is
   therefore opt-in per controller: a new controller or action that forgets the
   attribute is silently public and returns 200, never 401.
   Tell: a controller or action with no `[Authorize]`. Three of the four
   controllers carry it (`ImmichFrame.WebApi/Controllers/AssetController.cs:23`);
   `ConfigController` deliberately does not
   (`ImmichFrame.WebApi/Controllers/ConfigController.cs:9`) because the SPA loads
   config before it has a secret — so ask "is this one intended?" rather than
   flagging every absence.
   Safe: `[Authorize]` on the controller, and `[AllowAnonymous]` spelled out on
   any action that must stay open so the exception is stated rather than merely
   absent. Then check what that anonymous action returns — see class 3.

2. **Unsanitized client input reaching the log** *(C#)* — client-supplied strings
   go through `SanitizeString()` before they are logged, without exception
   (`ImmichFrame.Core/Helpers/ImmichFrameExtensionMethods.cs:7`); every controller
   does it for `clientIdentifier`
   (`ImmichFrame.WebApi/Controllers/AssetController.cs:40`) and the profile
   middleware for the profile name
   (`ImmichFrame.WebApi/Helpers/Profiles/UnknownProfileMiddleware.cs:26`). A
   missing call is log injection: the console sink is single-line, so an embedded
   newline forges a log record.
   Tell: a `_logger.Log*` whose arguments include a query-string, route or header
   value that has not been through `SanitizeString()`. Also an interpolated
   `$"..."` message instead of a structured template with named placeholders —
   interpolation defeats structured logging and is where the raw value usually
   slips in (`ImmichFrame.Core/Services/IcalCalendarService.cs:87`).
   Safe: sanitize into a local at the top of the action and log that local.
   `SanitizeString` is a *denylist* — it strips a fixed punctuation set plus
   CR/LF and leaves other control characters (tab, `\u001B`, `\u0085`, `\u2028`)
   intact — so treat it as "safe for this log sink", never as general escaping.

3. **Server-only setting leaking into the client DTO** *(C#)* —
   `ClientSettingsDto` is the *unauthenticated* `/api/Config` payload
   (`ImmichFrame.WebApi/Models/ClientSettingsDto.cs:5`). It must never carry API
   keys, the auth secret, webhook URLs or calendar URLs; `GetConfig_ContainsNoSecrets`
   asserts exactly that
   (`ImmichFrame.WebApi.Tests/Controllers/ConfigControllerTests.cs:121`). A new
   user-visible setting touches `IClientSettings`/`IGeneralSettings` in Core and
   both settings models in WebApi, so landing it on the wrong one is the standing
   mistake.
   Tell: a property added to `ClientSettingsDto` — or to `IClientSettings`, since
   `IGeneralSettings` implements it and widening the interface silently widens the
   payload — whose value is a credential, a URL the server dials, or anything the
   browser has no use for.
   Safe: keep it off `IClientSettings`; expose it via
   `IGeneralSettings`/`IServerBehaviorSettings` instead, and add the new secret's
   fixture value to the assertion list in `GetConfig_ContainsNoSecrets`.

4. **Cross-profile leakage** *(C#)* — one process serves several configuration
   profiles, selected per request by the `?profile=` query parameter (query, not
   header, because asset URLs are fetched bare by `<img src>`). Anything cached,
   tracked or memoised must be per-profile: `ProfileServices` builds a *separate*
   bloom-filter tracker per profile precisely because a shared one would let an
   asset requested under one profile be served from another profile's account
   (`ImmichFrame.WebApi/Helpers/Profiles/ProfileServices.cs:25`).
   Tell: a `static` or singleton cache, dictionary or filter keyed by asset id,
   account or client identifier with no profile in the key; a profile name
   resolved anywhere other than `ProfileRegistry`; code reading `?profile=` and
   re-deriving settings itself. `UnknownProfileMiddleware` 404s an unknown name
   *before* authentication
   (`ImmichFrame.WebApi/Helpers/Profiles/UnknownProfileMiddleware.cs:22`), so
   anything resolving a profile earlier in the pipeline sits outside that guard.
   Safe: hang per-profile state off `ProfileServices` so the registry owns its
   lifetime, and resolve settings through the scoped interfaces rather than
   `IConfigCatalog`/`ProfileRegistry` directly. Profile names are constrained to
   `^[A-Za-z0-9_-]{1,64}$` with reserved names
   (`ImmichFrame.WebApi/Helpers/Config/ConfigCatalog.cs:18`) — a name that reaches
   a cache key, path or URL from anywhere else has not been through that check.

5. **Untrusted identifier → path traversal via `Path.Combine`** *(C#)* —
   `Path.Combine` is not a sanitiser: it does **not** clean `..`, and if a later
   argument is rooted (`/etc/passwd`, `C:\...`, and on Windows also `\x` or `d:x`)
   it *discards everything before it* and returns just that argument. A base path
   therefore proves nothing about the result. The image cache composes a filename
   into a directory this way
   (`ImmichFrame.Core/Logic/PooledImmichFrameLogic.cs:163`); it is safe today only
   because the id is bound as a `Guid` and the extension comes from a two-value
   set.
   Tell: `Path.Combine`/`Path.Join` with any argument that is not a literal or a
   `Guid`/enum — a `string` id, a filename echoed from an upstream response
   header, a config value. Changing an action parameter from `Guid id` to
   `string id` removes the guard with no other visible edit. Where two segments
   are user-influenced, check that *both* are validated, not just the last one.
   Safe: keep identifiers typed so model binding rejects traversal at the
   boundary; where a string is unavoidable, validate against an allowlist pattern
   at the boundary **and** assert containment at use —
   `Path.GetFullPath(candidate).StartsWith(Path.GetFullPath(baseDir) + Path.DirectorySeparatorChar, StringComparison.Ordinal)`.

6. **Unbounded buffering of an upstream response** *(C#)* — this app proxies
   images and video from an Immich server it does not control, plus remote
   calendar feeds; buffering a whole response turns the upstream's size into this
   process's memory. `GetRandomImageAndInfo` copies the full asset into a
   `MemoryStream` and then Base64-encodes it — roughly 2.4x the asset in RAM per
   concurrent request (`ImmichFrame.WebApi/Controllers/AssetController.cs:163`) —
   and the calendar reader buffers each ICS body as a string with no cap
   (`ImmichFrame.Core/Services/IcalCalendarService.cs:83`). The range-video path
   is the hardened sibling: `HttpCompletionOption.ResponseHeadersRead` and a
   stream handed straight through (`ImmichFrame.Core/Api/ImmichApi.cs:24`).
   Tell: `ReadAsStringAsync` / `ReadAsByteArrayAsync` / `CopyToAsync(new
   MemoryStream())` on a response body; `new byte[x.Length]` where the length
   comes from upstream or request input; a client left at the default
   `MaxResponseContentBufferSize` next to a sibling that streams.
   Safe: stream to `Response.Body` instead of buffering. Where a buffer is
   genuinely required, bound it (`MaxResponseContentBufferSize`, or a
   fixed-capacity read that fails past the limit) and check `Content-Length`
   before allocating.

7. **Outbound fetch of a URL this process did not choose** *(C#)* — the server
   dials three externally-supplied URLs: the Immich base URL, each `Webcalendars`
   entry (`ImmichFrame.Core/Services/IcalCalendarService.cs:72`) and the webhook
   (`ImmichFrame.Core/Helpers/WebhookHelper.cs:22`). Today all three come from
   `Settings.json` — operator-supplied, so at the same trust level as the process
   — but there is no scheme or host allowlist anywhere, so the moment one becomes
   reachable from request input (a profile-scoped override, a new query
   parameter, a value read out of an upstream response) it is a full SSRF against
   loopback and the container network.
   Tell: an outbound URL or host that is not a literal — trace it to its source
   and ask whether *only* the operator can set it. Equally: a host taken from a
   response (`Location`, `WWW-Authenticate`) and then dialed, and credentials
   attached to a redirect-following request — the calendar reader lifts the URL's
   user-info into a `Basic` header and lets the handler follow redirects
   (`ImmichFrame.Core/Services/IcalCalendarService.cs:77`).
   Safe: allowlist the scheme (`http`/`https` only) and reject loopback, private,
   link-local, CGNAT `100.64.0.0/10` (the Tailscale range, and this app is
   commonly homelab-deployed) and unspecified addresses, matching on the
   *resolved* IP rather than the hostname. Where credentials are attached, set
   `AllowAutoRedirect = false` or re-check the host on every hop.

8. **Untrusted value interpolated into an outbound path or header** *(both)* — a
   value concatenated into an outbound URL path reshapes the request if it
   contains `/`, `?` or `#`; a value concatenated into a header can do the same to
   the header set. The hand-written `ImmichApi` partial escapes its path segment
   (`ImmichFrame.Core/Api/ImmichApi.cs:17`) but forwards the client's `Range`
   header verbatim with `TryAddWithoutValidation`
   (`ImmichFrame.Core/Api/ImmichApi.cs:22`). On the frontend the stream-URL
   builder is the hardened example (`immichFrame.Web/src/lib/index.ts:60`).
   Tell: string concatenation, `$"..."` or a template literal building a URL path
   or a header value; `TryAddWithoutValidation`; `new Uri(base + value)`. Sibling
   call sites escape and this one doesn't.
   Safe: `Uri.EscapeDataString` per path segment in C#, `encodeURIComponent` in
   TypeScript, `URLSearchParams` for query values; validate a forwarded header
   against its grammar (`bytes=<n>-<n>`) rather than passing it through.

9. **Culture-sensitive or NaN-permissive numeric parse of configuration** *(C#)* —
   `float.Parse`/`double.Parse`/`int.Parse` with no `IFormatProvider` use the
   *current* culture, so `47.5` parses as `475` under a comma-decimal locale; and
   `float.Parse` accepts `NaN`, `Infinity` and `-Infinity`, which slip through
   `> 0` / `>= 0` / `<= max` guards silently because every comparison with `NaN`
   is false. `WeatherLatLong` is split and parsed exactly this way
   (`ImmichFrame.Core/Services/OpenWeatherMapService.cs:19`) — where a value with
   no comma also throws `IndexOutOfRangeException` instead of a settings error.
   Tell: `Parse`/`TryParse` on a config or request string with no
   `CultureInfo.InvariantCulture`; a numeric setting used in arithmetic or as a
   limit with no finiteness check; an index into a `Split` result without checking
   `Length`.
   Safe: `double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out
   var v) && double.IsFinite(v)`, then range-check, then throw
   `SettingsNotValidException` on failure.

10. **Upstream exception text reaching a log or an HTTP response** *(C#)* — the
    NSwag-generated client throws `ApiException` carrying the upstream status and
    the first 512 bytes of the Immich response body. Controllers catch it narrowly
    — only `416` (`ImmichFrame.WebApi/Controllers/AssetController.cs:98`) — and let
    everything else propagate to the framework handler. Any message built from a URL, a
    settings value or an inner exception can carry an API key or an internal
    hostname out with it.
    Tell: `ex.Message`/`ex.ToString()` written to a response body, or to a log,
    where the exception came from a client constructed with a secret; a broad
    `catch` that rethrows with `$"...{ex.Message}"`
    (`ImmichFrame.WebApi/Helpers/Config/ConfigLoader.cs:170`).
    Safe: map upstream failures onto the domain exceptions in
    `ImmichFrame.Core/Exceptions/ImmichFrameExceptions.cs` and return those; log
    the detail at Debug with secret-bearing parts redacted; never let a raw
    upstream body reach the client.

11. **A guard dropped in one of a set of parallel implementations** *(both)* —
    when one of a near-identical set gains a check, the others usually don't.
    `JsonConfigDocument.Bind` and
    `YamlConfigDocument.Bind` both guard the deserialized-to-null case
    (`ImmichFrame.WebApi/Helpers/Config/JsonConfigDocument.cs:45`,
    `ImmichFrame.WebApi/Helpers/Config/YamlConfigDocument.cs:50`) — both
    `JsonSerializer.Deserialize<T>("null")` and YamlDotNet return `null` with no
    exception, so without the `?? throw` a settings file containing `null` starts
    the app with an empty configuration and no error. That pair is consistent
    today; the job is to keep it that way as readers and controllers are added.
    Tell: grep a validator or guard by name and ask why each *non*-call site is
    exempt. A doc comment claiming kinship ("mirrors X", "same as the JSON
    reader") is a claim to disprove by diffing the two bodies, not an all-clear.
    Safe: extract the check into one helper both sides call; where the duplication
    is deliberate, state in each doc comment which invariants that copy owns and
    pin each with its own test.

12. **The client's bearer secret leaving its origin** *(frontend)* — the auth
    secret is kept in `localStorage`
    (`immichFrame.Web/src/lib/stores/persist.store.ts:26`), pushed to the service
    worker over `postMessage` and held in a worker-scope variable for the worker's
    lifetime (`immichFrame.Web/static/pwa-service-worker.js:29`), and the worker's
    `fetch` handler attaches `Authorization: Bearer <secret>` to any request whose
    **path** matches `/^\/api\/Asset\/[^/]+\/Asset$/`
    (`immichFrame.Web/static/pwa-service-worker.js:69`) — on `url.pathname` alone,
    with no origin comparison, while the handler sees every request a controlled
    page makes, cross-origin included. Highest-priority frontend file in the repo.
    Tell: in a service worker, a `fetch` handler deciding on `url.pathname`
    without comparing `url.origin` to `self.location.origin`; a secret assigned to
    a worker-scope variable with no clear-on-logout path; a secret in a query
    string, where it reaches server logs and `Referer`. For `message` handlers,
    check `event.origin`/`event.source` — mandatory in a `window` listener,
    cheap insurance in the worker (its clients are same-origin by spec).
    Safe: gate the header on `url.origin === self.location.origin` before
    attaching it; never attach credentials to a request the app did not
    originate; clear the worker's copy when the stored secret is cleared.

13. **Frontend XSS via the escape hatch** *(frontend)* — Svelte escapes `{expr}`,
    so injection requires leaving that path — and the data that would carry a
    payload is already on screen and unsanitized: EXIF descriptions, people and
    tag names from Immich
    (`immichFrame.Web/src/lib/components/elements/asset-info.svelte:90`) and event
    summaries and descriptions from remote ICS feeds
    (`immichFrame.Web/src/lib/components/elements/appointments.svelte:65`).
    Tell: `{@html ...}`, `innerHTML`/`outerHTML`/`insertAdjacentHTML`,
    `document.write`, `eval`/`new Function`, or a settings-supplied value bound to
    `href`/`src` (`immichFrame.Web/src/lib/components/elements/clock.svelte:86`
    interpolates `weatherIconUrl` into an `img src`, escaping only the substituted
    icon id). There are currently **no** `{@html}` uses under
    `immichFrame.Web/src` — the first one is a finding until argued otherwise.
    Safe: keep the value inside `{...}`. If markup is genuinely required, sanitize
    at the boundary rather than at the sink; for URLs, validate the scheme is
    `http`/`https` before binding it to `href`.

---

This list is maintained: when a new recurring class is found and fixed, add it
here — same numbered format, same description / **tell** / **safe** shape, with
the `(C#)`/`(frontend)`/`(both)` tag and a `file:line` grounding it in this repo
— so the next sweep checks it explicitly.
