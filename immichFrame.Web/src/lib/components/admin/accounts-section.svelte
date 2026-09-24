<script lang="ts">
	import {
		ACCOUNTS_KEY,
		accountName,
		entriesUsingAccount,
		entryLabel,
		namedEntry,
		newAccount,
		newSelection,
		usedSelections,
		usesApiKeyFile,
		type EditableAccount,
		type EditableConfig,
		type EditableEntry
	} from './admin-config';

	interface Props {
		/**
		 * The whole configuration, not one entry of it. An Immich account belongs to the file rather
		 * than to whichever tab is open: the same account used by three profiles is one set of
		 * credentials, and editing it here is what keeps them from drifting apart.
		 */
		config: EditableConfig;
	}

	let { config }: Props = $props();

	/** Why a removal was refused, which is not a reason to block the rest of the form. */
	let removeError = $state('');

	let entries: EditableEntry[] = $derived([config.default, ...config.profiles]);

	/** {@link entriesUsingAccount} against this section's configuration. */
	function usedBy(account: EditableAccount): EditableEntry[] {
		return entriesUsingAccount(config, account);
	}

	/**
	 * Rows whose label is another row's, and the configurations each pair collides in.
	 *
	 * A label is what tells one account from another here, so two rows carrying one render the same
	 * header and there is nothing on either saying which is which. `validationErrors` refuses the save
	 * and explains it, but only once Save has been pressed - and this state is not something the
	 * administrator has to build to reach it: labels are normalised on read, so a hand-written file
	 * holding "Mum" and "mum " arrives already in it. Marked on the rows themselves so it is visible
	 * before then, and on the control that resolves it.
	 *
	 * Scoped per configuration and to the configurations that declare their own accounts, as the
	 * refusal is: two accounts sharing a label are ambiguous only within an entry that uses both, and
	 * an inheriting profile is showing the default configuration's list rather than one of its own.
	 */
	let collisions: { key: string; where: string }[] = $derived.by(() => {
		const byKey = new Map(config.accounts.map((account) => [account.key, account]));

		return entries.flatMap((entry) => {
			if (!entry.declared.includes(ACCOUNTS_KEY)) return [];

			// Compared as the server compares them, and as `validationErrors` does: trimmed and without
			// regard to case, so that "Mum" and "mum " are the one name they look like on screen. An
			// account with no label is dropped rather than grouped under the empty one - absence asserts
			// no identity, and two unlabelled accounts on one server have a refusal of their own.
			const labelled = usedSelections(entry)
				.map((selection) => ({
					key: selection.accountKey,
					label: (byKey.get(selection.accountKey)?.label.trim() ?? '').toLowerCase()
				}))
				.filter((row) => row.label);

			return labelled
				.filter((row, index) =>
					labelled.some((other, another) => another !== index && other.label === row.label)
				)
				.map((row) => ({ key: row.key, where: entryLabel(entry) }));
		});
	});

	/** The configurations this row's label collides in; empty where it collides in none. */
	function collidesIn(account: EditableAccount): string[] {
		return collisions
			.filter((collision) => collision.key === account.key)
			.map((collision) => collision.where);
	}

	function addAccount() {
		removeError = '';

		const account = newAccount();

		config.accounts = [...config.accounts, account];
		// Given to the default configuration straight away. It is the one entry that must declare
		// accounts, so it is where an account added from here is nearly always meant to be used - and
		// an account no configuration uses is written to no part of the settings file. Untick it
		// there to make it a profile's alone.
		config.default.accounts = [...config.default.accounts, newSelection(account.key)];
	}

	function removeAccount(account: EditableAccount, index: number) {
		removeError = '';

		const name = accountName(account, index);
		const used = usedBy(account);

		// `AdminConfigService` refuses this save by name - the default configuration is what every
		// profile inherits - so it is caught here, where it can be explained before anything is lost.
		if (used.some((entry) => entry.isDefault) && usedSelections(config.default).length === 1) {
			removeError =
				`${name} is the only Immich account the default configuration uses, and that is what ` +
				'every configuration profile inherits. Give the default configuration another account ' +
				'first: ImmichFrame cannot serve an image without one.';
			return;
		}

		const profiles = used.filter((entry) => !entry.isDefault);

		// Removing an account is destructive in a way the button does not look: it takes the account
		// out of every configuration that uses it, including profiles that are not on screen, and the
		// API key goes with it. Confirmed the way deleting a profile is.
		const confirmed = confirm(
			`Remove the Immich account '${name}'?\n\n` +
				(profiles.length === 0
					? 'No configuration profile uses it.'
					: profiles.length === 1
						? `Configuration profile '${profiles[0].name}' uses it and loses it too.`
						: `${profiles.length} configuration profiles use it and lose it too: ` +
							`${profiles.map((entry) => entry.name).join(', ')}.`) +
				'\n\nIts API key is removed from the settings file with it. Nothing changes until you save.'
		);

		if (!confirmed) return;

		config.accounts = config.accounts.filter((other) => other.key !== account.key);

		// Dropped from every entry, declaring or not: a selection left behind would name an account
		// that no longer has any credentials at all.
		for (const entry of entries) {
			entry.accounts = entry.accounts.filter((selection) => selection.accountKey !== account.key);
		}
	}
</script>

<section class="section" id="section-accounts">
	<header class="head">
		<div>
			<h2 class="title">Immich accounts</h2>
			<p class="blurb">
				Every Immich account this installation uses. Each configuration below chooses which of them
				it shows photos from.
			</p>
		</div>
		<p class="scope-note">Shared by every configuration</p>
	</header>

	{#if config.accounts.length === 0}
		<p class="empty">
			No Immich accounts are configured. ImmichFrame needs at least one to show anything.
		</p>
	{/if}

	<!-- Keyed by the account's own key rather than its position: position is deliberately not an
	     identity anywhere in this feature, and reusing a DOM node across a removal would put one
	     account's half-typed API key on another. -->
	{#each config.accounts as account, index (account.key)}
		{@const used = usedBy(account)}
		{@const colliding = collidesIn(account)}
		<article class="account">
			<header class="strip">
				<div class="ident">
					<div class="named">
						<h3 class="name">{accountName(account, index)}</h3>
						{#if colliding.length > 0}
							<!-- Both rows are shown and both are marked, rather than one merged into the other:
							     they are two accounts with two sets of credentials, and the header alone cannot
							     say so once they share a label. -->
							<span class="tag tag-danger pill">Duplicate label</span>
						{/if}
					</div>
					{#if account.label.trim() && account.serverUrl.trim()}
						<!-- Only beneath a name that is not already it: an account with no label is named by
						     its server URL, and `accountName` is what decides that. -->
						<p class="url">{account.serverUrl.trim()}</p>
					{/if}
				</div>
				<button
					type="button"
					class="btn btn-secondary remove"
					onclick={() => removeAccount(account, index)}
				>
					Remove account…
				</button>
			</header>

			<div class="fields">
				<div>
					<label class="field-label" for="{account.key}-label">Label</label>
					<input
						id="{account.key}-label"
						type="text"
						class="input"
						placeholder="Optional, e.g. Mum's photos"
						value={account.label}
						oninput={(event) => (account.label = event.currentTarget.value)}
					/>
					{#if colliding.length > 0}
						<p class="help collides">
							Another account used by {colliding.join(', ')} is labelled '{account.label.trim()}'
							too. A label is what tells one account from another here, so saving is refused until
							the two differ. Labels are compared ignoring case and surrounding spaces.
						</p>
					{:else}
						<p class="help">
							What tells two accounts on one Immich server apart. Written to the settings file only
							when you give one.
						</p>
					{/if}
				</div>

				<div>
					<label class="field-label" for="{account.key}-url">Immich server URL</label>
					<input
						id="{account.key}-url"
						type="text"
						class="input"
						placeholder="http://immich.example.com:2283"
						value={account.serverUrl}
						oninput={(event) => (account.serverUrl = event.currentTarget.value)}
					/>
				</div>

				<div>
					<label class="field-label" for="{account.key}-key-file">API key file</label>
					<input
						id="{account.key}-key-file"
						type="text"
						class="input"
						placeholder="Leave blank to store the key in the settings file"
						value={account.apiKeyFile}
						oninput={(event) => (account.apiKeyFile = event.currentTarget.value)}
					/>
				</div>
			</div>

			<div class="key">
				<!-- The heading is inside each branch rather than once above them because it is a
				     `<label for>` in only two of the four key states: the box it names is rendered when a
				     key is being typed and not otherwise, and a `for` pointing at an id that is not in the
				     document names nothing while looking like it does. Kept in the branch the input is in,
				     so the two cannot drift apart. -->
				{#if usesApiKeyFile(account)}
					<span class="field-label">API key</span>
					<p class="key-note">
						Read from the file above at start-up. ImmichFrame refuses a configuration that names
						both a key file and a key, so there is nothing to type here; clear the path to type a
						key instead.
					</p>
				{:else if account.hasStoredKey && !account.entering}
					<span class="field-label">API key</span>
					<div class="key-row">
						<span class="stored">Set</span>
						<button
							type="button"
							class="btn btn-secondary"
							onclick={() => (account.entering = true)}
						>
							Replace key
						</button>
					</div>
				{:else}
					<label class="field-label" for="{account.key}-key">API key</label>
					<div class="key-row">
						<input
							id="{account.key}-key"
							type="password"
							autocomplete="new-password"
							class="input key-input"
							placeholder="Immich API key"
							bind:value={account.apiKey}
						/>
						{#if account.hasStoredKey}
							<button
								type="button"
								class="btn btn-secondary"
								onclick={() => {
									account.entering = false;
									account.apiKey = '';
								}}
							>
								Keep stored key
							</button>
						{/if}
					</div>
					{#if !account.hasStoredKey}
						<p class="key-warning">
							The settings file has no stored key for this account, so one has to be entered before
							it can be saved.
						</p>
					{/if}
				{/if}
			</div>

			<footer class="foot">
				{#if account.disagreeing.length > 0}
					<!-- Said rather than quietly resolved: the file is asserting one account and then
					     describing it two ways, and a save writes the credentials above over every copy.
					     Amber rather than refused, because writing them through is the point of this
					     section. The one disagreement that cannot be written through - a stored key kept
					     here against an entry that reads its key from a file - is refused by
					     `validationErrors` instead, since that entry would be left with no credential. -->
					<p class="note warn">
						The settings file describes this account differently in {account.disagreeing
							.map(namedEntry)
							.join(', ')} - another server URL, or another API key file. Saving writes the label, server
						URL and API key file above to every configuration that uses it.
					</p>
				{/if}

				{#if used.length === 0}
					<p class="note warn">
						No configuration uses this account, so saving leaves it out of the settings file
						altogether. Tick it under a configuration below, or remove it.
					</p>
				{:else}
					<p class="note">
						Used by {used.map(entryLabel).join(', ')}. Editing anything above changes it for all of
						them.
					</p>
				{/if}
			</footer>
		</article>
	{/each}

	{#if removeError}
		<p class="error">{removeError}</p>
	{/if}

	<button type="button" class="btn btn-secondary add" onclick={addAccount}>Add account</button>
</section>

<style>
	/*
	 * The four elements of a section header, in the order and at the sizes `entry-editor.svelte`
	 * gives the five below this one: this section heads the same page and a second shape for the
	 * same thing would only say the two are unrelated. 26px against the sheet's 32px and 24px
	 * between sections are that file's reasoning as well - none of these headings is the page's
	 * title, and the rail's scroll-spy argues its detection band against this gap.
	 */
	.section {
		margin: var(--space-6) 0;
	}

	.head {
		display: flex;
		flex-wrap: wrap;
		align-items: flex-end;
		justify-content: space-between;
		gap: var(--space-3);
		padding-bottom: var(--space-2);
		border-bottom: 2px solid var(--color-divider);
	}

	.title {
		margin: 0;
		font-size: 26px;
	}

	.blurb {
		margin: 2px 0 0;
		font-size: 13px;
		color: var(--color-neutral-700);
	}

	/* Where the sections below say what inherits from them, this one says what it is not part of. */
	.scope-note {
		margin: 0;
		font-size: 11.5px;
		text-align: right;
		color: var(--color-neutral-700);
	}

	/*
	 * An installation with no accounts shows nothing at all, but that is a state to put right
	 * rather than an action that failed: the warning role, as the banner's read-only and
	 * legacy-schema notes take, and not the danger role this section spends on refusals.
	 */
	.empty {
		margin: var(--space-3) 0 0;
		font-size: 13px;
		color: var(--color-warning-700);
	}

	.account {
		margin-top: var(--space-3);
		border: 1px solid var(--color-divider);
	}

	.strip {
		display: flex;
		flex-wrap: wrap;
		align-items: center;
		justify-content: space-between;
		gap: var(--space-2);
		padding: 12px 16px;
		background: var(--color-neutral-200);
		border-bottom: 1px solid var(--color-neutral-300);
	}

	/*
	 * `min-width: 0` on the item and on the heading inside it, so a long name or a long URL is
	 * truncated rather than widening the card: a flex item refuses to shrink below its own content
	 * until it is told it may, and an account's name is whatever was typed into the box below.
	 */
	.ident {
		min-width: 0;
	}

	.named {
		display: flex;
		align-items: center;
		gap: var(--space-2);
	}

	.name,
	.url {
		min-width: 0;
		margin: 0;
		overflow: hidden;
		text-overflow: ellipsis;
		white-space: nowrap;
	}

	/* 16px against the sheet's 25px: this is a card's title, not a section's. */
	.name {
		font-size: 16px;
	}

	.url {
		font-family: 'Overpass Mono', ui-monospace, monospace;
		font-size: 12px;
		color: var(--color-neutral-700);
	}

	/* Compounded with `.tag`: the two carry equal specificity, so a bare `.pill` would be settled
	   by emission order the day it sets something the component class sets too. */
	.tag.pill {
		flex-shrink: 0;
	}

	/*
	 * Removing an account takes its credentials out of every configuration that uses it, so it
	 * wears the accent at 700 - the same dress, and the same reasoning, as the strip's
	 * `delete-profile`. Compounded with `.btn`, which sets the colour and the border itself.
	 */
	.btn.remove {
		flex-shrink: 0;
		font-size: 12.5px;
		color: var(--color-accent-700);
		border-color: var(--color-accent-300);
	}

	.btn.remove:hover {
		background: var(--color-accent-100);
	}

	/*
	 * The three credentials side by side where there is room and stacked where there is not:
	 * `auto-fit` drops a column each time the row can no longer give every one of them 240px,
	 * which is what carries this band through the 900px breakpoint the rail goes static at.
	 */
	.fields {
		display: grid;
		grid-template-columns: repeat(auto-fit, minmax(240px, 1fr));
		gap: 18px;
		padding: 18px 16px;
	}

	.field-label {
		display: block;
		margin-bottom: var(--space-1);
		font-size: 13px;
		font-weight: 600;
	}

	.help {
		margin: var(--space-1) 0 0;
		font-size: 11.5px;
		color: var(--color-neutral-700);
	}

	/*
	 * A collision is refused by `validationErrors` on save, so it takes the danger role the way the
	 * section's other refusal does. Compounded so it beats `.help`'s own colour on specificity
	 * rather than on which rule came second, as `secret-field.svelte`'s note does.
	 */
	.help.collides {
		color: var(--color-danger-700);
	}

	/*
	 * Its own band rather than a fourth cell of the grid above: three of the key's four states are
	 * not a text box at all - a paragraph, a word beside a button, or a box with a button beside it
	 * - and none of them is the shape a column sized for an input was drawn for.
	 */
	.key {
		padding: 0 16px 18px;
	}

	.key-row {
		display: flex;
		flex-wrap: wrap;
		align-items: center;
		gap: var(--space-2);
	}

	/*
	 * Shares its row with the button beside it, so it is sized by the flex line rather than by
	 * `.input`'s own `width: 100%`, and `min-width: 0` is what lets it shrink past the width a text
	 * box carries intrinsically. Compounded with `.input`, as `secret-field.svelte` does.
	 */
	.input.key-input {
		flex: 1;
		min-width: 0;
	}

	/*
	 * The admin theme has no success role to say "Set" in, so it is said by weight at full
	 * strength against the played-down notes around it - which is how `secret-field.svelte` says
	 * the same word. Its absence needs no tone of its own here: where there is no stored key the
	 * word is replaced by a box asking for one, and not by a second word.
	 */
	.stored {
		font-size: 14px;
		font-weight: 600;
	}

	/* Informational, and neither a warning nor a refusal: the played-down tone, at the 14px the
	   control it stands in for is set in rather than at a caption's size. */
	.key-note {
		margin: var(--space-1) 0 0;
		font-size: 14px;
		color: var(--color-neutral-700);
	}

	/* Something to put right before saving rather than something that failed: the warning role. */
	.key-warning {
		margin: var(--space-1) 0 0;
		font-size: 12px;
		color: var(--color-warning-700);
	}

	/*
	 * Both of these are statements about the configurations this account appears in rather than
	 * about the credentials above, which is why they share the band under the rule: what else is
	 * true of this account elsewhere.
	 */
	.foot {
		padding: 10px 16px;
		font-size: 11.5px;
		color: var(--color-neutral-700);
		border-top: 1px solid var(--color-neutral-300);
	}

	.note {
		margin: 0;
	}

	.note + .note {
		margin-top: var(--space-1);
	}

	.note.warn {
		color: var(--color-warning-700);
	}

	/* A refused removal is a failure, so it takes the danger role the save bar's errors take. */
	.error {
		margin: var(--space-3) 0 0;
		font-size: 13px;
		color: var(--color-danger-700);
	}

	.btn.add {
		margin-top: var(--space-3);
	}

	/*
	 * Tailwind's preflight paints a placeholder at half of `currentColor`, which composites to
	 * 3.10:1 on the input's own surface - clear of the 3:1 floor but short of AA, and all four of
	 * these are worked examples of what belongs in the box rather than decoration. Neutral-700 is
	 * 5.38:1 on that same surface, and is the retune `setting-field.svelte` and the profile strip's
	 * name box already make.
	 */
	.input::placeholder {
		color: var(--color-neutral-700);
	}
</style>
