import { error } from '@sveltejs/kit';
import * as api from '$lib/immichFrameApi';
import { configStore } from '$lib/stores/config.store.js';
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

	const configRequest = await api.getConfig({
		clientIdentifier: get(clientIdentifierStore),
		profile: get(profileStore)
	});

	// oazapfts does not throw for a status the spec does not declare - it hands the response
	// back with its status - so UnknownProfileMiddleware's 404 arrives here as a value. Only
	// 200 is declared on getConfig, hence the widening.
	const status: number = configRequest.status;
	if (status === 404) {
		error(404, `No configuration profile named '${params.config}' is configured.`);
	}

	configStore.ps(configRequest.data);
};
