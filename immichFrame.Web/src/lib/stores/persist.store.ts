import { get, writable } from 'svelte/store';
import { profileStore } from '$lib/stores/profile.store';

/**
 * localStorage, where the browser lets the page use it. Where it does not, the stores below hold
 * their values in memory alone, and only a reload forgets them.
 *
 * Both stores touch storage while this module is being imported, and everything from the route
 * loaders to the root error page imports it. Where site data is blocked - cookies blocked in Chrome
 * or Firefox, a sandboxed iframe without allow-same-origin - merely reading the `localStorage`
 * global throws a SecurityError, which `?.` does not guard; and setItem throws a QuotaExceededError
 * where storage is full or was never granted, as in Safari's private mode before 11. Either one
 * failed the import, so not even the error page could load, and SvelteKit's static error page -
 * which never reloads - stood in for it for good.
 */
const safeStorage = {
	get(key: string): string | null {
		try {
			return localStorage.getItem(key);
		} catch {
			return null;
		}
	},
	set(key: string, value: string) {
		try {
			localStorage.setItem(key, value);
		} catch {
			// The store already holds the value. Left to escape the store's subscriber, this would
			// also leave svelte/store's notification queue - which every store on the page shares -
			// undrained, and no store would notify its subscribers of a change again.
		}
	}
};

/**
 * What is stored under `key`, or `defaultValue` when nothing is - or when what is stored is not
 * JSON. Nothing here has ever written such a value, but should one turn up, JSON.parse would throw
 * on it as the module is imported and fail the import just as blocked storage did.
 */
function readStored(key: string, defaultValue: string | null) {
	const storedValue = safeStorage.get(key);
	if (!storedValue) return defaultValue;

	try {
		return JSON.parse(storedValue);
	} catch {
		return defaultValue;
	}
}

function persistStore(key: string, defaultValue: string | null) {
	const initialValue: string = readStored(key, defaultValue);

	const store = writable(initialValue);

	store.subscribe((value) => {
		safeStorage.set(key, JSON.stringify(value));
	});

	return store;
}

function generateGUID() {
	return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function (c) {
		const r = (Math.random() * 16) | 0,
			v = c === 'x' ? r : (r & 0x3) | 0x8;
		return v.toString(16);
	});
}

/**
 * Like persistStore, but stored under a key of its own per configuration profile, because the
 * secret it holds is per-profile too: one shared slot would send the kitchen frame's secret to
 * the bedroom profile, and the auth prompt would then overwrite it in turn.
 *
 * The default profile keeps the unsuffixed key so existing installations stay authenticated.
 */
function profileKeyedPersistStore(key: string, defaultValue: string | null) {
	const keyFor = (profile: string | undefined) => (profile ? `${key}:${profile}` : key);

	const read = (storageKey: string): string => readStored(storageKey, defaultValue);

	let currentKey = keyFor(get(profileStore));

	const store = writable(read(currentKey));

	// Subscribed before the write below, so a profile change has already repointed currentKey by
	// the time the new profile's value is written back.
	profileStore.subscribe((profile) => {
		const nextKey = keyFor(profile);
		if (nextKey === currentKey) return;

		currentKey = nextKey;
		store.set(read(nextKey));
	});

	store.subscribe((value) => {
		safeStorage.set(currentKey, JSON.stringify(value));
	});

	return store;
}

export const clientIdentifierStore = persistStore('clientIdentifier', generateGUID());
export const authSecretStore = profileKeyedPersistStore('authSecret', null);
