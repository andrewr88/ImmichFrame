<script lang="ts">
	import {
		accountFields,
		accountProps,
		usesApiKeyFile,
		type AccountProp,
		type EditableAccount,
		type FieldValue
	} from './admin-config';
	import SettingField from './setting-field.svelte';

	interface Props {
		account: EditableAccount;
		index: number;
		onRemove: () => void;
	}

	let { account, index, onRemove }: Props = $props();

	let fromFile = $derived(usesApiKeyFile(account));
	let prefix = $derived(`account-${index}`);

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
			<div class="flex items-start gap-3 border-t border-neutral-800 py-2">
				<div class="w-44 shrink-0">
					<label class="text-sm text-neutral-300" for="{prefix}-{prop}">
						{accountFields[prop].label}
					</label>
					{#if accountFields[prop].help}
						<p class="text-xs text-neutral-500">{accountFields[prop].help}</p>
					{/if}
				</div>
				<div class="min-w-0 flex-1">
					<SettingField
						id="{prefix}-{prop}"
						spec={accountFields[prop]}
						value={account.values[prop]}
						onChange={(value) => setValue(prop, value)}
					/>
				</div>
			</div>
		{/each}
	</div>
</div>
