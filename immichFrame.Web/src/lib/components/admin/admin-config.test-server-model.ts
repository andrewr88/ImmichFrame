import type { AdminAccountSettingsDto, AdminConfigUpdateDto } from '$lib/immichFrameApi';
import type { StoredAccount } from './admin-config.test-fixtures';

/**
 * What the server does with a saved configuration, modelled from the C# and from nothing else.
 *
 * Transcribed from `ImmichFrame.WebApi/Helpers/Config/AdminConfigService.cs` - `BuildDocument`,
 * `BuildEntry`, `Accounts` and `Account` - and from
 * `ImmichFrame.WebApi/Models/ServerSettings.cs`'s `ServerAccountSettings.ValidateAndInitialize`,
 * with `ConfigCatalog.Validate` for the order the two are reached in.
 *
 * Test support only. It imports nothing from `admin-config.ts` on purpose: a model derived from the
 * code under test restates that code's assumptions, so the two would agree about a credential shape
 * neither handles - which is exactly the defect this model exists to find. Where a rule below looks
 * arbitrary, the C# it came from is named beside it.
 *
 * Two things it does not model, because the grid does not vary them: an `ApiKeyFile` is assumed to
 * exist and to hold a non-empty key (`File.ReadAllText` throwing is an installation problem, not a
 * save the editor could have prevented), and general settings are only checked for being settings
 * the server knows about.
 *
 * ## Which of these rules the suite actually exercises
 *
 * One of the eight: `no-credential`. Every one of the 674 refusals the credential grid provokes is
 * that class, and the two regression tests in `admin-config.test.ts` name it.
 *
 * `duplicate-label` is reached but not asserted on. 3585 of the no-duplicate-handle invariant's
 * 12 096 documents are refused for it, because that test deliberately builds documents whose labels
 * collide and does not ask `validationErrors` - which refuses colliding labels outright - what it
 * makes of them. The only outcome it reads is `ambiguous-handle`, so a document refused for its
 * labels just drops out of the cross-check. Deleting this rule outright leaves the suite green.
 *
 * The other six are never reached at all, because the editor cannot produce the request:
 *
 * - `ambiguous-handle`, `unmatched-handle` - the no-duplicate-handle invariant asserts directly on
 *   the editor's output that no entry ever writes a repeated handle or one the read never issued.
 * - `default-has-no-accounts` - `validationErrors` refuses it by name first, with a test of its own.
 * - `bad-profile-name` - names are vetted by `profileNameError` as a profile is added, and every
 *   fixture here names valid ones.
 * - `unknown-key` - `declaredKeyOf`'s test pins the editor's key set to the list of settings below,
 *   in both directions.
 * - `key-and-key-file` - `toAccountDto` sends no `apiKey` at all for a row naming a key file.
 *
 * So seven of the eight rules are carried on inspection against the C# alone, and a wrong
 * transcription of one would not turn anything red. The single exception is half of one rule: the
 * no-duplicate-handle invariant treats an `ambiguous-handle` verdict as a failure, so that rule made
 * too eager would be caught, though made too lax it would not. Do not close the gap with synthetic
 * requests the editor cannot produce - a case built by hand to reach one of these would be testing
 * this file rather than the editor, and would pass by construction. Re-read them against
 * `AdminConfigService` when it changes instead.
 */

/** `AdminSecret.Placeholder`. */
const PLACEHOLDER = '********';

/** `ConfigCatalog.DefaultProfileName`. */
const DEFAULT_NAME = 'default';

/** `AdminConfigService.AccountsKey` and the prefix `BuildEntry` splits a general key on. */
const ACCOUNTS = 'Accounts';
const GENERAL = 'General';

/** `ConfigCatalog.ValidProfileName()` and `ConfigCatalog.ReservedProfileNames`. */
const PROFILE_NAME = /^[A-Za-z0-9_-]{1,64}$/;
const RESERVED = ['api', 'static', 'swagger', 'admin', DEFAULT_NAME];

/**
 * `WritableProperties(typeof(GeneralSettings))` - every settable property of `GeneralSettings` in
 * `ImmichFrame.WebApi/Models/ServerSettings.cs`, which is what `BuildEntry` will accept after the
 * `General.` prefix. Compared without regard to case, as the dictionary there is.
 */
export const SERVER_GENERAL_SETTINGS = [
	'DownloadImages',
	'Language',
	'ImageLocationFormat',
	'PhotoDateFormat',
	'Interval',
	'TransitionDuration',
	'ShowClock',
	'ClockFormat',
	'ClockDateFormat',
	'ShowProgressBar',
	'ShowPhotoDate',
	'ShowImageDesc',
	'ShowPeopleDesc',
	'ShowTagsDesc',
	'ShowAlbumName',
	'ShowImageLocation',
	'PrimaryColor',
	'SecondaryColor',
	'Style',
	'BaseFontSize',
	'ShowWeatherDescription',
	'WeatherIconUrl',
	'ImageZoom',
	'ImagePan',
	'ImageFill',
	'PlayAudio',
	'Layout',
	'RenewImagesDuration',
	'Webcalendars',
	'RefreshAlbumPeopleInterval',
	'WeatherApiKey',
	'UnitSystem',
	'WeatherLatLong',
	'Webhook',
	'AuthenticationSecret'
];

export type SaveRefusal =
	/** `Accounts`: two accounts in one list share a label. */
	| 'duplicate-label'
	/** `Accounts`: two accounts in one list keep the same stored account's key. */
	| 'ambiguous-handle'
	/** `Accounts`: a kept key whose handle names no stored account. */
	| 'unmatched-handle'
	/** `BuildDocument`: the default configuration declares no account. */
	| 'default-has-no-accounts'
	/** `BuildEntry`: a declared key that is not a setting. */
	| 'unknown-key'
	/** `BuildDocument` / `ConfigCatalog.ValidateProfileName`. */
	| 'bad-profile-name'
	/** `ServerAccountSettings.ValidateAndInitialize`. */
	| 'key-and-key-file'
	| 'no-credential';

export interface SaveRefused {
	accepted: false;
	refusal: SaveRefusal;
	/** The configuration the refusal is about, named as the server names it. */
	entry: string;
	/**
	 * Why, in words. Verbatim from the C# for `no-credential`, `key-and-key-file` and `unknown-key`;
	 * the rest are shortened paraphrases, because the server's sentences go on to tell the
	 * administrator what to do about it and to name the account through `Describe`, which this model
	 * has no account object to build. It also says `profile 'hall'` where the server says
	 * `configuration profile 'hall'`.
	 *
	 * Nothing asserts on this. It is here so that a grid failure prints the rule that fired and not
	 * just its name; {@link SaveRefused.refusal} is what a test matches on.
	 */
	message: string;
}

export type SaveOutcome = { accepted: true } | SaveRefused;

/** One account as the rewritten settings file would spell it; null is a key the file leaves out. */
interface WrittenAccount {
	apiKey: string | null;
	apiKeyFile: string | null;
}

class Refusal extends Error {
	constructor(
		readonly refusal: SaveRefusal,
		readonly entry: string,
		message: string
	) {
		super(message);
	}
}

/** `AdminSecret.KeepsStored`: absent or still the placeholder means "leave the stored one alone". */
function keepsStored(value: string | null | undefined): boolean {
	return value === null || value === undefined || value === PLACEHOLDER;
}

/** `string.IsNullOrWhiteSpace`. */
function blank(value: string | null | undefined): boolean {
	return value === null || value === undefined || value.trim() === '';
}

/** `AdminConfigService.NormalizedLabel`. */
function normalizedLabel(label: string | null | undefined): string | null {
	const trimmed = (label ?? '').trim();

	return trimmed === '' ? null : trimmed;
}

/**
 * `AdminConfigService.Accounts`: resolves each kept API key against the stored document, then
 * `Account` writes the result out.
 *
 * The two questions a handle answers have different scopes and are deliberately two variables in
 * the C# as well: `stored` - which stored account this is - may be answered by any entry in the
 * document, while `declared` - what this entry had already spelled out - is answered only by the
 * entry's own stored copy. Only the first decides whether the save is refused.
 */
function buildAccounts(
	entryName: string,
	accounts: AdminAccountSettingsDto[],
	storedById: Map<string, StoredAccount>
): WrittenAccount[] {
	const where = entryName === DEFAULT_NAME ? 'the default configuration' : `profile '${entryName}'`;
	const claimed = new Set<string>();
	const labelled = new Set<string>();
	const written: WrittenAccount[] = [];

	for (const account of accounts) {
		const label = normalizedLabel(account.label);

		if (label !== null && labelled.has(label.toLowerCase())) {
			throw new Refusal(
				'duplicate-label',
				entryName,
				`Two accounts in ${where} are labelled '${label}'.`
			);
		}

		if (label !== null) labelled.add(label.toLowerCase());

		// An id the document does not hold is not a candidate for anything: the C# only enters this
		// block when TryGetValue succeeds, so `stored` stays null and `ambiguous` stays false.
		let stored: StoredAccount | undefined;
		let ambiguous = false;

		if (account.id) {
			const match = storedById.get(account.id);

			if (match !== undefined) {
				if (claimed.has(account.id)) {
					ambiguous = true;
				} else {
					claimed.add(account.id);
					stored = match;
				}
			}
		}

		let apiKey = account.apiKey ?? null;

		if (keepsStored(apiKey)) {
			if (!blank(account.apiKeyFile)) {
				// An account naming a key file has no stored key to put back; the placeholder the
				// editor echoes in the field is normalised away rather than written beside the path.
				apiKey = null;
			} else if (ambiguous) {
				throw new Refusal(
					'ambiguous-handle',
					entryName,
					`Two accounts in ${where} name the same stored account.`
				);
			} else if (stored === undefined) {
				throw new Refusal(
					'unmatched-handle',
					entryName,
					`The API key for an account in ${where} could not be matched to a stored account.`
				);
			} else {
				// `stored[nameof(ServerAccountSettings.ApiKey)]?.GetValue<string>()`. A stored account
				// whose key is behind an ApiKeyFile has no ApiKey member at all, and this is null.
				apiKey = stored.apiKey ?? null;
			}
		}

		// `Account`: a null is left out of the file, and `ApiKey` binds back to `string.Empty`.
		// `ApiKeyFile` is written whenever the request carries one, the empty string included - it is
		// not the built-in default, which is null.
		written.push({ apiKey, apiKeyFile: account.apiKeyFile ?? null });
	}

	return written;
}

/** `AdminConfigService.BuildEntry`, for the one key that is not a general setting. */
function buildEntry(
	entryName: string,
	declaredKeys: string[],
	accounts: AdminAccountSettingsDto[],
	storedById: Map<string, StoredAccount>
): WrittenAccount[] | null {
	let written: WrittenAccount[] | null = null;

	for (const key of declaredKeys) {
		if (key.toLowerCase().startsWith(`${GENERAL.toLowerCase()}.`)) {
			const name = key.slice(GENERAL.length + 1);

			if (!SERVER_GENERAL_SETTINGS.some((known) => known.toLowerCase() === name.toLowerCase())) {
				throw new Refusal(
					'unknown-key',
					entryName,
					`'${key}' is not a setting ImmichFrame knows about.`
				);
			}
		} else if (key.toLowerCase() === ACCOUNTS.toLowerCase()) {
			written = buildAccounts(entryName, accounts, storedById);
		} else {
			throw new Refusal(
				'unknown-key',
				entryName,
				`'${key}' is not a setting ImmichFrame knows about.`
			);
		}
	}

	return written;
}

/**
 * `ServerAccountSettings.ValidateAndInitialize`, reached through `ConfigCatalog.Validate` once the
 * whole document has been rewritten and re-bound - which is why it runs after every refusal above.
 */
function validateAccount(entryName: string, account: WrittenAccount): void {
	// Binding: a key the file leaves out is the property's default, `string.Empty` for ApiKey and
	// null for ApiKeyFile.
	let apiKey = account.apiKey ?? '';
	const apiKeyFile = account.apiKeyFile;

	if (!blank(apiKeyFile)) {
		if (!blank(apiKey)) {
			throw new Refusal(
				'key-and-key-file',
				entryName,
				'Cannot specify both ApiKey and ApiKeyFile. Please provide only one.'
			);
		}

		// The file is assumed to hold a key; a missing one throws from File.ReadAllText instead.
		apiKey = 'from-file';
	}

	if (blank(apiKey)) {
		throw new Refusal('no-credential', entryName, 'Either ApiKey or ApiKeyFile must be provided.');
	}
}

/**
 * Whether `AdminConfigService.Save` would write this update, and why not where it would not.
 *
 * The order matters and is the C#'s: every entry is built first - the default configuration, then
 * each profile in order - and only then is the rewritten document bound and validated. So a
 * mismatched handle in a profile is reported before an account with no credential in the default.
 */
export function simulateSave(
	update: AdminConfigUpdateDto,
	storedById: Map<string, StoredAccount>
): SaveOutcome {
	try {
		const entries: { name: string; accounts: WrittenAccount[] | null }[] = [];
		const defaultEntry = update.default ?? {};

		const defaultAccounts = buildEntry(
			DEFAULT_NAME,
			defaultEntry.declaredKeys ?? [],
			defaultEntry.accounts ?? [],
			storedById
		);

		if (defaultAccounts === null || defaultAccounts.length === 0) {
			throw new Refusal(
				'default-has-no-accounts',
				DEFAULT_NAME,
				'The default configuration must declare at least one Immich account.'
			);
		}

		entries.push({ name: DEFAULT_NAME, accounts: defaultAccounts });

		const seen = new Set<string>();

		for (const profile of update.profiles ?? []) {
			const name = profile.name ?? '';

			if (!PROFILE_NAME.test(name) || RESERVED.includes(name.toLowerCase())) {
				throw new Refusal(
					'bad-profile-name',
					name,
					`'${name}' is not a valid configuration profile name.`
				);
			}

			if (seen.has(name.toLowerCase())) {
				throw new Refusal(
					'bad-profile-name',
					name,
					`There is more than one configuration profile named '${name}'.`
				);
			}

			seen.add(name.toLowerCase());
			entries.push({
				name,
				accounts: buildEntry(name, profile.declaredKeys ?? [], profile.accounts ?? [], storedById)
			});
		}

		// `ConfigCatalog.Validate`: the default configuration, then every profile over its *merged*
		// settings - so a profile that declares no account list validates the default's accounts
		// again rather than none at all.
		for (const entry of entries) {
			for (const account of entry.accounts ?? defaultAccounts) {
				validateAccount(entry.name, account);
			}
		}

		return { accepted: true };
	} catch (error) {
		if (error instanceof Refusal) {
			return {
				accepted: false,
				refusal: error.refusal,
				entry: error.entry,
				message: error.message
			};
		}

		throw error;
	}
}
