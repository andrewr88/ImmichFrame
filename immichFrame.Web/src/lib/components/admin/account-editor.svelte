<script lang="ts">
	import {
		accountFields,
		accountProps,
		usesApiKeyFile,
		type AccountProp,
		type EditableAccount,
		type FieldValue
	} from './admin-config';
	import { pickedValues, pickerSource, type PickerKind } from './immich-picker';
	import ImmichPicker from './immich-picker.svelte';
	import SettingField from './setting-field.svelte';

	interface Props {
		account: EditableAccount;
		index: number;
		/** The configuration this account belongs to, as the picker proxy names one. */
		profile: string;
		/**
		 * The loaded configuration's version token. The picker asks about the accounts on disk, which
		 * is what an account handle resolves against, so this stays the loaded one while there are
		 * unsaved edits rather than tracking them.
		 */
		version: string;
		onRemove: () => void;
	}

	let { account, index, profile, version, onRemove }: Props = $props();

	/** The four fields that hold Immich identifiers, and which list each is chosen from. */
	const pickers: Partial<Record<AccountProp, PickerKind>> = {
		albums: 'albums',
		excludedAlbums: 'albums',
		people: 'people',
		tags: 'tags'
	};

	let fromFile = $derived(usesApiKeyFile(account));
	let prefix = $derived(`account-${index}`);
	let source = $derived(pickerSource(account, profile, version));

	const control =
		'w-full rounded bg-neutral-900 border border-neutral-700 px-2 py-1 text-sm text-neutral-100';
	const button =
		'rounded border border-neutral-600 px-2 py-0.5 text-xs text-neutral-200 hover:border-neutral-400';

	function setValue(prop: AccountProp, value: FieldValue) {
		Object.assign(account.values, { [prop]: value });
	}
</script>

<div class="rounded border border-neutral-700 p-3">
	<div class="mb-3 flex items-center justify-between gap-2">
		<h4 class="text-sm font-semibold text-neutral-200">Account {index + 1}</h4>
		<button type="button" class={button} onclick={onRemove}>Remove account</button>
	</div>

	<div class="mb-3 grid grid-cols-1 gap-3 sm:grid-cols-2">
		<div>
			<label class="text-sm text-neutral-200" for="{prefix}-url">Immich server URL</label>
			<input
				id="{prefix}-url"
				type="text"
				class={control}
				placeholder="http://immich.example.com:2283"
				value={account.values.immichServerUrl ?? ''}
				oninput={(event) => (account.values.immichServerUrl = event.currentTarget.value)}
			/>
		</div>

		<div>
			<label class="text-sm text-neutral-200" for="{prefix}-key-file">API key file</label>
			<input
				id="{prefix}-key-file"
				type="text"
				class={control}
				placeholder="Leave blank to store the key in the settings file"
				value={account.values.apiKeyFile ?? ''}
				oninput={(event) => (account.values.apiKeyFile = event.currentTarget.value)}
			/>
		</div>
	</div>

	<div class="mb-3">
		<span class="text-sm text-neutral-200">API key</span>
		{#if fromFile}
			<p class="text-sm text-sky-300">
				Read from the file above at start-up. ImmichFrame refuses a configuration that names both a
				key file and a key, so there is nothing to type here; clear the path to type a key instead.
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
					id="{prefix}-key"
					type="password"
					autocomplete="new-password"
					class="min-w-0 flex-1 rounded border border-neutral-700 bg-neutral-900 px-2 py-1 text-sm
						text-neutral-100"
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
					This configuration has no stored key for this account, so one has to be entered before it
					can be saved.
				</p>
			{/if}
		{/if}
	</div>

	<div class="grid grid-cols-1 gap-x-6 sm:grid-cols-2">
		{#each accountProps as prop (prop)}
			{@const kind = pickers[prop]}
			<div class="flex items-start gap-3 border-t border-neutral-800 py-2">
				<div class="w-44 shrink-0">
					{#if kind}
						<!-- A picker has no single control to label: its own group is named by this, since
						     a `for` pointing at the button that opens it would name the button instead. -->
						<span id="{prefix}-{prop}-label" class="text-sm text-neutral-300">
							{accountFields[prop].label}
						</span>
					{:else}
						<label class="text-sm text-neutral-300" for="{prefix}-{prop}">
							{accountFields[prop].label}
						</label>
					{/if}
					{#if accountFields[prop].help}
						<p class="text-xs text-neutral-500">{accountFields[prop].help}</p>
					{/if}
				</div>
				<div class="min-w-0 flex-1">
					{#if kind}
						<ImmichPicker
							id="{prefix}-{prop}"
							{kind}
							{source}
							values={pickedValues(account.values[prop])}
							onChange={(values) => setValue(prop, values)}
						/>
					{:else}
						<SettingField
							id="{prefix}-{prop}"
							spec={accountFields[prop]}
							value={account.values[prop]}
							onChange={(value) => setValue(prop, value)}
						/>
					{/if}
				</div>
			</div>
		{/each}
	</div>
</div>
