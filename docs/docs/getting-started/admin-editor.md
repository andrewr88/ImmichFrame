---
sidebar_position: 3
---

# 🛠️ Admin configuration editor

ImmichFrame can edit its own settings file from a browser, at `/admin`. You sign in with your own
identity provider, change settings, accounts and [profiles](/docs/getting-started/configuration#configuration-profiles),
and the new configuration applies without restarting the container.

The editor is off unless you configure it. On an installation that has not, every admin endpoint
answers `404` except the one that reports this, and `/admin` itself loads only to tell you the
surface is off and what is missing.

## Turning it on

The editor is guarded by OpenID Connect, so you need a client on an identity provider you run or
trust (Authentik, Keycloak, Auth0, Google, and so on). Create a **confidential** client — one with a
client secret — using the authorization code flow, and register this redirect URI:

```
https://frame.example.com/signin-oidc
```

ImmichFrame asks for the `openid`, `profile` and `email` scopes, and uses PKCE.

Then set these environment variables:

| Variable | What it is |
| --- | --- |
| `IMMICHFRAME_OIDC_AUTHORITY` | Your provider's issuer URL, e.g. `https://idp.example.com/realms/immichframe`. **Must be `https://`** — see below. |
| `IMMICHFRAME_OIDC_CLIENT_ID` | The client id you registered. |
| `IMMICHFRAME_OIDC_CLIENT_SECRET` | That client's secret. |
| `IMMICHFRAME_OIDC_ADMINS` | Comma-separated list of who may edit the configuration. Empty means nobody. |
| `IMMICHFRAME_OIDC_TRUST_PROXY_HEADERS` | `true` only when a reverse proxy in front of ImmichFrame sets `X-Forwarded-Proto`/`X-Forwarded-Host`. Off by default. |

```yaml
    environment:
      TZ: "Europe/Berlin"
      IMMICHFRAME_OIDC_AUTHORITY: "https://idp.example.com/realms/immichframe"
      IMMICHFRAME_OIDC_CLIENT_ID: "immichframe"
      IMMICHFRAME_OIDC_CLIENT_SECRET: "your-client-secret"
      IMMICHFRAME_OIDC_ADMINS: "00000000-0000-0000-0000-000000000000"
      IMMICHFRAME_OIDC_TRUST_PROXY_HEADERS: "true"
```

The surface stays **off** unless the authority, the client id, the client secret *and* a non-empty
administrator list are all present. An empty `IMMICHFRAME_OIDC_ADMINS` never means "everyone" — it
means the editor is off, because a sign-in that can only produce a session nobody may use is not
worth offering.

These five are read from the environment only, and are deliberately not settings in
`Settings.json`/`Settings.yml`: the editor rewrites that file, so it must not be able to break the
way in to itself, and your client secret has no business in the file you paste into bug reports.
Changing them takes a restart.

Once it is on, open `https://frame.example.com/admin` and sign in.

:::warning An `http://` authority breaks the whole application
ImmichFrame requires HTTPS metadata from your identity provider, and there is no setting to relax
that. Point `IMMICHFRAME_OIDC_AUTHORITY` at a plain-HTTP provider and **every request to the API** —
not just the login — fails with `500 Internal Server Error`, and your frames stop loading photos.
Static files are still served, so a page shell can still load and make the installation look
healthy; nothing behind it works. The log says:

```
The MetadataAddress or Authority must use HTTPS unless disabled for development by setting RequireHttpsMetadata=false.
```

If you are testing against a local identity provider over plain HTTP, this is why everything
stopped. Give the provider a TLS certificate, or unset the variable to turn the editor back off.
:::

:::warning Behind a TLS-terminating proxy, set `IMMICHFRAME_OIDC_TRUST_PROXY_HEADERS=true`
If something in front of ImmichFrame terminates TLS, ImmichFrame only ever sees a plain HTTP
request, so it builds an `http://` `redirect_uri` and your provider rejects the sign-in as an
unregistered redirect. Setting `IMMICHFRAME_OIDC_TRUST_PROXY_HEADERS=true` makes it trust
`X-Forwarded-Proto` and `X-Forwarded-Host` and build the URL your proxy is actually serving.

Turn it on **only** when a proxy in front of you overwrites those headers. With it on and nothing in
front, any client can forge them and choose the host your sign-in is redirected to.
:::

## Who is an administrator

Each entry in `IMMICHFRAME_OIDC_ADMINS` is compared against both the `sub` and the `email` claim of
whoever signs in, and a match on either one grants access. `sub` is compared exactly; email is
compared case-insensitively.

Prefer `sub` on anything but a single-tenant identity provider you control. Your provider assigns a
`sub` and a user can never choose it. An email entry is refused only when the provider explicitly
reports `email_verified: false` — and most providers that let people register freely simply omit the
claim, in which case ImmichFrame cannot tell a verified address from one a stranger typed into their
own profile page. On such a provider, an email entry can be claimed by someone you did not mean to
let in.

Signing in successfully is not the same as being allowed in: someone your provider authenticates but
who is not on the list gets a "you are signed in, but not an administrator" screen and can sign out
again. Signing out clears the ImmichFrame session only — you stay signed in to your identity
provider.

## What you can edit

- Every general setting, as a list of rows. Each row is either **Overridden** — declared by the
  configuration you are editing — or shows where its value comes from instead: the default
  configuration, or ImmichFrame's built-in default.
- Immich accounts, including per-account filters. Albums, people and tags are chosen from lists read
  out of your Immich server, so you pick by name instead of pasting identifiers. There is a text
  fallback for each of them, so a picker that cannot reach Immich never stops you editing.
- Profiles: create them, delete them, and give each one its own overrides.

## What the editor will not do

**Edit a configuration that came from environment variables.** There is no settings file to write,
so the configuration is shown read-only. Create a `Settings.json` or `Settings.yml` in your
configuration directory and restart to use the editor.

**Convert an old-schema file without asking.** A file in the
[deprecated v1 schema](/docs/getting-started/configurationV1) is shown, but saving rewrites it in the
current schema and that cannot be undone from the editor, so you have to tick the conversion box
before the Save button works.

**Open a settings file that uses YAML anchors or aliases.** An alias resolves to the node the anchor
named, so the editor cannot tell a value a section shares from one it declares itself. Saving would
silently write the shared values out in full and turn everything a profile inherited into an
explicit override of its own. Rather than mangle the file, the editor refuses to load it and asks
you to write the anchored values out yourself.

**Preserve your comments.** The file is rewritten from the settings it holds, so comments in it are
lost the first time you save.

**Keep settings it does not recognise.** A key the current schema does not model — a typo, or a
setting removed in an upgrade — is not shown, and is dropped when the file is written.

**Rename a profile.** A profile's name is the URL path a frame is opened at, and the key its browser
stores that profile's authentication secret under, so a rename would log every frame on it out and
point it at a 404. You can create and delete profiles instead. Deleting asks first and names the URL
that will stop working.

## Saving

Your configuration is bound and validated before the file on disk is touched, so a configuration
that would not load is refused with a message naming what is wrong, and nothing is written. If the
file changed since the editor loaded it — you hand-edited it, or someone saved from another browser
tab — the save is refused too, and you reload and re-apply your changes.

A successful save writes a temporary file and renames it over the old one, so nothing ever reads a
half-written settings file, and the file keeps its original permissions. The previous version is
kept beside it as `Settings.yml.20250904T120000Z.bak`; the five most recent backups are kept and
older ones are removed.

The new configuration applies immediately — frames pick it up on their next request, with no
restart.

:::info The configuration directory has to be writable
ImmichFrame runs as UID 1000 inside the container and needs write access to the directory holding
your settings file, both to write the file and to keep backups beside it. A read-only mount refuses
the save with a message saying so, rather than half-applying it. See
[Docker Setup](/docs/getting-started/installation/docker).
:::

## Secrets

The weather API key, the webhook URL, the authentication secret and your Immich API keys are never
sent to the browser. Each is shown only as **Set** or **Not set**, and you either leave it alone —
in which case the stored value survives the save untouched — replace it, or clear it.

An account whose key comes from `ApiKeyFile` is shown as such; there is nothing to type, because
ImmichFrame refuses a configuration that names both a key file and a key. Clear the path if you want
to type a key instead. An account you have added but not yet saved cannot browse Immich from a key
file — ImmichFrame reads that file when the configuration is saved, so save first and then pick.

If you make a profile declare its own accounts, having inherited them until then, you have to enter
an API key for each one before saving. That profile has no stored key of its own to keep, and the
editor was never given the one it was inheriting — it only ever learns whether a key is set, never
what it is.

For the authentication secret on a profile, the editor also offers "no secret" as a distinct choice
from inheriting: it writes `AuthenticationSecret: null`, which overrides your default secret with
none, so one frame on your own network stays reachable without a prompt while the rest stay secured.
See [Authentication per profile](/docs/getting-started/configuration#authentication-per-profile) —
and note that anyone who can reach that profile's URL then sees everything it is configured to show.
