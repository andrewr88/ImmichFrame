import {
	loadPickerList,
	sourceKey,
	type PickerKind,
	type PickerResult,
	type PickerSource
} from './immich-picker';

/**
 * One list read, for the life of this page.
 *
 * Several pickers ask for the same list at once - `albums` and `excludedAlbums` are two fields on
 * one account, and a profile that overrides accounts draws a second set of them against the same
 * credentials - so what is held is the in-flight promise, not just its answer: everybody who asks
 * while a read is in the air waits on that read instead of starting another.
 */
interface Entry {
	promise: Promise<PickerResult>;
	/** The answer once there is one, for a caller that cannot await: the first render. */
	result?: PickerResult;
}

const entries = new Map<string, Entry>();

/**
 * Keyed by `sourceKey`, which already encodes which server is being asked and as whom, so an entry
 * read with one set of credentials can never be handed back for another.
 */
function cacheKey(kind: PickerKind, source: PickerSource): string {
	return `${sourceKey(source)}\n${kind}`;
}

/**
 * The list for these credentials, read once. Refusals are kept alongside answers: a server that is
 * down would otherwise be asked again by every picker on the page, each waiting out its own
 * timeout, to be told the same thing. Every read the administrator asks for drops the entry first,
 * so nothing here outlives a *Reload*.
 */
export function pickerList(kind: PickerKind, source: PickerSource): Promise<PickerResult> {
	// A blocked source names no server to ask. Nothing is fetched, and nothing is stored either -
	// the reason is a statement about the half-typed form rather than about a server, and it costs
	// nothing to produce again.
	if (source.kind === 'blocked') return loadPickerList(kind, source);

	const key = cacheKey(kind, source);
	const existing = entries.get(key);

	if (existing) return existing.promise;

	const entry: Entry = {
		promise: loadPickerList(kind, source).then((result) => {
			entry.result = result;

			return result;
		})
	};

	entries.set(key, entry);

	return entry.promise;
}

/**
 * What has already been read for these credentials, without asking for it if it has not been. For
 * the moment a picker is created: a list already in hand belongs on the very first render of its
 * rows, rather than a frame later.
 */
export function cachedPickerList(kind: PickerKind, source: PickerSource): PickerResult | undefined {
	if (source.kind === 'blocked') return undefined;

	return entries.get(cacheKey(kind, source))?.result;
}

/** Forgets one list, so that the next ask is a real read of Immich rather than a replay. */
export function dropPickerList(kind: PickerKind, source: PickerSource): void {
	if (source.kind === 'blocked') return;

	entries.delete(cacheKey(kind, source));
}
