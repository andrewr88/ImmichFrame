import { beforeEach, describe, expect, it, vi } from 'vitest';
import * as api from '$lib/immichFrameApi';
import { newAccount, type EditableAccount } from './admin-config';
import {
	loadPickerList,
	normalizedServerUrl,
	pickedValues,
	pickerSource,
	withChoice,
	type PickerKind,
	type PickerSource
} from './immich-picker';

// Every export replaced with a mock that answers nothing until a test says what it answers, so no
// request leaves the suite - and a list endpoint a test did not expect to be asked is asked for
// nothing at all.
vi.mock('$lib/immichFrameApi');

const IMMICH = 'https://immich.example';
const VERSION = 'sha256:loaded';

const SAVED: PickerSource = {
	kind: 'saved',
	profile: 'default',
	accountId: 'handle-default',
	version: VERSION
};
const INLINE: PickerSource = { kind: 'inline', serverUrl: IMMICH, apiKey: 'typed-key' };

beforeEach(() => {
	vi.resetAllMocks();
});

/**
 * What the generated client resolves with. Typed by oazapfts as the one status the OpenAPI
 * document declares, while carrying whatever the server answered - see `statusOf`.
 */
function reply(status: number, data: unknown): never {
	return { status, data, headers: new Headers() } as never;
}

/** One row of the accounts section, as the read left it and as it has been edited since. */
function account(edit: Partial<EditableAccount>): EditableAccount {
	return { ...newAccount(), entering: false, ...edit };
}

describe('pickerSource', () => {
	it.each<{ case: string; account: Partial<EditableAccount>; source: PickerSource }>([
		{
			case: 'a typed key and no server URL',
			account: { apiKey: 'typed-key', serverUrl: '   ' },
			source: { kind: 'blocked', reason: "Enter this account's Immich server URL first." }
		},
		{
			case: 'a typed key and a server URL',
			account: { apiKey: '  typed-key ', serverUrl: `  ${IMMICH}/ ` },
			source: { kind: 'inline', serverUrl: `${IMMICH}/`, apiKey: 'typed-key' }
		},
		{
			case: 'a typed key left behind a key file, on an account with a handle',
			account: {
				apiKey: 'typed-key',
				apiKeyFile: '/run/secrets/immich',
				ids: { default: 'handle-default' },
				serverUrl: IMMICH,
				savedServerUrl: IMMICH
			},
			source: SAVED
		},
		{
			case: 'a handle, and a server URL changed only in its trailing slash and case',
			account: {
				ids: { default: 'handle-default' },
				serverUrl: 'HTTPS://IMMICH.Example/',
				savedServerUrl: IMMICH
			},
			source: SAVED
		},
		{
			case: "a handle from a profile as well as the default configuration's",
			account: {
				ids: { kiosk: 'handle-kiosk', default: 'handle-default' },
				serverUrl: IMMICH,
				savedServerUrl: IMMICH
			},
			source: SAVED
		},
		{
			case: 'only a profiles handle',
			account: { ids: { kiosk: 'handle-kiosk' }, serverUrl: IMMICH, savedServerUrl: IMMICH },
			source: { kind: 'saved', profile: 'kiosk', accountId: 'handle-kiosk', version: VERSION }
		}
	])('$case, browses as $source.kind', ({ account: edit, source }) => {
		// Assert: a key typed into the form is the account being described, whatever is stored - but
		// not one an account reading its key from a file has left in the box behind it. Otherwise
		// the stored account, through the default configuration's handle where there is one, against
		// the version the configuration was loaded at.
		expect(pickerSource(account(edit), VERSION)).toEqual(source);
	});

	it.each<{ case: string; account: Partial<EditableAccount>; reason: string }>([
		{
			case: 'a handle, and a server URL moved to another server',
			account: {
				ids: { default: 'handle-default' },
				serverUrl: 'https://moved.example',
				savedServerUrl: IMMICH
			},
			reason: "This account's Immich server URL has been changed"
		},
		{
			case: 'a key file and no handle',
			account: { apiKeyFile: '/run/secrets/immich', apiKey: 'typed-key', serverUrl: IMMICH },
			reason: 'ImmichFrame will not read an API key file named in this editor'
		},
		{
			case: 'neither a typed key nor a handle',
			account: { serverUrl: IMMICH },
			reason: "Enter this account's Immich server URL and API key to choose from Immich."
		}
	])('$case, is blocked', ({ account: edit, reason }) => {
		// Assert: a stored key belongs to the server it was stored against, so a handle is not sent
		// once the URL names another one - it would list that server's albums under this address.
		expect(pickerSource(account(edit), VERSION)).toEqual({
			kind: 'blocked',
			reason: expect.stringContaining(reason)
		});
	});
});

describe('loadPickerList', () => {
	it('albums, keyed by id with the id-less dropped and a name for the unnamed', async () => {
		// Arrange
		vi.mocked(api.getAdminImmichAlbums).mockResolvedValue(
			reply(200, [
				{ id: 'album-1', albumName: ' Holiday ', assetCount: 12 },
				{ albumName: 'No id' },
				{ id: 'album-2', albumName: '   ' }
			])
		);

		// Act
		const result = await loadPickerList('albums', SAVED);

		// Assert: and asked with the handle alone - the proxy refuses a request naming an account
		// both ways rather than guess which one was meant.
		expect(api.getAdminImmichAlbums).toHaveBeenCalledWith({
			profile: 'default',
			accountId: 'handle-default',
			version: VERSION
		});
		expect(result).toEqual({
			ok: true,
			list: {
				items: [
					{ key: 'album-1', label: 'Holiday', detail: '12 assets' },
					{ key: 'album-2', label: 'Unnamed album', detail: '0 assets' }
				],
				total: 2,
				truncated: false
			}
		});
	});

	it('people, keyed by id, with the total and truncation the server reports', async () => {
		// Arrange
		vi.mocked(api.getAdminImmichPeople).mockResolvedValue(
			reply(200, {
				people: [
					{ id: 'person-1234567890', name: 'Mum' },
					{ name: 'No id' },
					{ id: 'person-2', name: null }
				],
				total: 250,
				truncated: true
			})
		);

		// Act
		const result = await loadPickerList('people', INLINE);

		// Assert: an inline account is named by what was typed, and nothing else.
		expect(api.getAdminImmichPeople).toHaveBeenCalledWith({
			serverUrl: IMMICH,
			apiKey: 'typed-key'
		});
		expect(result).toEqual({
			ok: true,
			list: {
				items: [
					{
						key: 'person-1234567890',
						label: 'Mum',
						detail: '#person-1',
						personId: 'person-1234567890'
					},
					{ key: 'person-2', label: 'Unnamed person', detail: '#person-2', personId: 'person-2' }
				],
				total: 250,
				truncated: true
			}
		});
	});

	it('people with no total reported, are as many as were listed and not truncated', async () => {
		// Arrange
		vi.mocked(api.getAdminImmichPeople).mockResolvedValue(
			reply(200, { people: [{ id: 'person-1' }, { id: 'person-2' }] })
		);

		// Act
		const result = await loadPickerList('people', SAVED);

		// Assert
		expect(result).toMatchObject({ ok: true, list: { total: 2, truncated: false } });
	});

	it('tags, keyed by their value rather than their id or name', async () => {
		// Arrange
		vi.mocked(api.getAdminImmichTags).mockResolvedValue(
			reply(200, [
				{ id: 'tag-1', value: 'Travel/Europe', name: 'Europe' },
				{ id: 'tag-2', value: null, name: 'No value' },
				{ id: 'tag-3', value: 'Family', name: '  ' }
			])
		);

		// Act
		const result = await loadPickerList('tags', SAVED);

		// Assert: `TagAssetsPool` keys every tag by its value, so anything else would write a
		// configuration that loads and then selects nothing.
		expect(result).toEqual({
			ok: true,
			list: {
				items: [
					{ key: 'Travel/Europe', label: 'Europe', detail: 'Travel/Europe' },
					{ key: 'Family', label: 'Family', detail: 'Family' }
				],
				total: 2,
				truncated: false
			}
		});
	});

	it.each<PickerKind>(['albums', 'people', 'tags'])(
		'a blocked source, asks for no %s',
		async (kind) => {
			// Act
			const result = await loadPickerList(kind, { kind: 'blocked', reason: 'Why not.' });

			// Assert
			expect(result).toEqual({ ok: false, message: 'Why not.' });
			expect(api.getAdminImmichAlbums).not.toHaveBeenCalled();
			expect(api.getAdminImmichPeople).not.toHaveBeenCalled();
			expect(api.getAdminImmichTags).not.toHaveBeenCalled();
		}
	);

	it.each<{ kind: PickerKind; status: number; data: unknown; message: string }>([
		{
			kind: 'albums',
			status: 401,
			data: { detail: 'Not shown.' },
			message:
				'Your administrator session has expired, so the albums could not be read. Reload the ' +
				'page and sign in again - reloading discards any edits you have not saved.'
		},
		{
			kind: 'people',
			status: 403,
			data: { detail: 'Not shown.' },
			message:
				'This account is not on the administrator allowlist, so the people could not be read.'
		},
		{
			kind: 'tags',
			status: 400,
			data: { title: 'Bad Request', detail: 'Immich rejected the API key.' },
			message: 'Immich rejected the API key.'
		},
		{
			kind: 'albums',
			status: 502,
			data: '<html>Bad Gateway</html>',
			message: 'The albums could not be read (HTTP 502).'
		},
		{
			kind: 'people',
			status: 502,
			data: { title: 'Immich could not be reached.' },
			message: 'Immich could not be reached.'
		}
	])('$kind answered with $status, says $message', async ({ kind, status, data, message }) => {
		// Arrange
		for (const list of [
			api.getAdminImmichAlbums,
			api.getAdminImmichPeople,
			api.getAdminImmichTags
		] as const) {
			vi.mocked(list).mockResolvedValue(reply(status, data));
		}

		// Act / Assert: a session or allowlist refusal in words of ours; anything the proxy wrote for
		// the operator, as it arrived; and something it never wrote, as the status.
		expect(await loadPickerList(kind, SAVED)).toEqual({ ok: false, message });
	});

	it.each<PickerKind>(['albums', 'people', 'tags'])(
		'%s whose request throws, asks whether ImmichFrame is still running',
		async (kind) => {
			// Arrange
			for (const list of [
				api.getAdminImmichAlbums,
				api.getAdminImmichPeople,
				api.getAdminImmichTags
			] as const) {
				vi.mocked(list).mockRejectedValue(new TypeError('fetch failed'));
			}

			// Act / Assert: a refusal to show rather than a throw, so the field stays editable.
			expect(await loadPickerList(kind, SAVED)).toEqual({
				ok: false,
				message: `The ${kind} could not be read. Is ImmichFrame still running?`
			});
		}
	);
});

describe('withChoice', () => {
	it.each<{ case: string; values: string[]; value: string; chosen: boolean; after: string[] }>([
		{
			case: 'ticking one, appends it',
			values: ['gone', 'album-1'],
			value: 'album-2',
			chosen: true,
			after: ['gone', 'album-1', 'album-2']
		},
		{
			case: 'unticking one, keeps every other in its order',
			values: ['album-1', 'gone', 'album-2'],
			value: 'album-1',
			chosen: false,
			after: ['gone', 'album-2']
		},
		{
			case: 'unticking one that is not there, changes nothing',
			values: ['gone', 'album-1'],
			value: 'album-2',
			chosen: false,
			after: ['gone', 'album-1']
		}
	])('$case', ({ values, value, chosen, after }) => {
		// Assert: a change to what is configured, never a rebuild from what Immich listed - so an id
		// Immich no longer lists ('gone') survives every tick.
		expect(withChoice(values, value, chosen)).toEqual(after);
	});

	it('ticking one already chosen, is no change at all', () => {
		// Arrange
		const values = ['album-1', 'album-2'];

		// Act / Assert
		expect(withChoice(values, 'album-1', true)).toBe(values);
	});
});

describe('pickedValues', () => {
	it.each<{ value: unknown; strings: string[] }>([
		{
			value: ['album-1', 2, null, 'album-2', undefined, { id: 'x' }],
			strings: ['album-1', 'album-2']
		},
		{ value: 'album-1', strings: [] },
		{ value: null, strings: [] },
		{ value: undefined, strings: [] }
	])('$value, is the strings $strings', ({ value, strings }) => {
		expect(pickedValues(value)).toEqual(strings);
	});
});

describe('normalizedServerUrl', () => {
	it.each<{ value: string | null | undefined; normalized: string }>([
		{ value: `  ${IMMICH}/  `, normalized: IMMICH },
		{ value: `${IMMICH}///`, normalized: IMMICH },
		{ value: 'HTTPS://IMMICH.Example:2283/', normalized: 'https://immich.example:2283' },
		{ value: `${IMMICH}/Immich/`, normalized: `${IMMICH}/Immich` },
		{ value: `${IMMICH}/immich`, normalized: `${IMMICH}/immich` },
		{ value: '  Immich.Example/  ', normalized: 'Immich.Example' },
		{ value: 'half typed', normalized: 'half typed' },
		{ value: null, normalized: '' },
		{ value: undefined, normalized: '' }
	])("'$value', is '$normalized'", ({ value, normalized }) => {
		// Assert: trailing slashes and surrounding space go, and scheme and host are case-folded -
		// none of them decides which server is talked to - while the path keeps its case, since a
		// proxy in front of Immich may tell /immich from /Immich. Text that is not a URL yet is
		// compared as the text it is.
		expect(normalizedServerUrl(value)).toBe(normalized);
	});
});
