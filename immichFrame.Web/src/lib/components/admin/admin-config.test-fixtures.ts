import type {
	AdminAccountSettingsDto,
	AdminConfigDto,
	AdminConfigEntryDto,
	AdminGeneralSettingsDto
} from '$lib/immichFrameApi';

/**
 * `AdminConfigDto`-shaped read payloads, built the way `AdminConfigService.ReadCurrent` builds
 * them, plus the stored document each one was read from.
 *
 * Test support only - nothing in the app imports this. It deliberately imports nothing from
 * `admin-config.ts`: a fixture spelled by the code under test would agree with it about `Accounts`,
 * `default` and what a read reports for a key-file account whether or not either is right. Every
 * constant here is spelled from the C# instead, cited where it is not obvious.
 */

/** `AdminConfigService.AccountsKey`. */
const ACCOUNTS = 'Accounts';

/** `ConfigCatalog.DefaultProfileName`. */
export const DEFAULT_NAME = 'default';

/** How the settings file holds one account's credential. */
export type StoredCredential = 'key' | 'file';

/** One account as one entry's declared `Accounts` list holds it. */
export interface AccountFixture {
	/** `ServerAccountSettings.Label`, verbatim - blank and whitespace are states worth testing. */
	label?: string;
	url?: string;
	/** Default `'key'`: an `ApiKey` in the file. `'file'` puts the key behind an `ApiKeyFile`. */
	credential?: StoredCredential;
	apiKeyFile?: string;
	/** The photo-selection settings this copy differs from the built-in defaults in. */
	values?: Partial<AdminAccountSettingsDto>;
}

/** One configuration - the default one or a profile - as the settings file declares it. */
export interface EntryFixture {
	name?: string;
	/**
	 * Undefined means this entry declares no `Accounts` list and shows the default configuration's,
	 * which is what an inheriting profile does.
	 */
	accounts?: AccountFixture[];
	/** The `General.*` keys the entry declares, spelled as the server spells them. */
	declares?: string[];
	/**
	 * The `General.*` values this entry's own section of the settings file holds. What a read
	 * reports is the merged result rather than these - see {@link defaultGeneral} - so a key named
	 * in {@link declares} and left out here arrives as whatever the entry inherits, exactly as it
	 * would from the server.
	 */
	general?: AdminGeneralSettingsDto;
}

export interface DocumentFixture {
	version?: string;
	default: EntryFixture;
	profiles?: EntryFixture[];
}

/**
 * One stored account as `AdminConfigService.StoredAccount` holds it: the entry whose declared list
 * it belongs to, and the two credential members of the JSON object the save reads back out of.
 * An absent member is one the settings file does not spell out at all.
 */
export interface StoredAccount {
	owner: string;
	apiKey?: string;
	apiKeyFile?: string;
}

export interface ReadDocument {
	dto: AdminConfigDto;
	/** `AdminConfigService.AccountsById`: every stored account under the handle the read issued. */
	storedById: Map<string, StoredAccount>;
	/** The handle the read issued for one entry's nth declared account. */
	handle(entryName: string, index: number): string;
}

/**
 * `ServerAccountSettings`' built-in defaults, which a read reports for every setting the file
 * leaves out: the DTO is built from the bound settings object, so it is never sparse.
 */
function defaultValues(): AdminAccountSettingsDto {
	return {
		showMemories: false,
		showFavorites: false,
		showArchived: false,
		showVideos: false,
		imagesFromDays: null,
		imagesFromDate: null,
		imagesUntilDate: null,
		albums: [],
		excludedAlbums: [],
		people: [],
		tags: [],
		rating: null
	};
}

/**
 * `GeneralSettings`' built-in defaults, in the same spirit as {@link defaultValues}: `Entry` builds
 * the DTO from `document.Bind<ServerSettings>(name)`, the *merged* settings, so a read's `General`
 * is never sparse and never only what the entry declared.
 *
 * `Required` rather than the DTO's own all-optional shape on purpose. Every key has to be spelled,
 * because a key this leaves out arrives as `undefined`, and `toEntryDto` drops a declared key whose
 * value is `undefined` - so a fixture that declared a setting without spelling it would produce an
 * update with the key silently missing and assert nothing. A setting added to the API's DTO fails
 * to compile here until it is given its default, the same way `generalFields` is exhaustive over
 * `GeneralProp`.
 *
 * The three secrets are null because a read never populates them: `AdminGeneralSettingsDto`'s
 * constructor leaves the properties unassigned and reports presence through the `has…` flags
 * instead. `PrimaryColor`, `SecondaryColor` and `BaseFontSize` are null because that genuinely is
 * their built-in default, so a read reports null for them too - `"PrimaryColor": null` binds to the
 * same value as leaving the key out. A fixture that wants one of those three declared has to give
 * it a value, which is exactly the condition a real settings file is under.
 */
function defaultGeneral(): Required<AdminGeneralSettingsDto> {
	return {
		interval: 45,
		transitionDuration: 1,
		downloadImages: false,
		renewImagesDuration: 30,
		showClock: true,
		clockFormat: 'hh:mm',
		clockDateFormat: 'eee, MMM d',
		showPhotoDate: true,
		showProgressBar: true,
		photoDateFormat: 'MM/dd/yyyy',
		showImageDesc: true,
		showPeopleDesc: true,
		showTagsDesc: true,
		showAlbumName: true,
		showImageLocation: true,
		imageLocationFormat: 'City,State,Country',
		primaryColor: null,
		secondaryColor: null,
		style: 'none',
		baseFontSize: null,
		showWeatherDescription: true,
		weatherIconUrl: 'https://openweathermap.org/img/wn/{IconId}.png',
		imageZoom: true,
		imagePan: false,
		imageFill: false,
		playAudio: false,
		layout: 'splitview',
		language: 'en',
		webcalendars: [],
		refreshAlbumPeopleInterval: 12,
		weatherLatLong: '40.7128,74.0060',
		unitSystem: 'imperial',
		weatherApiKey: null,
		webhook: null,
		authenticationSecret: null,
		hasWeatherApiKey: false,
		hasWebhook: false,
		hasAuthenticationSecret: false
	};
}

/**
 * The handle. Opaque to the editor, which only echoes it back, so the SHA-256 of
 * `AdminConfigService.AccountId` buys the tests nothing; what matters is that it names one entry
 * and one position, and that no two differ only by case of the entry name - the real one lowercases
 * the profile name before hashing.
 */
function accountId(entryName: string, index: number): string {
	return `handle:${entryName.toLowerCase()}:${index}`;
}

/** `AdminConfigService.NormalizedLabel`: absent, empty and whitespace are all "no label". */
function normalizedLabel(label: string | undefined): string | null {
	return label === undefined || label.trim() === '' ? null : label.trim();
}

function keyFilePath(fixture: AccountFixture, entryName: string, index: number): string {
	return fixture.apiKeyFile ?? `/run/secrets/${entryName}-${index}`;
}

/**
 * One account as a read reports it. `AdminAccountSettingsDto`'s constructor binds without
 * `ValidateAndInitialize`, so a key-file account has an empty `ApiKey` and is reported through
 * `ApiKeyFromFile` instead; `HasApiKey` is `ApiKeyFromFile || ApiKey != ''`, and a stored account
 * that loaded at all has a key by one of the two means. `ApiKey` itself is never populated.
 */
function accountDto(
	fixture: AccountFixture,
	entryName: string,
	index: number,
	declared: boolean
): AdminAccountSettingsDto {
	const fromFile = fixture.credential === 'file';

	return {
		...defaultValues(),
		...fixture.values,
		id: declared ? accountId(entryName, index) : null,
		label: normalizedLabel(fixture.label),
		immichServerUrl: fixture.url ?? 'https://immich.example',
		apiKeyFile: fromFile ? keyFilePath(fixture, entryName, index) : null,
		apiKeyFromFile: fromFile,
		hasApiKey: true
	};
}

function storedAccount(fixture: AccountFixture, entryName: string, index: number): StoredAccount {
	return fixture.credential === 'file'
		? { owner: entryName, apiKeyFile: keyFilePath(fixture, entryName, index) }
		: { owner: entryName, apiKey: `stored-key-${entryName}-${index}` };
}

/**
 * Turns a description of a settings file into the read payload the editor is given and the stored
 * document a save of it resolves handles against.
 *
 * An entry that declares no `Accounts` list is shown the default configuration's accounts with no
 * handles - `AdminConfigService.Entry` issues one only for a position the entry's own declared list
 * holds - and contributes no stored accounts: the ones it shows are already indexed under the
 * default configuration's handles.
 */
export function buildDocument(fixture: DocumentFixture): ReadDocument {
	const version = fixture.version ?? 'sha256:fixture';
	const storedById = new Map<string, StoredAccount>();

	const defaultAccounts = (fixture.default.accounts ?? []).map((account, index) =>
		accountDto(account, DEFAULT_NAME, index, true)
	);

	const inherited = () => defaultAccounts.map((account) => ({ ...account, id: null }));

	/** What a profile's binding merges its own values over; for the default entry, its own again. */
	const inheritedGeneral = fixture.default.general ?? {};

	function entryDto(entry: EntryFixture, name: string): AdminConfigEntryDto {
		const declared = entry.accounts;

		(declared ?? []).forEach((account, index) =>
			storedById.set(accountId(name, index), storedAccount(account, name, index))
		);

		return {
			name,
			declaredKeys: [...(entry.declares ?? []), ...(declared === undefined ? [] : [ACCOUNTS])],
			general: { ...defaultGeneral(), ...inheritedGeneral, ...entry.general },
			accounts:
				declared === undefined
					? inherited()
					: name === DEFAULT_NAME
						? defaultAccounts
						: declared.map((account, index) => accountDto(account, name, index, true))
		};
	}

	const dto: AdminConfigDto = {
		version,
		source: { format: 'json', path: '/config/Settings.json', legacySchema: false, editable: true },
		default: entryDto(fixture.default, DEFAULT_NAME),
		profiles: (fixture.profiles ?? []).map((profile, index) =>
			entryDto(profile, profile.name ?? `profile${index + 1}`)
		)
	};

	return { dto, storedById, handle: accountId };
}
