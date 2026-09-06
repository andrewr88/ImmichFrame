<script lang="ts">
	import type { AdminGeneralSettingsDto } from '$lib/immichFrameApi';
	import {
		ACCOUNTS_KEY,
		declaredKeyOf,
		generalFields,
		generalSections,
		newAccount,
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
		/** The configuration this one inherits from, or null for the default configuration itself. */
		inheritFrom: EditableEntry | null;
	}

	let { entry, inheritFrom }: Props = $props();

	// The default configuration has nothing above it, so its unset keys fall back to the setting's
	// built-in default rather than to another configuration.
	let inheritLabel = $derived(inheritFrom ? 'Inherited' : 'Built-in default');
	let accountsDeclared = $derived(entry.declared.includes(ACCOUNTS_KEY));
	let inheritedAccounts = $derived(inheritFrom?.accounts ?? []);

	const button =
		'rounded border border-neutral-600 px-2 py-1 text-xs text-neutral-200 hover:border-neutral-400';

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

		// A declared account list replaces the inherited one outright, and the API keys behind it
		// were never sent to the browser, so each account has to be given one here rather than
		// letting the save be refused for it.
		if (!entry.accountsWereDeclared) {
			entry.accounts = inheritedAccounts.map((account) => newAccount(account.values));
		}

		entry.declared = [...entry.declared, ACCOUNTS_KEY];
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
	<section class="mb-6">
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

<section class="mb-6">
	<div class="mb-2 flex flex-wrap items-center justify-between gap-2">
		<h3 class="text-sm font-semibold uppercase tracking-wide text-neutral-400">Immich accounts</h3>
		{#if entry.isDefault}
			<span class="text-xs text-neutral-500">
				The default configuration always declares its own accounts.
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
				This profile now declares its own accounts instead of inheriting them, so it cannot keep API
				keys it never had of its own. Enter the key for each account below before saving.
			</p>
		{/if}

		<div class="space-y-3">
			<!-- Keyed by the account object rather than its index: position is deliberately not an
			     identity anywhere in this feature, and reusing a DOM node across a delete would put
			     one account's half-typed API key on another. -->
			{#each entry.accounts as account, index (account)}
				<AccountEditor
					{account}
					{index}
					onRemove={() => (entry.accounts = entry.accounts.filter((_, other) => other !== index))}
				/>
			{/each}
		</div>

		<button
			type="button"
			class="{button} mt-3"
			onclick={() => (entry.accounts = [...entry.accounts, newAccount()])}
		>
			Add account
		</button>
	{:else}
		<p class="mb-2 text-xs text-neutral-500">
			Inherited from the default configuration. Override to give this profile its own accounts.
		</p>
		<ul class="space-y-1 text-sm text-neutral-300">
			{#each inheritedAccounts as account (account)}
				<li class="rounded border border-neutral-800 px-2 py-1">
					{account.values.immichServerUrl || '(no server URL)'}
				</li>
			{/each}
		</ul>
	{/if}
</section>
