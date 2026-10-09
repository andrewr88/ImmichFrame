import type {
	AdminAccountSettingsDto,
	AdminConfigDto,
	AdminConfigEntryDto,
	AdminConfigSourceDto,
	AdminConfigUpdateDto,
	AdminGeneralSettingsDto
} from '$lib/immichFrameApi';
import { format } from 'date-fns';
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
const RESERVED_PROFILE_NAMES = [
	'api',
	'static',
	'swagger',
	'admin',
	DEFAULT_ENTRY_NAME,
	// The OpenID Connect handshake paths, which the server reserves whether or not sign-in is on.
	'signin-oidc',
	'signout-callback-oidc',
	'signout-oidc'
];

/** `ConfigCatalog.ValidProfileName()`. */
const PROFILE_NAME_PATTERN = /^[A-Za-z0-9_-]{1,64}$/;

/** The three general settings the API masks; every other general setting is a plain value. */
export const SECRET_PROPS = ['weatherApiKey', 'webhook', 'authenticationSecret'] as const;

export type SecretProp = (typeof SECRET_PROPS)[number];

/** The read-only flags a read reports each secret's presence through. */
type SecretPresence = 'hasWeatherApiKey' | 'hasWebhook' | 'hasAuthenticationSecret';

/**
 * Which flag says whether each secret is stored, since the secret itself never reaches the browser.
 *
 * Exhaustive over {@link SecretProp} on purpose, as {@link generalFields} is over `GeneralProp`: a
 * secret added to the API fails to compile here until it is given its flag, rather than falling
 * through to another secret's and reporting that one's presence as its own.
 */
export const SECRET_PRESENCE: Record<SecretProp, SecretPresence> = {
	weatherApiKey: 'hasWeatherApiKey',
	webhook: 'hasWebhook',
	authenticationSecret: 'hasAuthenticationSecret'
};

/** Every general setting the editor can write; the `has…` flags are read-only presence reports. */
export type GeneralProp = Exclude<keyof AdminGeneralSettingsDto, SecretPresence>;

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

/** The general settings a frame hands to date-fns as format patterns, which are free text. */
const DATE_FORMAT_PROPS: readonly GeneralProp[] = [
	'clockFormat',
	'clockDateFormat',
	'photoDateFormat'
];

/** Every key of {@link generalFields} appears in exactly one of these. */
export const generalSections: { title: string; blurb: string; props: GeneralProp[] }[] = [
	{
		title: 'Slideshow',
		blurb: 'How long each photo stays on screen, and how it moves.',
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
		blurb: 'What is drawn over the photo.',
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
		blurb: 'Colour, type and language of the frame itself.',
		props: ['primaryColor', 'secondaryColor', 'style', 'baseFontSize', 'language']
	},
	{
		title: 'Weather',
		blurb: 'The weather panel and where it reads from.',
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
		blurb: 'Caching, refresh intervals and the secrets a frame must send.',
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
 * The account settings the server holds as whole numbers, `int?` on `AdminAccountSettingsDto`. Read
 * off their kind rather than listed by name, so that one added to {@link accountFields} as an
 * integer is checked as one without anything here having to remember it.
 */
const wholeNumberAccountProps = accountProps.filter(
	(prop) => accountFields[prop].kind === 'integer'
);

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
	 * copy, so which entry is about to lose its version of them has to be visible first. All but a key
	 * file, which the server keeps only for a copy that already reads it, so that where the copies
	 * disagree about one `validationErrors` refuses the save rather than write it.
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
 * Turns one entry's override of a general setting on or off, as the setting's override control
 * does.
 *
 * Off stops declaring the key and leaves the value where it is, unsent: only what an entry declares
 * is written. On starts the override from what was being inherited - every time, since that is what
 * the row was showing a moment before - so a profile's override turned off and on again starts again
 * from the default configuration's value rather than from the one it held. The default
 * configuration's is not seeded at all, and so gets back what it held. A secret is not seeded
 * either way: what it is set to lives in {@link EditableEntry.secrets}, which this leaves alone.
 */
export function toggleGeneral(
	entry: EditableEntry,
	prop: GeneralProp,
	declared: boolean,
	inheritFrom: EditableEntry | null
): void {
	const key = declaredKeyOf(prop);

	if (!declared) {
		entry.declared = entry.declared.filter((other) => other !== key);
		return;
	}

	if (entry.declared.includes(key)) return;

	// An override starts from what was being inherited, so adding one and saving without further
	// edits changes nothing but which configuration the value is written in. The default
	// configuration needs no seeding: what it shows for an undeclared key is already the
	// setting's built-in default, straight off the merged read.
	if (inheritFrom) Object.assign(entry.general, { [prop]: inheritFrom.general[prop] });

	entry.declared = [...entry.declared, key];
}

/**
 * Turns one entry's account override on or off, as the photo selection's override control does.
 *
 * Off stops declaring the list and keeps every selection in it, ticked or not, for the override
 * being turned on again; a list an entry does not declare is not sent. On where it is already on
 * changes nothing: the default configuration always declares its accounts, and is asked to by
 * anything that turns every entry's override on.
 */
export function toggleAccounts(
	entry: EditableEntry,
	declared: boolean,
	inheritFrom: EditableEntry | null
): void {
	if (!declared) {
		entry.declared = entry.declared.filter((other) => other !== ACCOUNTS_KEY);
		return;
	}

	if (entry.declared.includes(ACCOUNTS_KEY)) return;

	// A declared account list replaces the inherited one outright, so the profile starts from what
	// it was inheriting: the same accounts, ticked, showing the same photos. Each keeps the handle
	// the read issued for it under the default configuration, so their stored API keys come across
	// on save without the administrator retyping a key they were already using.
	//
	// Only when this profile has nothing of its own, which is the first time the override is
	// turned on and no other. Seeding again would overwrite whatever it had been given since -
	// including selections it is holding unticked - with the default configuration's, and turning
	// an override off and on again is not a request to discard the override's contents.
	if (!entry.accountsWereDeclared && entry.accounts.length === 0) {
		entry.accounts = (inheritFrom ? usedSelections(inheritFrom) : []).map((selection) =>
			newSelection(selection.accountKey, selection.values)
		);
	}

	entry.declared = [...entry.declared, ACCOUNTS_KEY];
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

/**
 * Adds an account with nothing filled in and no stored key anywhere, as the accounts section's Add
 * account does, and returns it.
 *
 * Given to the default configuration straight away. It is the one entry that must declare
 * accounts, so it is where an account added from there is nearly always meant to be used - and an
 * account no configuration uses is written to no part of the settings file. Untick it there to make
 * it a profile's alone.
 */
export function addAccount(config: EditableConfig): EditableAccount {
	const account = newAccount();

	config.accounts = [...config.accounts, account];
	config.default.accounts = [...config.default.accounts, newSelection(account.key)];

	return account;
}

/**
 * Why this account cannot be removed, or null where it can.
 *
 * `AdminConfigService` refuses a save that leaves the default configuration using no account, and
 * refuses it by name - the default configuration is what every profile inherits - so removing the
 * last account it uses is refused here instead, where it can be explained before anything is lost.
 * The last one it *uses*: a selection it holds unticked is written nowhere, so it serves nothing,
 * and the account behind it can go.
 */
export function removeAccountRefusal(
	config: EditableConfig,
	account: EditableAccount
): string | null {
	const usedByDefault = entriesUsingAccount(config, account).some((entry) => entry.isDefault);

	if (!usedByDefault || usedSelections(config.default).length !== 1) return null;

	const name = accountName(
		account,
		config.accounts.findIndex((other) => other.key === account.key)
	);

	return (
		`${name} is the only Immich account the default configuration uses, and that is what ` +
		'every configuration profile inherits. Give the default configuration another account ' +
		'first: ImmichFrame cannot serve an image without one.'
	);
}

/**
 * Takes an account out of the configuration: its row, and every entry's selection of it.
 *
 * Dropped from every entry, declaring or not and ticked or not: a selection left behind would name
 * an account that no longer has any credentials at all. Asked nothing first - whether it may go is
 * {@link removeAccountRefusal}'s question, and whether it should is the administrator's.
 */
export function removeAccount(config: EditableConfig, account: EditableAccount): void {
	config.accounts = config.accounts.filter((other) => other.key !== account.key);

	for (const entry of [config.default, ...config.profiles]) {
		entry.accounts = entry.accounts.filter((selection) => selection.accountKey !== account.key);
	}
}

export function usesApiKeyFile(account: EditableAccount): boolean {
	return !!account.apiKeyFile.trim();
}

/** The accounts section's rows, by the key an entry's selection names them with. */
export function accountsByKey(config: EditableConfig): Map<string, EditableAccount> {
	return new Map(config.accounts.map((account) => [account.key, account]));
}

/** Accounts sharing one label within one configuration's own account list. */
export interface LabelCollision {
	entry: EditableEntry;
	/**
	 * {@link EditableAccount.key} of each account carrying the label, in the order `entry` lists
	 * them.
	 */
	keys: string[];
}

/**
 * Every label two or more accounts share within one configuration, one group per label.
 *
 * `AdminConfigService.Accounts` refuses a list holding one label twice, because a label is what says
 * two entries mean one account, and a hand-written file can already be in the state: labels are
 * normalised on read. Asked here for both of the places that need it - `validationErrors`, which
 * refuses the save, and the accounts section, which marks the rows before Save is pressed - so that
 * the two cannot come to disagree about what collides.
 *
 * Compared as the server compares them: trimmed and without regard to case, so that "Mum" and "mum "
 * are the one name they look like on screen. An account with no label is in no group, rather than
 * grouped under the empty one - absence asserts no identity, and two unlabelled accounts on one
 * server have a refusal of their own. Scoped to the configurations that declare their own accounts,
 * and to what each of them uses: two accounts sharing a label are ambiguous only within an entry
 * that writes both, and an inheriting profile is showing the default configuration's list rather
 * than one of its own.
 */
export function labelCollisions(config: EditableConfig): LabelCollision[] {
	const accounts = accountsByKey(config);

	return [config.default, ...config.profiles].flatMap((entry) => {
		if (!entry.declared.includes(ACCOUNTS_KEY)) return [];

		const byLabel = new Map<string, string[]>();

		for (const selection of usedSelections(entry)) {
			const label = accounts.get(selection.accountKey)?.label.trim().toLowerCase();

			if (label) byLabel.set(label, [...(byLabel.get(label) ?? []), selection.accountKey]);
		}

		return [...byLabel.values()].filter((keys) => keys.length > 1).map((keys) => ({ entry, keys }));
	});
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
	const declares = new Set(entry.declared.map((key) => key.toLowerCase()));
	const general: AdminGeneralSettingsDto = {};
	const cleared: string[] = [];

	// Only the general settings this entry declares, which are the only ones the server reads. The
	// model holds a value for every setting - the read reports the merged result, and an override
	// turned off keeps what it held rather than discarding it - and sending all of it put values in
	// front of the server's binder that nothing here checks any more, since `validationErrors` asks
	// only about what is declared. A fraction left in Interval before its override was turned off
	// refused the whole save, in the binder's words and about a setting the profile no longer
	// showed.
	//
	// Asked of every setting the read reported rather than only the ones this build has a field
	// for: a newer server's setting arrives in the read and in `DeclaredKeys` alike, and the server
	// takes a declared setting's value from the request whether or not this build knows it, so
	// leaving one out would write null over what the file held. Matched without regard to case, as
	// the server matches a declared key, because .NET's camel-casing is not `declaredKeyOf` run
	// backwards: `HDRMode` arrives as `hdrMode`.
	for (const [prop, value] of Object.entries(entry.general)) {
		if (declares.has(`general.${prop.toLowerCase()}`)) Object.assign(general, { [prop]: value });
	}

	for (const prop of SECRET_PROPS) {
		const key = declaredKeyOf(prop);

		// An undeclared secret is never read by the server, so it stays out with every other
		// undeclared setting rather than being sent as anything at all - least of all the empty
		// string, which is not an absence but a value: "no secret here".
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

	// Only the accounts this entry shows: the ticked ones, of a list it declares.
	//
	// An unticked selection is kept so that ticking the account again restores it, and writing it
	// out would make the tick decorative. A list the entry does not declare - one whose override
	// has been turned off - is kept the same way, for the override being turned on again, and the
	// server does not read it at all. So, like an undeclared general setting, it is not sent: a
	// rating of 2.5 left in it was refused by the binder, for a list the entry no longer had. Empty
	// rather than absent, because the request requires the member.
	const shown = entry.declared.includes(ACCOUNTS_KEY) ? usedSelections(entry) : [];

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
		accounts: shown.flatMap((selection) => {
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
 * The profiles a save will delete, once a profile called `name` has been added.
 *
 * A profile deleted and then added back under its name is not deleted by the save at all: it is
 * written as a new profile with nothing of its own, and every frame on it keeps working - so the
 * save bar has to stop saying it will be deleted. What does go is the settings the deleted one had,
 * and the editor already shows that, since the profile added back declares nothing. Compared
 * case-insensitively, as {@link profileNameError} compares, because that is how the server tells
 * one profile from another: 'Kitchen' added back as 'kitchen' is the same profile.
 */
export function withoutPendingDeletion(pending: string[], name: string): string[] {
	return pending.filter((deleted) => deleted.toLowerCase() !== name.toLowerCase());
}

/**
 * The profiles a save will delete, once the profile called `name` has been deleted.
 *
 * Only a profile the server holds is deleted by a save, so `onServer` is the profile names the last
 * read or save returned. One added since then never reached the settings file, and deleting it
 * again leaves nothing for a save to delete - listing it had the save bar say that a profile nobody
 * ever saved will be deleted when you save. Compared case-insensitively, as
 * {@link withoutPendingDeletion} compares: a profile deleted and added back in another case is still
 * the one the server holds, and deleting it a second time deletes that.
 */
export function withPendingDeletion(pending: string[], name: string, onServer: string[]): string[] {
	const held = onServer.some((stored) => stored.toLowerCase() === name.toLowerCase());

	return held ? [...pending, name] : pending;
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
 * One of the two disagreements between an account's copies that a save cannot write through.
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
 * opposite direction does not write through either - a row that names a key file sends the path to
 * every entry, and the server keeps it only for an entry whose handle names a copy that already
 * reads it - and {@link keyFileErrors} refuses that one. Only a key being typed is sent to every
 * entry and kept by every entry.
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
			'file path to read the key from the file, or replace its key to store one in the settings ' +
			'file instead.'
	];
}

/**
 * The disagreement a save cannot write through in the other direction: a key file, sent with every
 * entry that writes the account, where the server will not keep it.
 *
 * `AdminConfigService` keeps an ApiKeyFile only where it is exactly the one already stored on the
 * account the handle names - compared ordinally, untrimmed - because validating the save reads the
 * file it names into the account's API key, and a path taken from the editor would let an
 * administrator read any file ImmichFrame can and send it to a server of their choosing. So a row
 * naming a key file writes it through only to the entries whose handle names a stored copy that
 * reads that very file. Every other entry writing the account has the save refused by the server,
 * in words that name the account by its server URL and give neither path: one whose stored copy
 * keeps a key in the settings file instead, one whose stored copy reads another file, and every
 * entry of an account the read issued no handle for at all, whose path can only have been typed
 * here.
 *
 * Asked per written entry and answered from the handle's owner, as {@link keptKeyErrors} is and for
 * the same reason: an adopting entry sends a borrowed handle, and it is the copy that handle names
 * which the path is held against. The handle itself is left alone. Sending another entry's instead,
 * one whose copy does read the path, would answer what this entry had already spelled out from that
 * other entry - see {@link accountHandle} - and would quietly trade the key this entry's copy stores
 * for the file, which is the choice this refusal leaves with the administrator.
 */
function keyFileErrors(account: EditableAccount, name: string, writtenBy: string[]): string[] {
	const path = account.apiKeyFile;
	const refused = writtenBy.flatMap((entryName) => {
		const owner = handleOwner(account, entryName);
		// The path the owner's stored copy reads, or null where it reads none - an own key, for the
		// reason `keptKeyErrors` gives. A record is only ever made for a path that is there, so a
		// stored copy with a blank one has none, and a blank path is not what is being sent.
		const stored =
			owner !== null && Object.hasOwn(account.keyFromFile, owner)
				? account.keyFromFile[owner]
				: null;

		// Exactly, as the server compares them. The path goes out as the box holds it, and a stray
		// space is another file to `File.ReadAllText`; quoted below for the same reason, so that a
		// difference only in spaces can be seen in the message at all.
		return stored === path ? [] : [{ entry: entryName, owner, stored }];
	});

	if (refused.length === 0) return [];

	const where = refused
		.map(({ entry, owner, stored }) => {
			const reads = stored === null ? 'keeps the key in the settings file' : `reads '${stored}'`;
			// Whose copy it is, where that is not the entry being written, for the reason
			// `keptKeyErrors` names the owner too: the profile to go and look at is that one.
			const copy =
				owner === null
					? 'nothing is stored for this account yet'
					: entry === owner
						? `its stored copy ${reads}`
						: `keeping ${namedEntry(owner)}'s copy, which ${reads}`;

			return `${namedEntry(entry)} (${copy})`;
		})
		.join(', ');

	return [
		`${name} names '${path}' as its API key file, which the settings on the server do not give it ` +
			`for ${where}. ImmichFrame keeps a key file only where they already do, since it reads ` +
			"whatever file is named. Clear the API key file above and enter the account's API key " +
			`instead, or untick ${name} there.`
	];
}

/** Why date-fns refuses a date format, or null where it takes it. */
function dateFormatError(pattern: string): string | null {
	try {
		format(new Date(), pattern);
		return null;
	} catch (error) {
		return error instanceof Error ? error.message : String(error);
	}
}

/**
 * `int.MinValue` and `int.MaxValue`. Every setting the editor calls an 'integer' is an `int?` on the
 * server, general and account alike, and System.Text.Json refuses a number past either end of the
 * range just as it refuses a fraction.
 */
const INT32_MIN = -2147483648;
const INT32_MAX = 2147483647;

/**
 * The account settings the server holds to a narrower range than an int's. `ImagesFromDays` is
 * bounded by `ServerAccountSettings.MaxImagesFromDays`: a negative number moves the cut-off into the
 * future, and one a date cannot count back from today failed every request for the account's photos.
 * The server refuses either, but in words that name neither the configuration nor the account.
 */
const accountRanges: Partial<Record<AccountProp, readonly [min: number, max: number]>> = {
	imagesFromDays: [0, 36500]
};

/**
 * What a box holding a whole-number setting needs to hold instead, or null where the server can take
 * what it holds. The range is an int's, unless the setting has a narrower one of its own.
 *
 * One answer per box, and the range comes first: a number past it is told the range whether or not
 * it is also a fraction, because that one sentence is everything it needs - told only that it needs
 * a whole number, 2147483647.5 could be rounded to 2147483648 and refused a second time. A fraction
 * inside the range is told only that, because the range has nothing to do with what is wrong with
 * it, and two billion is no help to someone who typed a rating of 2.5.
 */
function wholeNumberNeed(
	value: NonNullable<FieldValue>,
	[min, max]: readonly [number, number] = [INT32_MIN, INT32_MAX]
): string | null {
	if (typeof value === 'number' && (value < min || value > max)) {
		return `a whole number between ${min} and ${max}`;
	}

	return Number.isInteger(value) ? null : 'a whole number';
}

/**
 * What the editor can tell is wrong before the file is touched. Deliberately not a re-implementation
 * of the server's validation - it is the last word - but the cases where letting the save run would
 * either be refused for something the browser already knows, or accepted into a configuration that
 * cannot serve an image - or, for a date format, that every frame would quietly ignore.
 */
export function validationErrors(config: EditableConfig): string[] {
	const errors: string[] = [];
	const accounts = accountsByKey(config);
	const collisions = labelCollisions(config);
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
				} else if (spec.kind === 'integer') {
					// The server holds an 'integer' setting as an int, and System.Text.Json refuses a
					// fraction for one before the save is so much as looked at - and a whole number
					// past an int's range, which the box has no maximum to stop and `Number` reads
					// like any other. What came back was the model binder's message rather than
					// anything about this setting: a JSON path and a CLR type name, and on a profile
					// a path naming the profile by its position in the request rather than by its
					// name. The number box's step of 1 marks 2.5 invalid without stopping it being
					// committed, and rounding it there would save a number nobody typed - so it is
					// refused here, by name. Asked only of a number, since a box with none in it has
					// just been told so.
					const need = wholeNumberNeed(value);

					if (need) errors.push(`${entryLabel(entry)}: '${spec.label}' needs ${need}.`);
				}

				continue;
			}

			// A frame shows its own default in place of a format date-fns refuses, and says so only in
			// a console nobody in front of a wall display is reading - so the save is refused here,
			// with date-fns's reason, where it can be acted on. An empty box is left alone: a frame
			// takes it for no format at all, and uses the default for that too.
			if (DATE_FORMAT_PROPS.includes(prop)) {
				const value = entry.general[prop];
				const error = typeof value === 'string' && value !== '' ? dateFormatError(value) : null;

				if (error) {
					errors.push(
						`${entryLabel(entry)}: '${spec.label}' is not a date format the frame can use: ${error}`
					);
				}

				continue;
			}

			// An empty box mid-edit is the dangerous one, and it reads as harmless: it would
			// silently unset a secret the administrator only meant to replace. Having no secret is
			// a choice of its own here - and on a profile it is an override rather than an absence
			// - so an empty box is refused rather than quietly taken for either.
			//
			// So are two values that do not look empty at all, because the server does not take
			// them for values either. Nothing but spaces is "no secret here" to it, however it is
			// spelled. And the mask is "keep what is stored" - which keeps the old secret through a
			// Replace, and where nothing is stored keeps nothing: the authentication secret is then
			// dropped from the default configuration, or written as the null that overrides an
			// inherited one with none, and every frame URL it guarded opens with nothing said.
			// Blank is whatever `trim` empties, the nearest JavaScript has to
			// `string.IsNullOrWhiteSpace`; the mask is compared exactly, as `AdminSecret.KeepsStored`
			// compares it.
			if (spec.kind === 'secret' && entry.secrets[prop as SecretProp].mode === 'set') {
				const value = entry.secrets[prop as SecretProp].value;

				if (!value.trim() || value === SECRET_PLACEHOLDER) {
					errors.push(
						`${entryLabel(entry)}: enter a value for '${spec.label}', choose to have none, ` +
							'or turn its override off. A value cannot be blank - the server would take ' +
							`that for no secret at all - nor ${SECRET_PLACEHOLDER}, which asks it to keep ` +
							'the stored one.'
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

		// Each account after the first to carry a label this entry has already met, which is where
		// reading the list in order first finds the collision - so the refusal names the label the
		// way the account that repeats it spells it.
		const repeats = new Set(
			collisions
				.filter((collision) => collision.entry === entry)
				.flatMap((collision) => collision.keys.slice(1))
		);
		const unlabelled = new Set<string>();

		for (const selection of selections) {
			const account = accounts.get(selection.accountKey);

			if (!account) continue;

			used.set(account.key, [...(used.get(account.key) ?? []), entry.name]);

			// The same refusal as a general setting's, for what this entry shows from the account -
			// where the binder's path was worse still, naming the account by its place in a list,
			// and on a profile the profile by its place in the request. Blank is a value of its own
			// here, no limit or any rating, so only a value that is there, and is not a whole number
			// an int can hold, is refused.
			const name = accountName(account, config.accounts.indexOf(account));

			for (const prop of wholeNumberAccountProps) {
				const value = selection.values[prop];
				const need =
					value === null || value === undefined
						? null
						: wholeNumberNeed(value, accountRanges[prop]);

				if (!need) continue;

				errors.push(
					`${entryLabel(entry)}: '${accountFields[prop].label}' for ${name} needs ${need}, ` +
						'or to be left blank.'
				);
			}

			const label = account.label.trim();

			if (label) {
				// The server refuses this too - see `labelCollisions`. Two rows sharing a name would
				// be two accounts nothing on screen can tell apart, so it is refused rather than
				// resolved.
				if (repeats.has(account.key)) {
					errors.push(
						`${entryLabel(entry)}: two accounts are labelled '${label}'. A label is what tells ` +
							'one account from another here, so give them names that differ.'
					);
				}

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

		// A key file is the credential, though only in the entries the server will keep it for. An
		// account carrying a handle keeps its stored key wherever it is used - including in an entry
		// that has just adopted it, which is what the handle is for. What is left is an account
		// nothing in the file has a key for.
		if (usesApiKeyFile(account)) {
			errors.push(...keyFileErrors(account, name, writtenBy));
			return;
		}

		if (account.hasStoredKey && !account.entering) {
			errors.push(...keptKeyErrors(account, name, writtenBy));
			return;
		}

		if (!account.apiKey) {
			errors.push(`Enter the API key for ${name}.`);
		}
	});

	// Each problem once, in the order it was first found. One cause can be found more than once in
	// the same words - two new rows given one label and nothing else each need a server URL and a
	// key, under the one name, and a third row with that label collides with the second as well as
	// the first - and this list is read as the things to fix before a save, where a sentence said
	// twice reads as two of them.
	return [...new Set(errors)];
}
