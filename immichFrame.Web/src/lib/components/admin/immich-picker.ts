import * as api from '$lib/immichFrameApi';
import { problemDetail, statusOf, usesApiKeyFile, type EditableAccount } from './admin-config';

/** Which of the three list endpoints a picker reads. */
export type PickerKind = 'albums' | 'people' | 'tags';

/** What one entry of each list is called, for wording that has to name it. */
export const pickerNouns: Record<PickerKind, { one: string; many: string }> = {
	albums: { one: 'album', many: 'albums' },
	people: { one: 'person', many: 'people' },
	tags: { one: 'tag', many: 'tags' }
};

/** One row of a picker: what it draws, and the one string it would put in the configuration. */
export interface PickerItem {
	/**
	 * Exactly what the settings file holds for this entry. Albums and people carry the Immich id;
	 * a tag carries its `value`, never its id or its name - `TagAssetsPool` keys every tag by
	 * `TagResponseDto.Value`, so anything else writes a configuration that loads, validates and
	 * then selects no assets at all.
	 */
	key: string;
	label: string;
	detail?: string;
	/** The person id the thumbnail endpoint takes. Absent for albums and tags. */
	personId?: string;
}

export interface PickerList {
	items: PickerItem[];
	/** How many entries the server says it has, whether or not they were all read. */
	total: number;
	/** Whether the server stopped short of that total. */
	truncated: boolean;
}

/**
 * Which credentials a picker asks with, decided by the account being edited rather than by the
 * form as a whole.
 */
export type PickerSource =
	| { kind: 'saved'; profile: string; accountId: string; version: string }
	| { kind: 'inline'; serverUrl: string; apiKey: string }
	/** Neither set of credentials describes the account on screen. `reason` says which. */
	| { kind: 'blocked'; reason: string };

export type PickerResult = { ok: true; list: PickerList } | { ok: false; message: string };

const URL_CHANGED =
	"This account's Immich server URL has been changed, but its API key is still the stored one, " +
	'which belongs to the server that was there before. Browsing would list that server under the ' +
	'new address. Save the configuration first, or enter the API key for the new server.';

const KEY_FROM_FILE =
	"This account's API key is read from the file named above, and ImmichFrame reads that file when " +
	'the configuration is saved. Save it first, then choose from Immich.';

/**
 * A server URL reduced to what actually decides which server is talked to, so that a rewrite which
 * changes nothing is not read as a change of server.
 *
 * `AdminImmichAccounts.TryDescribe` trims and drops a trailing slash before connecting, and a URL
 * pasted out of a browser address bar always carries that slash; scheme and host are
 * case-insensitive by definition. The path is left exactly as typed, because a reverse proxy in
 * front of Immich is free to distinguish /immich from /Immich.
 */
function normalizedServerUrl(value: string | null | undefined): string {
	const trimmed = (value ?? '').trim().replace(/\/+$/, '');

	try {
		const url = new URL(trimmed);

		return `${url.protocol.toLowerCase()}//${url.host.toLowerCase()}${url.pathname.replace(/\/+$/, '')}${url.search}${url.hash}`;
	} catch {
		// Not a URL at all yet - half typed, or missing its scheme. Compared as the text it is; the
		// proxy is what refuses it, with a message about the URL rather than about the key.
		return trimmed;
	}
}

/**
 * The account this picker is for, as credentials the proxy can resolve.
 *
 * The cases are pinned rather than inferred, because the wrong one browses the wrong server and the
 * administrator then saves that server's identifiers onto this account:
 *
 * - A key typed into the form is the account being described, whatever is stored. Inline.
 * - No typed key and a handle from the load: the stored account. Saved - and the handle resolves
 *   against the configuration *on disk*, which is why the version is the loaded one even when there
 *   are unsaved edits.
 * - No typed key and no handle - a new account, or one whose key is read from a file that has not
 *   been saved yet. There is nothing to browse with.
 *
 * The sharp case is the second rule: a stored key belongs to the URL it was stored against, so once
 * that URL is edited the saved handle would list the *old* server's albums under the new address.
 * Refused rather than answered.
 */
export function pickerSource(
	account: EditableAccount,
	profile: string,
	version: string
): PickerSource {
	const serverUrl = account.values.immichServerUrl?.trim() ?? '';
	// An account naming an ApiKeyFile has no key in the form - the server reads it from that path -
	// so whatever is left in the input behind it is not this account's credential.
	const typedKey = usesApiKeyFile(account) ? '' : account.apiKey.trim();

	if (typedKey) {
		if (!serverUrl) {
			return { kind: 'blocked', reason: "Enter this account's Immich server URL first." };
		}

		return { kind: 'inline', serverUrl, apiKey: typedKey };
	}

	if (account.id) {
		if (normalizedServerUrl(serverUrl) !== normalizedServerUrl(account.savedServerUrl)) {
			return { kind: 'blocked', reason: URL_CHANGED };
		}

		return { kind: 'saved', profile, accountId: account.id, version };
	}

	return {
		kind: 'blocked',
		reason: usesApiKeyFile(account)
			? KEY_FROM_FILE
			: "Enter this account's Immich server URL and API key to choose from Immich."
	};
}

/**
 * A value that changes whenever a picker would be asking a different server, or asking it as
 * somebody else. Results are dropped when it changes, so a list read from one server can never be
 * shown as the contents of another.
 */
export function sourceKey(source: PickerSource): string {
	if (source.kind === 'saved') {
		return `saved\n${source.profile}\n${source.accountId}\n${source.version}`;
	}

	if (source.kind === 'inline') {
		return `inline\n${source.serverUrl}\n${source.apiKey}`;
	}

	return `blocked\n${source.reason}`;
}

/**
 * Where a person's face comes from. Saved accounts only: an `<img src>` cannot POST, so there is no
 * way to hand the endpoint a key that is still being typed. Built by hand rather than through the
 * generated client, which fetches the bytes as a blob - `getAssetStreamUrl` does the same for the
 * same reason.
 */
export function personThumbnailUrl(source: PickerSource, personId: string): string | null {
	if (source.kind !== 'saved') return null;

	const query = new URLSearchParams({
		accountId: source.accountId,
		version: source.version,
		profile: source.profile
	});

	return `/api/admin/immich/people/${encodeURIComponent(personId)}/thumbnail?${query}`;
}

function accountRef(source: PickerSource): api.AdminImmichAccountRefDto {
	// Exactly one of the two ways of naming an account: the proxy refuses a request carrying both
	// rather than guessing which one the editor meant.
	return source.kind === 'saved'
		? { profile: source.profile, accountId: source.accountId, version: source.version }
		: source.kind === 'inline'
			? { serverUrl: source.serverUrl, apiKey: source.apiKey }
			: {};
}

function albumItems(albums: api.AdminImmichAlbumDto[]): PickerItem[] {
	return albums
		.filter((album): album is api.AdminImmichAlbumDto & { id: string } => !!album.id)
		.map((album) => ({
			key: album.id,
			label: album.albumName?.trim() || 'Unnamed album',
			detail: `${album.assetCount ?? 0} assets`
		}));
}

function personItems(people: api.AdminImmichPersonDto[]): PickerItem[] {
	return people
		.filter((person): person is api.AdminImmichPersonDto & { id: string } => !!person.id)
		.map((person) => ({
			key: person.id,
			// Immich names a person only once somebody has labelled that face cluster, so most of a
			// real library has no name at all. The id fragment is what tells two of them apart when
			// there is no thumbnail to look at either.
			label: person.name?.trim() || 'Unnamed person',
			detail: `#${person.id.slice(0, 8)}`,
			personId: person.id
		}));
}

function tagItems(tags: api.AdminImmichTagDto[]): PickerItem[] {
	return tags
		.filter((tag): tag is api.AdminImmichTagDto & { value: string } => !!tag.value)
		.map((tag) => ({
			key: tag.value,
			label: tag.name?.trim() || tag.value,
			// The full path: what a nested tag is really called, and what gets submitted.
			detail: tag.value
		}));
}

/**
 * Reads one list from the picker proxy. Every refusal comes back as a message to show rather than
 * as a throw: a rejected key (400) or an unreachable server (502) has to leave the field editable,
 * because losing the ability to edit the configuration while Immich is down would be worse than
 * editing identifiers by hand.
 */
export async function loadPickerList(
	kind: PickerKind,
	source: PickerSource
): Promise<PickerResult> {
	if (source.kind === 'blocked') return { ok: false, message: source.reason };

	const ref = accountRef(source);
	const noun = pickerNouns[kind].many;

	try {
		if (kind === 'people') {
			const response = await api.getAdminImmichPeople(ref);
			const status = statusOf(response);

			if (status !== 200) return { ok: false, message: failure(response.data, status, noun) };

			const people = response.data.people ?? [];

			return {
				ok: true,
				list: {
					items: personItems(people),
					total: response.data.total ?? people.length,
					truncated: response.data.truncated === true
				}
			};
		}

		const response =
			kind === 'albums' ? await api.getAdminImmichAlbums(ref) : await api.getAdminImmichTags(ref);
		const status = statusOf(response);

		if (status !== 200) return { ok: false, message: failure(response.data, status, noun) };

		const items =
			kind === 'albums'
				? albumItems(response.data as api.AdminImmichAlbumDto[])
				: tagItems(response.data as api.AdminImmichTagDto[]);

		// The three list endpoints read everything they can reach, so what came back is the whole of
		// it; only people can stop short, and only the people endpoint says so.
		return { ok: true, list: { items, total: items.length, truncated: false } };
	} catch {
		return { ok: false, message: `The ${noun} could not be read. Is ImmichFrame still running?` };
	}
}

function failure(data: unknown, status: number, noun: string): string {
	if (status === 401) {
		return (
			`Your administrator session has expired, so the ${noun} could not be read. Reload the page ` +
			'and sign in again - reloading discards any edits you have not saved.'
		);
	}

	if (status === 403) {
		return `This account is not on the administrator allowlist, so the ${noun} could not be read.`;
	}

	// The proxy writes its 400s and 502s for the operator who will read them - a rejected API key,
	// a URL that is not an Immich server - so they are shown as they arrive.
	return problemDetail(data, `The ${noun} could not be read (HTTP ${status}).`);
}

/**
 * The field after one entry is ticked or unticked, and the one place a picked field changes shape.
 *
 * It is a change to what is configured, never a rebuild of it from what the server returned. A
 * configured id Immich no longer lists - deleted, or belonging to another account entirely - would
 * be dropped by "save what is ticked": the save would succeed, validation would pass, and the frame
 * would quietly stop showing a set of photos. Such an id is not in `items` and is not ticked, and
 * it survives here because it is never consulted.
 */
export function withChoice(values: string[], value: string, chosen: boolean): string[] {
	if (!chosen) return values.filter((other) => other !== value);

	return values.includes(value) ? values : [...values, value];
}

/** The string list behind a picked field, for a value the account DTO types loosely. */
export function pickedValues(value: unknown): string[] {
	return Array.isArray(value)
		? value.filter((item): item is string => typeof item === 'string')
		: [];
}
