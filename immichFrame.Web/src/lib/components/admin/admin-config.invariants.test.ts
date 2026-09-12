import { describe, expect, it } from 'vitest';
import { ACCOUNTS_KEY, setAccountUse, toUpdate, validationErrors } from './admin-config';
import { DEFAULT_NAME, type AccountFixture } from './admin-config.test-fixtures';
import {
	accountLabelled,
	accountsWritten,
	declareAccounts,
	entryNamed,
	open
} from './admin-config.test-editor';
import { simulateSave } from './admin-config.test-server-model';

const IMMICH = 'https://immich.example';
const BACKUP = 'https://backup.example';

/** How one configuration's declared account list holds the account the grid is about. */
type Stored = 'inline' | 'file' | 'absent';

/** A profile may also declare no account list at all, and show the default configuration's. */
type ProfileStored = Stored | 'inherits';

/** What the administrator does to the row's API key file box. */
type BoxEdit = 'unchanged' | 'cleared' | 'set';

/**
 * And to its API key: keep the stored one, type a replacement, or press Replace key and type
 * nothing yet - which is the state an administrator is in mid-edit, and reads as harmless.
 */
type KeyEdit = 'keep' | 'typed' | 'replacing';

const STORED: Stored[] = ['inline', 'file', 'absent'];
const PROFILE_STORED: ProfileStored[] = ['inherits', 'inline', 'file', 'absent'];
const BOX_EDITS: BoxEdit[] = ['unchanged', 'cleared', 'set'];
const KEY_EDITS: KeyEdit[] = ['keep', 'typed', 'replacing'];
const TICKS = [true, false];

/**
 * One configuration's declared `Accounts` list, or undefined where it declares none.
 *
 * Every declared list holds `Backup` whether or not it holds the account under test, so that
 * unticking that account never leaves a configuration declaring an account list it uses nothing
 * from. That state is legal on the server - a declared empty list overrides the inherited one - and
 * refused by the editor as a configuration that cannot serve an image, which is a deliberate
 * difference between the two and would drown out the credential question this grid is asking.
 */
function declaredList(stored: ProfileStored, entryName: string): AccountFixture[] | undefined {
	const backup: AccountFixture = { label: 'Backup', url: BACKUP };

	if (stored === 'inherits') return undefined;
	if (stored === 'absent') return [backup];

	return [
		backup,
		{
			label: 'Att',
			url: IMMICH,
			credential: stored === 'file' ? 'file' : 'key',
			apiKeyFile: `/run/secrets/${entryName}`
		}
	];
}

interface GridCase {
	defaultStored: Stored;
	alphaStored: ProfileStored;
	betaStored: ProfileStored;
	box: BoxEdit;
	key: KeyEdit;
	defaultUses: boolean;
	alphaUses: boolean;
	betaUses: boolean;
}

function describeCase(shape: GridCase): string {
	return (
		`default ${shape.defaultStored}${shape.defaultUses ? '+used' : ''}, ` +
		`alpha ${shape.alphaStored}${shape.alphaUses ? '+used' : ''}, ` +
		`beta ${shape.betaStored}${shape.betaUses ? '+used' : ''}, ` +
		`key file ${shape.box}, key ${shape.key}`
	);
}

/** Every combination of the dimensions above, in a fixed order. */
function everyCase(): GridCase[] {
	const cases: GridCase[] = [];

	for (const defaultStored of STORED) {
		for (const alphaStored of PROFILE_STORED) {
			for (const betaStored of PROFILE_STORED) {
				for (const box of BOX_EDITS) {
					for (const key of KEY_EDITS) {
						for (const defaultUses of TICKS) {
							for (const alphaUses of TICKS) {
								for (const betaUses of TICKS) {
									cases.push({
										defaultStored,
										alphaStored,
										betaStored,
										box,
										key,
										defaultUses,
										alphaUses,
										betaUses
									});
								}
							}
						}
					}
				}
			}
		}
	}

	return cases;
}

describe('the credential-shape grid', () => {
	/**
	 * Every shape one Immich account's credentials can be in across a three-configuration document,
	 * against what the server would do with the save the editor produces from it.
	 *
	 * Enumerated rather than sampled, deliberately. The defect this grid was written for - an entry
	 * keeping a stored key whose owner reads it from a file - needs a document where the entry that
	 * writes the account is not the entry whose stored copy the handle names, and a 250 000-document
	 * random fuzz over that space found it zero times. Answering the question from the entry sending
	 * the handle rather than from the entry that owns it fails ten of the 3168 cases below - one case
	 * in 317, too rare to meet by chance and certain to be met by enumeration.
	 *
	 * Both directions are checked. A save the server would take but the editor refuses is as much a
	 * defect as one the editor lets through: the first blocks a legitimate save behind a message
	 * about a file the administrator never edited.
	 */
	it('the editor refuses exactly the saves the server would refuse', () => {
		// Arrange
		const missed: string[] = [];
		const overRefused: string[] = [];
		let checked = 0;
		let refused = 0;

		for (const shape of everyCase()) {
			// A document in which no configuration stores this account at all has no row to edit: the
			// account simply is not in the settings file. Out of the grid's space rather than a case
			// with an expected answer.
			const storedSomewhere =
				shape.defaultStored !== 'absent' ||
				shape.alphaStored === 'inline' ||
				shape.alphaStored === 'file' ||
				shape.betaStored === 'inline' ||
				shape.betaStored === 'file';

			if (!storedSomewhere) continue;

			checked++;

			const { read, config } = open({
				default: { accounts: declaredList(shape.defaultStored, DEFAULT_NAME) },
				profiles: [
					{ name: 'alpha', accounts: declaredList(shape.alphaStored, 'alpha') },
					{ name: 'beta', accounts: declaredList(shape.betaStored, 'beta') }
				]
			});

			// Act: edit the row, as the accounts section's controls do.
			const att = accountLabelled(config, 'Att');

			if (shape.box === 'cleared') att.apiKeyFile = '';
			if (shape.box === 'set') att.apiKeyFile = '/run/secrets/typed';

			if (shape.key !== 'keep') {
				att.entering = true;
				att.apiKey = shape.key === 'typed' ? 'typed-key' : '';
			}

			// The profiles first and the default configuration last: turning a profile's account
			// override on starts it from what it was inheriting, so the default's ticks have to still
			// be the ones the read produced when that happens.
			for (const [name, uses] of [
				['alpha', shape.alphaUses],
				['beta', shape.betaUses]
			] as const) {
				const entry = entryNamed(config, name);

				if (uses) declareAccounts(config, entry);
				if (entry.declared.includes(ACCOUNTS_KEY)) setAccountUse(entry, att, uses, config.default);
			}

			setAccountUse(config.default, att, shape.defaultUses, null);

			const outcome = simulateSave(toUpdate(config), read.storedById);
			const errors = validationErrors(config);

			// Assert
			if (outcome.accepted) {
				if (errors.length > 0) overRefused.push(`${describeCase(shape)} -> ${errors[0]}`);
			} else {
				refused++;

				if (errors.length === 0) {
					missed.push(`${describeCase(shape)} -> ${outcome.refusal}: ${outcome.message}`);
				}
			}
		}

		expect(missed.slice(0, 5)).toEqual([]);
		expect(overRefused.slice(0, 5)).toEqual([]);
		expect({ missed: missed.length, overRefused: overRefused.length }).toEqual({
			missed: 0,
			overRefused: 0
		});

		// Assert: and the grid is the size it is meant to be, with both answers in it - a grid where
		// every case is accepted proves nothing.
		expect(checked).toBe(3168);
		expect(refused).toBeGreaterThan(0);
		expect(checked - refused).toBeGreaterThan(0);
	});
});

/** The account lists a document in the handle invariant below can declare. */
const LISTS: Record<string, AccountFixture[]> = {
	'one labelled': [{ label: 'Att', url: IMMICH }],
	'two labelled': [
		{ label: 'Att', url: IMMICH },
		{ label: 'Backup', url: BACKUP }
	],
	'two unlabelled on one server': [{ url: IMMICH }, { url: IMMICH }],
	'labelled and unlabelled on one server': [{ label: 'Att', url: IMMICH }, { url: IMMICH }],
	'three, one from a key file': [
		{ label: 'Att', url: IMMICH },
		{ url: IMMICH },
		{ label: 'Backup', url: BACKUP, credential: 'file' }
	],
	'two labelled, reordered': [
		{ label: 'Backup', url: BACKUP },
		{ label: 'Att', url: IMMICH }
	],
	// Labels are normalised on read, so a hand-written file holding "Att" and "att " arrives already
	// in this state. The two are still two accounts, and the roster has to keep them apart: merging
	// them would leave one entry holding two selections of one row, and therefore writing one handle
	// twice.
	'two whose labels collide': [
		{ label: 'Att', url: IMMICH },
		{ label: 'att ', url: BACKUP }
	]
};

/** One of the lists above by name, or nothing for a configuration that declares none. */
function listNamed(name: string): AccountFixture[] | undefined {
	return name === 'inherits' ? undefined : LISTS[name];
}

/** What the administrator does to one configuration's ticks. */
type Ticks = 'as read' | 'use every account' | 'use only the first';

const TICK_EDITS: Ticks[] = ['as read', 'use every account', 'use only the first'];

describe('the no-duplicate-handle invariant', () => {
	/**
	 * No configuration ever writes two accounts carrying one handle.
	 *
	 * `AdminConfigService.Accounts` refuses that outright - two accounts naming one stored account
	 * is an ambiguity it will not resolve by guessing - and the refusal names an account the
	 * administrator never touched. It is a property of arbitrary documents rather than a fact about
	 * one, so it is checked over every combination of three configurations' account lists and of
	 * what the administrator then ticks, rather than over a single case.
	 */
	it('no configuration writes two accounts on one handle, over every generated document', () => {
		// Arrange
		const problems: string[] = [];
		const shapes = Object.keys(LISTS);
		let checked = 0;

		for (const defaultList of shapes) {
			for (const alphaList of [...shapes, 'inherits']) {
				for (const betaList of [...shapes, 'inherits']) {
					for (const defaultTicks of TICK_EDITS) {
						for (const alphaTicks of TICK_EDITS) {
							for (const betaTicks of TICK_EDITS) {
								checked++;

								const { read, config } = open({
									default: { accounts: listNamed(defaultList) },
									profiles: [
										{ name: 'alpha', accounts: listNamed(alphaList) },
										{ name: 'beta', accounts: listNamed(betaList) }
									]
								});

								// Act
								for (const [name, ticks] of [
									[DEFAULT_NAME, defaultTicks],
									['alpha', alphaTicks],
									['beta', betaTicks]
								] as const) {
									const entry = entryNamed(config, name);

									if (ticks === 'as read') continue;

									declareAccounts(config, entry);

									for (const [index, account] of config.accounts.entries()) {
										setAccountUse(
											entry,
											account,
											ticks === 'use every account' || index === 0,
											config.default
										);
									}
								}

								const update = toUpdate(config);
								const where =
									`default ${defaultList}/${defaultTicks}, alpha ${alphaList}/${alphaTicks}, ` +
									`beta ${betaList}/${betaTicks}`;

								// Assert
								for (const name of [DEFAULT_NAME, 'alpha', 'beta']) {
									const handles = accountsWritten(update, name)
										.map((account) => account.id)
										.filter((id): id is string => id !== undefined);

									if (new Set(handles).size !== handles.length) {
										problems.push(`${where}: '${name}' wrote ${JSON.stringify(handles)}`);
									}

									for (const handle of handles) {
										if (!read.storedById.has(handle)) {
											problems.push(`${where}: '${name}' wrote an invented handle '${handle}'`);
										}
									}
								}

								const outcome = simulateSave(update, read.storedById);

								if (!outcome.accepted && outcome.refusal === 'ambiguous-handle') {
									problems.push(`${where}: the server called '${outcome.entry}' ambiguous`);
								}
							}
						}
					}
				}
			}
		}

		expect(problems.slice(0, 5)).toEqual([]);
		expect(problems).toHaveLength(0);
		expect(checked).toBe(12096);
	});
});
