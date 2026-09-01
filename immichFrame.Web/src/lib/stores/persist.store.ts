
import { get, writable } from 'svelte/store';
import { profileStore } from '$lib/stores/profile.store';

function persistStore(key: string, defaultValue: string | null) {
    const storedValue = localStorage?.getItem(key);
    const initialValue: string = storedValue ? JSON.parse(storedValue) : defaultValue;

    const store = writable(initialValue);

    store.subscribe((value) => {
        localStorage?.setItem(key, JSON.stringify(value));
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

    const read = (storageKey: string) => {
        const storedValue = localStorage?.getItem(storageKey);
        const value: string = storedValue ? JSON.parse(storedValue) : defaultValue;
        return value;
    };

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
        localStorage?.setItem(currentKey, JSON.stringify(value));
    });

    return store;
}

export const clientIdentifierStore = persistStore('clientIdentifier', generateGUID());
export const authSecretStore = profileKeyedPersistStore('authSecret', null);