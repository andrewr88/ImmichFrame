import type { AdminAccountSettingsDto, AdminConfigUpdateDto } from '$lib/immichFrameApi';
import {
	toEditable,
	toggleAccounts,
	type EditableAccount,
	type EditableConfig,
	type EditableEntry
} from './admin-config';
import {
	buildDocument,
	DEFAULT_NAME,
	type DocumentFixture,
	type ReadDocument
} from './admin-config.test-fixtures';

/**
 * Driving the editor's model the way the admin components drive it, and looking things up in what
 * comes out.
 *
 * Test support only. Unlike the fixtures and the server model this does import the code under test:
 * it is the caller's side of it, not a second opinion about what it should do.
 */

export interface OpenEditor {
	/** The read the editor was loaded from, and the stored document a save resolves against. */
	read: ReadDocument;
	config: EditableConfig;
}

/** Reads a settings file into the editor, as `config-editor.svelte` does on load. */
export function open(fixture: DocumentFixture): OpenEditor {
	const read = buildDocument(fixture);

	return { read, config: toEditable(read.dto) };
}

export function entryNamed(config: EditableConfig, name: string): EditableEntry {
	const entry =
		name === DEFAULT_NAME
			? config.default
			: config.profiles.find((profile) => profile.name === name);

	if (!entry) throw new Error(`no configuration named '${name}'`);

	return entry;
}

/** The accounts section's row for the account carrying this label. */
export function accountLabelled(config: EditableConfig, label: string): EditableAccount {
	const matches = config.accounts.filter((account) => account.label === label);

	if (matches.length !== 1) {
		throw new Error(`expected one account row labelled '${label}', found ${matches.length}`);
	}

	return matches[0];
}

/**
 * Turning a configuration's account override on, as `entry-editor.svelte` does: through
 * {@link toggleAccounts} itself, handed what `config-editor.svelte` hands that editor to inherit
 * from - nothing for the default configuration, and the default configuration for a profile.
 */
export function declareAccounts(config: EditableConfig, entry: EditableEntry): void {
	toggleAccounts(entry, true, entry.isDefault ? null : config.default);
}

/** What one configuration would be saved with. */
export function accountsWritten(
	update: AdminConfigUpdateDto,
	entryName: string
): AdminAccountSettingsDto[] {
	const entry =
		entryName === DEFAULT_NAME
			? update.default
			: (update.profiles ?? []).find((profile) => profile.name === entryName);

	if (!entry) throw new Error(`'${entryName}' is not in the update`);

	return entry.accounts ?? [];
}

/** The one account this configuration writes for a given label, or a failure naming what it wrote. */
export function accountWritten(
	update: AdminConfigUpdateDto,
	entryName: string,
	label: string
): AdminAccountSettingsDto {
	const written = accountsWritten(update, entryName);
	const matches = written.filter((account) => account.label === label);

	if (matches.length !== 1) {
		throw new Error(
			`expected '${entryName}' to write one account labelled '${label}', ` +
				`it wrote ${JSON.stringify(written.map((account) => account.label))}`
		);
	}

	return matches[0];
}
