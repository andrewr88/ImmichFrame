// Service worker for ImmichFrame PWA
const CACHE_NAME = 'immichframe-v1';

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
    event.waitUntil(
        caches.keys().then((cacheNames) => {
            return Promise.all(
                cacheNames.map((cacheName) => {
                    if (cacheName !== CACHE_NAME) {
                        return caches.delete(cacheName);
                    }
                })
            );
        }).then(() => self.clients.claim())
    );
});

// Listen for auth secret updates from the main app. A client only knows its own profile, so
// each reply is stored under the profile that reply names and resolves only that profile's
// waiters - a waiter for another profile keeps waiting for a client that can answer it.
self.addEventListener('message', (event) => {
    if (event.data && event.data.type === 'SET_AUTH_SECRET') {
        const profile = event.data.profile || '';
        const secret = event.data.authSecret;
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

    // Basic network-first strategy for other requests
    event.respondWith(
        fetch(event.request).catch(() => caches.match(event.request))
    );
});