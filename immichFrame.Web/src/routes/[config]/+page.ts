import { loadConfig } from '$lib/load-config';
import { clientIdentifierStore } from '$lib/stores/persist.store';
import { profileStore } from '$lib/stores/profile.store';
import { get } from 'svelte/store';
import type { PageLoad } from './$types';

// adapter-static cannot prerender a dynamic route without a list of entries, and the profiles
// are only known to the backend. The SPA fallback (`fallback: 'index.html'`, and
// MapFallbackToFile on the API) is what serves /<profile>.
export const prerender = false;

export const load: PageLoad = async ({ params, url }) => {
	profileStore.set(params.config);

	const clientParam = url.searchParams.get('client');
	if (clientParam) {
		clientIdentifierStore.set(clientParam);
	}

	await loadConfig({
		clientIdentifier: get(clientIdentifierStore),
		profile: get(profileStore)
	});
};
