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

	const control =
		'w-full rounded bg-neutral-900 border border-neutral-700 px-2 py-1 text-sm text-neutral-100';
	const button =
		'rounded border border-neutral-600 px-2 py-0.5 text-xs text-neutral-200 hover:border-neutral-400';

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

<section class="mb-6">
	<div class="mb-2 flex flex-wrap items-center justify-between gap-2">
		<h3 class="text-sm font-semibold uppercase tracking-wide text-neutral-400">Immich accounts</h3>
		<span class="text-xs text-neutral-500">
			Every Immich account this installation uses. Each configuration below chooses which of them it
			shows photos from.
		</span>
	</div>

	{#if config.accounts.length === 0}
		<p class="mb-2 text-xs text-amber-400">
			No Immich accounts are configured. ImmichFrame needs at least one to show anything.
		</p>
	{/if}

	<div class="space-y-3">
		<!-- Keyed by the account's own key rather than its position: position is deliberately not an
		     identity anywhere in this feature, and reusing a DOM node across a removal would put one
		     account's half-typed API key on another. -->
		{#each config.accounts as account, index (account.key)}
			{@const used = usedBy(account)}
			{@const colliding = collidesIn(account)}
			<div class="rounded border border-neutral-700 p-3">
				<div class="mb-3 flex items-center justify-between gap-2">
					<div class="flex min-w-0 items-center gap-2">
						<h4 class="truncate text-sm font-semibold text-neutral-200">
							{accountName(account, index)}
						</h4>
						{#if colliding.length > 0}
							<!-- Both rows are shown and both are marked, rather than one merged into the other:
							     they are two accounts with two sets of credentials, and the header alone cannot
							     say so once they share a label. -->
							<span
								class="shrink-0 rounded border border-red-700 bg-red-950/40 px-1.5 py-0.5 text-xs
									text-red-300"
							>
								Duplicate label
							</span>
						{/if}
					</div>
					<button
						type="button"
						class="shrink-0 rounded border border-red-700 px-2 py-0.5 text-xs text-red-300
							hover:border-red-500 hover:text-red-200"
						onclick={() => removeAccount(account, index)}
					>
						Remove account…
					</button>
				</div>

				<div class="mb-3 grid grid-cols-1 gap-3 sm:grid-cols-3">
					<div>
						<label class="text-sm text-neutral-200" for="{account.key}-label">Label</label>
						<input
							id="{account.key}-label"
							type="text"
							class={control}
							placeholder="Optional, e.g. Mum's photos"
							value={account.label}
							oninput={(event) => (account.label = event.currentTarget.value)}
						/>
						{#if colliding.length > 0}
							<p class="text-xs text-red-300">
								Another account used by {colliding.join(', ')} is labelled '{account.label.trim()}'
								too. A label is what tells one account from another here, so saving is refused until
								the two differ. Labels are compared ignoring case and surrounding spaces.
							</p>
						{:else}
							<p class="text-xs text-neutral-500">
								What tells two accounts on one Immich server apart. Written to the settings file
								only when you give one.
							</p>
						{/if}
					</div>

					<div>
						<label class="text-sm text-neutral-200" for="{account.key}-url">Immich server URL</label
						>
						<input
							id="{account.key}-url"
							type="text"
							class={control}
							placeholder="http://immich.example.com:2283"
							value={account.serverUrl}
							oninput={(event) => (account.serverUrl = event.currentTarget.value)}
						/>
					</div>

					<div>
						<label class="text-sm text-neutral-200" for="{account.key}-key-file">API key file</label
						>
						<input
							id="{account.key}-key-file"
							type="text"
							class={control}
							placeholder="Leave blank to store the key in the settings file"
							value={account.apiKeyFile}
							oninput={(event) => (account.apiKeyFile = event.currentTarget.value)}
						/>
					</div>
				</div>

				<div class="mb-2">
					<span class="text-sm text-neutral-200">API key</span>
					{#if usesApiKeyFile(account)}
						<p class="text-sm text-sky-300">
							Read from the file above at start-up. ImmichFrame refuses a configuration that names
							both a key file and a key, so there is nothing to type here; clear the path to type a
							key instead.
						</p>
					{:else if account.hasStoredKey && !account.entering}
						<div class="flex flex-wrap items-center gap-2">
							<span class="text-sm text-emerald-400">Set</span>
							<button type="button" class={button} onclick={() => (account.entering = true)}>
								Replace key
							</button>
						</div>
					{:else}
						<div class="flex flex-wrap items-center gap-2">
							<input
								id="{account.key}-key"
								type="password"
								autocomplete="new-password"
								class="min-w-0 flex-1 rounded border border-neutral-700 bg-neutral-900 px-2 py-1
									text-sm text-neutral-100"
								placeholder="Immich API key"
								bind:value={account.apiKey}
							/>
							{#if account.hasStoredKey}
								<button
									type="button"
									class={button}
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
							<p class="mt-1 text-xs text-amber-400">
								The settings file has no stored key for this account, so one has to be entered
								before it can be saved.
							</p>
						{/if}
					{/if}
				</div>

				{#if account.disagreeing.length > 0}
					<!-- Said rather than quietly resolved: the file is asserting one account and then
					     describing it two ways, and a save writes the credentials above over every copy.
					     Amber rather than refused, because writing them through is the point of this
					     section. The one disagreement that cannot be written through - a stored key kept
					     here against an entry that reads its key from a file - is refused by
					     `validationErrors` instead, since that entry would be left with no credential. -->
					<p class="mb-1 text-xs text-amber-400">
						The settings file describes this account differently in {account.disagreeing
							.map(namedEntry)
							.join(', ')} - another server URL, or another API key file. Saving writes the label, server
						URL and API key file above to every configuration that uses it.
					</p>
				{/if}

				{#if used.length === 0}
					<p class="text-xs text-amber-400">
						No configuration uses this account, so saving leaves it out of the settings file
						altogether. Tick it under a configuration below, or remove it.
					</p>
				{:else}
					<p class="text-xs text-neutral-500">
						Used by {used.map(entryLabel).join(', ')}. Editing anything above changes it for all of
						them.
					</p>
				{/if}
			</div>
		{/each}
	</div>

	{#if removeError}
		<p class="mt-2 text-sm text-red-300">{removeError}</p>
	{/if}

	<button type="button" class="{button} mt-3" onclick={addAccount}>Add account</button>
</section>
