<script lang="ts">
	import type { AdminGeneralSettingsDto } from '$lib/immichFrameApi';
	import {
		ACCOUNTS_KEY,
		accountName,
		declaredKeyOf,
		entryLabel,
		generalFields,
		generalSections,
		newSelection,
		setAccountUse,
		usedSelections,
		type AccountSelection,
		type EditableAccount,
		type EditableEntry,
		type FieldValue,
		type GeneralProp,
		type SecretProp
	} from './admin-config';
	import AccountEditor from './account-editor.svelte';
	import OverrideRow from './override-row.svelte';
	import SecretField from './secret-field.svelte';
	import SettingField from './setting-field.svelte';

	interface Props {
		entry: EditableEntry;
		/**
		 * Every Immich account in the configuration. This section assigns them rather than describing
		 * them: the credentials belong to the account, and are edited once, above the tabs.
		 */
		accounts: EditableAccount[];
		/** The configuration this one inherits from, or null for the default configuration itself. */
		inheritFrom: EditableEntry | null;
		/** The loaded configuration's version token, which the Immich pickers ask against. */
		version: string;
	}

	let { entry, accounts, inheritFrom, version }: Props = $props();

	// The default configuration has nothing above it, so its unset keys fall back to the setting's
	// built-in default rather than to another configuration.
	let inheritLabel = $derived(inheritFrom ? 'Inherited' : 'Built-in default');
	let accountsDeclared = $derived(entry.declared.includes(ACCOUNTS_KEY));
	let inherited = $derived(inheritFrom ? usedSelections(inheritFrom) : []);
	/** The accounts this profile is inheriting, in the order the section above lists them. */
	let inheritedAccounts = $derived(
		accounts
			.map((account, index) => ({ account, index }))
			.filter((row) => inherited.some((selection) => selection.accountKey === row.account.key))
	);

	function declares(prop: GeneralProp): boolean {
		return entry.declared.includes(declaredKeyOf(prop));
	}

	/** The configuration a setting's displayed value comes from while it is not declared here. */
	function valuesFor(prop: GeneralProp): AdminGeneralSettingsDto {
		return declares(prop) ? entry.general : (inheritFrom?.general ?? entry.general);
	}

	function hasSecret(values: AdminGeneralSettingsDto, prop: SecretProp): boolean {
		if (prop === 'weatherApiKey') return values.hasWeatherApiKey === true;
		if (prop === 'webhook') return values.hasWebhook === true;
		return values.hasAuthenticationSecret === true;
	}

	function toggleGeneral(prop: GeneralProp, declared: boolean) {
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

	function toggleAccounts(declared: boolean) {
		if (!declared) {
			entry.declared = entry.declared.filter((other) => other !== ACCOUNTS_KEY);
			return;
		}

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
			entry.accounts = inherited.map((selection) =>
				newSelection(selection.accountKey, selection.values)
			);
		}

		entry.declared = [...entry.declared, ACCOUNTS_KEY];
	}

	/** This entry's selection for an account, ticked or not: an unticked one is kept, not dropped. */
	function selectionFor(account: EditableAccount): AccountSelection | undefined {
		return entry.accounts.find((selection) => selection.accountKey === account.key);
	}

	function setGeneral(prop: GeneralProp, value: FieldValue) {
		Object.assign(entry.general, { [prop]: value });
	}

	/**
	 * A profile with no authentication secret is a documented capability - it is how a frame on a
	 * trusted network skips the prompt - and it is also the one choice here that makes a URL
	 * publicly readable. Said at the point of choosing rather than left to the documentation.
	 */
	function noSecretWarning(prop: SecretProp): string | undefined {
		if (prop !== 'authenticationSecret' || inheritFrom === null) return undefined;

		return (
			`Anyone who can reach /${entry.name} will then see everything this profile is ` +
			'configured to show, without a secret.'
		);
	}
</script>

{#each generalSections as section (section.title)}
	<section class="mb-6" id="section-{section.title.toLowerCase()}">
		<h3 class="mb-1 text-sm font-semibold uppercase tracking-wide text-neutral-400">
			{section.title}
		</h3>
		{#each section.props as prop (prop)}
			{@const spec = generalFields[prop]}
			{@const id = `${entry.name}-${prop}`}
			<OverrideRow
				{id}
				label={spec.label}
				help={spec.help}
				declared={declares(prop)}
				{inheritLabel}
				onToggle={(declared) => toggleGeneral(prop, declared)}
			>
				{#if spec.kind === 'secret'}
					<SecretField
						{id}
						label={spec.label}
						secret={entry.secrets[prop as SecretProp]}
						hasValue={hasSecret(valuesFor(prop), prop as SecretProp)}
						declared={declares(prop)}
						canKeep={entry.originallyDeclared.includes(declaredKeyOf(prop))}
						inherits={inheritFrom !== null}
						noSecretWarning={noSecretWarning(prop as SecretProp)}
					/>
				{:else}
					<SettingField
						{id}
						{spec}
						value={valuesFor(prop)[prop]}
						disabled={!declares(prop)}
						onChange={(value) => setGeneral(prop, value)}
					/>
				{/if}
			</OverrideRow>
		{/each}
	</section>
{/each}

<section class="mb-6" id="section-photo-selection">
	<div class="mb-2 flex flex-wrap items-center justify-between gap-2">
		<h3 class="text-sm font-semibold uppercase tracking-wide text-neutral-400">Immich accounts</h3>
		{#if entry.isDefault}
			<span class="text-xs text-neutral-500">
				The default configuration always uses at least one of the accounts above.
			</span>
		{:else}
			<label class="flex items-center gap-2 text-xs text-neutral-400">
				<input
					type="checkbox"
					class="h-3.5 w-3.5 accent-sky-500"
					checked={accountsDeclared}
					onchange={(event) => toggleAccounts(event.currentTarget.checked)}
				/>
				<span>{accountsDeclared ? 'Overridden' : 'Inherited'}</span>
			</label>
		{/if}
	</div>

	{#if accountsDeclared}
		{#if !entry.isDefault && !entry.accountsWereDeclared}
			<p class="mb-2 rounded border border-amber-600 bg-amber-950/40 p-2 text-xs text-amber-300">
				This profile now declares its own accounts instead of inheriting them. The accounts ticked
				below keep the API keys already stored for them, but changes to which accounts the default
				configuration uses no longer reach this profile.
			</p>
		{/if}

		{#if accounts.length === 0}
			<p class="text-xs text-neutral-500">
				There are no Immich accounts to choose from. Add one in the Immich accounts section above.
			</p>
		{/if}

		<div class="space-y-3">
			<!-- Keyed by the account's own key rather than its position: position is deliberately not an
			     identity anywhere in this feature, and reusing a DOM node across a removal would put one
			     account's photo selection under another. -->
			{#each accounts as account, index (account.key)}
				{@const selection = selectionFor(account)}
				<div class="rounded border border-neutral-700 p-3">
					<label class="flex items-center gap-2">
						<input
							type="checkbox"
							class="h-4 w-4 shrink-0 accent-sky-500"
							checked={selection?.uses === true}
							onchange={(event) =>
								setAccountUse(entry, account, event.currentTarget.checked, inheritFrom)}
						/>
						<span class="truncate text-sm text-neutral-200">{accountName(account, index)}</span>
						{#if account.label.trim() && account.serverUrl.trim()}
							<span class="truncate text-xs text-neutral-500">{account.serverUrl.trim()}</span>
						{/if}
					</label>

					{#if selection?.uses}
						<div class="mt-3">
							<AccountEditor
								id="{entry.name}-{account.key}"
								{account}
								{selection}
								{version}
								title="What {entryLabel(entry)} shows from this account"
							/>
						</div>
					{/if}
				</div>
			{/each}
		</div>
	{:else}
		<p class="mb-2 text-xs text-neutral-500">
			Inherited from the default configuration. Override to choose which accounts this profile shows
			and what it shows from each.
		</p>
		<ul class="space-y-1 text-sm text-neutral-300">
			{#each inheritedAccounts as row (row.account.key)}
				<li class="rounded border border-neutral-800 px-2 py-1">
					{accountName(row.account, row.index)}
				</li>
			{/each}
		</ul>
	{/if}
</section>
