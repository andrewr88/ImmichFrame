<script lang="ts">
	import type { Snippet } from 'svelte';

	interface Props {
		id: string;
		label: string;
		help?: string;
		/** Whether this configuration declares the setting rather than inheriting it. */
		declared: boolean;
		/** Where the value comes from while it is not declared. */
		inheritLabel: string;
		/** How the override's tooltip names the configuration being edited, from `entryLabel`. */
		entryName: string;
		onToggle: (declared: boolean) => void;
		children: Snippet;
	}

	let { id, label, help, declared, inheritLabel, entryName, onToggle, children }: Props = $props();
</script>

<div class="row">
	<div>
		<label class="setting" for={id}>{label}</label>
		{#if help}
			<p class="help">{help}</p>
		{/if}
	</div>

	<div class="value">
		{@render children()}
	</div>

	<!-- The mock draws this as a pressed button. It stays a `<label>` around a real checkbox: the
	     rendered result is the same, and a checkbox is what keeps the space bar, the focus ring and
	     an announced checked state rather than a pressed one. -->
	<label
		class="override"
		class:is-declared={declared}
		title={declared
			? 'This configuration writes this setting. Click to stop overriding it.'
			: `Click to override this setting in ${entryName}.`}
	>
		<!-- The label's own text is the same three words on every row, so on its own it leaves a
		     screen reader thirty identical "Overridden, checkbox" controls. The name says which
		     setting it belongs to, and keeps the visible word at the front of it so a voice user can
		     still ask for what they can see. `title` names nothing: a label with text of its own
		     never lets its `title` into the name. -->
		<input
			type="checkbox"
			class="box"
			aria-label="{declared ? 'Overridden' : inheritLabel}: {label}"
			checked={declared}
			onchange={(event) => onToggle(event.currentTarget.checked)}
		/>
		<span>{declared ? 'Overridden' : inheritLabel}</span>
	</label>
</div>

<style>
	/*
	 * The track list comes from the section rather than from here, so the column headings above the
	 * rows cannot drift away from the rows they head: `entry-editor.svelte` declares `--settings-grid`
	 * once, including what it collapses to on a narrow viewport, and both read that one declaration.
	 * The fallback is that same track list, so a row rendered somewhere that forgot to declare it
	 * degrades to the layout it was drawn for rather than to a single column.
	 */
	.row {
		display: grid;
		grid-template-columns: var(--settings-grid, minmax(190px, 250px) minmax(0, 1fr) 210px);
		gap: 20px;
		align-items: start;
		padding: 14px 0;
		border-bottom: 1px solid var(--color-neutral-300);
	}

	.setting {
		display: block;
		font-size: 14.5px;
		font-weight: 600;
	}

	.help {
		margin: 2px 0 0;
		font-size: 12px;
		color: var(--color-neutral-700);
	}

	/*
	 * The middle track is `minmax(0, 1fr)`, which lets the track shrink but not the item in it: a
	 * form control carries an intrinsic width, so without this a long `<select>` option would push
	 * its own column over the one beside it rather than being capped by its own `max-width`.
	 */
	.value {
		min-width: 0;
	}

	.override {
		display: flex;
		align-items: center;
		gap: var(--space-2);
		padding: 6px 8px;
		font-size: 12.5px;
		font-weight: 400;
		color: var(--color-neutral-700);
		border: 1px solid var(--color-neutral-300);
		cursor: pointer;
	}

	/* The accent says here what it says in the rail's badge: written into this configuration. */
	.override.is-declared {
		font-weight: 600;
		color: var(--color-accent-700);
		background: var(--color-accent-100);
		border-color: var(--color-accent);
	}

	.box {
		flex-shrink: 0;
		width: 16px;
		height: 16px;
		margin: 0;
		accent-color: var(--color-accent);
	}
</style>
