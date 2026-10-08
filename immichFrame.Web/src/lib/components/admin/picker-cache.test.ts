import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { PickerResult, PickerSource } from './immich-picker';

// Every export replaced with a mock that answers nothing until a test says what it answers, so no
// request leaves the suite.
vi.mock('$lib/immichFrameApi');

const SAVED: PickerSource = {
	kind: 'saved',
	profile: 'default',
	accountId: 'handle-default',
	version: 'sha256:loaded'
};
const BLOCKED: PickerSource = {
	kind: 'blocked',
	reason: "Enter this account's Immich server URL."
};

/** What the generated client resolves with; see `immich-picker.test.ts`. */
function reply(status: number, data: unknown): never {
	return { status, data, headers: new Headers() } as never;
}

/** A read the test settles when it chooses, so that what is asked while it is in the air shows. */
function inFlight() {
	let settle!: (value: never) => void;
	const answer = new Promise<never>((resolve) => (settle = resolve));

	return { answer, settle };
}

/**
 * The cache and the client it reads through, both loaded afresh.
 *
 * The cache is a map the module holds for the life of the page, so a module shared between tests
 * would carry one test's reads into the next. Reset first and then imported, the two come out of
 * one new module graph - so the mock asked here is the very one the new cache reads through.
 */
async function freshCache() {
	vi.resetModules();

	const api = vi.mocked(await import('$lib/immichFrameApi'));
	const cache = await import('./picker-cache');

	return { api, cache };
}

const ALBUMS: PickerResult = {
	ok: true,
	list: {
		items: [{ key: 'album-1', label: 'Holiday', detail: '3 assets' }],
		total: 1,
		truncated: false
	}
};

beforeEach(() => {
	vi.resetAllMocks();
});

describe('pickerList', () => {
	it('two asks for one list while it is being read, share the one read', async () => {
		// Arrange
		const { api, cache } = await freshCache();
		const read = inFlight();

		api.getAdminImmichAlbums.mockReturnValue(read.answer);

		// Act: the albums and excluded albums fields of one account, asking at once - the second
		// with credentials that are equal rather than the same object.
		const first = cache.pickerList('albums', SAVED);
		const second = cache.pickerList('albums', { ...SAVED });

		read.settle(reply(200, [{ id: 'album-1', albumName: 'Holiday', assetCount: 3 }]));

		// Assert
		expect(second).toBe(first);
		expect(await first).toEqual(ALBUMS);
		expect(api.getAdminImmichAlbums).toHaveBeenCalledTimes(1);
	});

	it('another list, or the same list at another version, is a read of its own', async () => {
		// Arrange
		const { api, cache } = await freshCache();

		api.getAdminImmichAlbums.mockResolvedValue(reply(200, []));
		api.getAdminImmichTags.mockResolvedValue(reply(200, []));

		// Act
		await cache.pickerList('albums', SAVED);
		await cache.pickerList('tags', SAVED);
		await cache.pickerList('albums', { ...SAVED, version: 'sha256:saved-since' });
		await cache.pickerList('albums', SAVED);

		// Assert: the version is part of what is being asked about - a handle resolves against the
		// configuration that version names - so a list read at one is never handed back for another.
		expect(api.getAdminImmichAlbums).toHaveBeenCalledTimes(2);
		expect(api.getAdminImmichTags).toHaveBeenCalledTimes(1);
	});

	it('a blocked source, is answered with its reason and never stored', async () => {
		// Arrange
		const { api, cache } = await freshCache();

		// Act
		const first = cache.pickerList('albums', BLOCKED);
		const second = cache.pickerList('albums', BLOCKED);

		// Assert: the reason is a statement about the half-typed form rather than about a server,
		// and there is no server to ask.
		expect(await first).toEqual({ ok: false, message: BLOCKED.reason });
		expect(second).not.toBe(first);
		expect(cache.cachedPickerList('albums', BLOCKED)).toBeUndefined();
		expect(api.getAdminImmichAlbums).not.toHaveBeenCalled();
	});

	it('a refusal, is kept like an answer and not asked for again', async () => {
		// Arrange
		const { api, cache } = await freshCache();

		api.getAdminImmichAlbums.mockResolvedValue(reply(502, { detail: 'Immich is down.' }));

		// Act
		const first = await cache.pickerList('albums', SAVED);
		const second = await cache.pickerList('albums', SAVED);

		// Assert: a server that is down would otherwise be asked again by every picker on the page,
		// each waiting out its own timeout to be told the same thing.
		expect(first).toEqual({ ok: false, message: 'Immich is down.' });
		expect(second).toEqual(first);
		expect(cache.cachedPickerList('albums', SAVED)).toEqual(first);
		expect(api.getAdminImmichAlbums).toHaveBeenCalledTimes(1);
	});
});

describe('cachedPickerList', () => {
	it('is nothing until the read settles, and then what it read', async () => {
		// Arrange
		const { api, cache } = await freshCache();
		const read = inFlight();

		api.getAdminImmichAlbums.mockReturnValue(read.answer);

		// Act
		const pending = cache.pickerList('albums', SAVED);

		// Assert: asked for nothing it does not have, and never a promise.
		expect(cache.cachedPickerList('albums', SAVED)).toBeUndefined();

		// Act
		read.settle(reply(200, [{ id: 'album-1', albumName: 'Holiday', assetCount: 3 }]));
		await pending;

		// Assert
		expect(cache.cachedPickerList('albums', SAVED)).toEqual(ALBUMS);
		expect(cache.cachedPickerList('tags', SAVED)).toBeUndefined();
		expect(api.getAdminImmichAlbums).toHaveBeenCalledTimes(1);
	});
});

describe('dropPickerList', () => {
	it('forgets one list, so the next ask reads Immich again', async () => {
		// Arrange
		const { api, cache } = await freshCache();

		api.getAdminImmichAlbums.mockResolvedValue(reply(200, []));
		api.getAdminImmichTags.mockResolvedValue(reply(200, []));

		await cache.pickerList('albums', SAVED);
		await cache.pickerList('tags', SAVED);

		// Act: what a picker's Reload does.
		cache.dropPickerList('albums', SAVED);
		cache.dropPickerList('albums', BLOCKED);

		// Assert: that list alone is gone.
		expect(cache.cachedPickerList('albums', SAVED)).toBeUndefined();
		expect(cache.cachedPickerList('tags', SAVED)).toBeDefined();

		// Act
		await cache.pickerList('albums', SAVED);
		await cache.pickerList('tags', SAVED);

		// Assert
		expect(api.getAdminImmichAlbums).toHaveBeenCalledTimes(2);
		expect(api.getAdminImmichTags).toHaveBeenCalledTimes(1);
	});
});
