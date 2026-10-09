import { afterEach, describe, expect, it, vi } from 'vitest';
import { AssetTypeEnum, getAssetStreamUrl } from './index';

afterEach(() => {
	vi.unstubAllGlobals();
});

/** A localStorage with a secret for each profile, stored as persist.store stores them. */
function installSecrets(secrets: Record<string, string>) {
	const items = new Map(
		Object.entries(secrets).map(([key, secret]) => [key, JSON.stringify(secret)])
	);
	vi.stubGlobal('localStorage', {
		getItem: (key: string) => items.get(key) ?? null,
		setItem: (key: string, value: string) => {
			items.set(key, value);
		}
	});
}

/** A page the service worker already controls; what it posts to the worker is returned. */
function installServiceWorker() {
	const postMessage = vi.fn();
	vi.stubGlobal('navigator', {
		serviceWorker: {
			controller: { postMessage },
			// Settled, it would post the same message once more. Never settling, it leaves every
			// message a test sees one that the call under test posted.
			ready: new Promise(() => {}),
			addEventListener: () => {}
		}
	});
	return postMessage;
}

/**
 * index.ts and the stores it reads, loaded afresh over the globals just installed.
 *
 * What init sets up lasts as long as the page - its subscription to the profile, and the stores
 * holding the profile and its secret - so a module shared between tests would carry one test's
 * page into the next. Reset first and then imported, the two come out of one new module graph, and
 * the generated client's `defaults` that init writes the header into is that graph's too.
 */
async function loadPage() {
	vi.resetModules();

	const api = await import('./index');
	const { profileStore } = await import('./stores/profile.store');

	return { api, profileStore };
}

describe('init', () => {
	it('sets the bearer header, and posts the secret to the worker, for the profile being served', async () => {
		// Arrange: the [config] route sets the profile before the page's components call init.
		installSecrets({ authSecret: 'default-secret', 'authSecret:kitchen': 's3cret' });
		const postMessage = installServiceWorker();
		const { api, profileStore } = await loadPage();
		profileStore.set('kitchen');

		// Act
		api.init();

		// Assert
		expect(api.defaults.headers?.Authorization).toBe('Bearer s3cret');
		expect(postMessage).toHaveBeenCalledExactlyOnceWith({
			type: 'SET_AUTH_SECRET',
			profile: 'kitchen',
			authSecret: 's3cret'
		});
	});

	it("then a profile change, sets the header and posts the secret again, the new profile's", async () => {
		// Arrange: a client-side navigation to another profile keeps this module, and what init
		// did, alive.
		installSecrets({ authSecret: 'default-secret', 'authSecret:kitchen': 's3cret' });
		const postMessage = installServiceWorker();
		const { api, profileStore } = await loadPage();
		api.init();
		postMessage.mockClear();

		// Act
		profileStore.set('kitchen');

		// Assert
		expect(api.defaults.headers?.Authorization).toBe('Bearer s3cret');
		expect(postMessage).toHaveBeenCalledExactlyOnceWith({
			type: 'SET_AUTH_SECRET',
			profile: 'kitchen',
			authSecret: 's3cret'
		});
	});
});

describe('getAssetStreamUrl', () => {
	it.each([
		{
			profile: 'kitchen',
			url: '/api/Asset/a%2Fb/Asset?clientIdentifier=frame&profile=kitchen&assetType=video'
		},
		// The worker keys the default profile '', and finds it under a URL with no `profile`.
		{ profile: undefined, url: '/api/Asset/a%2Fb/Asset?clientIdentifier=frame&assetType=video' }
	])("for the profile '$profile', is the URL the worker signs for it", ({ profile, url }) => {
		// Act: an id with a '/' in it, which unencoded would move the request to another path - one
		// that is neither the endpoint nor one the worker signs.
		const streamUrl = getAssetStreamUrl('a/b', 'frame', AssetTypeEnum.Video, profile);

		// Assert
		expect(streamUrl).toBe(url);
	});
});
