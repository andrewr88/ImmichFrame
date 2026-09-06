import type {
	AdminAccountSettingsDto,
	AdminConfigDto,
	AdminConfigEntryDto,
	AdminConfigSourceDto,
	AdminConfigUpdateDto,
	AdminGeneralSettingsDto
} from '$lib/immichFrameApi';

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

/** Every account setting rendered by the generic field renderer; the rest have bespoke UI. */
export type AccountProp = Exclude<
	keyof AdminAccountSettingsDto,
	'id' | 'apiKey' | 'hasApiKey' | 'apiKeyFromFile' | 'immichServerUrl' | 'apiKeyFile'
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

export interface EditableAccount {
	/**
	 * The handle the read issued, echoed back unchanged so the server can put a masked key back on
	 * the account it came from. Null on an inherited account and on one the editor has just added -
	 * neither has a stored key of its own, so both must be given one outright.
	 */
	id: string | null;
	values: AdminAccountSettingsDto;
	/** This account has a key stored in the settings file under the configuration being edited. */
	hasStoredKey: boolean;
	/** The administrator is typing a key rather than keeping the stored one. */
	entering: boolean;
	apiKey: string;
	/**
	 * The server URL this account was read with, kept so an edit to it can be noticed. A stored API
	 * key belongs to the server it was stored against, so once the URL in the form has moved on, the
	 * stored key is no longer a credential for what the form describes.
	 */
	savedServerUrl: string | null;
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
	accounts: EditableAccount[];
	accountsWereDeclared: boolean;
}

export interface EditableConfig {
	version: string;
	source: AdminConfigSourceDto;
	default: EditableEntry;
	profiles: EditableEntry[];
	convertLegacySchema: boolean;
}

function toAccount(dto: AdminAccountSettingsDto, declared: boolean): EditableAccount {
	const fromFile = dto.apiKeyFromFile === true;

	return {
		id: declared ? (dto.id ?? null) : null,
		// The whole DTO is kept, not just the fields the editor renders, so a setting this build
		// does not know about survives a round trip instead of being written back as null.
		values: { ...dto, apiKey: undefined },
		hasStoredKey: declared && !fromFile && dto.hasApiKey === true && !!dto.id,
		entering: declared && !fromFile && dto.hasApiKey !== true,
		apiKey: '',
		savedServerUrl: dto.immichServerUrl ?? null
	};
}

function toEntry(dto: AdminConfigEntryDto, isDefault: boolean): EditableEntry {
	const declared = [...(dto.declaredKeys ?? [])];
	const accountsDeclared = declared.includes(ACCOUNTS_KEY);
	const secrets = {} as Record<SecretProp, SecretEdit>;

	for (const prop of SECRET_PROPS) {
		// Only a configuration that already declares a secret has one of its own to keep. Defaulting
		// the rest to 'set' is what stops adding an override from writing the placeholder's stand-in
		// for a value the server would look for and not find.
		secrets[prop] = { mode: declared.includes(declaredKeyOf(prop)) ? 'keep' : 'set', value: '' };
	}

	return {
		name: dto.name ?? (isDefault ? DEFAULT_ENTRY_NAME : ''),
		isDefault,
		declared,
		originallyDeclared: [...declared],
		general: { ...(dto.general ?? {}) },
		secrets,
		accounts: (dto.accounts ?? []).map((account) => toAccount(account, accountsDeclared)),
		accountsWereDeclared: accountsDeclared
	};
}

export function toEditable(dto: AdminConfigDto): EditableConfig {
	return {
		version: dto.version ?? '',
		source: dto.source ?? {},
		default: toEntry(dto.default ?? {}, true),
		profiles: (dto.profiles ?? []).map((profile) => toEntry(profile, false)),
		convertLegacySchema: false
	};
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
export function newAccount(values: AdminAccountSettingsDto = {}): EditableAccount {
	return {
		id: null,
		values: { ...values, id: undefined, apiKey: undefined },
		hasStoredKey: false,
		entering: true,
		apiKey: '',
		// No handle and no stored key, so there is nothing for a URL edit to disagree with.
		savedServerUrl: null
	};
}

export function usesApiKeyFile(account: EditableAccount): boolean {
	return !!account.values.apiKeyFile?.trim();
}

function toAccountDto(account: EditableAccount): AdminAccountSettingsDto {
	return {
		...account.values,
		id: account.id ?? undefined,
		// Normalised rather than sent as the empty string the input holds after it is cleared: an
		// empty ApiKeyFile is not the built-in default, so the server would spell it out in the file.
		apiKeyFile: usesApiKeyFile(account) ? account.values.apiKeyFile : undefined,
		// An account naming a key file has no key of its own to send; the server reads it from that
		// path, and a file naming both an ApiKey and an ApiKeyFile is refused by the loader.
		apiKey: usesApiKeyFile(account)
			? undefined
			: account.hasStoredKey && !account.entering
				? SECRET_PLACEHOLDER
				: account.apiKey
	};
}

function toEntryDto(entry: EditableEntry): AdminConfigEntryDto {
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
		accounts: entry.accounts.map(toAccountDto)
	};
}

export function toUpdate(config: EditableConfig): AdminConfigUpdateDto {
	return {
		version: config.version,
		default: toEntryDto(config.default),
		profiles: config.profiles.map(toEntryDto),
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

function accountLabel(account: EditableAccount, index: number): string {
	return account.values.immichServerUrl?.trim() || `account ${index + 1}`;
}

function entryLabel(entry: EditableEntry): string {
	return entry.isDefault ? 'the default configuration' : `profile '${entry.name}'`;
}

/**
 * What the editor can tell is wrong before the file is touched. Deliberately not a re-implementation
 * of the server's validation - it is the last word - but the cases where letting the save run would
 * either be refused for something the browser already knows, or accepted into a configuration that
 * cannot serve an image.
 */
export function validationErrors(config: EditableConfig): string[] {
	const errors: string[] = [];

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

		if (entry.accounts.length === 0) {
			errors.push(`${entryLabel(entry)} declares its own accounts but lists none.`);
		}

		entry.accounts.forEach((account, index) => {
			const label = accountLabel(account, index);

			if (!account.values.immichServerUrl?.trim()) {
				errors.push(`${entryLabel(entry)}: ${label} needs an Immich server URL.`);
			}

			if (usesApiKeyFile(account)) return;
			if (account.hasStoredKey && !account.entering) return;

			if (!account.apiKey) {
				errors.push(`${entryLabel(entry)}: enter the API key for ${label}.`);
			}
		});
	}

	return errors;
}
