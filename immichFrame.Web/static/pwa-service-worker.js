// Service worker for ImmichFrame PWA

// Auth secrets for video streaming requests, keyed by configuration profile.
// One worker controls every open tab, and two tabs may be serving different profiles with
// different secrets, so a single shared slot would sign one profile's stream requests with
// whichever secret was posted last. The default profile is keyed by the empty string:
// getAssetStreamUrl omits the parameter entirely there, so the URL carries no `profile`.
const authSecrets = new Map();
const authSecretResolvers = new Map();

self.addEventListener('install', (event) => {
    console.log('Service worker installing...');
    event.waitUntil(self.skipWaiting());
});

self.addEventListener('activate', (event) => {
    console.log('Service worker activating...');
    // This worker keeps no cache, so every one found is a leftover from an older version of it
    // ('immichframe-v1' among them).
    event.waitUntil(
        caches.keys().then((cacheNames) => {
            return Promise.all(cacheNames.map((cacheName) => caches.delete(cacheName)));
        }).then(() => self.clients.claim())
    );
});

// Listen for auth secret updates from the main app. A client only knows its own profile, so
// each reply is stored under the profile that reply names and resolves only that profile's
// waiters - a waiter for another profile keeps waiting for a client that can answer it.
//
// A reply with no secret in it is no answer at all. Every tab posts when it loads, when its
// profile changes and whenever it is asked, including one with no secret stored for its profile -
// opened, say, before the secret was entered in another tab. Stored, its null would replace the
// secret that other tab gave; resolved with, it would send a stream unsigned while that tab's
// answer was on its way.
self.addEventListener('message', (event) => {
    if (event.data && event.data.type === 'SET_AUTH_SECRET') {
        const profile = event.data.profile || '';
        const secret = event.data.authSecret;
        if (!secret) return;
        authSecrets.set(profile, secret);

        // Resolve any pending auth requests for this profile
        const resolvers = authSecretResolvers.get(profile);
        if (resolvers) {
            authSecretResolvers.delete(profile);
            resolvers.forEach(resolve => resolve(secret));
        }
    }
});

// Request a profile's auth secret from clients if not available
async function getAuthSecret(profile, timeoutMs = 2000) {
    const cached = authSecrets.get(profile);
    if (cached) return cached;

    // Request auth from all clients; each answers for the profile it is serving
    const clients = await self.clients.matchAll({ type: 'window' });
    clients.forEach(client => {
        client.postMessage({ type: 'REQUEST_AUTH_SECRET' });
    });

    return new Promise((resolve) => {
        const timeout = setTimeout(() => {
            // Remove this resolver and resolve with current value (may be undefined)
            const pending = authSecretResolvers.get(profile);
            if (pending) {
                const index = pending.indexOf(resolveAuth);
                if (index > -1) pending.splice(index, 1);
                if (pending.length === 0) authSecretResolvers.delete(profile);
            }
            resolve(authSecrets.get(profile));
        }, timeoutMs);

        const resolveAuth = (secret) => {
            clearTimeout(timeout);
            resolve(secret);
        };

        const pending = authSecretResolvers.get(profile);
        if (pending) {
            pending.push(resolveAuth);
        } else {
            authSecretResolvers.set(profile, [resolveAuth]);
        }
    });
}

self.addEventListener('fetch', (event) => {
    // A navigation is never signed, not even to a stream URL. The frame's pages fetch streams -
    // a <video> as no-cors, an image preload as cors - but never navigate to one, while any site
    // can send the browser to one with a link. Signing those would lend the secret to whoever
    // started the navigation, as a cookie would be - which is what a bearer header exists to avoid.
    if (event.request.mode === 'navigate') return;

    const url = new URL(event.request.url);

    // Intercept video streaming requests to add Authorization header.
    // The origin check is load-bearing: a service worker sees every request its
    // controlled pages make, cross-origin included, so matching on the path alone
    // would attach the auth secret to any host that happens to serve this path.
    if (url.origin === self.location.origin && url.pathname.match(/^\/api\/Asset\/[^/]+\/Asset$/)) {
        event.respondWith(
            (async () => {
                // No `profile` parameter means the default profile, which is keyed by ''.
                const profile = url.searchParams.get('profile') || '';
                const secret = await getAuthSecret(profile);
                if (!secret) {
                    // No auth available, let the request fail naturally
                    return fetch(event.request);
                }

                const headers = new Headers();
                for (const [key, value] of event.request.headers) {
                    headers.set(key, value);
                }
                headers.set('Authorization', 'Bearer ' + secret);

                const modifiedRequest = new Request(url.href, {
                    method: event.request.method,
                    headers: headers,
                    mode: 'cors',
                    credentials: 'same-origin'
                });

                return fetch(modifiedRequest);
            })()
        );
        return;
    }

    // Every other request is left to the browser: with no respondWith it goes straight to the
    // network. There is no cache to fall back on offline, so proxying it would only put every
    // request the page makes - cross-origin and admin ones included - through the worker.
});