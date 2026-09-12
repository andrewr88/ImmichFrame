<script lang="ts">
	import {
		accountFields,
		accountProps,
		type AccountProp,
		type AccountSelection,
		type EditableAccount,
		type FieldValue
	} from './admin-config';
	import { pickedValues, pickerSource, type PickerKind } from './immich-picker';
	import ImmichPicker from './immich-picker.svelte';
	import SettingField from './setting-field.svelte';

	interface Props {
		/** Namespace for this panel's controls: one configuration's view of one account. */
		id: string;
		/**
		 * The account these photos come from. Nothing credential is edited here - the server URL, the
		 * label, the key and the key file belong to the account and are edited once, in the accounts
		 * section - but the account is what the Immich pickers browse with.
		 */
		account: EditableAccount;
		/** What this configuration shows from that account, which is all this panel edits. */
		selection: AccountSelection;
		/**
		 * The loaded configuration's version token. The picker asks about the accounts on disk, which
		 * is what an account handle resolves against, so this stays the loaded one while there are
		 * unsaved edits rather than tracking them.
		 */
		version: string;
		/** Which configuration is doing the showing, said in words rather than left to the tab strip. */
		title: string;
	}

	let { id, account, selection, version, title }: Props = $props();

	/** The four fields that hold Immich identifiers, and which list each is chosen from. */
	const pickers: Partial<Record<AccountProp, PickerKind>> = {
		albums: 'albums',
		excludedAlbums: 'albums',
		people: 'people',
		tags: 'tags'
	};

	// Named by the account rather than by the configuration looking at it: an account is the same
	// account in every entry that uses it, so they all browse it with one set of credentials.
	let source = $derived(pickerSource(account, version));

	function setValue(prop: AccountProp, value: FieldValue) {
		Object.assign(selection.values, { [prop]: value });
	}
</script>

<h5 class="mb-1 text-xs uppercase tracking-wide text-neutral-500">{title}</h5>

<div class="grid grid-cols-1 gap-x-6 sm:grid-cols-2">
	{#each accountProps as prop (prop)}
		{@const kind = pickers[prop]}
		<div class="flex items-start gap-3 border-t border-neutral-800 py-2">
			<div class="w-44 shrink-0">
				{#if kind}
					<!-- A picker has no single control to label: its own group is named by this, since
					     a `for` pointing at the button that opens it would name the button instead. -->
					<span id="{id}-{prop}-label" class="text-sm text-neutral-300">
						{accountFields[prop].label}
					</span>
				{:else}
					<label class="text-sm text-neutral-300" for="{id}-{prop}">
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
						id="{id}-{prop}"
						{kind}
						{source}
						values={pickedValues(selection.values[prop])}
						onChange={(values) => setValue(prop, values)}
					/>
				{:else}
					<SettingField
						id="{id}-{prop}"
						spec={accountFields[prop]}
						value={selection.values[prop]}
						onChange={(value) => setValue(prop, value)}
					/>
				{/if}
			</div>
		</div>
	{/each}
</div>
