import { afterEach, describe, expect, it, vi, type Mock } from 'vitest';
import { AssetTypeEnum, getAssetStreamUrl } from '$lib/index';
import workerSource from '../static/pwa-service-worker.js?raw';

// The worker is a classic script that static/ serves as it is, so there is nothing in it to import.
// It is run from its source instead, as the body of a function whose parameters are the two globals
// it reaches for - `self` and `fetch` - and so stand in for them wherever it does. Each start is a
// worker with no secret cached, as after the browser restarts it. A `node:vm` context would isolate
// it as well, but there are no Node type definitions here for svelte-check to check this file with.
//
// The suite only finds tests under src/, and this one cannot sit next to the worker: everything in
// static/ is copied into the build and served.

const ORIGIN = 'https://frame.local';

/** A request a page makes, as far as the worker reads it. */
type PageRequest = {
	url: string;
	mode: RequestMode;
	destination: RequestDestination;
	headers?: HeadersInit;
};

function startWorker() {
	const listeners = new Map<string, (event: object) => void>();
	const dispatch = (type: string, event: object) => {
		const listener = listeners.get(type);
		// Rather than pass vacuously, a test that expects no answer fails if nothing could answer.
		if (!listener) throw new Error(`The worker listens for no '${type}' event.`);
		listener(event);
	};

	// One open tab. It answers the worker's request for its secret only when the test answers for it.
	let onAsked!: () => void;
	const askedForSecret = new Promise<void>((resolve) => (onAsked = resolve));
	const client = { postMessage: vi.fn(() => onAsked()) };

	const fetch = vi.fn<(request: Request) => Promise<Response>>(async () => new Response());
	const self = {
		location: new URL(ORIGIN),
		clients: { matchAll: async () => [client] },
		addEventListener: (type: string, listener: (event: object) => void) => {
			listeners.set(type, listener);
		}
	};

	new Function('self', 'fetch', workerSource)(self, fetch);

	return {
		client,
		/** Settles once the worker has asked the tab for its secret, and waits on the answer. */
		askedForSecret,
		fetch,
		/** Posts to the worker what a tab posts. */
		message(data: object) {
			dispatch('message', { data });
		},
		/** Makes a request from the page, which the worker answers through respondWith or not at all. */
		request(page: PageRequest) {
			// A plain object rather than a Request: Node's refuses mode 'navigate', and its destination
			// is always ''.
			const request = {
				method: 'GET',
				...page,
				url: new URL(page.url, ORIGIN).href,
				headers: new Headers(page.headers)
			};
			const respondWith = vi.fn<(response: Promise<Response>) => void>();
			dispatch('fetch', { request, respondWith });
			return { request, respondWith };
		}
	};
}

/** What the worker answered a request with; the test fails if it left the request alone. */
async function answered(respondWith: Mock<(response: Promise<Response>) => void>) {
	expect(respondWith).toHaveBeenCalledOnce();
	return respondWith.mock.calls[0][0];
}

/** The one request the worker sent on. */
function sent(worker: ReturnType<typeof startWorker>) {
	expect(worker.fetch).toHaveBeenCalledOnce();
	return worker.fetch.mock.calls[0][0];
}

/** The kitchen profile's stream for a video, from the URL home-page.svelte gives its <video>. */
const KITCHEN_STREAM: PageRequest = {
	url: getAssetStreamUrl('a/b', 'frame', AssetTypeEnum.Video, 'kitchen'),
	mode: 'no-cors',
	destination: 'video',
	headers: { Range: 'bytes=0-' }
};

afterEach(() => {
	vi.useRealTimers();
});

describe('pwa-service-worker', () => {
	it.each<{ request: string; page: PageRequest; secret: string }>([
		{
			// The <video> asks for the URL itself, as no-cors.
			request: "a <video>'s stream request for a profile",
			page: KITCHEN_STREAM,
			secret: 'kitchen-secret'
		},
		{
			// The default profile has no `profile` in the URL, and is keyed '' in the worker.
			request: "a <video>'s stream request for the default profile",
			page: { ...KITCHEN_STREAM, url: getAssetStreamUrl('a/b', 'frame', AssetTypeEnum.Video) },
			secret: 'default-secret'
		},
		{
			// An image is preloaded through the generated client's getAsset instead: a fetch(), so cors,
			// and with no destination.
			request: 'an image preload for a profile',
			page: {
				url: '/api/Asset/a%2Fb/Asset?clientIdentifier=frame&profile=kitchen&assetType=image',
				mode: 'cors',
				destination: ''
			},
			secret: 'kitchen-secret'
		}
	])(
		"$request, goes out with that profile's secret and its own headers",
		async ({ page, secret }) => {
			// Arrange: a secret for each profile, as tabs serving the two have posted.
			const worker = startWorker();
			worker.message({ type: 'SET_AUTH_SECRET', profile: undefined, authSecret: 'default-secret' });
			worker.message({ type: 'SET_AUTH_SECRET', profile: 'kitchen', authSecret: 'kitchen-secret' });

			// Act
			const { request, respondWith } = worker.request(page);
			await answered(respondWith);

			// Assert
			const signed = sent(worker);
			expect(signed.url).toBe(request.url);
			expect(signed.headers.get('Authorization')).toBe(`Bearer ${secret}`);
			expect(signed.headers.get('Range')).toBe(request.headers.get('Range'));
		}
	);

	it.each<{ request: string; page: PageRequest }>([
		{
			// The user opening a stream URL, or any other site linking to one: no page of the frame's
			// navigates to one, so signing it would only lend the secret to whoever started it.
			request: 'a navigation to a stream URL',
			page: { url: KITCHEN_STREAM.url, mode: 'navigate', destination: 'document' }
		},
		{
			// The worker sees a controlled page's cross-origin requests as well.
			request: 'a request on the stream path to another origin',
			page: {
				...KITCHEN_STREAM,
				url: new URL(KITCHEN_STREAM.url, 'https://elsewhere.example').href
			}
		}
	])('$request, is left to the browser, unsigned', ({ page }) => {
		// Arrange: a secret cached for the profile the URL names.
		const worker = startWorker();
		worker.message({ type: 'SET_AUTH_SECRET', profile: 'kitchen', authSecret: 's3cret' });

		// Act
		const { respondWith } = worker.request(page);

		// Assert
		expect(respondWith).not.toHaveBeenCalled();
	});

	it('a stream request no tab has a secret for, asks the tabs, and when none answers goes out as it came', async () => {
		// Arrange
		vi.useFakeTimers();
		const worker = startWorker();

		// Act: the worker waits two seconds for an answer.
		const { request, respondWith } = worker.request(KITCHEN_STREAM);
		const response = answered(respondWith);
		await vi.advanceTimersByTimeAsync(2000);
		await response;

		// Assert: unsigned, so it fails at the server as it would with no worker in the way.
		expect(worker.client.postMessage).toHaveBeenCalledWith({ type: 'REQUEST_AUTH_SECRET' });
		expect(sent(worker)).toBe(request);
	});
});

// Every tab posts its secret when it loads, when its profile changes and when the worker asks for
// it: a tab with none stored for its profile as much as any other, and its post has null for the
// secret. It can be a tab opened before the secret was entered in another, as a tab reads its
// secret from storage only as it loads, or as its profile changes.
describe('pwa-service-worker, a post from a tab with no secret', () => {
	it('answering first, leaves the stream waiting on the tab with the secret', async () => {
		// Arrange
		const worker = startWorker();
		const { respondWith } = worker.request(KITCHEN_STREAM);
		await worker.askedForSecret;

		// Act
		worker.message({ type: 'SET_AUTH_SECRET', profile: 'kitchen', authSecret: null });
		worker.message({ type: 'SET_AUTH_SECRET', profile: 'kitchen', authSecret: 's3cret' });
		await answered(respondWith);

		// Assert
		expect(sent(worker).headers.get('Authorization')).toBe('Bearer s3cret');
	});

	it('as it loads, leaves the secret another tab gave cached', async () => {
		// Arrange
		const worker = startWorker();
		worker.message({ type: 'SET_AUTH_SECRET', profile: 'kitchen', authSecret: 's3cret' });

		// Act
		worker.message({ type: 'SET_AUTH_SECRET', profile: 'kitchen', authSecret: null });
		const { respondWith } = worker.request(KITCHEN_STREAM);
		await answered(respondWith);

		// Assert: signed from the cache, without asking the tabs.
		expect(worker.client.postMessage).not.toHaveBeenCalled();
		expect(sent(worker).headers.get('Authorization')).toBe('Bearer s3cret');
	});
});
