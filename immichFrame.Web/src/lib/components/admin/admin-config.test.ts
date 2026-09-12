import { describe, expect, it } from 'vitest';
import {
	ACCOUNTS_KEY,
	SECRET_PLACEHOLDER,
	accountHandle,
	declaredKeyOf,
	entriesUsingAccount,
	generalFields,
	newAccount,
	newSelection,
	savedAccountHandle,
	setAccountUse,
	toUpdate,
	usedSelections,
	validationErrors,
	type GeneralProp
} from './admin-config';
import { DEFAULT_NAME } from './admin-config.test-fixtures';
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
		const added = newAccount();

		added.serverUrl = 'https://new.example';
		config.accounts = [...config.accounts, added];
		config.default.accounts = [...config.default.accounts, newSelection(added.key)];

		// Assert: nothing in the file has a key for it, and no handle can reach one.
		expect(validationErrors(config)).toEqual([
			expect.stringContaining('Enter the API key for https://new.example')
		]);
	});

	it('an account no configuration uses, is not asked for a key', () => {
		// Arrange
		const { config } = open({ default: { accounts: [{ label: 'Att' }] } });

		// Act: added and never ticked, so it is written to no part of the settings file.
		const added = newAccount();

		added.serverUrl = 'https://new.example';
		config.accounts = [...config.accounts, added];

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
