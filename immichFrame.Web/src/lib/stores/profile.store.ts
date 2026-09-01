import { writable } from 'svelte/store';

/**
 * The configuration profile the current page is serving, taken from the `[config]` segment of
 * the URL. `undefined` means the default profile.
 *
 * Deliberately a plain `writable` and not a `persistStore`: the profile comes from the URL on
 * every visit, so persisting it would make a later visit to `/` serve whichever profile was
 * opened last.
 */
export const profileStore = writable<string | undefined>(undefined);
