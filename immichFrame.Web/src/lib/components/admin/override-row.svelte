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
		border-bottom: 1px solid var(--color-divider);
	}

	/* A rule between rows and not under the last: the card's own edge closes the list. */
	.row:last-child {
		border-bottom: 0;
	}

	.setting {
		display: block;
		font-size: 14.5px;
		font-weight: 600;
		line-height: 1.3;
	}

	/* Muted, 7.37:1 on the card the rows sit in. */
	.help {
		margin: 3px 0 0;
		font-size: 12px;
		line-height: 1.4;
		color: var(--color-text-muted);
	}

	/*
	 * The middle track is `minmax(0, 1fr)`, which lets the track shrink but not the item in it: a
	 * form control carries an intrinsic width, so without this a long `<select>` option would push
	 * its own column over the one beside it rather than being capped by its own `max-width`.
	 */
	.value {
		min-width: 0;
	}

	/*
	 * The design's source pill. `justify-self` keeps it to the width of what it says rather than the
	 * width of its column. Inherited, it is muted on white, 7.56:1, and its divider edge (1.21:1 on
	 * the card) is decoration: the words are what identify it.
	 */
	.override {
		display: flex;
		justify-self: start;
		align-items: center;
		gap: var(--space-2);
		padding: 5px 12px 5px 6px;
		font-size: 12.5px;
		font-weight: 500;
		line-height: 1.2;
		color: var(--color-text-muted);
		background: var(--color-bg);
		border: 1px solid var(--color-divider);
		border-radius: var(--radius-pill);
		cursor: pointer;
	}

	/*
	 * The accent says here what it says in the rail's badge: written into this configuration. The
	 * primary on its tint, 6.17:1, and ruled in the primary.
	 */
	.override.is-declared {
		font-weight: 600;
		color: var(--color-accent-700);
		background: var(--color-accent-100);
		border-color: var(--color-accent);
	}

	/* A read-only configuration disables the box; the words stay readable, since where each value
	   comes from is still worth reading when nothing can be changed. */
	.override:has(.box:disabled) {
		cursor: not-allowed;
	}

	/*
	 * The design's round box, a real checkbox with the platform's rendering taken off. Empty, its
	 * ring is the faint tone, 4.83:1 on the pill's white: past the 3:1 WCAG 1.4.11 asks of a
	 * control's boundary. Declared, it fills with the primary, 6.17:1 on the tint, and the tick is
	 * white on that, 7.00:1. The tick is the design's own stroke, traced as a polygon so it takes a
	 * token like everything else here.
	 */
	.box {
		appearance: none;
		display: grid;
		place-content: center;
		flex: none;
		width: 16px;
		height: 16px;
		margin: 0;
		background: transparent;
		border: 1px solid var(--color-text-faint);
		border-radius: var(--radius-pill);
		cursor: pointer;
	}

	.box:checked {
		background: var(--color-accent);
		border-color: var(--color-accent);
	}

	.box:checked::after {
		content: '';
		width: 12px;
		height: 12px;
		background: var(--color-bg);
		clip-path: polygon(12% 55%, 37.5% 81%, 88% 30%, 78% 20%, 37.5% 61%, 21.5% 45%);
	}

	.box:disabled {
		opacity: 0.45;
		cursor: not-allowed;
	}

	/*
	 * The escape hatch that travels with `appearance: none`: forced-colors mode rewrites colour,
	 * background and border into the user's own palette, but it cannot rewrite that declaration,
	 * which removes the rendering the rewriting relies on - with the primary and the tick's white
	 * both forced to `Canvas`, a declared box would be an empty one. Hand the box back to the
	 * platform, which draws a tick that mode understands and greys it when inactive (so the fade
	 * goes, as the disabled `.input`'s does in `admin-theme.css`), and drop the traced mark so it
	 * cannot be painted over the platform's own.
	 */
	@media (forced-colors: active) {
		.box {
			appearance: auto;
		}

		.box:checked::after {
			content: none;
		}

		.box:disabled {
			opacity: 1;
		}
	}
</style>
