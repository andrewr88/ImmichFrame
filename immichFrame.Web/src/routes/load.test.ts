import { get } from 'svelte/store';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ClientSettingsDto } from '$lib/immichFrameApi';
import { configStore } from '$lib/stores/config.store';
import { load as loadDefault } from './+page';
import { load as loadProfile } from './[config]/+page';

// persist.store reads localStorage the moment it is imported, and node has none. The loaders take
// nothing else from it.
vi.mock('$lib/stores/persist.store', async () => {
	const { writable } = await import('svelte/store');
	return { clientIdentifierStore: writable('frame') };
});

/** What the frame is already running on, which a failed load has to leave exactly as it was. */
const RUNNING: ClientSettingsDto = { interval: 45, layout: 'splitview' };

const fetchMock = vi.fn<typeof fetch>();

beforeEach(() => {
	// fetch is stubbed rather than getConfig mocked, so the generated client and its runtime are
	// under test as well: what they make of a body that is not JSON is the whole of the bug.
	vi.stubGlobal('fetch', fetchMock);
	configStore.ps(RUNNING);
});

afterEach(() => {
	vi.unstubAllGlobals();
	fetchMock.mockReset();
});

function answer(status: number, body: string | null = null, contentType?: string) {
	fetchMock.mockImplementation(
		async () =>
			new Response(body, { status, headers: contentType ? { 'Content-Type': contentType } : {} })
	);
}

const openDefault = () =>
	loadDefault({
		url: new URL('http://frame.local/'),
		params: {}
	} as unknown as Parameters<typeof loadDefault>[0]);

const openProfile = (profile: string) =>
	loadProfile({
		url: new URL(`http://frame.local/${profile}`),
		params: { config: profile }
	} as unknown as Parameters<typeof loadProfile>[0]);

describe.each([
	{ route: '/', open: openDefault, request: '/api/Config?clientIdentifier=frame' },
	{
		route: '/[config]',
		open: () => openProfile('kitchen'),
		request: '/api/Config?clientIdentifier=frame&profile=kitchen'
	}
])('load on $route', ({ open, request }) => {
	it("a reverse proxy's 502 page, fails the load with that status and leaves configStore alone", async () => {
		// Arrange: what a proxy answers while the backend restarts behind it. The generated client
		// hands the page back as the response's data rather than throwing.
		answer(502, '<html>Bad Gateway</html>', 'text/html');

		// Act / Assert
		await expect(open()).rejects.toMatchObject({ status: 502 });
		expect(get(configStore)).toBe(RUNNING);
	});

	it('a 500 with no body, fails the load with that status and leaves configStore alone', async () => {
		// Arrange: no body and no content type, which the generated client reads as ''.
		answer(500);

		// Act / Assert
		await expect(open()).rejects.toMatchObject({ status: 500 });
		expect(get(configStore)).toBe(RUNNING);
	});

	it('a 204, is no configuration either, and fails the load as a 500 that still names it', async () => {
		// Arrange: a status error() refuses, so passed straight through it would have failed the
		// load on SvelteKit's complaint about the status rather than with a message of ours.
		answer(204);

		// Act / Assert
		await expect(open()).rejects.toMatchObject({
			status: 500,
			body: { message: expect.stringContaining('HTTP 204') }
		});
		expect(get(configStore)).toBe(RUNNING);
	});

	it('a 200, populates configStore from the request for its own profile', async () => {
		// Arrange
		answer(200, JSON.stringify({ interval: 7 }), 'application/json; charset=utf-8');

		// Act
		await open();

		// Assert
		expect(get(configStore)).toEqual({ interval: 7 });
		expect(fetchMock).toHaveBeenCalledWith(request, expect.anything());
	});
});

describe('load: a 404', () => {
	it('for a profile, keeps the message that names the profile', async () => {
		// Arrange: UnknownProfileMiddleware's answer.
		answer(404, 'The requested configuration profile is not configured.', 'text/plain');

		// Act / Assert
		await expect(openProfile('kitchen')).rejects.toMatchObject({
			status: 404,
			body: { message: "No configuration profile named 'kitchen' is configured." }
		});
		expect(get(configStore)).toBe(RUNNING);
	});

	it('for the default configuration, is not put down to a profile it never asked for', async () => {
		// Arrange: no profile goes out, so this cannot be UnknownProfileMiddleware's; it is some
		// proxy's that has lost its backend.
		answer(404, '404 page not found', 'text/plain');

		// Act / Assert
		await expect(openDefault()).rejects.toMatchObject({
			status: 404,
			body: { message: expect.not.stringContaining('profile') }
		});
		expect(get(configStore)).toBe(RUNNING);
	});
});
