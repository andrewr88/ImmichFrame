import type {
	AdminAccountSettingsDto,
	AdminConfigDto,
	AdminConfigEntryDto,
	AdminConfigSourceDto,
	AdminConfigUpdateDto,
	AdminGeneralSettingsDto
} from '$lib/immichFrameApi';
import { normalizedServerUrl } from './immich-picker';

/**
 * What the admin API documents a masked secret as carrying back: absent or exactly this value
 * means "keep whatever is stored". Spelled the way `AdminSecret.Placeholder` spells it, because a
 * sentinel of our own invention would be written to the settings file as the secret itself.
 */
export const SECRET_PLACEHOLDER = '********';

/** The key that stands for the whole account list in `DeclaredKeys`. */
export const ACCOUNTS_KEY = 'Accounts';

/** `ConfigCatalog.DefaultProfileName` - the name the API gives the default configuration. */
export const DEFAULT_ENTRY_NAME = 'default';

/** `ConfigCatalog.ReservedProfileNames`, so a name the editor accepts is one the API accepts. */
const RESERVED_PROFILE_NAMES = ['api', 'static', 'swagger', 'admin', DEFAULT_ENTRY_NAME];

/** `ConfigCatalog.ValidProfileName()`. */
const PROFILE_NAME_PATTERN = /^[A-Za-z0-9_-]{1,64}$/;

/** The three general settings the API masks; every other general setting is a plain value. */
export const SECRET_PROPS = ['weatherApiKey', 'webhook', 'authenticationSecret'] as const;

export type SecretProp = (typeof SECRET_PROPS)[number];

/** Every general setting the editor can write; the `has…` flags are read-only presence reports. */
export type GeneralProp = Exclude<
	keyof AdminGeneralSettingsDto,
	'hasWeatherApiKey' | 'hasWebhook' | 'hasAuthenticationSecret'
>;

/**
 * Every account setting rendered by the generic field renderer; the rest have bespoke UI.
 *
 * `label` is excluded because it is not a photo-selection setting at all: it names the account, so
 * it belongs wherever the account itself is presented rather than in the generic field list.
 */
export type AccountProp = Exclude<
	keyof AdminAccountSettingsDto,
	'id' | 'label' | 'apiKey' | 'hasApiKey' | 'apiKeyFromFile' | 'immichServerUrl' | 'apiKeyFile'
>;

export type FieldKind =
	| 'text'
	| 'integer'
	| 'decimal'
	| 'boolean'
	| 'select'
	| 'lines'
	| 'date'
	| 'secret';

/** Anything the generic field renderer can hold. */
export type FieldValue = string | number | boolean | string[] | null | undefined;

export interface FieldSpec {
	label: string;
	kind: FieldKind;
	options?: readonly string[];
	help?: string;
	placeholder?: string;
}

/**
 * Exhaustive over {@link GeneralProp} on purpose: a setting added to the API's DTO fails to
 * compile here until it is given a label and a kind, rather than silently becoming uneditable.
 */
export const generalFields: Record<GeneralProp, FieldSpec> = {
	interval: { label: 'Interval', kind: 'integer', help: 'Seconds each image is shown.' },
	transitionDuration: { label: 'Transition duration', kind: 'decimal', help: 'Seconds.' },
	layout: { label: 'Layout', kind: 'select', options: ['single', 'splitview'] },
	imageZoom: { label: 'Image zoom', kind: 'boolean' },
	imagePan: { label: 'Image pan', kind: 'boolean' },
	imageFill: { label: 'Image fill', kind: 'boolean' },
	playAudio: { label: 'Play audio', kind: 'boolean' },
	showClock: { label: 'Show clock', kind: 'boolean' },
	clockFormat: { label: 'Clock format', kind: 'text', placeholder: 'hh:mm' },
	clockDateFormat: { label: 'Clock date format', kind: 'text', placeholder: 'eee, MMM d' },
	showProgressBar: { label: 'Show progress bar', kind: 'boolean' },
	showPhotoDate: { label: 'Show photo date', kind: 'boolean' },
	photoDateFormat: { label: 'Photo date format', kind: 'text', placeholder: 'MM/dd/yyyy' },
	showImageDesc: { label: 'Show image description', kind: 'boolean' },
	showPeopleDesc: { label: 'Show people', kind: 'boolean' },
	showTagsDesc: { label: 'Show tags', kind: 'boolean' },
	showAlbumName: { label: 'Show album name', kind: 'boolean' },
	showImageLocation: { label: 'Show image location', kind: 'boolean' },
	imageLocationFormat: {
		label: 'Image location format',
		kind: 'text',
		placeholder: 'City,State,Country'
	},
	primaryColor: { label: 'Primary colour', kind: 'text', placeholder: '#f5deb3' },
	secondaryColor: { label: 'Secondary colour', kind: 'text', placeholder: '#000000' },
	style: { label: 'Style', kind: 'select', options: ['none', 'solid', 'transition', 'blur'] },
	baseFontSize: { label: 'Base font size', kind: 'text', placeholder: '17px' },
	language: { label: 'Language', kind: 'text', help: 'Two letter ISO code.', placeholder: 'en' },
	weatherApiKey: { label: 'Weather API key', kind: 'secret', help: 'From OpenWeatherMap.' },
	unitSystem: { label: 'Unit system', kind: 'select', options: ['imperial', 'metric'] },
	weatherLatLong: { label: 'Weather lat/long', kind: 'text', placeholder: '40.730610,-73.935242' },
	showWeatherDescription: { label: 'Show weather description', kind: 'boolean' },
	weatherIconUrl: { label: 'Weather icon URL', kind: 'text' },
	downloadImages: { label: 'Download images', kind: 'boolean' },
	renewImagesDuration: { label: 'Renew images after', kind: 'integer', help: 'Days.' },
	refreshAlbumPeopleInterval: {
		label: 'Refresh albums and people every',
		kind: 'integer',
		help: 'Hours.'
	},
	webcalendars: { label: 'Web calendars', kind: 'lines', help: 'One .ics URL per line.' },
	webhook: { label: 'Webhook', kind: 'secret', help: 'URL notified on frame events.' },
	authenticationSecret: {
		label: 'Authentication secret',
		kind: 'secret',
		help: 'Every frame must send this as a bearer token.'
	}
};

/** Every key of {@link generalFields} appears in exactly one of these. */
export const generalSections: { title: string; props: GeneralProp[] }[] = [
	{
		title: 'Slideshow',
		props: [
			'interval',
			'transitionDuration',
			'layout',
			'imageZoom',
			'imagePan',
			'imageFill',
			'playAudio'
		]
	},
	{
		title: 'Overlay',
		props: [
			'showClock',
			'clockFormat',
			'clockDateFormat',
			'showProgressBar',
			'showPhotoDate',
			'photoDateFormat',
			'showImageDesc',
			'showPeopleDesc',
			'showTagsDesc',
			'showAlbumName',
			'showImageLocation',
			'imageLocationFormat'
		]
	},
	{
		title: 'Appearance',
		props: ['primaryColor', 'secondaryColor', 'style', 'baseFontSize', 'language']
	},
	{
		title: 'Weather',
		props: [
			'weatherApiKey',
			'unitSystem',
			'weatherLatLong',
			'showWeatherDescription',
			'weatherIconUrl'
		]
	},
	{
		title: 'Server',
		props: [
			'downloadImages',
			'renewImagesDuration',
			'refreshAlbumPeopleInterval',
			'webcalendars',
			'webhook',
			'authenticationSecret'
		]
	}
];

export const accountFields: Record<AccountProp, FieldSpec> = {
	showMemories: { label: 'Show memories', kind: 'boolean' },
	showFavorites: { label: 'Show favorites', kind: 'boolean' },
	showArchived: { label: 'Show archived', kind: 'boolean' },
	showVideos: { label: 'Show videos', kind: 'boolean' },
	imagesFromDays: {
		label: 'Images from the last',
		kind: 'integer',
		help: 'Days. Blank for no limit.'
	},
	imagesFromDate: { label: 'Images from date', kind: 'date' },
	imagesUntilDate: { label: 'Images until date', kind: 'date' },
	// The four picked fields keep 'lines' as their kind: it is what the manual fallback renders, and
	// what a value written by hand is still parsed as when Immich cannot be reached.
	albums: { label: 'Albums', kind: 'lines', help: 'Only these albums are shown. Stored as ids.' },
	excludedAlbums: {
		label: 'Excluded albums',
		kind: 'lines',
		help: 'Photos in these albums are never shown. Stored as ids.'
	},
	people: { label: 'People', kind: 'lines', help: 'Only photos of these people. Stored as ids.' },
	tags: {
		label: 'Tags',
		kind: 'lines',
		help: 'Only photos with these tags, stored as the full path, e.g. Travel/Europe.'
	},
	rating: { label: 'Rating', kind: 'integer', help: 'Exact star rating, -1 to 5. Blank for any.' }
};

export const accountProps = Object.keys(accountFields) as AccountProp[];

/**
 * `DeclaredKeys` names general settings the way the C# settings class spells them
 * (`General.ShowClock`), while the JSON DTO is camel-cased.
 */
export function declaredKeyOf(prop: GeneralProp): string {
	return `General.${prop.charAt(0).toUpperCase()}${prop.slice(1)}`;
}

/** The inverse, for the keys this build knows; null for `Accounts` and anything it does not. */
function generalPropOf(key: string): GeneralProp | null {
	if (!key.startsWith('General.')) return null;

	const prop = key.slice('General.'.length);
	const camel = `${prop.charAt(0).toLowerCase()}${prop.slice(1)}`;

	return camel in generalFields ? (camel as GeneralProp) : null;
}

export interface SecretEdit {
	/**
	 * keep: send the placeholder, and the server keeps what is stored. set: send what was typed.
	 * none: send the empty string, which is how the API spells "this configuration has no secret
	 * here" - written as an explicit null on a profile, and as no key at all on the default
	 * configuration, where null and absent say the same thing.
	 */
	mode: 'keep' | 'set' | 'none';
	value: string;
}

/**
 * One Immich account, held once for the whole configuration rather than once per entry that uses
 * it.
 *
 * Everything credential lives here - server URL, label, key and key file - because those are
 * properties of the account and not of the entry that mentions it. A settings file is free to
 * describe one account two ways in two entries, and that is a bug in the file rather than a feature
 * to preserve: the same account editable in one place is the whole point of this section.
 */
export interface EditableAccount {
	/**
	 * What an entry's selection points at, for the life of this page. Deliberately not the account's
	 * identity in the settings file - that is its label, or failing that its server URL - because
	 * both of those are editable, and a reference that moved as they were typed would detach every
	 * entry that had been given the account.
	 */
	key: string;
	/**
	 * The handle the read issued for this account, per entry that declared it. A handle resolves
	 * against every entry in the document on save, so an entry adopting an account it never declared
	 * sends the handle of an entry that did - in practice the default configuration's - and the
	 * server copies that account's stored key across without it ever reaching the browser.
	 */
	ids: Record<string, string>;
	/** Exactly what was typed. Empty means this account has no label, and none is invented for it. */
	label: string;
	serverUrl: string;
	apiKeyFile: string;
	/**
	 * There is a stored key for this account somewhere in the settings file. A statement about the
	 * document rather than about one entry, which is what makes adoption work: the handle above
	 * reaches that key from anywhere, so an entry that has never declared the account still has a key
	 * to keep rather than one to type.
	 */
	hasStoredKey: boolean;
	/**
	 * Entries whose stored copy of this account reads its key from a file, and the path each names.
	 *
	 * Per entry rather than document-wide, unlike {@link hasStoredKey}, because a key file is not
	 * reachable through a handle the way a stored key is: an entry whose stored account holds an
	 * ApiKeyFile holds no ApiKey beside it, so keeping its stored key resolves to nothing at all.
	 * What an entry keeping that stored key needs is the path, sent with it - and if the row above no
	 * longer names one, it is about to be written with neither and the save has to be refused before
	 * it is.
	 *
	 * Keyed by the entry the stored copy belongs to, which is the entry that owns the handle rather
	 * than necessarily the entry about to send it: an adopting entry borrows a handle, and inherits
	 * this problem with it. `keptKeyErrors` resolves the owner before it looks here.
	 */
	keyFromFile: Record<string, string>;
	/** The administrator is typing a key rather than keeping the stored one. */
	entering: boolean;
	apiKey: string;
	/**
	 * The server URL this account was read with, kept so an edit to it can be noticed. A stored API
	 * key belongs to the server it was stored against, so once the URL in the form has moved on, the
	 * stored key is no longer a credential for what the form describes.
	 */
	savedServerUrl: string | null;
	/**
	 * Entries whose stored copy of this account describes it differently - another server URL, or
	 * another key file. Recorded rather than reconciled: a save writes the values above over every
	 * copy, so which entry is about to lose its version of them has to be visible first.
	 */
	disagreeing: string[];
}

/** What one entry shows from one account: its photo selection, and nothing credential. */
export interface AccountSelection {
	/** {@link EditableAccount.key} of the account this selects from. */
	accountKey: string;
	/**
	 * Whether this entry currently uses the account. False is a selection the administrator has
	 * unticked and kept: nothing is written for it, and ticking the account again restores exactly
	 * these values rather than seeding another copy of the default configuration's.
	 */
	uses: boolean;
	/**
	 * This entry's own copy of the account, kept whole rather than reduced to the fields the editor
	 * renders, so a setting this build does not know about survives a round trip instead of being
	 * written back as null. The credential fields on it go stale the moment the account is edited and
	 * are overwritten from the account on the way out.
	 */
	values: AdminAccountSettingsDto;
}

export interface EditableEntry {
	name: string;
	isDefault: boolean;
	/** The keys this configuration will declare when saved. */
	declared: string[];
	/** The keys it declared when it was read, which is what has a stored value to keep. */
	originallyDeclared: string[];
	general: AdminGeneralSettingsDto;
	secrets: Record<SecretProp, SecretEdit>;
	/** Which accounts this configuration uses, and what it shows from each. */
	accounts: AccountSelection[];
	accountsWereDeclared: boolean;
}

export interface EditableConfig {
	version: string;
	source: AdminConfigSourceDto;
	/** Every distinct Immich account in the document, each appearing exactly once. */
	accounts: EditableAccount[];
	default: EditableEntry;
	profiles: EditableEntry[];
	convertLegacySchema: boolean;
}

/**
 * Unique for the life of the page and never reused, so that an account removed and another added
 * cannot be handed one key while a selection still names it.
 */
let accountKeys = 0;

function accountKey(): string {
	return `account-${++accountKeys}`;
}

/**
 * An empty record keyed by entry name.
 *
 * Null-prototyped, because the keys are names out of the settings file and a profile name is any of
 * 64 letters, digits, underscores and hyphens - so 'constructor', 'toString', 'valueOf' and
 * 'hasOwnProperty' are all legal names. On an ordinary object literal those read back as Object's
 * members rather than as absent, and a lookup falling through on a missing entry would instead be
 * handed a function: `account.ids['toString'] ?? savedAccountHandle(account)?.id` would answer with
 * `Object.prototype.toString`, JSON would drop it, and the account would be written with no handle
 * at all. With no prototype there is nothing to inherit, so every read is answered by what was put
 * here - including reads added later. `Object.keys`, `Object.entries` and `JSON.stringify` see
 * exactly what they saw before; only the inherited members are gone.
 */
function perEntry(): Record<string, string> {
	return Object.create(null) as Record<string, string>;
}

/** A label as the server compares and stores it: absent, empty and whitespace all mean "none". */
function normalizedLabel(label: string | null | undefined): string {
	return (label ?? '').trim();
}

/**
 * What makes two entries' accounts one account.
 *
 * A label is an assertion of identity and is matched on its own, trimmed and case-insensitively -
 * the comparison `AdminConfigService` makes, so the editor and the server agree on what one account
 * is. Without one there is only the server URL, which is not an identity (two accounts may be two
 * users on one Immich server) but is the best there is, tie-broken by the order the entry lists
 * them in. A labelled account never matches an unlabelled one, even at the same URL: the absence of
 * a label asserts nothing, and no label is invented to fill the gap.
 */
function accountIdentity(label: string, serverUrl: string, ordinal: number): string {
	return label
		? `label\n${label.toLowerCase()}`
		: `url\n${normalizedServerUrl(serverUrl)}\n${ordinal}`;
}

/** The accounts seen so far while a read is being turned into the model, and how to find them. */
interface Roster {
	accounts: EditableAccount[];
	byIdentity: Map<string, EditableAccount>;
}

/** Whether this entry's copy of an account is one whose stored key a save can put back. */
function hasStoredKey(dto: AdminAccountSettingsDto): boolean {
	return !!dto.id && dto.hasApiKey === true && dto.apiKeyFromFile !== true;
}

function rosterAccount(roster: Roster, identity: string, dto: AdminAccountSettingsDto) {
	const account: EditableAccount = {
		key: accountKey(),
		ids: perEntry(),
		label: normalizedLabel(dto.label),
		serverUrl: dto.immichServerUrl ?? '',
		apiKeyFile: dto.apiKeyFile ?? '',
		hasStoredKey: false,
		keyFromFile: perEntry(),
		entering: false,
		apiKey: '',
		savedServerUrl: dto.immichServerUrl ?? null,
		disagreeing: []
	};

	roster.byIdentity.set(identity, account);
	roster.accounts.push(account);

	return account;
}

/**
 * Folds one entry's declared accounts into the roster, and returns what that entry shows from each.
 *
 * Only an entry that declares its own account list is passed here. The accounts an inheriting
 * profile is shown are the default configuration's, already rostered under the default's handles,
 * and taking them again would turn one account into two.
 */
function rosterAccounts(
	roster: Roster,
	entryName: string,
	accounts: AdminAccountSettingsDto[]
): AccountSelection[] {
	const taken = new Set<string>();
	const ordinals = new Map<string, number>();

	return accounts.map((dto, index) => {
		const label = normalizedLabel(dto.label);
		const serverUrl = dto.immichServerUrl ?? '';
		const url = normalizedServerUrl(serverUrl);
		const ordinal = label ? 0 : (ordinals.get(url) ?? 0);

		if (!label) ordinals.set(url, ordinal + 1);

		let identity = accountIdentity(label, serverUrl, ordinal);

		// Two accounts in one entry that normalise to one identity are still two accounts, whatever
		// the file says: labels are normalised on read, so a hand-written "Mum" and "mum " both
		// arrive as `Mum`. Merging them into one row would edit one account's credentials through the
		// other's, so the second gets an identity nothing else can match and stays a row of its own.
		// `validationErrors` is what explains the collision and refuses the save, as the server does.
		if (taken.has(identity)) identity = `collision\n${entryName}\n${index}`;

		taken.add(identity);

		const existing = roster.byIdentity.get(identity);
		const account = existing ?? rosterAccount(roster, identity, dto);

		if (existing) noteDisagreement(existing, dto, entryName);
		if (dto.id) account.ids[entryName] = dto.id;
		// Asked of the whole document: one entry's stored key is every entry's, once a handle reaches
		// it. This is exactly what stopped being true of a single entry when handles went
		// document-wide, and what lets a profile adopt an account without retyping anything.
		if (hasStoredKey(dto)) account.hasStoredKey = true;
		// Asked of this entry alone, for the opposite reason: a key file is not something a handle
		// can fetch, so it has to be sent with the account or the entry loses its credential. The
		// path is always there when the flag is - the read computes one from the other,
		// `ApiKeyFromFile = !IsNullOrWhiteSpace(ApiKeyFile)` - and the fallback is only what the
		// generated type's nullable string needs to become a string.
		if (dto.apiKeyFromFile === true) account.keyFromFile[entryName] = dto.apiKeyFile ?? '';

		return newSelection(account.key, dto);
	});
}

function noteDisagreement(
	account: EditableAccount,
	dto: AdminAccountSettingsDto,
	entryName: string
) {
	const sameUrl =
		normalizedServerUrl(dto.immichServerUrl) === normalizedServerUrl(account.serverUrl);
	const sameFile = (dto.apiKeyFile ?? '').trim() === account.apiKeyFile.trim();

	if (!sameUrl || !sameFile) account.disagreeing.push(entryName);
}

function toEntry(dto: AdminConfigEntryDto, isDefault: boolean, roster: Roster): EditableEntry {
	const declared = [...(dto.declaredKeys ?? [])];
	const accountsDeclared = declared.includes(ACCOUNTS_KEY);
	const name = dto.name ?? (isDefault ? DEFAULT_ENTRY_NAME : '');
	const secrets = {} as Record<SecretProp, SecretEdit>;

	for (const prop of SECRET_PROPS) {
		// Only a configuration that already declares a secret has one of its own to keep. Defaulting
		// the rest to 'set' is what stops adding an override from writing the placeholder's stand-in
		// for a value the server would look for and not find.
		secrets[prop] = { mode: declared.includes(declaredKeyOf(prop)) ? 'keep' : 'set', value: '' };
	}

	return {
		name,
		isDefault,
		declared,
		originallyDeclared: [...declared],
		general: { ...(dto.general ?? {}) },
		secrets,
		accounts: accountsDeclared ? rosterAccounts(roster, name, dto.accounts ?? []) : [],
		accountsWereDeclared: accountsDeclared
	};
}

export function toEditable(dto: AdminConfigDto): EditableConfig {
	const roster: Roster = { accounts: [], byIdentity: new Map() };
	// The default configuration first, deliberately: it is the entry that always declares its
	// accounts, so its copy is the one the row is built from and its handle is the one an adopting
	// profile sends.
	const config: EditableConfig = {
		version: dto.version ?? '',
		source: dto.source ?? {},
		accounts: roster.accounts,
		default: toEntry(dto.default ?? {}, true, roster),
		profiles: (dto.profiles ?? []).map((profile) => toEntry(profile, false, roster)),
		convertLegacySchema: false
	};

	for (const account of roster.accounts) {
		// Settled once the whole document has been read, because the key that makes this false may
		// belong to an entry read after the one the row came from.
		account.entering = !usesApiKeyFile(account) && !account.hasStoredKey;
	}

	return config;
}

/** A profile that declares nothing: every setting is inherited until an override is added. */
export function newProfile(name: string): EditableEntry {
	const secrets = {} as Record<SecretProp, SecretEdit>;
	for (const prop of SECRET_PROPS) {
		secrets[prop] = { mode: 'set', value: '' };
	}

	return {
		name,
		isDefault: false,
		declared: [],
		originallyDeclared: [],
		general: {},
		secrets,
		accounts: [],
		accountsWereDeclared: false
	};
}

/** An account with no stored key anywhere, so its key has to be typed before it can be saved. */
export function newAccount(): EditableAccount {
	return {
		key: accountKey(),
		ids: perEntry(),
		label: '',
		serverUrl: '',
		apiKeyFile: '',
		hasStoredKey: false,
		keyFromFile: perEntry(),
		entering: true,
		apiKey: '',
		// No handle and no stored key, so there is nothing for a URL edit to disagree with.
		savedServerUrl: null,
		disagreeing: []
	};
}

/**
 * What an entry shows from an account it has just been given, seeded from what it was already
 * showing where there is such a copy - so that ticking an inherited account and saving without
 * further edits changes nothing but which configuration the values are written in.
 */
export function newSelection(key: string, from: AdminAccountSettingsDto = {}): AccountSelection {
	// The handle and the key belong to the account and are put back on the way out; carrying the
	// read's copy of them here would only be something to go stale.
	const values: AdminAccountSettingsDto = { ...from, id: undefined, apiKey: undefined };

	// Albums, people and tags arrive as arrays, and a spread copies the reference rather than the
	// list. Two entries' selections holding one array would be one list of albums reachable down two
	// $state paths - the shape the account key exists to avoid - so an in-place edit added later
	// would silently change both. Copied by shape rather than by name, so a list setting this build
	// does not know about is copied too.
	for (const [prop, value] of Object.entries(values)) {
		if (Array.isArray(value)) Object.assign(values, { [prop]: [...value] });
	}

	return { accountKey: key, uses: true, values };
}

/**
 * What an entry actually shows, which is what gets written. The rest are selections the
 * administrator has unticked and kept, held so that ticking the account again restores them.
 */
export function usedSelections(entry: EditableEntry): AccountSelection[] {
	return entry.accounts.filter((selection) => selection.uses);
}

/**
 * Ticks or unticks one account under one entry.
 *
 * Unticking marks the selection unused rather than dropping it, so ticking again restores what this
 * entry was showing from the account. Dropping it would make a stray click the most destructive
 * control on the page: the seeding below would refill it from the *default configuration's* current
 * selection, so a profile's albums, people, tags and date filters would be overwritten with
 * another configuration's, and the only way back would be a reload that loses every other unsaved
 * edit too.
 *
 * Seeding from what this entry was inheriting therefore happens only when there is genuinely nothing
 * to restore, and then for the reason an overridden general setting is seeded: ticking an account it
 * was already showing and saving without further edits changes nothing but which configuration the
 * values are written in.
 */
export function setAccountUse(
	entry: EditableEntry,
	account: EditableAccount,
	uses: boolean,
	inheritFrom: EditableEntry | null
): void {
	const existing = entry.accounts.find((selection) => selection.accountKey === account.key);

	if (existing) {
		existing.uses = uses;
		return;
	}

	if (!uses) return;

	const was = inheritFrom
		? usedSelections(inheritFrom).find((selection) => selection.accountKey === account.key)
		: undefined;

	entry.accounts = [...entry.accounts, newSelection(account.key, was?.values)];
}

/**
 * Which configurations show photos from this account, and would therefore be changed by an edit to
 * it or lose it with it.
 *
 * A profile that declares no account list is counted when the default configuration uses the
 * account, because that is what it is showing: inheriting is not "not using it", it is using the
 * default configuration's list. Counting only declared lists is what made removal report "no
 * configuration profile uses it" while every inheriting profile lost its photos - the silent change
 * the removal dialog exists to surface.
 *
 * A profile that declares its own list is counted on its own selections, and only the ticked ones:
 * an unticked selection is held for the tick being turned back on and is written nowhere.
 */
export function entriesUsingAccount(
	config: EditableConfig,
	account: EditableAccount
): EditableEntry[] {
	return [config.default, ...config.profiles].filter((entry) => {
		const source = entry.declared.includes(ACCOUNTS_KEY) ? entry : config.default;

		return usedSelections(source).some((selection) => selection.accountKey === account.key);
	});
}

export function usesApiKeyFile(account: EditableAccount): boolean {
	return !!account.apiKeyFile.trim();
}

/** The accounts section's rows, by the key an entry's selection names them with. */
export function accountsByKey(config: EditableConfig): Map<string, EditableAccount> {
	return new Map(config.accounts.map((account) => [account.key, account]));
}

/**
 * A handle for this account together with the entry it was issued for, which is how the picker
 * proxy resolves one.
 *
 * Any of them names a stored copy of this row, which is all its callers need: the question a handle
 * answers document-wide is which stored account it is, not what that account says. The copies need
 * not say the same thing - a settings file may describe one account with two server URLs or two key
 * files, which is what {@link EditableAccount.disagreeing} collects and the accounts section says
 * out loud - so nothing here assumes they agree. Browsing with one is safe regardless:
 * `pickerSource` refuses to send a handle at all once the row's server URL has moved off
 * {@link EditableAccount.savedServerUrl}.
 *
 * The default configuration's is preferred because it is the entry that always declares its
 * accounts: its copy is the one the row was built from, and it is the likeliest to still be in the
 * running configuration, which the proxy requires of the entry a handle names.
 */
export function savedAccountHandle(account: EditableAccount): { entry: string; id: string } | null {
	const preferred = account.ids[DEFAULT_ENTRY_NAME];

	if (preferred) return { entry: DEFAULT_ENTRY_NAME, id: preferred };

	const [entry, id] = Object.entries(account.ids)[0] ?? [];

	return entry && id ? { entry, id } : null;
}

/**
 * The handle one entry sends for this account.
 *
 * Its own where it has one, because the server answers two questions from a handle and only one of
 * them is document-wide: which stored key this is, and what this entry had already spelled out. The
 * second is answered only from the entry the handle was issued for, so borrowing another's answers
 * it with nothing - and every setting this entry had spelled out at what is also the built-in
 * default drops back out of the file, leaving it sparser than it was written and letting a later
 * change to that default move an entry that had said otherwise.
 *
 * Otherwise any handle the account carries, which is what adoption rests on: without one the save
 * is refused and the administrator is asked for a key they should never have to type.
 *
 * Two accounts in one entry can never carry the same handle. Every handle is recorded against the
 * single row it was read from, and an entry holds at most one selection per row.
 */
export function accountHandle(account: EditableAccount, entryName: string): string | undefined {
	return account.ids[entryName] ?? savedAccountHandle(account)?.id;
}

/**
 * Whose stored account the handle {@link accountHandle} sends for this entry resolves to.
 *
 * The server answers a kept key from the account the handle was issued against, wherever in the
 * document that is - so what an entry will be written with is a property of the handle's owner
 * rather than of the entry sending it. The two are the same entry whenever it declared the account
 * itself, and differ exactly when it is adopting one.
 *
 * Resolved from the handle rather than recomputed alongside it, so this cannot answer for one handle
 * while the save sends another.
 */
function handleOwner(account: EditableAccount, entryName: string): string | null {
	const handle = accountHandle(account, entryName);

	if (!handle) return null;

	const owner = Object.entries(account.ids).find(([, id]) => id === handle);

	return owner?.[0] ?? null;
}

function toAccountDto(
	selection: AccountSelection,
	account: EditableAccount,
	entryName: string
): AdminAccountSettingsDto {
	return {
		...selection.values,
		id: accountHandle(account, entryName),
		// Credentials come from the account rather than from this entry's copy of it, which is what
		// makes editing one row change every entry that uses it. The key file especially: an adopted
		// account whose key is read from a file, sent without the path, is written with neither a key
		// nor a file and the loader refuses the save.
		label: account.label.trim() || undefined,
		immichServerUrl: account.serverUrl,
		// Normalised rather than sent as the empty string the input holds after it is cleared: an
		// empty ApiKeyFile is not the built-in default, so the server would spell it out in the file.
		apiKeyFile: usesApiKeyFile(account) ? account.apiKeyFile : undefined,
		// An account naming a key file has no key of its own to send; the server reads it from that
		// path, and a file naming both an ApiKey and an ApiKeyFile is refused by the loader.
		apiKey: usesApiKeyFile(account)
			? undefined
			: account.hasStoredKey && !account.entering
				? SECRET_PLACEHOLDER
				: account.apiKey
	};
}

function toEntryDto(
	entry: EditableEntry,
	accounts: Map<string, EditableAccount>
): AdminConfigEntryDto {
	const general: AdminGeneralSettingsDto = { ...entry.general };
	const cleared: string[] = [];

	for (const prop of SECRET_PROPS) {
		const key = declaredKeyOf(prop);

		// An undeclared secret is never read by the server, and leaving it as the null the read
		// handed us is more honest than sending it the empty string.
		if (!entry.declared.includes(key)) continue;

		const secret = entry.secrets[prop];

		// "No secret here" is the empty string on the wire either way, but it lands differently one
		// level down. A profile keeps the key and gets an explicit null, which is what overrides an
		// inherited secret with none. The default configuration has nothing above it to override, so
		// there the server writes no key at all - and the declared set has to say the same, or it
		// would disagree with the file on the very next read.
		if (secret.mode === 'none' && entry.isDefault) cleared.push(key);

		Object.assign(general, {
			// Spelled out rather than falling through to secret.value, so a value left in the box
			// before "no secret" was chosen cannot be sent as the secret.
			[prop]:
				secret.mode === 'keep' ? SECRET_PLACEHOLDER : secret.mode === 'none' ? '' : secret.value
		});
	}

	return {
		name: entry.name,
		// Exactly the keys the administrator means to declare, never the merged result: writing a
		// profile as its merged values would look identical today and then quietly cut it off from
		// every later edit to the default configuration.
		declaredKeys: entry.declared.filter((key) => {
			if (cleared.includes(key)) return false;

			// A key whose value is null declares nothing a file can hold: `"PrimaryColor": null`
			// binds to the same built-in default as leaving the key out. Dropped rather than
			// written, so ticking the override on a setting that has no built-in value and typing
			// nothing is a no-op rather than a null in the settings file.
			const prop = generalPropOf(key);

			return prop === null || (general[prop] !== null && general[prop] !== undefined);
		}),
		general,
		// Only the ticked ones: an unticked selection is kept so that ticking the account again
		// restores it, and writing it out would make the tick decorative.
		accounts: usedSelections(entry).flatMap((selection) => {
			const account = accounts.get(selection.accountKey);

			// Removing an account removes it from every entry, so a selection with no account behind
			// it is a row mid-removal rather than an account to write out with no credentials at all.
			return account ? [toAccountDto(selection, account, entry.name)] : [];
		})
	};
}

export function toUpdate(config: EditableConfig): AdminConfigUpdateDto {
	const accounts = accountsByKey(config);

	return {
		version: config.version,
		default: toEntryDto(config.default, accounts),
		profiles: config.profiles.map((profile) => toEntryDto(profile, accounts)),
		convertLegacySchema: config.convertLegacySchema
	};
}

/**
 * oazapfts types a response as the single status the OpenAPI document declares but returns whatever
 * the server actually answered, so every refusal arrives as a value rather than a throw.
 * `src/routes/[config]/+page.ts` widens the same way for its 404.
 */
export function statusOf(response: { status: number }): number {
	return response.status;
}

/** The server writes these messages for an operator, so they are shown as they arrive. */
export function problemDetail(data: unknown, fallback: string): string {
	if (!data || typeof data !== 'object') return fallback;

	const problem = data as { detail?: unknown; title?: unknown; errors?: unknown };

	if (typeof problem.detail === 'string' && problem.detail.trim()) return problem.detail;

	// A request the controller never sees carries its messages somewhere else. Model binding fails
	// before the action runs and [ApiController] answers with a ValidationProblemDetails, which has
	// a title and an `errors` map and no `detail` at all - and that is exactly what a mistyped album
	// or person UUID produces, which the pickers narrow the chances of but do not remove.
	const messages =
		problem.errors && typeof problem.errors === 'object'
			? Object.entries(problem.errors as Record<string, unknown>).flatMap(([field, value]) =>
					(Array.isArray(value) ? value : [value])
						.filter((message): message is string => typeof message === 'string')
						.map((message) => (field && field !== '$' ? `${field}: ${message}` : message))
				)
			: [];

	if (messages.length > 0) return messages.join(' ');
	if (typeof problem.title === 'string' && problem.title.trim()) return problem.title;

	return fallback;
}

export function profileNameError(name: string, existing: string[]): string | null {
	if (!PROFILE_NAME_PATTERN.test(name)) {
		return 'A profile name may only contain letters, digits, underscores and hyphens.';
	}

	if (RESERVED_PROFILE_NAMES.includes(name.toLowerCase())) {
		return `'${name}' is a reserved name.`;
	}

	if (existing.some((other) => other.toLowerCase() === name.toLowerCase())) {
		return `There is already a profile named '${name}'. Profile names are case-insensitive.`;
	}

	return null;
}

/**
 * How a message names an account. Its label if it has one, and otherwise the server URL - never a
 * name made up here, because a name this editor invented would be indistinguishable in the settings
 * file from one the administrator chose.
 */
export function accountName(account: EditableAccount, index: number): string {
	return account.label.trim() || account.serverUrl.trim() || `account ${index + 1}`;
}

/** How a message names one configuration, given the name the API uses for it. */
export function namedEntry(name: string): string {
	return name === DEFAULT_ENTRY_NAME ? 'the default configuration' : `profile '${name}'`;
}

export function entryLabel(entry: EditableEntry): string {
	return namedEntry(entry.isDefault ? DEFAULT_ENTRY_NAME : entry.name);
}

/**
 * The one disagreement between an account's copies that a save cannot write through.
 *
 * An account kept on its stored key sends the masking placeholder, and the server resolves that to
 * the key stored against the handle the entry carries. Which stored account that is is a question
 * about the handle rather than about the entry sending it: an entry adopting an account it never
 * declared borrows another entry's handle, and it is that entry's stored copy the placeholder
 * resolves against. A stored copy that reads its key from a file has no stored key beside it - the
 * loader refuses a file naming both - so whichever entry sends the placeholder against it would be
 * written with neither a key nor a key file, and the save refused for a file the administrator never
 * edited.
 *
 * Asked per written entry and answered from that entry's handle, therefore, rather than from its own
 * name: scoping it to entries that hold a {@link EditableAccount.keyFromFile} record misses every
 * adoption, because the record belongs to the entry that declared the account and not to the one
 * borrowing its handle. Where the entry did declare it the owner is itself, which is the same test
 * as before.
 *
 * Refused here instead, naming the path and the two ways out. Not resolved: which of the two is this
 * account's credential is a question about the installation, and the row cannot answer it. The
 * opposite direction needs no refusal, because it does write through - a row that names a key file
 * sends the path to every entry, and one whose key is being typed sends that key to every entry.
 */
function keptKeyErrors(account: EditableAccount, name: string, writtenBy: string[]): string[] {
	const stranded = writtenBy.flatMap((entryName) => {
		const owner = handleOwner(account, entryName);

		// An own key rather than a truthy value: what the record holds is a path, and it is the
		// presence of one that says this entry's stored copy reads its key from a file. `perEntry`
		// leaves no prototype for a lookup to answer from, but an entry name comes out of the settings
		// file - 'constructor' and 'toString' are both legal profile names - so asking for an own key
		// says what is meant whatever the record is built from.
		if (owner === null || !Object.hasOwn(account.keyFromFile, owner)) return [];

		// The path is never blank: the read sets the flag this record is built from only where there
		// is one, `ApiKeyFromFile = !IsNullOrWhiteSpace(ApiKeyFile)`.
		return [{ entry: entryName, owner, path: account.keyFromFile[owner].trim() }];
	});

	if (stranded.length === 0) return [];

	const where = stranded
		.map(({ entry, owner, path }) =>
			// Whose key file it is, where that is not the entry being written: an administrator sent to
			// look at profile 'hall' for a key file that is the default configuration's would not find
			// one there.
			entry === owner
				? `${namedEntry(entry)} (${path})`
				: `${namedEntry(entry)} (keeping ${namedEntry(owner)}'s key, read from ${path})`
		)
		.join(', ');

	return [
		`${name} has no API key file set above, but the stored API key it keeps is read from one. ` +
			`Saving would leave ${where} with neither a key nor a key file. Give this account that key ` +
			'file path to read the key from the file everywhere, or replace its key to store one in the ' +
			'settings file instead.'
	];
}

/**
 * What the editor can tell is wrong before the file is touched. Deliberately not a re-implementation
 * of the server's validation - it is the last word - but the cases where letting the save run would
 * either be refused for something the browser already knows, or accepted into a configuration that
 * cannot serve an image.
 */
export function validationErrors(config: EditableConfig): string[] {
	const errors: string[] = [];
	const accounts = accountsByKey(config);
	/** Which entries write each account out, by {@link EditableAccount.key}. */
	const used = new Map<string, string[]>();

	for (const entry of [config.default, ...config.profiles]) {
		for (const prop of Object.keys(generalFields) as GeneralProp[]) {
			const spec = generalFields[prop];
			if (!entry.declared.includes(declaredKeyOf(prop))) continue;

			if (spec.kind === 'integer' || spec.kind === 'decimal') {
				const value = entry.general[prop];
				if (typeof value !== 'number' || !Number.isFinite(value)) {
					errors.push(`${entryLabel(entry)}: '${spec.label}' needs a number.`);
				}

				continue;
			}

			// An empty box mid-edit is the dangerous one, and it reads as harmless: it would
			// silently unset a secret the administrator only meant to replace. Having no secret is
			// a choice of its own here - and on a profile it is an override rather than an absence
			// - so an empty box is refused rather than quietly taken for either.
			if (spec.kind === 'secret' && entry.secrets[prop as SecretProp].mode === 'set') {
				if (!entry.secrets[prop as SecretProp].value) {
					errors.push(
						`${entryLabel(entry)}: enter a value for '${spec.label}', choose to have none, ` +
							'or turn its override off.'
					);
				}
			}
		}

		if (!entry.declared.includes(ACCOUNTS_KEY)) continue;

		const selections = usedSelections(entry);

		if (selections.length === 0) {
			// The default is refused by name on the server - it is what every profile inherits - so
			// unticking its last account is caught here, where it can be explained rather than
			// arriving as a rejected save.
			errors.push(
				entry.isDefault
					? 'The default configuration must use at least one Immich account. It is what every ' +
							'configuration profile inherits, and ImmichFrame cannot serve an image without one.'
					: `${entryLabel(entry)} declares its own accounts but uses none.`
			);
		}

		const labelled = new Set<string>();
		const unlabelled = new Set<string>();

		for (const selection of selections) {
			const account = accounts.get(selection.accountKey);

			if (!account) continue;

			used.set(account.key, [...(used.get(account.key) ?? []), entry.name]);

			const label = account.label.trim();

			if (label) {
				// The server refuses this too, and a hand-written file can already be in the state,
				// since labels are normalised on read. Two rows sharing a name would be two accounts
				// nothing on screen can tell apart, so it is refused rather than resolved.
				if (labelled.has(label.toLowerCase())) {
					errors.push(
						`${entryLabel(entry)}: two accounts are labelled '${label}'. A label is what tells ` +
							'one account from another here, so give them names that differ.'
					);
				}

				labelled.add(label.toLowerCase());
				continue;
			}

			// Unlabelled accounts on one server are told apart by their order within the entry, which
			// is deterministic and fragile: reordering the list by hand re-pairs them with the
			// default configuration's. A label is the only stable answer, so one is asked for.
			const url = normalizedServerUrl(account.serverUrl);

			if (unlabelled.has(url)) {
				errors.push(
					`${entryLabel(entry)}: two accounts with no label are on the same Immich server ` +
						`(${account.serverUrl.trim() || 'no server URL'}). Give them labels, so that which ` +
						'is which does not depend on the order they are listed in.'
				);
			}

			unlabelled.add(url);
		}
	}

	config.accounts.forEach((account, index) => {
		// An account no configuration uses is written to no part of the file, so there is nothing
		// here to be wrong yet. The accounts section is where it says it will be dropped.
		const writtenBy = used.get(account.key);

		if (!writtenBy) return;

		const name = accountName(account, index);

		if (!account.serverUrl.trim()) {
			errors.push(`${name} needs an Immich server URL.`);
		}

		// A key file is the credential, and an account carrying a handle keeps its stored key
		// wherever it is used - including in an entry that has just adopted it, which is what the
		// handle is for. What is left is an account nothing in the file has a key for.
		if (usesApiKeyFile(account)) return;

		if (account.hasStoredKey && !account.entering) {
			errors.push(...keptKeyErrors(account, name, writtenBy));
			return;
		}

		if (!account.apiKey) {
			errors.push(`Enter the API key for ${name}.`);
		}
	});

	return errors;
}
