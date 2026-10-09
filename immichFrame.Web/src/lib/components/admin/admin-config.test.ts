import { describe, expect, it, vi } from 'vitest';
import type { AdminGeneralSettingsDto } from '$lib/immichFrameApi';
import {
	ACCOUNTS_KEY,
	SECRET_PLACEHOLDER,
	accountHandle,
	accountsByKey,
	addAccount,
	declaredKeyOf,
	entriesUsingAccount,
	entryLabel,
	generalFields,
	labelCollisions,
	newAccount,
	problemDetail,
	profileNameError,
	removeAccount,
	removeAccountRefusal,
	savedAccountHandle,
	setAccountUse,
	toUpdate,
	toggleAccounts,
	toggleGeneral,
	usedSelections,
	validationErrors,
	withPendingDeletion,
	withoutPendingDeletion,
	type AccountProp,
	type EditableConfig,
	type GeneralProp,
	type SecretEdit
} from './admin-config';
import { DEFAULT_NAME, type DocumentFixture } from './admin-config.test-fixtures';
import {
	accountLabelled,
	accountWritten,
	accountsWritten,
	declareAccounts,
	entryNamed,
	open
} from './admin-config.test-editor';
import { SERVER_GENERAL_SETTINGS, simulateSave } from './admin-config.test-server-model';

const IMMICH = 'https://immich.example';

describe('toEditable: account identity', () => {
	it('one Immich account used by the default configuration and two profiles, is one row', () => {
		// Arrange: the same labelled account declared three times, spelled differently each time.
		const { config } = open({
			default: { accounts: [{ label: 'Att', url: IMMICH }] },
			profiles: [
				{ name: 'hall', accounts: [{ label: 'att', url: IMMICH }] },
				{ name: 'kiosk', accounts: [{ label: ' ATT ', url: 'HTTPS://IMMICH.EXAMPLE/' }] }
			]
		});

		// Assert: one row, carrying a handle from each of the three configurations that declared it.
		expect(config.accounts).toHaveLength(1);
		expect(Object.keys(config.accounts[0].ids)).toEqual([DEFAULT_NAME, 'hall', 'kiosk']);
	});

	it('two labels differing only in case and spaces, are one account', () => {
		// Arrange
		const { config } = open({
			default: { accounts: [{ label: 'Mum' }] },
			profiles: [{ name: 'hall', accounts: [{ label: '  mum  ' }] }]
		});

		// Assert: matched on the trimmed, case-folded label, and the row keeps the label as read.
		expect(config.accounts).toHaveLength(1);
		expect(config.accounts[0].label).toBe('Mum');
	});

	it('unlabelled accounts on one server, are matched by normalised URL and ordinal', () => {
		// Arrange: two unlabelled accounts on one Immich server, in both configurations, with the
		// profile spelling the URL differently.
		const { config } = open({
			default: { accounts: [{ url: IMMICH }, { url: IMMICH }] },
			profiles: [
				{ name: 'hall', accounts: [{ url: 'HTTPS://IMMICH.EXAMPLE/' }, { url: `${IMMICH}/` }] }
			]
		});

		// Assert: two rows, each paired with the profile's account at the same position.
		expect(config.accounts).toHaveLength(2);
		expect(config.accounts.map((account) => Object.keys(account.ids))).toEqual([
			[DEFAULT_NAME, 'hall'],
			[DEFAULT_NAME, 'hall']
		]);
	});

	it('a labelled and an unlabelled account at one URL, stay two accounts', () => {
		// Arrange
		const { config } = open({
			default: { accounts: [{ label: 'Att', url: IMMICH }, { url: IMMICH }] }
		});

		// Assert: the absence of a label asserts nothing, so no label is invented to match it with.
		expect(config.accounts).toHaveLength(2);
		expect(config.accounts.map((account) => account.label)).toEqual(['Att', '']);
	});

	it('a labelled account listed first, does not consume the unlabelled ordinal', () => {
		// Arrange: the profile's only account is unlabelled and therefore ordinal 0 on that server.
		// The default configuration's unlabelled account is at index 1, behind a labelled one.
		const { config } = open({
			default: { accounts: [{ label: 'Att', url: IMMICH }, { url: IMMICH }] },
			profiles: [{ name: 'hall', accounts: [{ url: IMMICH }] }]
		});

		// Assert: still two rows - the profile's joined the unlabelled one rather than making a third.
		expect(config.accounts).toHaveLength(2);
		expect(Object.keys(config.accounts[1].ids)).toEqual([DEFAULT_NAME, 'hall']);
		expect(Object.keys(config.accounts[0].ids)).toEqual([DEFAULT_NAME]);
	});

	it('two accounts in one entry whose labels collide, stay two rows', () => {
		// Arrange: a hand-written file can already be in this state, since labels are normalised on
		// read.
		const { config } = open({
			default: {
				accounts: [
					{ label: 'Mum', url: IMMICH },
					{ label: 'mum ', url: 'https://other.example' }
				]
			}
		});

		// Assert: merging them would edit one account's credentials through the other's.
		expect(config.accounts).toHaveLength(2);
		expect(validationErrors(config)).toEqual([
			expect.stringContaining("two accounts are labelled 'mum'")
		]);
	});

	it('a profile that declares no accounts, contributes no row', () => {
		// Arrange: the read shows an inheriting profile the default configuration's accounts, with no
		// handles of its own.
		const { config } = open({
			default: { accounts: [{ label: 'Att' }] },
			profiles: [{ name: 'hall' }]
		});

		// Assert: taking them again would turn one account into two.
		expect(config.accounts).toHaveLength(1);
		expect(Object.keys(config.accounts[0].ids)).toEqual([DEFAULT_NAME]);
		expect(entryNamed(config, 'hall').accounts).toEqual([]);
	});

	it('a stored key in one entry and a key file in another, is one row that has both', () => {
		// Arrange
		const { config } = open({
			default: { accounts: [{ label: 'Att', credential: 'file', apiKeyFile: '/run/secrets/a' }] },
			profiles: [{ name: 'hall', accounts: [{ label: 'Att', credential: 'key' }] }]
		});

		// Assert: a stored key is a fact about the document - any entry's handle reaches it - while a
		// key file is a fact about the entry that names it, because no handle can fetch one.
		const account = accountLabelled(config, 'Att');

		expect(account.hasStoredKey).toBe(true);
		expect(account.entering).toBe(false);
		expect(Object.keys(account.keyFromFile)).toEqual([DEFAULT_NAME]);
		expect(account.apiKeyFile).toBe('/run/secrets/a');
	});
});

describe('toUpdate: round-tripping an untouched configuration', () => {
	it('an untouched configuration, sends the handles and selections it was read with', () => {
		// Arrange
		const { read, config } = open({
			default: {
				declares: ['General.Interval'],
				general: { interval: 30 },
				accounts: [
					{ label: 'Att', url: IMMICH, values: { albums: ['album-1'], showMemories: true } },
					{ label: 'Backup', url: 'https://backup.example' }
				]
			},
			profiles: [{ name: 'hall' }]
		});

		// Act
		const update = toUpdate(config);

		// Assert: the same handles, the same photo selection, and the key kept rather than sent.
		expect(accountsWritten(update, DEFAULT_NAME).map((account) => account.id)).toEqual([
			read.handle(DEFAULT_NAME, 0),
			read.handle(DEFAULT_NAME, 1)
		]);

		const att = accountWritten(update, DEFAULT_NAME, 'Att');

		expect(att.albums).toEqual(['album-1']);
		expect(att.showMemories).toBe(true);
		expect(att.apiKey).toBe(SECRET_PLACEHOLDER);
		expect(att.apiKeyFile).toBeUndefined();
		expect(update.default?.declaredKeys).toEqual(['General.Interval', ACCOUNTS_KEY]);
		expect(update.default?.general?.interval).toBe(30);
	});

	it('a profile that inherits, still declares no accounts', () => {
		// Arrange
		const { read, config } = open({
			default: { accounts: [{ label: 'Att' }] },
			profiles: [{ name: 'hall' }]
		});

		// Act
		const update = toUpdate(config);

		// Assert: writing it back as the merged result would cut it off from later edits to the
		// default configuration.
		expect(update.profiles?.[0].declaredKeys).toEqual([]);
		expect(accountsWritten(update, 'hall')).toEqual([]);
		expect(simulateSave(update, read.storedById)).toEqual({ accepted: true });
	});

	it('a list setting seeded from another entry, is copied rather than shared', () => {
		// Arrange
		const { config } = open({
			default: { accounts: [{ label: 'Att', values: { albums: ['album-1'] } }] },
			profiles: [{ name: 'hall' }]
		});

		// Act: the profile is seeded from what it was inheriting, then edits its own list in place.
		const hall = entryNamed(config, 'hall');

		declareAccounts(config, hall);
		usedSelections(hall)[0].values.albums?.push('album-2');

		// Assert: two entries holding one array would be one list of albums reachable two ways.
		expect(usedSelections(config.default)[0].values.albums).toEqual(['album-1']);
		expect(usedSelections(hall)[0].values.albums).toEqual(['album-1', 'album-2']);
	});
});

describe('toUpdate: secrets', () => {
	/** What one configuration is saved with once its webhook is edited, both having one as read. */
	function savedWith(entryName: string, secret: SecretEdit) {
		const declaring = { declares: ['General.Webhook'], accounts: [{ label: 'Att' }] };
		const { config } = open({ default: declaring, profiles: [{ name: 'hall', ...declaring }] });

		entryNamed(config, entryName).secrets.webhook = secret;

		const update = toUpdate(config);

		return entryName === DEFAULT_NAME ? update.default : update.profiles?.[0];
	}

	const STALE = 'https://hooks.example/left-in-the-box';

	it.each<{ case: string; entry: string; secret: SecretEdit; sent: string; declared: boolean }>([
		{
			case: 'no secret on a profile',
			entry: 'hall',
			secret: { mode: 'none', value: '' },
			sent: '',
			declared: true
		},
		{
			case: 'no secret on the default configuration',
			entry: DEFAULT_NAME,
			secret: { mode: 'none', value: '' },
			sent: '',
			declared: false
		},
		{
			case: 'no secret, chosen with a value still in the box',
			entry: 'hall',
			secret: { mode: 'none', value: STALE },
			sent: '',
			declared: true
		},
		{
			case: 'the stored one kept, with a value still in the box',
			entry: DEFAULT_NAME,
			secret: { mode: 'keep', value: STALE },
			sent: SECRET_PLACEHOLDER,
			declared: true
		},
		{
			case: 'a value typed, spaces and all',
			entry: 'hall',
			secret: { mode: 'set', value: ' https://hooks.example/new ' },
			sent: ' https://hooks.example/new ',
			declared: true
		}
	])('$case, is sent as $sent', ({ entry, secret, sent, declared }) => {
		// Act
		const written = savedWith(entry, secret);

		// Assert: "no secret here" is the empty string on the wire either way, and lands one level
		// apart. A profile keeps the key, which the server writes as the explicit null that overrides
		// an inherited secret with none; the default configuration has nothing above it to override,
		// so the key goes and the next read agrees with the file. What is left in the box is sent
		// only when it is the value being set - and then exactly as typed.
		expect(written?.general?.webhook).toBe(sent);
		expect(written?.declaredKeys?.includes('General.Webhook')).toBe(declared);
	});

	it('a secret this configuration does not declare, is not sent at all', () => {
		// Arrange
		const { config } = open({
			default: { accounts: [{ label: 'Att' }] },
			profiles: [{ name: 'hall' }]
		});

		// Act: a value in the box of a secret whose override is off.
		entryNamed(config, 'hall').secrets.authenticationSecret = { mode: 'set', value: STALE };

		const written = toUpdate(config).profiles?.[0];

		// Assert: the server never reads an undeclared secret, so it is not sent as anything - least
		// of all the empty string, which is a value of its own: no secret here.
		expect(written?.general).toEqual({});
		expect(written?.declaredKeys).toEqual([]);
	});
});

describe('toUpdate: a declared setting with no value', () => {
	it.each([null, undefined])('a declared setting emptied to %s, declares nothing', (value) => {
		// Arrange: a profile overriding two settings.
		const { config } = open({
			default: { accounts: [{ label: 'Att' }] },
			profiles: [
				{
					name: 'hall',
					declares: ['General.PrimaryColor', 'General.ShowClock'],
					general: { primaryColor: '#f5deb3', showClock: false }
				}
			]
		});

		// Act: its colour box emptied.
		entryNamed(config, 'hall').general.primaryColor = value;

		// Assert: `"PrimaryColor": null` binds to the same built-in default as leaving the key out, so
		// the key is dropped rather than written as a null. What else it declares is untouched.
		expect(toUpdate(config).profiles?.[0].declaredKeys).toEqual(['General.ShowClock']);
	});

	it('an override ticked on a setting with no built-in value, declares nothing', () => {
		// Arrange
		const { config } = open({
			default: { accounts: [{ label: 'Att' }] },
			profiles: [{ name: 'hall' }]
		});
		const hall = entryNamed(config, 'hall');

		// Act: the override ticked, which seeds it with what is inherited - nothing.
		toggleGeneral(hall, 'primaryColor', true, config.default);

		// Assert: ticked in the editor, and no null in the settings file.
		expect(hall.declared).toEqual(['General.PrimaryColor']);
		expect(toUpdate(config).profiles?.[0].declaredKeys).toEqual([]);
	});
});

describe('accountHandle and savedAccountHandle', () => {
	it('an entry that declared the account, sends its own handle rather than another entrys', () => {
		// Arrange
		const { read, config } = open({
			default: { accounts: [{ label: 'Att' }] },
			profiles: [{ name: 'hall', accounts: [{ label: 'Att' }] }]
		});

		// Act
		const att = accountLabelled(config, 'Att');

		// Assert: the server answers two questions from a handle and only one is document-wide.
		// Borrowing another entry's answers "what had this entry already spelled out?" with nothing,
		// so every setting this profile spelled out at what is also a built-in default would drop
		// back out of the file, leaving it sparser than it was written.
		expect(accountHandle(att, 'hall')).toBe(read.handle('hall', 0));
		expect(accountHandle(att, DEFAULT_NAME)).toBe(read.handle(DEFAULT_NAME, 0));
	});

	it('an entry that declared nothing, borrows the default configurations handle', () => {
		// Arrange: the default configuration declares this account, and so does a profile.
		const { read, config } = open({
			default: { accounts: [{ label: 'Att' }] },
			profiles: [{ name: 'kiosk', accounts: [{ label: 'Att' }] }, { name: 'hall' }]
		});

		// Act
		const att = accountLabelled(config, 'Att');

		// Assert: the default configuration's, because it is the entry that always declares its
		// accounts and the likeliest to still be in the running configuration the picker proxy needs.
		expect(savedAccountHandle(att)).toEqual({
			entry: DEFAULT_NAME,
			id: read.handle(DEFAULT_NAME, 0)
		});
		expect(accountHandle(att, 'hall')).toBe(read.handle(DEFAULT_NAME, 0));
	});

	it('an account the default configuration never declared, borrows whichever handle there is', () => {
		// Arrange
		const { read, config } = open({
			default: { accounts: [{ label: 'Backup', url: 'https://backup.example' }] },
			profiles: [{ name: 'kiosk', accounts: [{ label: 'Att' }] }, { name: 'hall' }]
		});

		// Act
		const att = accountLabelled(config, 'Att');

		// Assert: without one the save is refused and the administrator is asked for a key they
		// should never have to type.
		expect(savedAccountHandle(att)).toEqual({ entry: 'kiosk', id: read.handle('kiosk', 0) });
		expect(accountHandle(att, 'hall')).toBe(read.handle('kiosk', 0));
	});

	it('an account with no stored copy at all, has no handle to send', () => {
		// Arrange
		const { config } = open({ default: { accounts: [{ label: 'Att' }] } });

		// Act
		const added = newAccount();

		// Assert
		expect(savedAccountHandle(added)).toBeNull();
		expect(accountHandle(added, DEFAULT_NAME)).toBeUndefined();
		expect(config.accounts).toHaveLength(1);
	});
});

describe('toUpdate: adoption', () => {
	it('a profile given an account it never declared, sends the default configurations handle', () => {
		// Arrange
		const { read, config } = open({
			default: { accounts: [{ label: 'Att' }] },
			profiles: [{ name: 'hall' }]
		});

		// Act: turn the profile's account override on, which starts it from what it was inheriting.
		declareAccounts(config, entryNamed(config, 'hall'));

		const update = toUpdate(config);

		// Assert: the handle of an entry that did declare the account, and the masked placeholder, so
		// the server copies the stored key across without it reaching the browser.
		const adopted = accountWritten(update, 'hall', 'Att');

		expect(adopted.id).toBe(read.handle(DEFAULT_NAME, 0));
		expect(adopted.apiKey).toBe(SECRET_PLACEHOLDER);

		// Assert: and the administrator is not asked for a key they should never have to type.
		expect(validationErrors(config)).toEqual([]);
		expect(simulateSave(update, read.storedById)).toEqual({ accepted: true });
	});

	it('an adopted account whose key is read from a file, sends its path and no key', () => {
		// Arrange
		const { read, config } = open({
			default: {
				accounts: [{ label: 'Att', credential: 'file', apiKeyFile: '/run/secrets/immich' }]
			},
			profiles: [{ name: 'hall' }]
		});

		// Act
		declareAccounts(config, entryNamed(config, 'hall'));

		const update = toUpdate(config);

		// Assert: a key file is not something a handle can fetch, so it travels with the account.
		const adopted = accountWritten(update, 'hall', 'Att');

		expect(adopted.apiKeyFile).toBe('/run/secrets/immich');
		expect(adopted.apiKey).toBeUndefined();
		expect(validationErrors(config)).toEqual([]);
		expect(simulateSave(update, read.storedById)).toEqual({ accepted: true });
	});

	it('an account edited in the accounts section, is written through to every entry using it', () => {
		// Arrange
		const { config } = open({
			default: { accounts: [{ label: 'Att', url: IMMICH }] },
			profiles: [{ name: 'hall', accounts: [{ label: 'Att', url: IMMICH }] }]
		});

		// Act: one row owns the credentials, whatever each entry's stored copy said.
		accountLabelled(config, 'Att').serverUrl = 'https://moved.example';

		const update = toUpdate(config);

		// Assert
		expect(accountWritten(update, DEFAULT_NAME, 'Att').immichServerUrl).toBe(
			'https://moved.example'
		);
		expect(accountWritten(update, 'hall', 'Att').immichServerUrl).toBe('https://moved.example');
	});

	it('an unticked selection, is written nowhere and is kept', () => {
		// Arrange
		const { config } = open({
			default: { accounts: [{ label: 'Att' }, { label: 'Backup' }] },
			profiles: [{ name: 'hall', accounts: [{ label: 'Att' }, { label: 'Backup' }] }]
		});

		// Act
		const hall = entryNamed(config, 'hall');

		setAccountUse(hall, accountLabelled(config, 'Backup'), false, config.default);

		// Assert: written nowhere, but still held so that ticking it again restores it.
		expect(accountsWritten(toUpdate(config), 'hall').map((account) => account.label)).toEqual([
			'Att'
		]);
		expect(hall.accounts).toHaveLength(2);
	});
});

describe('validationErrors', () => {
	it('a profile that declares its own accounts but uses none, is refused', () => {
		// Arrange
		const { config } = open({
			default: { accounts: [{ label: 'Att' }] },
			profiles: [{ name: 'hall', accounts: [{ label: 'Att' }] }]
		});

		// Act
		const hall = entryNamed(config, 'hall');

		setAccountUse(hall, accountLabelled(config, 'Att'), false, config.default);

		// Assert
		expect(validationErrors(config)).toEqual([
			expect.stringContaining("profile 'hall' declares its own accounts but uses none")
		]);
	});

	it('the default configuration using no account, is refused by name', () => {
		// Arrange
		const { config } = open({ default: { accounts: [{ label: 'Att' }] } });

		// Act
		setAccountUse(config.default, accountLabelled(config, 'Att'), false, null);

		// Assert: refused here, where it can be explained rather than arriving as a rejected save.
		expect(validationErrors(config)).toEqual([
			expect.stringContaining('The default configuration must use at least one Immich account')
		]);
	});

	it('an account with no server URL, is refused', () => {
		// Arrange
		const { config } = open({ default: { accounts: [{ label: 'Att' }] } });

		// Act
		accountLabelled(config, 'Att').serverUrl = '   ';

		// Assert
		expect(validationErrors(config)).toEqual([
			expect.stringContaining('Att needs an Immich server URL')
		]);
	});

	it('a genuinely new account with no key, is refused', () => {
		// Arrange
		const { config } = open({ default: { accounts: [{ label: 'Att' }] } });

		// Act: what the accounts section's Add button does.
		const added = addAccount(config);

		added.serverUrl = 'https://new.example';

		// Assert: nothing in the file has a key for it, and no handle can reach one.
		expect(validationErrors(config)).toEqual([
			expect.stringContaining('Enter the API key for https://new.example')
		]);
	});

	it('an account no configuration uses, is not asked for a key', () => {
		// Arrange
		const { config } = open({ default: { accounts: [{ label: 'Att' }] } });

		// Act: added, and unticked again under the default configuration it was given to, so it is
		// written to no part of the settings file.
		const added = addAccount(config);

		added.serverUrl = 'https://new.example';
		setAccountUse(config.default, added, false, null);

		// Assert
		expect(validationErrors(config)).toEqual([]);
	});

	it('a declared numeric setting with no number, is refused', () => {
		// Arrange
		const { config } = open({
			default: { declares: ['General.Interval'], general: { interval: 45 }, accounts: [{}] }
		});

		// Act: an emptied box.
		config.default.general.interval = null;

		// Assert
		expect(validationErrors(config)).toEqual([
			expect.stringContaining("the default configuration: 'Interval' needs a number")
		]);

		// Act: and a box holding something that is not one.
		config.default.general.interval = Number.NaN;

		// Assert
		expect(validationErrors(config)).toEqual([
			expect.stringContaining("the default configuration: 'Interval' needs a number")
		]);
	});

	it('a declared date format date-fns refuses, is refused with its reason', () => {
		// Arrange: date-fns warns about `DD` itself before it throws on it.
		const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
		const { config } = open({
			default: {
				declares: ['General.ClockFormat', 'General.PhotoDateFormat'],
				general: { clockFormat: 'h:mm A', photoDateFormat: 'DD.MM.YYYY' },
				accounts: [{}]
			}
		});

		// Act
		const errors = validationErrors(config);
		warn.mockRestore();

		// Assert: a frame would quietly show its default in place of either.
		expect(errors).toEqual([
			expect.stringMatching(
				/^the default configuration: 'Clock format' .*unescaped latin alphabet character `A`/
			),
			expect.stringMatching(
				/^the default configuration: 'Photo date format' .*Use `dd` instead of `DD`/
			)
		]);
	});

	it('a declared date format that quotes its literal text, or is empty, is accepted', () => {
		// Arrange: an emptied box is no format at all, and a frame uses its default for it.
		const { config } = open({
			default: {
				declares: ['General.ClockFormat', 'General.ClockDateFormat'],
				general: { clockFormat: "HH:mm 'Uhr'", clockDateFormat: '' },
				accounts: [{}]
			}
		});

		// Assert
		expect(validationErrors(config)).toEqual([]);
	});

	it('a declared secret with an empty box in set mode, is refused', () => {
		// Arrange
		const { config } = open({
			default: { declares: ['General.AuthenticationSecret'], accounts: [{}] }
		});

		// Act: choosing to replace the stored secret, and typing nothing.
		config.default.secrets.authenticationSecret = { mode: 'set', value: '' };

		// Assert: an empty box would silently unset a secret the administrator meant to replace.
		expect(validationErrors(config)).toEqual([
			expect.stringContaining("enter a value for 'Authentication secret'")
		]);
	});

	it('a declared secret left as read, keeps the stored one and is accepted', () => {
		// Arrange
		const { config } = open({
			default: { declares: ['General.AuthenticationSecret'], accounts: [{}] }
		});

		// Assert: a configuration that already declares a secret has one of its own to keep.
		expect(config.default.secrets.authenticationSecret.mode).toBe('keep');
		expect(validationErrors(config)).toEqual([]);
		expect(toUpdate(config).default?.general?.authenticationSecret).toBe(SECRET_PLACEHOLDER);
	});

	it('two unlabelled accounts on one server in one entry, is refused', () => {
		// Arrange
		const { config } = open({ default: { accounts: [{ url: IMMICH }, { url: IMMICH }] } });

		// Assert: which is which would depend on the order they are listed in.
		expect(validationErrors(config)).toEqual([
			expect.stringContaining('two accounts with no label are on the same Immich server')
		]);
	});
});

describe('regression: a secret typed as something the server does not take for a value', () => {
	/**
	 * The default configuration holding an authentication secret of its own, and a profile
	 * inheriting it. Typing into the default's box is pressing Replace; typing into the profile's is
	 * turning its override on, which starts a secret it never declared in the box rather than on
	 * the stored one.
	 */
	function typedAsSecret(entryName: string, value: string) {
		const { config } = open({
			default: { declares: ['General.AuthenticationSecret'], accounts: [{ label: 'Att' }] },
			profiles: [{ name: 'hall' }]
		});
		const entry = entryNamed(config, entryName);

		// Already declared on the default configuration, so this changes nothing there.
		toggleGeneral(entry, 'authenticationSecret', true, entry.isDefault ? null : config.default);
		entry.secrets.authenticationSecret = { mode: 'set', value };

		return config;
	}

	it.each([
		{ entry: DEFAULT_NAME, value: '   ', where: 'the default configuration' },
		{ entry: DEFAULT_NAME, value: SECRET_PLACEHOLDER, where: 'the default configuration' },
		{ entry: 'hall', value: '   ', where: "profile 'hall'" },
		{ entry: 'hall', value: SECRET_PLACEHOLDER, where: "profile 'hall'" }
	])("'$value' typed as $entry's authentication secret, is refused", ({ entry, value, where }) => {
		// Act
		const config = typedAsSecret(entry, value);

		// Assert: neither is a secret to the server. Nothing but spaces is read as "no secret here",
		// and so is the mask the editor sends to keep a stored one wherever there is none to keep -
		// the key dropped on the default configuration, an explicit null on a profile - and either
		// way every frame URL the secret guarded opened, with nothing said. Where there is one to
		// keep, the mask keeps it: Replace saved, and the old secret still in force.
		expect(validationErrors(config)).toEqual([
			`${where}: enter a value for 'Authentication secret', choose to have none, or turn its ` +
				'override off. A value cannot be blank - the server would take that for no secret at ' +
				`all - nor ${SECRET_PLACEHOLDER}, which asks it to keep the stored one.`
		]);
	});

	it.each([DEFAULT_NAME, 'hall'])("a real value typed as %s's secret, is accepted", (entry) => {
		// Act
		const config = typedAsSecret(entry, 'correct horse battery staple');

		// Assert
		expect(validationErrors(config)).toEqual([]);
	});
});

describe('declaredKeyOf', () => {
	it('every general setting the editor renders, names a setting the server knows about', () => {
		// Arrange
		const known = new Set(SERVER_GENERAL_SETTINGS.map((name) => `General.${name}`));

		// Act
		const declared = (Object.keys(generalFields) as GeneralProp[]).map(declaredKeyOf);

		// Assert: a key the server does not know is refused outright, so the two lists must agree in
		// both directions - one missing here is a setting nobody can edit.
		expect([...declared].sort()).toEqual([...known].sort());
	});
});

describe('problemDetail', () => {
	const FALLBACK = 'The configuration was not saved (HTTP 400).';

	it.each<{ case: string; data: unknown; message: string }>([
		{ case: 'no body at all', data: undefined, message: FALLBACK },
		{ case: 'a null body', data: null, message: FALLBACK },
		{ case: "a reverse proxy's page", data: '<html>Bad Gateway</html>', message: FALLBACK },
		{ case: 'a bare number', data: 400, message: FALLBACK },
		{ case: 'an empty problem', data: {}, message: FALLBACK },
		{
			case: 'a detail',
			data: {
				title: 'Bad Request',
				detail: 'Two accounts are labelled Mum.',
				errors: { a: ['b'] }
			},
			message: 'Two accounts are labelled Mum.'
		},
		{
			case: 'a blank detail beside a title',
			data: { title: 'Bad Request', detail: '   ' },
			message: 'Bad Request'
		},
		{
			case: 'a detail that is not a string',
			data: { title: 'Bad Request', detail: 42 },
			message: 'Bad Request'
		},
		{
			case: 'a ValidationProblemDetails',
			data: {
				title: 'One or more validation errors occurred.',
				errors: {
					'$.profiles[0].accounts[0].albums[0]': ['The JSON value could not be converted.'],
					$: ['The input does not contain any JSON tokens.'],
					'': ['A non-empty request body is required.']
				}
			},
			message:
				'$.profiles[0].accounts[0].albums[0]: The JSON value could not be converted. The input ' +
				'does not contain any JSON tokens. A non-empty request body is required.'
		},
		{
			case: 'an error that is not in a list',
			data: { errors: { Name: 'The Name field is required.' } },
			message: 'Name: The Name field is required.'
		},
		{
			case: 'errors that are not strings',
			data: { errors: { Name: [1, null, 'Required.'], Other: { nested: 'Dropped.' } } },
			message: 'Name: Required.'
		},
		{
			case: 'errors with nothing to say, beside a title',
			data: { title: 'Bad Request', errors: { Name: [1] } },
			message: 'Bad Request'
		},
		{ case: 'a blank title', data: { title: '   ' }, message: FALLBACK }
	])('$case, reads as what it carries', ({ data, message }) => {
		// Assert: a detail first; a model-binding failure's messages, which never reach the
		// controller and so carry no detail, each beside the field it names - unless that is the body
		// as a whole; then the title; and then what the caller knows, the status.
		expect(problemDetail(data, FALLBACK)).toBe(message);
	});
});

describe('profileNameError', () => {
	const PATTERN = 'A profile name may only contain letters, digits, underscores and hyphens.';

	it.each<{ case: string; name: string; existing?: string[]; error: string | null }>([
		{ case: 'sixty-four characters', name: 'a'.repeat(64), error: null },
		{ case: 'letters, digits, an underscore and a hyphen', name: 'Hall_2-b', error: null },
		{ case: 'sixty-five characters', name: 'a'.repeat(65), error: PATTERN },
		{ case: 'nothing', name: '', error: PATTERN },
		{ case: 'a space', name: 'living room', error: PATTERN },
		{ case: 'a dot', name: 'hall.tv', error: PATTERN },
		{ case: 'a letter outside ASCII', name: 'küche', error: PATTERN },
		...[
			'api',
			'STATIC',
			'Swagger',
			'ADMIN',
			'Default',
			'signin-oidc',
			'SignOut-Callback-OIDC',
			'signout-oidc'
		].map((name) => ({
			case: `the reserved '${name}'`,
			name,
			error: `'${name}' is a reserved name.`
		})),
		{
			case: 'a name taken in another case',
			name: 'kitchen',
			existing: ['Kitchen'],
			error: "There is already a profile named 'kitchen'. Profile names are case-insensitive."
		},
		{ case: 'a name like a taken one', name: 'kitchen-2', existing: ['Kitchen'], error: null }
	])('$case, is answered as the server would', ({ name, existing = [], error }) => {
		// Assert: `ConfigCatalog`'s own rules - `^[A-Za-z0-9_-]{1,64}$`, its reserved names, and no
		// two profiles named alike in any case - so a name the editor accepts is one the API accepts.
		expect(profileNameError(name, existing)).toBe(error);
	});
});

describe('withoutPendingDeletion', () => {
	it.each([
		{ deleted: ['kitchen', 'hall'], added: 'kitchen', left: ['hall'] },
		{ deleted: ['Kitchen', 'hall'], added: 'kitchen', left: ['hall'] },
		{ deleted: ['kitchen', 'hall'], added: 'kitchen-2', left: ['kitchen', 'hall'] }
	])("'$added' added with $deleted to be deleted, leaves $left", ({ deleted, added, left }) => {
		// Act / Assert: a profile deleted and added back is not deleted by the save - it is written
		// as a new profile with nothing of its own, and every frame on it keeps working - and a
		// profile name is case-insensitive, so 'Kitchen' added back as 'kitchen' is that profile too.
		expect(withoutPendingDeletion(deleted, added)).toEqual(left);
	});
});

describe('withPendingDeletion', () => {
	/** One press of Add profile or Delete profile, and the name it was pressed for. */
	type Step = [action: 'add' | 'delete', name: string];

	/** What the save bar lists after a run of steps, taken the way `config-editor.svelte` takes them. */
	function deletedAfter(onServer: string[], steps: Step[]) {
		return steps.reduce<string[]>(
			(deleted, [action, name]) =>
				action === 'add'
					? withoutPendingDeletion(deleted, name)
					: withPendingDeletion(deleted, name, onServer),
			[]
		);
	}

	it.each<{ case: string; onServer: string[]; steps: Step[]; left: string[] }>([
		{
			case: 'a profile the server holds, deleted',
			onServer: ['kitchen'],
			steps: [['delete', 'kitchen']],
			left: ['kitchen']
		},
		{
			case: 'a profile added and deleted again before a save',
			onServer: ['kitchen'],
			steps: [
				['add', 'hall'],
				['delete', 'hall']
			],
			left: []
		},
		{
			case: 'a profile the server holds, deleted, added back and deleted again',
			onServer: ['kitchen'],
			steps: [
				['delete', 'kitchen'],
				['add', 'kitchen'],
				['delete', 'kitchen']
			],
			left: ['kitchen']
		},
		{
			case: 'the same, added back in another case',
			onServer: ['Kitchen'],
			steps: [
				['delete', 'Kitchen'],
				['add', 'kitchen'],
				['delete', 'kitchen']
			],
			left: ['kitchen']
		},
		{
			case: 'two profiles the server holds, deleted either side of one that never reached it',
			onServer: ['kitchen', 'hall'],
			steps: [
				['delete', 'kitchen'],
				['add', 'lounge'],
				['delete', 'lounge'],
				['delete', 'hall']
			],
			left: ['kitchen', 'hall']
		}
	])('$case, leaves $left to be deleted', ({ onServer, steps, left }) => {
		// Assert: a save deletes only what the settings file holds. A profile added since it was read
		// never reached it, so deleting that one again deletes nothing - and the save bar said it
		// would be deleted when you save. Compared case-insensitively, as the server tells one profile
		// from another.
		expect(deletedAfter(onServer, steps)).toEqual(left);
	});
});

describe('regression: unticking and re-ticking an account on a profile', () => {
	it('re-ticking, restores what this profile was showing rather than the default configurations', () => {
		// Arrange: the profile shows different albums from the same account.
		const { config } = open({
			default: { accounts: [{ label: 'Att', values: { albums: ['default-album'] } }] },
			profiles: [{ name: 'hall', accounts: [{ label: 'Att', values: { albums: ['hall-album'] } }] }]
		});

		const hall = entryNamed(config, 'hall');
		const att = accountLabelled(config, 'Att');

		// Act: a stray click, and then putting it back.
		setAccountUse(hall, att, false, config.default);
		setAccountUse(hall, att, true, config.default);

		// Assert: the profile's own photo selection, not the default configuration's. Dropping the
		// selection on the untick made re-ticking seed from the default configuration instead, which
		// made a stray click the most destructive control on the page.
		expect(accountWritten(toUpdate(config), 'hall', 'Att').albums).toEqual(['hall-album']);
	});
});

describe('regression: a kept key whose stored copy is read from a file', () => {
	it('an entry keeping its own file-backed key with the path cleared, is refused', () => {
		// Arrange: the default configuration reads its key from a file, a profile stores one inline,
		// so the row has both a key file and a stored key somewhere in the document.
		const { read, config } = open({
			default: {
				accounts: [{ label: 'Att', credential: 'file', apiKeyFile: '/run/secrets/immich' }]
			},
			profiles: [{ name: 'hall', accounts: [{ label: 'Att', credential: 'key' }] }]
		});

		// Act: clear the key file box, keeping the stored key.
		accountLabelled(config, 'Att').apiKeyFile = '';

		// Assert: the default configuration's stored copy has no key beside the file, so keeping it
		// resolves to nothing at all.
		expect(simulateSave(toUpdate(config), read.storedById)).toMatchObject({
			accepted: false,
			refusal: 'no-credential'
		});
		expect(validationErrors(config)).toEqual([
			expect.stringContaining('has no API key file set above')
		]);
	});

	it('an entry keeping a borrowed key whose owner reads it from a file, is refused', () => {
		// Arrange: the default configuration does not declare this account at all. One profile stores
		// it behind a key file, another stores it inline, and both have it unticked - so the only
		// entry that writes it is a third one adopting it, on a handle it borrowed.
		const backup = { label: 'Backup', url: 'https://backup.example' };
		const { read, config } = open({
			default: { accounts: [backup] },
			profiles: [
				{
					name: 'kiosk',
					accounts: [
						backup,
						{ label: 'Att', credential: 'file', apiKeyFile: '/run/secrets/immich' }
					]
				},
				{ name: 'lounge', accounts: [backup, { label: 'Att', credential: 'key' }] },
				{ name: 'hall' }
			]
		});

		const att = accountLabelled(config, 'Att');

		// Act
		att.apiKeyFile = '';
		setAccountUse(entryNamed(config, 'kiosk'), att, false, config.default);
		setAccountUse(entryNamed(config, 'lounge'), att, false, config.default);
		declareAccounts(config, entryNamed(config, 'hall'));
		setAccountUse(entryNamed(config, 'hall'), att, true, config.default);

		// Assert: the borrowed handle is the kiosk profile's, so it is the kiosk profile's stored copy
		// the placeholder resolves against - and that one reads its key from a file. Answering this
		// from the entry sending the handle rather than from the entry that owns it misses every
		// adoption, and the save dies on the loader's "Either ApiKey or ApiKeyFile must be provided."
		const outcome = simulateSave(toUpdate(config), read.storedById);

		expect(outcome).toMatchObject({ accepted: false, refusal: 'no-credential' });
		expect(validationErrors(config)).toEqual([
			expect.stringContaining(
				"profile 'hall' (keeping profile 'kiosk''s key, read from /run/secrets/immich)"
			)
		]);
	});
});

describe('regression: a key file written for an entry whose stored copy does not name it', () => {
	it('a default configurations key file, written to a profile storing a key, is refused', () => {
		// Arrange: one account, labelled alike at one URL, so one row - built from the default
		// configuration's copy, which reads its key from a file, and saying that the profile's copy
		// keeps a key of its own in the settings file instead.
		const { read, config } = open({
			default: { accounts: [{ label: 'Mum', url: IMMICH, credential: 'file', apiKeyFile: '/k' }] },
			profiles: [{ name: 'hall', accounts: [{ label: 'Mum', url: IMMICH, credential: 'key' }] }]
		});
		const mum = accountLabelled(config, 'Mum');

		expect(mum.apiKeyFile).toBe('/k');
		expect(mum.disagreeing).toEqual(['hall']);

		// Act
		const update = toUpdate(config);

		// Assert: the profile sends its own handle, which is what keeps the settings it spelled out,
		// with the row's path - and the server keeps a key file only where it is exactly the one
		// stored on the account that handle names. So the save is refused, for a file nobody edited.
		expect(accountWritten(update, 'hall', 'Mum')).toMatchObject({
			id: read.handle('hall', 0),
			apiKeyFile: '/k'
		});
		expect(simulateSave(update, read.storedById)).toMatchObject({
			accepted: false,
			refusal: 'unstored-key-file',
			entry: 'hall'
		});

		// Assert: and the editor refuses it first, naming the profile, both credentials, and the
		// ways out.
		expect(validationErrors(config)).toEqual([
			"Mum names '/k' as its API key file, which the settings on the server do not give it for " +
				"profile 'hall' (its stored copy keeps the key in the settings file). ImmichFrame keeps " +
				'a key file only where they already do, since it reads whatever file is named. Clear the ' +
				"API key file above and enter the account's API key instead, or untick Mum there."
		]);
	});

	it.each<{
		case: string;
		fixture: DocumentFixture;
		act?: (config: EditableConfig) => void;
		refused: string;
		where: string;
	}>([
		{
			case: 'a profile whose own copy reads another file',
			fixture: {
				default: {
					accounts: [{ label: 'Mum', url: IMMICH, credential: 'file', apiKeyFile: '/k' }]
				},
				profiles: [
					{
						name: 'hall',
						accounts: [{ label: 'Mum', url: IMMICH, credential: 'file', apiKeyFile: '/other' }]
					}
				]
			},
			refused: 'hall',
			where: "profile 'hall' (its stored copy reads '/other')"
		},
		{
			case: 'a new account given a path typed here',
			fixture: { default: { accounts: [{ label: 'Att', url: IMMICH }] } },
			act: (config) => {
				const added = addAccount(config);

				added.label = 'Mum';
				added.serverUrl = 'https://new.example';
				added.apiKeyFile = '/k';
			},
			refused: DEFAULT_NAME,
			where: 'the default configuration (nothing is stored for this account yet)'
		},
		{
			case: "a profile adopting an account on another profile's handle, whose copy stores a key",
			fixture: {
				default: { accounts: [{ label: 'Backup', url: 'https://backup.example' }] },
				profiles: [
					{
						name: 'kiosk',
						accounts: [
							{ label: 'Backup', url: 'https://backup.example' },
							{ label: 'Mum', url: IMMICH, credential: 'key' }
						]
					},
					{ name: 'hall' }
				]
			},
			act: (config) => {
				const mum = accountLabelled(config, 'Mum');

				mum.apiKeyFile = '/k';
				setAccountUse(entryNamed(config, 'kiosk'), mum, false, config.default);
				declareAccounts(config, entryNamed(config, 'hall'));
				setAccountUse(entryNamed(config, 'hall'), mum, true, config.default);
			},
			refused: 'hall',
			where:
				"profile 'hall' (keeping profile 'kiosk''s copy, which keeps the key in the settings file)"
		}
	])('$case, is refused', ({ fixture, act, refused, where }) => {
		// Arrange
		const { read, config } = open(fixture);

		// Act
		act?.(config);

		// Assert: the server would refuse it for the entry named, and the editor says why first.
		expect(simulateSave(toUpdate(config), read.storedById)).toMatchObject({
			accepted: false,
			refusal: 'unstored-key-file',
			entry: refused
		});
		expect(validationErrors(config)).toEqual([expect.stringContaining(`for ${where}.`)]);
	});

	it.each<{ way: string; out: (config: EditableConfig) => void }>([
		{
			way: 'clearing the key file and entering the key',
			out: (config) => {
				const mum = accountLabelled(config, 'Mum');

				mum.apiKeyFile = '';
				mum.entering = true;
				mum.apiKey = 'typed-key';
			}
		},
		{
			way: 'unticking the account in the profile that disagrees',
			out: (config) =>
				setAccountUse(
					entryNamed(config, 'hall'),
					accountLabelled(config, 'Mum'),
					false,
					config.default
				)
		}
	])('the same document after $way, is accepted', ({ out }) => {
		// Arrange
		const { read, config } = open({
			default: { accounts: [{ label: 'Mum', url: IMMICH, credential: 'file', apiKeyFile: '/k' }] },
			profiles: [
				{
					name: 'hall',
					accounts: [
						{ label: 'Mum', url: IMMICH, credential: 'key' },
						{ label: 'Backup', url: 'https://backup.example' }
					]
				}
			]
		});

		// Act
		out(config);

		// Assert: both ways out the refusal names are ones the server takes.
		expect(validationErrors(config)).toEqual([]);
		expect(simulateSave(toUpdate(config), read.storedById)).toEqual({ accepted: true });
	});

	it('a key file every entry writing the account already reads, is accepted', () => {
		// Arrange
		const { read, config } = open({
			default: { accounts: [{ label: 'Mum', url: IMMICH, credential: 'file', apiKeyFile: '/k' }] },
			profiles: [
				{
					name: 'hall',
					accounts: [{ label: 'Mum', url: IMMICH, credential: 'file', apiKeyFile: '/k' }]
				}
			]
		});

		// Assert: nothing disagrees, so there is nothing to refuse.
		expect(accountLabelled(config, 'Mum').disagreeing).toEqual([]);
		expect(validationErrors(config)).toEqual([]);
		expect(simulateSave(toUpdate(config), read.storedById)).toEqual({ accepted: true });
	});
});

describe('regression: which configurations use an account', () => {
	it('a profile that inherits the default configurations accounts, counts as using them', () => {
		// Arrange
		const { config } = open({
			default: { accounts: [{ label: 'Att' }] },
			profiles: [{ name: 'hall' }, { name: 'kiosk', accounts: [{ label: 'Att' }] }]
		});

		// Act
		const using = entriesUsingAccount(config, accountLabelled(config, 'Att'));

		// Assert: inheriting is not "not using it", it is using the default configuration's list -
		// counting only the configurations that declare accounts reported "no configuration profile
		// uses it" while every inheriting profile silently lost its photos.
		expect(using.map((entry) => entry.name)).toEqual([DEFAULT_NAME, 'hall', 'kiosk']);
	});

	it('an account an inheriting profile does not show, is not counted against it', () => {
		// Arrange: the profile inherits, and the account is unticked on the default configuration.
		const { config } = open({
			default: { accounts: [{ label: 'Att' }, { label: 'Backup' }] },
			profiles: [{ name: 'hall' }]
		});

		// Act
		setAccountUse(config.default, accountLabelled(config, 'Att'), false, null);

		// Assert
		expect(entriesUsingAccount(config, accountLabelled(config, 'Att'))).toEqual([]);
		expect(
			entriesUsingAccount(config, accountLabelled(config, 'Backup')).map((entry) => entry.name)
		).toEqual([DEFAULT_NAME, 'hall']);
	});
});

describe('removeAccountRefusal and removeAccount', () => {
	const BACKUP = { label: 'Backup', url: 'https://backup.example' };

	it.each<{ case: string; fixture: DocumentFixture; untick?: string; removing: string }>([
		{
			case: "the default configuration's only account",
			fixture: { default: { accounts: [{ label: 'Att' }] } },
			removing: 'Att'
		},
		{
			case: "the default configuration's only ticked account, beside one it holds unticked",
			fixture: { default: { accounts: [{ label: 'Att' }, BACKUP] } },
			untick: 'Backup',
			removing: 'Att'
		}
	])('$case, is refused', ({ fixture, untick, removing }) => {
		// Arrange
		const { config } = open(fixture);

		if (untick) setAccountUse(config.default, accountLabelled(config, untick), false, null);

		// Act
		const refusal = removeAccountRefusal(config, accountLabelled(config, removing));

		// Assert: the server refuses a default configuration using no account, by name. An unticked
		// selection serves nothing, so it does not count as another account to fall back on.
		expect(refusal).toBe(
			`${removing} is the only Immich account the default configuration uses, and that is what ` +
				'every configuration profile inherits. Give the default configuration another account ' +
				'first: ImmichFrame cannot serve an image without one.'
		);
	});

	it.each<{ case: string; fixture: DocumentFixture; untick?: string; removing: string }>([
		{
			case: 'one of two accounts the default configuration uses',
			fixture: { default: { accounts: [{ label: 'Att' }, BACKUP] } },
			removing: 'Att'
		},
		{
			case: 'an account the default configuration holds unticked',
			fixture: { default: { accounts: [{ label: 'Att' }, BACKUP] } },
			untick: 'Backup',
			removing: 'Backup'
		},
		{
			case: 'an account only a profile uses',
			fixture: {
				default: { accounts: [{ label: 'Att' }] },
				profiles: [{ name: 'hall', accounts: [BACKUP] }]
			},
			removing: 'Backup'
		}
	])('$case, may be removed', ({ fixture, untick, removing }) => {
		// Arrange
		const { config } = open(fixture);

		if (untick) setAccountUse(config.default, accountLabelled(config, untick), false, null);

		// Assert: the guard is about the account the default configuration would be left without,
		// and only that one.
		expect(removeAccountRefusal(config, accountLabelled(config, removing))).toBeNull();
	});

	it('a removed account, is held by no entry and written nowhere', () => {
		// Arrange: one account used every way an entry can hold it - ticked by the default
		// configuration and a profile, held unticked by another, kept by one whose override has been
		// turned off, and inherited by one that never declared a list.
		const both = [{ label: 'Att' }, BACKUP];
		const { read, config } = open({
			default: { accounts: both },
			profiles: [
				{ name: 'hall', accounts: both },
				{ name: 'kiosk', accounts: both },
				{ name: 'lounge', accounts: both },
				{ name: 'porch' }
			]
		});
		const att = accountLabelled(config, 'Att');

		setAccountUse(entryNamed(config, 'kiosk'), att, false, config.default);
		toggleAccounts(entryNamed(config, 'lounge'), false, config.default);

		// Act
		removeAccount(config, att);

		// Assert: no selection left anywhere that names it, ticked or not, declared or not.
		expect(config.accounts.map((account) => account.label)).toEqual(['Backup']);
		expect(
			[config.default, ...config.profiles].flatMap((entry) =>
				entry.accounts.filter((selection) => selection.accountKey === att.key)
			)
		).toEqual([]);

		// Act: and the kept list's override turned back on, which would restore a selection still in
		// it.
		toggleAccounts(entryNamed(config, 'lounge'), true, config.default);

		// Assert: written by no entry, under its label or under any handle the read issued for it.
		const update = toUpdate(config);
		const handles = new Set(Object.values(att.ids));
		const written = [DEFAULT_NAME, 'hall', 'kiosk', 'lounge', 'porch'].flatMap((name) =>
			accountsWritten(update, name)
		);

		expect(written.map((account) => account.label)).not.toContain('Att');
		expect(written.filter((account) => handles.has(account.id ?? ''))).toEqual([]);
		expect(validationErrors(config)).toEqual([]);
		expect(simulateSave(update, read.storedById)).toEqual({ accepted: true });
	});
});

describe('toggleGeneral', () => {
	it('an override turned on, moves the value into the profile and changes nothing else', () => {
		// Arrange: the default configuration overrides Interval, and has just been given a value that
		// is not saved yet; both profiles inherit it.
		const { config } = open({
			default: {
				declares: ['General.Interval'],
				general: { interval: 30 },
				accounts: [{ label: 'Att' }]
			},
			profiles: [{ name: 'hall' }, { name: 'kiosk' }]
		});
		const hall = entryNamed(config, 'hall');

		config.default.general.interval = 20;

		const before = toUpdate(config);

		// Act
		toggleGeneral(hall, 'interval', true, config.default);

		const after = toUpdate(config);

		// Assert: the profile starts from what it was being shown - the default configuration's
		// value as it stands, unsaved edit and all, rather than the merged value the read reported
		// for the profile - so the only change a save makes is which configuration holds it.
		expect(hall.general.interval).toBe(20);
		expect(after.profiles?.[0]).toEqual({
			...before.profiles?.[0],
			declaredKeys: ['General.Interval'],
			general: { interval: 20 }
		});
		expect({ ...after, profiles: after.profiles?.slice(1) }).toEqual({
			...before,
			profiles: before.profiles?.slice(1)
		});
	});

	it('the default configurations override turned off and on again, keeps what it held', () => {
		// Arrange
		const { config } = open({ default: { accounts: [{ label: 'Att' }] } });

		// Act: overridden and edited, then turned off.
		toggleGeneral(config.default, 'interval', true, null);
		config.default.general.interval = 10;
		toggleGeneral(config.default, 'interval', false, null);

		// Assert: kept in the model, and not sent.
		expect(config.default.general.interval).toBe(10);
		expect(toUpdate(config).default?.general).toEqual({});

		// Act
		toggleGeneral(config.default, 'interval', true, null);

		// Assert: there is nothing above it to seed from, so the edit is what comes back.
		expect(toUpdate(config).default?.general).toEqual({ interval: 10 });
	});

	it('a profiles secret override turned off and on again, keeps what was typed', () => {
		// Arrange
		const { config } = open({
			default: { accounts: [{ label: 'Att' }] },
			profiles: [{ name: 'hall' }]
		});
		const hall = entryNamed(config, 'hall');

		// Act
		toggleGeneral(hall, 'webhook', true, config.default);
		hall.secrets.webhook.value = 'https://hooks.example/frame';
		toggleGeneral(hall, 'webhook', false, config.default);

		// Assert
		expect(toUpdate(config).profiles?.[0].general).toEqual({});

		// Act
		toggleGeneral(hall, 'webhook', true, config.default);

		// Assert: what a secret is set to is held apart from the general values a toggle seeds.
		expect(toUpdate(config).profiles?.[0].general).toEqual({
			webhook: 'https://hooks.example/frame'
		});
	});

	it('a profiles override turned off and on again, starts again from what it inherits', () => {
		// Arrange
		const { config } = open({
			default: { accounts: [{ label: 'Att' }] },
			profiles: [{ name: 'hall' }]
		});
		const hall = entryNamed(config, 'hall');

		// Act
		toggleGeneral(hall, 'interval', true, config.default);
		hall.general.interval = 10;
		toggleGeneral(hall, 'interval', false, config.default);

		// Assert: kept in the model while the override is off...
		expect(hall.general.interval).toBe(10);

		// Act
		toggleGeneral(hall, 'interval', true, config.default);

		// Assert: ...but an override starts from what was being inherited each time it is turned
		// on, which is what the row was showing while it was off: the built-in 45 the default
		// configuration has, rather than the 10 it held.
		expect(toUpdate(config).profiles?.[0].general).toEqual({ interval: 45 });
	});
});

describe('regression: one problem found more than once', () => {
	it.each([2, 3])('%i new accounts labelled alike, have each problem listed once', (count) => {
		// Arrange
		const { config } = open({ default: { accounts: [{ label: 'Att', url: IMMICH }] } });

		// Act: Add account pressed again and again, and every new row given the same label and
		// nothing else - so each of them is found to want a server URL and a key, in the same
		// words, and every label after the first is found to collide with the one before it.
		for (let added = 0; added < count; added++) addAccount(config).label = 'X';

		const errors = validationErrors(config);

		// Assert: no message twice. The list is read as a list of things to fix, and the save bar
		// keyed it by message, so a repeat threw in place of the only explanation the refused
		// save had.
		expect(errors).toEqual([...new Set(errors)]);

		// Assert: and nothing dropped, in the order it was found.
		expect(errors).toEqual([
			expect.stringContaining("two accounts are labelled 'X'"),
			'X needs an Immich server URL.',
			'Enter the API key for X.'
		]);
	});

	it('three new unlabelled accounts on one server, have each problem listed once', () => {
		// Arrange
		const { config } = open({ default: { accounts: [{ label: 'Att', url: IMMICH }] } });

		// Act
		for (let added = 0; added < 3; added++) addAccount(config).serverUrl = 'https://new.example';

		const errors = validationErrors(config);

		// Assert
		expect(errors).toEqual([...new Set(errors)]);
		expect(errors).toEqual([
			expect.stringContaining(
				'two accounts with no label are on the same Immich server (https://new.example)'
			),
			'Enter the API key for https://new.example.'
		]);
	});
});

describe('labelCollisions', () => {
	const OTHER = 'https://other.example';

	it.each<{
		case: string;
		fixture: DocumentFixture;
		act?: (config: EditableConfig) => void;
		groups: { entry: string; labels: string[] }[];
	}>([
		{
			case: 'two accounts in one entry whose labels collide',
			fixture: {
				default: {
					accounts: [
						{ label: 'Mum', url: IMMICH },
						{ label: 'mum ', url: OTHER }
					]
				}
			},
			groups: [{ entry: DEFAULT_NAME, labels: ['Mum', 'mum'] }]
		},
		{
			case: 'three new accounts labelled alike',
			fixture: { default: { accounts: [{ label: 'Att', url: IMMICH }] } },
			act: (config) => {
				for (let added = 0; added < 3; added++) addAccount(config).label = 'X';
			},
			groups: [{ entry: DEFAULT_NAME, labels: ['X', 'X', 'X'] }]
		},
		{
			case: "two whose labels collide in a profile's own list, beside one that inherits",
			fixture: {
				default: { accounts: [{ label: 'Att', url: IMMICH }] },
				profiles: [
					{
						name: 'hall',
						accounts: [
							{ label: 'Att', url: IMMICH },
							{ label: 'att ', url: OTHER }
						]
					},
					{ name: 'porch' }
				]
			},
			groups: [{ entry: 'hall', labels: ['Att', 'att'] }]
		},
		{
			case: 'one label in two entries',
			fixture: {
				default: { accounts: [{ label: 'Mum' }] },
				profiles: [{ name: 'hall', accounts: [{ label: ' mum' }] }]
			},
			groups: []
		},
		{
			case: 'two whose labels collide, one of them unticked',
			fixture: {
				default: {
					accounts: [
						{ label: 'Mum', url: IMMICH },
						{ label: 'mum', url: OTHER }
					]
				}
			},
			act: (config) => setAccountUse(config.default, accountLabelled(config, 'mum'), false, null),
			groups: []
		}
	])('$case, is exactly what validationErrors refuses', ({ fixture, act, groups }) => {
		// Arrange
		const { config } = open(fixture);

		act?.(config);

		const rows = accountsByKey(config);

		// Act
		const collisions = labelCollisions(config);

		// Assert: one group per label per entry, each account as it spells its label. A label shared
		// across entries is how a profile says it means the default configuration's account.
		expect(
			collisions.map((collision) => ({
				entry: collision.entry.name,
				labels: collision.keys.map((key) => rows.get(key)?.label)
			}))
		).toEqual(groups);

		// Assert: and the save is refused for its labels in exactly the entries holding a group.
		const refusedIn = validationErrors(config)
			.filter((error) => error.includes(': two accounts are labelled '))
			.map((error) => error.slice(0, error.indexOf(': two accounts are labelled ')));

		expect([...new Set(refusedIn)]).toEqual(
			collisions.map((collision) => entryLabel(collision.entry))
		);
	});
});

/** One number box in one configuration, and what it is given. */
interface NumberEdit {
	entry: string;
	/** One of the entry's general settings, or of what it shows from the account labelled Att. */
	on: 'general' | 'account';
	prop: GeneralProp | AccountProp;
	value: number | null;
}

function errorsAfter({ entry, on, prop, value }: NumberEdit): string[] {
	// Arrange: every general setting edited below is overridden, and the account is declared, in
	// both configurations. That way every box edited below is one a save writes out.
	const declaring = {
		declares: [
			'General.Interval',
			'General.TransitionDuration',
			'General.RefreshAlbumPeopleInterval'
		],
		accounts: [{ label: 'Att' }]
	};
	const { config } = open({ default: declaring, profiles: [{ name: 'hall', ...declaring }] });
	const edited = entryNamed(config, entry);

	// Act: what the number box commits, where `entry-editor.svelte` and `account-editor.svelte`
	// write it.
	Object.assign(on === 'general' ? edited.general : usedSelections(edited)[0].values, {
		[prop]: value
	});

	return validationErrors(config);
}

describe('regression: a fraction in a setting the server holds as a whole number', () => {
	it.each<NumberEdit & { refusal: string }>([
		{
			entry: DEFAULT_NAME,
			on: 'general',
			prop: 'interval',
			value: 2.5,
			refusal: "the default configuration: 'Interval' needs a whole number."
		},
		{
			entry: 'hall',
			on: 'general',
			prop: 'interval',
			value: 2.5,
			refusal: "profile 'hall': 'Interval' needs a whole number."
		},
		{
			entry: DEFAULT_NAME,
			on: 'account',
			prop: 'rating',
			value: 2.5,
			refusal:
				"the default configuration: 'Rating' for Att needs a whole number, or to be left blank."
		},
		{
			entry: 'hall',
			on: 'account',
			prop: 'rating',
			value: 2.5,
			refusal: "profile 'hall': 'Rating' for Att needs a whole number, or to be left blank."
		}
	])('$on $prop at $value in $entry, is refused by name', (edit) => {
		// Assert: the server holds this as an int, and refused the fraction in the model binder's
		// words instead - a JSON path and a CLR type name, the path naming a profile by its position
		// in the request rather than by its name.
		expect(errorsAfter(edit)).toEqual([edit.refusal]);
	});

	it.each<NumberEdit>([
		{ entry: DEFAULT_NAME, on: 'general', prop: 'interval', value: 2 },
		{ entry: DEFAULT_NAME, on: 'general', prop: 'transitionDuration', value: 2.5 },
		{ entry: DEFAULT_NAME, on: 'account', prop: 'imagesFromDays', value: 30 },
		{ entry: DEFAULT_NAME, on: 'account', prop: 'imagesFromDays', value: null }
	])('$on $prop at $value in $entry, is accepted', (edit) => {
		// Assert: a whole number where the server holds an int, a fraction where it holds a double,
		// and an account setting left blank - which is no limit, rather than a number missing.
		expect(errorsAfter(edit)).toEqual([]);
	});
});

describe('regression: a whole number past the range the server holds it in', () => {
	// General settings only. Each whole-number account setting has a narrower range of its own,
	// tested below with the setting it belongs to.
	describe.each<Pick<NumberEdit, 'on' | 'prop'> & { refusal: string }>([
		{
			on: 'general',
			prop: 'interval',
			refusal:
				"the default configuration: 'Interval' needs a whole number between -2147483648 and " +
				'2147483647.'
		}
	])('$on $prop', ({ on, prop, refusal }) => {
		it.each([2147483648, -2147483649, 1e21, 2147483647.5])('at %s, is refused by name', (value) => {
			// Assert: the server holds this as an int, and refused a number past an int's range in the
			// model binder's words, just as it refused a fraction - and the number box has no maximum,
			// so 3000000000 or 1e21 is committed like any other number. A fraction past the range is
			// told the range and nothing else, because that is all it needs: told only that it needs a
			// whole number, it could be rounded to 2147483648 and refused a second time.
			expect(errorsAfter({ entry: DEFAULT_NAME, on, prop, value })).toEqual([refusal]);
		});

		it.each([2147483647, -2147483648])('at %s, is accepted', (value) => {
			// Assert: both ends of the range are an int's own.
			expect(errorsAfter({ entry: DEFAULT_NAME, on, prop, value })).toEqual([]);
		});
	});
});

describe('regression: a number of days the server will not count back', () => {
	const prop = 'imagesFromDays';

	it.each([-1, 36501, 2147483647, 36500.5])('imagesFromDays at %s, is refused by name', (value) => {
		// Assert: the server refuses ImagesFromDays outside 0 to 36500 - a negative number moves the
		// cut-off into the future, and one a date cannot count back failed every request for the
		// account's photos - but says so naming neither the configuration nor the account. Past the
		// range is told the range, fraction or not, as it is for an int's.
		expect(errorsAfter({ entry: DEFAULT_NAME, on: 'account', prop, value })).toEqual([
			"the default configuration: 'Images from the last' for Att needs a whole number between 0 " +
				'and 36500, or to be left blank.'
		]);
	});

	it.each([0, 36500])('imagesFromDays at %s, is accepted', (value) => {
		// Assert: 0 is today's photos alone, and both ends of the range are its own.
		expect(errorsAfter({ entry: DEFAULT_NAME, on: 'account', prop, value })).toEqual([]);
	});
});

describe('regression: a rating Immich does not give a photo', () => {
	const prop = 'rating';

	it.each([-1, 0, 6, 2147483648, 5.5])('rating at %s, is refused by name', (value) => {
		// Assert: the server refuses a Rating outside 1 to 5, because an account shows only photos
		// rated exactly that and so a number outside the range matched none. Its message does not
		// say which account, so the editor names it. A number past the range is told the range,
		// whether or not it is a fraction, as with an int's range.
		expect(errorsAfter({ entry: DEFAULT_NAME, on: 'account', prop, value })).toEqual([
			"the default configuration: 'Rating' for Att needs a whole number between 1 and 5, or to " +
				'be left blank.'
		]);
	});

	it.each([1, 3, 5])('rating at %s, is accepted', (value) => {
		// Assert: both ends of the range, and a number between them.
		expect(errorsAfter({ entry: DEFAULT_NAME, on: 'account', prop, value })).toEqual([]);
	});
});

describe('regression: a refresh interval the server cannot cache for', () => {
	const prop = 'refreshAlbumPeopleInterval';

	it.each([-1, 87601, 2147483647, 87600.5])(
		'refreshAlbumPeopleInterval at %s, is refused by name',
		(value) => {
			// Assert: the server refuses RefreshAlbumPeopleInterval outside 0 to 87600 hours.
			// int.MaxValue hours is more than a TimeSpan can hold, a hundred million hours is further
			// ahead than the cache can set an expiry, and either failed every request for photos.
			expect(errorsAfter({ entry: DEFAULT_NAME, on: 'general', prop, value })).toEqual([
				"the default configuration: 'Refresh albums and people every' needs a whole number " +
					'between 0 and 87600.'
			]);
		}
	);

	it.each([0, 87600])('refreshAlbumPeopleInterval at %s, is accepted', (value) => {
		// Assert: 0 fetches the albums and people again for every request, and 87600 is ten years.
		expect(errorsAfter({ entry: DEFAULT_NAME, on: 'general', prop, value })).toEqual([]);
	});
});

describe('regression: a save sends only what a configuration declares', () => {
	it('a general setting overridden, edited and turned off again, is not sent', () => {
		// Arrange: a profile overriding one setting.
		const { config } = open({
			default: { accounts: [{ label: 'Att' }] },
			profiles: [{ name: 'hall', declares: ['General.ShowClock'], general: { showClock: false } }]
		});
		const hall = entryNamed(config, 'hall');
		// What the save bar compares the model with to say whether anything is unsaved.
		const untouched = JSON.stringify(toUpdate(config));

		// Act: Interval overridden and given a fraction, and then its override turned off again -
		// which keeps the value in the model rather than discarding it.
		toggleGeneral(hall, 'interval', true, config.default);
		hall.general.interval = 2.5;
		toggleGeneral(hall, 'interval', false, config.default);

		const update = toUpdate(config);

		// Assert: nothing for the editor to refuse, since the profile no longer declares the box...
		expect(validationErrors(config)).toEqual([]);
		expect(hall.general.interval).toBe(2.5);

		// Assert: ...and so nothing sent from it. Sent anyway, the fraction reached the server's int
		// binding, which refused the whole save naming `$.profiles[0].general.interval` - a setting
		// the profile no longer showed. The setting still declared is sent and nothing else the read
		// reported is, so the edit undone leaves nothing unsaved.
		expect(update.profiles?.[0].general).toEqual({ showClock: false });
		expect(JSON.stringify(update)).toBe(untouched);
	});

	it('an account list overridden, edited and turned off again, is not sent', () => {
		// Arrange: a profile showing the default configuration's accounts.
		const { config } = open({
			default: { accounts: [{ label: 'Att' }] },
			profiles: [{ name: 'hall' }]
		});
		const hall = entryNamed(config, 'hall');
		const untouched = JSON.stringify(toUpdate(config));

		// Act: its account override turned on, a fraction given as the rating, and the override
		// turned off again - which keeps the selections.
		declareAccounts(config, hall);
		usedSelections(hall)[0].values.rating = 2.5;
		toggleAccounts(hall, false, config.default);

		const update = toUpdate(config);

		// Assert: the profile shows the default configuration's accounts again, so there is nothing
		// of its own to refuse and nothing of its own is sent - an empty list rather than none, since
		// the request requires one. Sent anyway, the rating reached the int binding and the whole
		// save was refused for a list the profile no longer had.
		expect(validationErrors(config)).toEqual([]);
		expect(update.profiles?.[0].accounts).toEqual([]);
		expect(JSON.stringify(update)).toBe(untouched);

		// Act: and the override turned on again, which restores what it held.
		declareAccounts(config, hall);

		// Assert: so it is sent again, and refused again, by name.
		expect(accountWritten(toUpdate(config), 'hall', 'Att').rating).toBe(2.5);
		expect(validationErrors(config)).toEqual([
			"profile 'hall': 'Rating' for Att needs a whole number, or to be left blank."
		]);
	});

	it('a declared setting this build has no field for, is sent as it was read', () => {
		// Arrange: a read from a newer server, whose profile declares a setting this build does not
		// know about. Named the way .NET camel-cases a name that opens with an acronym - `HDRMode`
		// arrives as `hdrMode` - which is not `declaredKeyOf` run backwards.
		const general: AdminGeneralSettingsDto & Record<string, unknown> = { hdrMode: 'auto' };
		const { config } = open({
			default: { accounts: [{ label: 'Att' }] },
			profiles: [{ name: 'hall', declares: ['General.HDRMode'], general }]
		});

		// Act
		const update = toUpdate(config);

		// Assert: still declared, and still carrying its value. The server reads a declared setting
		// back by name, so left out here it would be written as null over what the file held.
		expect(update.profiles?.[0].declaredKeys).toEqual(['General.HDRMode']);
		expect(update.profiles?.[0].general).toEqual({ hdrMode: 'auto' });
	});
});
