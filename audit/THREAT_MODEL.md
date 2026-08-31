# Threat model checklist

This is the security lens for the sweep. On every file/package review, walk this
list and check each applicable class against the code under review. These are
real, recurring findings in this repo — the same classes keep being
re-discovered one at a time, so check them explicitly rather than from memory.

The classes themselves are language-neutral; the tells and safe patterns are
written in Go because Go is the kit's reference adapter. Consumers on another
language should translate them to their language's idioms (and may drop
Go-only classes). After install this file is owned by the consuming repo and
is **expected to diverge** from the kit — sweeps append new classes as they
are discovered.

**Sibling-inconsistency is the strongest tell**: when one call site is hardened
and a neighbouring one isn't (one endpoint escapes, the next interpolates; one
read caps the body, the next doesn't), the un-hardened sibling is almost always
a real finding.

For each class: description, **tell** (how to spot it while reading), **safe**
(the fix pattern).

1. **Leftmost X-Forwarded-For client IP** — rate-limit / login-lockout keyed on
   the leftmost XFF entry is spoofable if the proxy appends rather than replaces.
   Tell: `strings.Split(xff, ",")[0]` feeding a limiter or lockout key.
   Safe: take the rightmost trusted hop, or key off a proxy-verified client IP.

2. **Untrusted identifier → path traversal** — a slug/name/key flowing into
   `filepath.Join` (which Cleans `..`) escapes the base dir.
   Tell: a user/DB-supplied string joined to a base path then opened/written.
   Safe: validate the identifier is filesystem-safe at the boundary AND at use.

3. **Unbounded `resp.Body` / allocation from untrusted size** — success-path
   `io.ReadAll(resp.Body)` left uncapped while error paths use
   `io.LimitReader(.., 512)`; or a buffer/slice/image sized from an
   attacker-influenced length or dimension. Memory-exhaustion class.
   Tell: an uncapped read or a `make`/canvas sized from request input.
   Safe: `io.LimitReader` on success reads too; bound sizes before allocating.

4. **SSRF** — a user-controlled URL/host fetched server-side, including
   `file://` schemes and internal/loopback hosts.
   Tell: outbound request to a URL derived from request input without
   scheme/host allowlisting.
   Safe: allowlist scheme + host; reject `file://`, loopback, link-local.

5. **Mid-rune byte truncation** — `s[:N]` on a UTF-8 string can split a
   multi-byte rune, producing invalid UTF-8 (breaks LLM prompts / display).
   Tell: `s[:N]` on free-text / user / LLM strings.
   Safe: `string([]rune(s)[:max])`.

6. **NaN/Inf form floats** — `strconv.ParseFloat` accepts `NaN`/`Inf`, and
   `<= 0` / `< 0` guards do NOT reject `NaN`/`+Inf`.
   Tell: a form float used in math/limits without `math.IsNaN`/`math.IsInf`.
   Safe: explicitly reject `math.IsNaN(f) || math.IsInf(f, 0)` after parse.

7. **URL path injection via unescaped interpolation** — an untrusted value
   `fmt.Sprintf`'d into an outbound HTTP path; `/`, `?`, `#` reshape the request.
   Tell: sibling endpoints escape, this one doesn't.
   Safe: `url.PathEscape` each path segment.

8. **`templ.SafeURL` bypasses sanitisation** — wrapping a third-party/user URL
   in `templ.SafeURL` disables templ's URL sanitisation → stored XSS
   (`javascript:` etc.).
   Tell: `templ.SafeURL` on any non-constant URL.
   Safe: leave the URL as a plain string so templ sanitises it, or validate the
   scheme before wrapping.

9. **Dependency error strings leak secret-bearing URLs** — chromedp/stdlib echo
   the dialed `ws://host?token=<secret>` into error text.
   Tell: an error from a client constructed with a secret in the URL, surfaced
   to logs/users.
   Safe: redact at the wrapping boundary with an unanchored regex that catches
   the embedded token.

10. **JSON null unmarshal no-op** — `json.Unmarshal([]byte("null"), &m)` succeeds
    with a nil map and nil error, so object-shape validation silently passes.
    Tell: a decoder that expects an object but never checks for nil after
    unmarshal.
    Safe: add a `== nil` guard after unmarshal for object-expecting decoders.

11. **CLI option injection via leading-dash positional** — an untrusted bare
    positional passed to `exec.Command` is parsed as an option if it begins with
    `-` (no shell needed; e.g. `git worktree add <untrusted-branch>`).
    Tell: untrusted value as a bare positional arg to `exec.Command`.
    Safe: a `--` separator before positionals, or reject leading-dash values.

12. **Finalize on expired context** — poll/retry loops that run heartbeat/cleanup
    DB writes on the per-cycle context, which is often already expired by
    finalize time.
    Tell: a deferred/finalize write reusing the cycle's (cancelled/timed-out) ctx.
    Safe: run finalize/cleanup writes on a fresh context.

13. **Integer overflow in parser offset arithmetic** — in a binary / length-prefixed
    parser, `next = pos + size` (with `size` from an untrusted 32/64-bit field) can
    overflow to a NEGATIVE value that forward-only bounds checks (`next > end`,
    `int(next) > len(data)`) miss; the next iteration then indexes with the negative
    offset (`data[pos:pos+4]`) → panic (DoS on a single crafted/corrupt input).
    Tell: `pos + size` / `off + length` derived from parsed bytes, guarded only by
    `> end` / `> len` with no lower-bound or non-advance check.
    Safe: reject non-advancing/overflowed offsets (`next <= pos`), or bound `size`
    against the remaining length before adding.

14. **Driver-ignored DSN options void a read-only contract** — modernc.org/sqlite
    strips ALL query params from a non-`file:` DSN (splitting at the first `?`)
    and opens READWRITE|CREATE, so `path?mode=ro` silently opens read-write, a
    mistyped path is CREATED as a 0-byte junk DB instead of erroring, and a `?`
    in the path truncates it.
    Tell: a plain-path SQLite DSN carrying query params (especially `mode=ro`),
    or any "read-only source" promise enforced only by a DSN option.
    Safe: a `file:`-URI DSN with the path URL-escaped (see
    internal/legacysqlite.ReadOnlyDSN); pin with a test proving a missing path
    errors without creating a file and writes are refused.

15. **Credentialed follow of a server-supplied redirect/challenge host** — a
    client follows a host taken from the *server's* response (an OAuth/Bearer
    `WWW-Authenticate` realm, a `Location` redirect, a webhook callback) and
    attaches the operator's credentials to that request. A scheme-only guard
    (`https`) doesn't stop it: a compromised/malicious upstream points the host
    at an internal address (blind SSRF) or an attacker host (credential
    exfiltration cross-origin).
    Tell: a URL parsed from a response header/body that then drives an outbound
    request carrying `SetBasicAuth`/a token, guarded only by scheme (or not at
    all). Distinct from an operator-supplied URL (trusted) — the host here is
    upstream-controlled.
    Safe: reject internal-range hosts (loopback/private/link-local/unspecified,
    IP-literal + best-effort DNS) at the fetch choke point via an injectable
    guard so httptest survives (see internal/registry.blockedRealmHost); a
    same-host requirement is usually WRONG (legitimate cross-host realms exist,
    e.g. auth.docker.io for registry-1.docker.io).

16. **In-handler auth gate on a public-path route drops a middleware
    side-effect** — a route registered under a public/exempt path prefix (so the
    global auth middleware short-circuits it) re-implements the session check
    inside the handler, but reproduces only the *decision* and not the
    middleware's side-effects: the activity/heartbeat touch that feeds a sliding
    expiry, a setup/needs-provisioning gate, request-logger enrichment, an
    elevation check. The gate looks correct in isolation because it reads the
    same state the middleware does — it just never writes it, so traffic that
    stays on those routes validates against a window it never advances and the
    credential dies under continuous use.
    Tell: a public-path prefix list (`isPublicPath`, `*ExemptPrefixes`) whose
    entries are re-gated by a per-handler wrapper; then diff that wrapper
    against the middleware line by line and look for a *write* the wrapper
    omits. A doc comment cheerfully stating the divergence ("unlike the
    middleware this does not …") is the loudest tell, not an all-clear.
    Safe: have the in-handler gate call the same helper the middleware calls
    rather than re-deriving the check, and cross-reference both doc comments so
    a later "deduplicate this" refactor cannot silently drop the write. Pin it
    with a test that drives a request through the wrapper and asserts the
    side-effect, not just the status code.

17. **Internal-IP denylist built from `net.IP` method calls** — an SSRF guard
    that answers "is this address internal?" with a disjunction of stdlib
    predicates (`IsLoopback` / `IsPrivate` / `IsLinkLocalUnicast` /
    `IsMulticast` / `IsUnspecified`) covers only the ranges the stdlib chose to
    name. It silently omits deployment-relevant ones — above all
    **`100.64.0.0/10` (RFC 6598 CGNAT), which is the Tailscale range**, plus
    `0.0.0.0/8` beyond the unspecified address itself, IPv4-*compatible* IPv6
    (`::a.b.c.d`, where `To4()` returns nil so the v4 predicates never fire),
    NAT64 `64:ff9b::/96`, and `255.255.255.255`. The guard reads as complete
    because every line is a named, meaningful check — the gap is in what was
    never named. On a tailnet homelab this is not theoretical: the most
    privileged hosts are reachable at exactly the unblocked range.
    Tell: an internal-IP predicate whose body is `ip.IsX() || ip.IsY() || …`
    with no explicit CIDR list. Strongest tell of all — two guards in the same
    repo where one enumerates CIDRs and the other calls methods: the
    method-call one is behind, and the CIDR one usually gained its extra
    ranges from a prior finding.
    Safe: make an explicit CIDR list (parsed once at init) the authoritative
    check; normalise IPv4-mapped IPv6 to its v4 form before matching; pin the
    whole matrix — including the deployment's own ranges — in a table-driven
    test, with genuinely-public controls that must NOT be blocked. Also set
    `Proxy: nil` on the transport: under `HTTP_PROXY`/`HTTPS_PROXY` the dial
    target becomes the proxy and the user-supplied host is resolved and
    fetched *by the proxy*, so a resolved-IP guard never sees it. Guarding at
    the dialer's `Control` hook (post-DNS, on the concrete IP) is the right
    architecture — it also closes DNS rebinding and covers every redirect hop —
    but it is only as good as the predicate underneath it.

18. **Containment check that constrains only the inner half** — a path guard of
    the shape `dir := base/<A>; full := filepath.Join(dir, <B>); rel, _ :=
    filepath.Rel(dir, full); reject if rel != <B> || strings.HasPrefix(rel,
    "..")` looks like a traversal guard and *is* one — for `<B>`. It asserts
    nothing about `<A>`. If `<A>` is also attacker-influenced, `dir` itself
    escapes the intended root and the `Rel` check then passes happily, because
    it is measuring containment against the already-escaped base. The tell is
    that the guard's own comment names the attack it stops ("rejects
    ../../etc/passwd") without saying *which* variable it stops it in.
    Tell: two user-influenced segments composed into one path, but only one of
    them validated. Grep for `filepath.Rel(` and check what the base was built
    from. Also: a helper that resolves a directory from an identifier and
    returns it *without* validating that identifier, leaving validation to
    whichever caller remembers.
    Safe: validate every user-influenced segment at the point the path is
    built, not at the call sites — put the check at the TOP of the resolver so
    future callers inherit it. Test both halves: a traversal fixture for each
    segment independently, since a suite that only exercises the inner one
    mirrors the same blind spot as the code.

19. **Guard dropped while copying a function** — a helper is duplicated into
    another package or layer with a comment saying "mirrors X" / "sibling copy
    of X", and the copy silently omits a validation the original had. The prose
    asserts the equivalence that the code no longer honours, so reviewers
    reading either side see a consistent story. Distinct from ordinary
    duplication (class 17): there the copies agree and are all wrong together;
    here they *disagree*, and the un-guarded one is usually the one nobody
    re-derived.
    Tell: a doc comment claiming kinship with another implementation — diff the
    two bodies rather than trusting the claim. Equally: a validator that is
    called in two or three places but conspicuously not in a fourth on the same
    data (grep the validator's name and ask why each *non*-call site is exempt).
    Safe: when the duplication is deliberate, make the doc comment state which
    invariants the copy is responsible for, and pin each with its own test.
    When it is not, converge them.

20. **Field rule enforced on create but not on edit** — a write-path *pair*
    guards the same column asymmetrically: the create handler rejects a value
    (blank, out-of-range, wrong vocabulary) while the edit handler applies only
    the cheap checks (length, enum) and writes the rest through. Nothing looks
    wrong in either handler read alone — create is visibly careful, and edit is
    visibly *doing validation*, just not that one. Distinct from class 19: these
    two bodies were written independently rather than copied, so there is no
    "mirrors X" comment to disprove and no shared validator whose call sites you
    can grep. The consequence is that the edit form becomes the only door to a
    state the data model never intended, which is why the invalid rows look
    impossible when you reason from the create path and the schema.
    Tell: for each create/edit pair on the same resource, list the rules the
    create path enforces and check each one against the edit path — a rule
    written inline (not extracted into a validator) is the one that goes
    missing. A matching gap in the templates (`required` on the create input,
    absent on the edit input) usually accompanies it, and render-site fallbacks
    ("if X is blank, show the title instead") hide the result rather than
    signalling it. Ask specifically: can any *other* write path reach this state?
    If not, the edit path is the sole door and the rule is load-bearing there.
    Safe: enforce the rule server-side on every write path, with identical
    wording so the two rejections read as one rule; mirror it in the template
    only as an affordance, never as the guard. Pin the edit-path rejection with
    its own test asserting the stored row is unchanged — a re-render assertion
    alone passes even when the write leaked through.

---

This list is maintained: when a new recurring class is found and fixed, add it
here so the next sweep checks it explicitly.
