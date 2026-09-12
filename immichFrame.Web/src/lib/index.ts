// place files you want to import through the `$lib` alias in this folder.
import { defaults, type AssetTypeEnum } from './immichFrameApi.js';
import { authSecretStore } from '$lib/stores/persist.store';
import { profileStore } from '$lib/stores/profile.store';
import { get } from 'svelte/store';

export * from './immichFrameApi.js';

let isAuthListenerRegistered = false;
let isProfileListenerRegistered = false;

export const init = () => {
	setBearer();
	sendAuthSecretToServiceWorker();
	followProfileChanges();
};

/**
 * Both the bearer header and the secret cached by the service worker are per-profile, and
 * neither is re-derived by a client-side navigation that keeps this module alive. Re-run
 * init's work on every profile change instead of relying on the page being remounted.
 *
 * `authSecretStore` subscribes to `profileStore` when it is imported, which is before this
 * runs, so by the time this handler fires the store has already been repointed at the new
 * profile's secret.
 */
const followProfileChanges = () => {
	if (isProfileListenerRegistered) return;
	isProfileListenerRegistered = true;

	// subscribe() fires synchronously with the current value; init has just done that work.
	let currentProfile = get(profileStore);
	profileStore.subscribe((profile) => {
		if (profile === currentProfile) return;
		currentProfile = profile;

		setBearer();
		sendAuthSecretToServiceWorker();
	});
};

const sendMessage = () => {
	if (navigator.serviceWorker.controller) {
		navigator.serviceWorker.controller.postMessage({
			type: 'SET_AUTH_SECRET',
			// Read at call time: the worker is shared by every tab, so it keys the secret by the
			// profile of whichever client answered.
			profile: get(profileStore),
			authSecret: get(authSecretStore)
		});
	}
};

export const sendAuthSecretToServiceWorker = () => {
	if (typeof navigator === 'undefined' || !('serviceWorker' in navigator)) return;

	// Send immediately if controller is ready
	sendMessage();

	// Also send when service worker becomes ready (for initial page load)
	navigator.serviceWorker.ready.then(sendMessage);

	// Listen for auth secret requests from service worker (register only once)
	if (!isAuthListenerRegistered) {
		isAuthListenerRegistered = true;
		navigator.serviceWorker.addEventListener('message', (event) => {
			if (event.data && event.data.type === 'REQUEST_AUTH_SECRET') {
				sendMessage();
			}
		});
	}
};

export const getBaseUrl = () => defaults.baseUrl;

export const setBaseUrl = (baseUrl: string) => {
	defaults.baseUrl = baseUrl;
};

export const setBearer = () => {
	defaults.headers = defaults.headers || {};
	defaults.headers['Authorization'] = 'Bearer ' + get(authSecretStore);
};

export const getAssetStreamUrl = (
	id: string,
	clientIdentifier?: string,
	assetType?: AssetTypeEnum,
	profile?: string
) => {
	const params = new URLSearchParams();
	if (clientIdentifier) params.set('clientIdentifier', clientIdentifier);
	if (profile) params.set('profile', profile);
	if (assetType !== undefined) params.set('assetType', String(assetType));
	const query = params.toString();
	return `/api/Asset/${encodeURIComponent(id)}/Asset${query ? '?' + query : ''}`;
};
