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

	/**
	 * The three groups the panel shows, each read off what a field *is* rather than off where it
	 * sits - and where it sits would not do: `accountProps` is the declaration order in
	 * `admin-config.ts`, and that order does not group them. `rating` is declared after the four
	 * picked fields, so three positional slices would file it with the pickers instead of with the
	 * other values. Deriving keeps that true of a field added or moved there later too: it lands in
	 * the group its kind and the map above put it in rather than in whichever one it fell between.
	 *
	 * Between them they are exhaustive by construction - every prop is a boolean, a picked field or
	 * neither - so nothing can be dropped from the panel by being forgotten here.
	 */
	const includes = accountProps.filter((prop) => accountFields[prop].kind === 'boolean');
	const picked = accountProps
		.map((prop) => ({ prop, kind: pickers[prop] }))
		.filter((field): field is { prop: AccountProp; kind: PickerKind } => field.kind !== undefined);
	const values = accountProps.filter(
		(prop) => accountFields[prop].kind !== 'boolean' && pickers[prop] === undefined
	);

	// Named by the account rather than by the configuration looking at it: an account is the same
	// account in every entry that uses it, so they all browse it with one set of credentials.
	let source = $derived(pickerSource(account, version));

	function setValue(prop: AccountProp, value: FieldValue) {
		Object.assign(selection.values, { [prop]: value });
	}
</script>

<h5 class="kicker">{title}</h5>

<!-- A switch and the name of what it turns on. None of the four booleans carries help text in the
     model, so today that is all this renders - but it renders it the way the two groups below do,
     stacked under the label, because a `help` added to one of them in `admin-config.ts` would
     otherwise be dropped here without a word. -->
<div class="group includes">
	{#each includes as prop (prop)}
		<div class="include">
			<SettingField
				id="{id}-{prop}"
				spec={accountFields[prop]}
				value={selection.values[prop]}
				onChange={(value) => setValue(prop, value)}
			/>
			<div>
				<label class="include-label" for="{id}-{prop}">{accountFields[prop].label}</label>
				{#if accountFields[prop].help}
					<p class="help">{accountFields[prop].help}</p>
				{/if}
			</div>
		</div>
	{/each}
</div>

<div class="group values">
	{#each values as prop (prop)}
		<div>
			<label class="value-label" for="{id}-{prop}">{accountFields[prop].label}</label>
			<SettingField
				id="{id}-{prop}"
				spec={accountFields[prop]}
				value={selection.values[prop]}
				onChange={(value) => setValue(prop, value)}
			/>
			{#if accountFields[prop].help}
				<p class="help">{accountFields[prop].help}</p>
			{/if}
		</div>
	{/each}
</div>

<div class="group pickers">
	{#each picked as { prop, kind } (prop)}
		<div class="picked">
			<div>
				<!-- A picker has no single control to label: its own group is named by this, since
				     a `for` pointing at the button that opens it would name the button instead. -->
				<span id="{id}-{prop}-label" class="picked-label">
					{accountFields[prop].label}
				</span>
				{#if accountFields[prop].help}
					<p class="help">{accountFields[prop].help}</p>
				{/if}
			</div>
			<ImmichPicker
				id="{id}-{prop}"
				{kind}
				{source}
				values={pickedValues(selection.values[prop])}
				onChange={(values) => setValue(prop, values)}
			/>
		</div>
	{/each}
</div>

<style>
	/*
	 * The panel's body. The card it sits in - the border, the header strip above it and this body's
	 * own padding - belongs to `entry-editor.svelte`, which owns the panel; what is here is the
	 * twelve settings and the three groups they fall into.
	 *
	 * Everything below is coloured against the panel card's `--color-surface`: the body carries no
	 * fill of its own, so the card shows through it.
	 */

	/*
	 * The design's faint caption, as the settings table's column headings wear it: 4.72:1 on the
	 * card, and at its regular weight rather than the sheet's heading weight. Still the sheet's
	 * `h5` in every other respect - the heading of the panel's body.
	 */
	.kicker {
		margin: 0 0 var(--space-3);
		font-size: 10px;
		font-weight: 400;
		letter-spacing: 0.13em;
		text-transform: uppercase;
		color: var(--color-text-faint);
	}

	/* The include, value and picker groups are parted by a rule, as the design parts them. */
	.group + .group {
		margin-top: 18px;
		padding-top: 18px;
		border-top: 1px solid var(--color-divider);
	}

	/*
	 * The two grids are `auto-fit` rather than a fixed column count, so each drops a column as the
	 * panel narrows instead of at one breakpoint: these panels sit in a column that loses the rail
	 * at 900px and keeps narrowing after it.
	 */
	.includes {
		display: grid;
		grid-template-columns: repeat(auto-fit, minmax(230px, 1fr));
		gap: 10px 24px;
	}

	/* The design's toggle row: the switch, then its name in the body's own size and weight. */
	.include {
		display: flex;
		align-items: center;
		gap: var(--space-3);
		padding: var(--space-1) 0;
	}

	.include-label {
		font-size: 14px;
	}

	.values {
		display: grid;
		grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
		gap: 18px 24px;
	}

	.value-label {
		display: block;
		margin-bottom: 5px;
		font-size: 13px;
		font-weight: 600;
	}

	/*
	 * A row each rather than a cell each: a picker is as tall as the chips chosen in it, and four
	 * of them side by side would leave three columns sized by the fullest.
	 */
	.picked {
		display: grid;
		grid-template-columns: minmax(180px, 230px) minmax(0, 1fr);
		gap: 20px;
		align-items: start;
		padding: 18px 0;
	}

	.picked + .picked {
		border-top: 1px solid var(--color-divider);
	}

	/* The group's own padding already parts the first row from the rule above it, and the body's
	   from the edge below. */
	.picked:first-child {
		padding-top: 0;
	}

	.picked:last-child {
		padding-bottom: 0;
	}

	.picked-label {
		font-size: 14px;
		font-weight: 600;
	}

	/* Muted, 7.37:1 on the card. */
	.help {
		margin: 5px 0 0;
		font-size: 11.5px;
		line-height: 1.45;
		color: var(--color-text-muted);
	}

	.picked .help {
		margin-top: 3px;
	}
</style>
