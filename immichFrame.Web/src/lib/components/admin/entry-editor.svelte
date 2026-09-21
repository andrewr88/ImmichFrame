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
	// The same fact read from the other end, for the section headers: what the reader is looking at
	// is either the values every profile starts from, or a profile that falls back to them.
	let inheritNote = $derived(
		inheritFrom
			? 'Anything not overridden follows the default configuration'
			: 'Every profile inherits what is not overridden here'
	);
	/** How a row's override control names this configuration when it offers to write into it. */
	let editing = $derived(entryLabel(entry));
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
	<section class="section" id="section-{section.title.toLowerCase()}">
		<header class="head">
			<div>
				<h2 class="title">{section.title}</h2>
				<p class="blurb">{section.blurb}</p>
			</div>
			<p class="inherit-note">{inheritNote}</p>
		</header>

		<div class="columns">
			<span>Setting</span>
			<span>Value</span>
			<span>Where it comes from</span>
		</div>

		{#each section.props as prop (prop)}
			{@const spec = generalFields[prop]}
			{@const id = `${entry.name}-${prop}`}
			<OverrideRow
				{id}
				label={spec.label}
				help={spec.help}
				declared={declares(prop)}
				{inheritLabel}
				entryName={editing}
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

<section class="section" id="section-photo-selection">
	<header class="head">
		<div>
			<h2 class="title">Photo selection</h2>
			<p class="blurb">
				Which of those accounts this configuration shows photos from, and what it shows from each.
			</p>
		</div>
		<p class="inherit-note">{inheritNote}</p>
	</header>

	<!-- The last of the containment wrappers, and it goes with task 005. `.modernist` redefines
	     `--color-neutral-100` through `-900`, which is what Tailwind v4 resolves `text-neutral-*` and
	     `border-neutral-*` through, so this body on the light ground would not merely be recoloured,
	     it would be inverted to around 1.3:1. The section's header above it is converted and sits
	     outside the wrapper, so the rail still finds a section it can read. -->
	<div class="bg-neutral-950 p-4 text-neutral-100">
		<div class="mb-2 flex flex-wrap items-center justify-between gap-2">
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
				Inherited from the default configuration. Override to choose which accounts this profile
				shows and what it shows from each.
			</p>
			<ul class="space-y-1 text-sm text-neutral-300">
				{#each inheritedAccounts as row (row.account.key)}
					<li class="rounded border border-neutral-800 px-2 py-1">
						{accountName(row.account, row.index)}
					</li>
				{/each}
			</ul>
		{/if}
	</div>
</section>

<style>
	/*
	 * The track list is declared here rather than in `override-row.svelte` so that the column
	 * headings and the rows they head read one declaration between them, and so that the breakpoint
	 * below governs both at once. 24px between sections is what the rail's scroll-spy reasons
	 * against when it argues for a one-pixel detection band.
	 */
	.section {
		--settings-grid: minmax(190px, 250px) minmax(0, 1fr) 210px;

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

	/* 26px against the sheet's 32px: five of these head one page and none of them is its title. */
	.title {
		margin: 0;
		font-size: 26px;
	}

	.blurb {
		margin: 2px 0 0;
		font-size: 13px;
		color: var(--color-neutral-700);
	}

	.inherit-note {
		margin: 0;
		font-size: 11.5px;
		text-align: right;
		color: var(--color-neutral-700);
	}

	/*
	 * The mock's neutral-600 reads 3.85:1 on the page ground, under AA for a 10px label - the same
	 * figure and the same answer as the profile strip's caption, which took neutral-700 for it.
	 * That is 5.83:1 and is the tone every other played down caption on this page already wears.
	 */
	.columns {
		display: grid;
		grid-template-columns: var(--settings-grid);
		gap: 20px;
		padding: var(--space-3) 0 var(--space-1);
		font-size: 10px;
		letter-spacing: 0.13em;
		text-transform: uppercase;
		color: var(--color-neutral-700);
		border-bottom: 1px solid var(--color-neutral-300);
	}

	/*
	 * Below 900px a row is one column, where `config-editor.svelte` drops the rail out of a column
	 * of its own. The headings go with it: three column names over a single column head nothing.
	 */
	@media (max-width: 900px) {
		.section {
			--settings-grid: minmax(0, 1fr);
		}

		.columns {
			display: none;
		}
	}
</style>
