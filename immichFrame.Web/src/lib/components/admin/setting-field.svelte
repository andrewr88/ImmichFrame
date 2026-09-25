<script lang="ts">
	import type { FieldSpec, FieldValue } from './admin-config';

	interface Props {
		id: string;
		spec: FieldSpec;
		value: FieldValue;
		disabled?: boolean;
		onChange: (value: FieldValue) => void;
	}

	let { id, spec, value, disabled = false, onChange }: Props = $props();

	let text = $derived(typeof value === 'string' ? value : '');
	let lines = $derived(Array.isArray(value) ? value.join('\n') : '');
	// The API models these as DateTime, which System.Text.Json only reads with a time on it, so
	// a date input's yyyy-MM-dd is widened on the way out and trimmed on the way in.
	//
	// (Lists commit on change rather than on input for a related reason: normalising per keystroke
	// rewrites the textarea's value and sends the caret to the end mid-line.)
	let date = $derived(typeof value === 'string' ? value.slice(0, 10) : '');
	let number = $derived(typeof value === 'number' ? String(value) : '');

	function toNumber(raw: string): FieldValue {
		if (raw.trim() === '') return null;
		const parsed = Number(raw);
		return Number.isFinite(parsed) ? parsed : null;
	}
</script>

{#if spec.kind === 'boolean'}
	<!-- `role="switch"` says what the control is drawn as; the checked state stays the element's
	     own, which is why there is no `aria-checked` beside it to disagree with it. -->
	<input
		{id}
		type="checkbox"
		role="switch"
		class="switch"
		checked={value === true}
		{disabled}
		onchange={(event) => onChange(event.currentTarget.checked)}
	/>
{:else if spec.kind === 'select'}
	<select
		{id}
		class="input select"
		value={text}
		{disabled}
		onchange={(event) => onChange(event.currentTarget.value)}
	>
		{#each spec.options ?? [] as option (option)}
			<option value={option}>{option}</option>
		{/each}
	</select>
{:else if spec.kind === 'integer' || spec.kind === 'decimal'}
	<input
		{id}
		type="number"
		step={spec.kind === 'decimal' ? 'any' : '1'}
		class="input number"
		value={number}
		{disabled}
		onchange={(event) => onChange(toNumber(event.currentTarget.value))}
	/>
{:else if spec.kind === 'date'}
	<input
		{id}
		type="date"
		class="input date"
		value={date}
		{disabled}
		onchange={(event) =>
			onChange(event.currentTarget.value ? `${event.currentTarget.value}T00:00:00` : null)}
	/>
{:else if spec.kind === 'lines'}
	<textarea
		{id}
		rows="3"
		class="input lines"
		value={lines}
		{disabled}
		onchange={(event) =>
			onChange(
				event.currentTarget.value
					.split('\n')
					.map((line) => line.trim())
					.filter((line) => line !== '')
			)}
	></textarea>
{:else}
	<input
		{id}
		type="text"
		class="input text"
		placeholder={spec.placeholder ?? ''}
		value={text}
		{disabled}
		oninput={(event) => onChange(event.currentTarget.value)}
	/>
{/if}

<style>
	/*
	 * Each kind is capped at the width the design gives it. `.input` is full width, and the value
	 * column is as wide as the window: a two letter language code in a box that crosses the screen
	 * reads as a box that expects a paragraph.
	 *
	 * Compounded with `.input` rather than written alone, as the chrome above does: `.admin-theme
	 * .input` and `.admin-theme textarea.input` set several of these properties themselves, so a
	 * bare `.lines` would win or lose on whichever stylesheet the bundler happened to emit second.
	 */
	.input.select {
		max-width: 340px;
	}

	.input.text,
	.input.number,
	.input.date {
		max-width: 380px;
	}

	.input.lines {
		max-width: 420px;
		font-family: 'Overpass Mono', ui-monospace, monospace;
		font-size: 13px;
		/* The three rows the design asks for, restated as a height: `.admin-theme textarea.input`'s
		   90px floor beats `rows="3"` and renders every list deeper than it is drawn. Three line boxes
		   at the sheet's 1.55 on this 13px, plus `.input`'s own 10px padding and 1px border on each
		   edge. The theme's 90px is right for a textarea with no row count of its own and stays as it
		   is; this is the only one here that has one. */
		min-height: calc(3 * 1.55 * 13px + 2 * 10px + 2 * 1px);
	}

	/*
	 * Tailwind's preflight paints a placeholder at half of `currentColor`, which composites to
	 * 3.29:1 on the input's own fill and 3.38:1 on the white a focused box turns - clear of the 3:1
	 * floor but short of AA, and these placeholders are worked examples of a format rather than
	 * decoration. Muted is 6.80:1 on the fill and 7.56:1 focused, and still reads as a hint beside
	 * the value's 15.97:1: the tone the profile strip's name box took for the same reason.
	 *
	 * That AA is an enabled field's, and the claim goes no further. The `opacity: 0.7` on a disabled
	 * `.input` fades the fill and the hint written on it together, so one of the nine `text`
	 * placeholders (`admin-config.ts:86-108`) on an inherited row reads 3.35:1 against the faded
	 * fill it sits on and 3.54:1 against the card behind that - short of AA again, in the state most
	 * rows on this page are in. Deliberate, not missed: text in an inactive control is incidental
	 * under 1.4.3, and a tone chosen to clear AA through the fade would land where the faded value
	 * lands (6.17:1), so the hint would read as a value. The trade is the hint's contrast for the
	 * two staying told apart.
	 */
	.input::placeholder {
		color: var(--color-text-muted);
	}

	/*
	 * A real checkbox drawn as the design's switch, not a button wearing `aria-pressed`: the label
	 * association, the space bar and the announced checked state all come with the element and none
	 * of them come with the button. `appearance: none` takes the platform's box off, the element
	 * itself is the track and `::before` the knob.
	 *
	 * The track is neutral-300 off and the primary on, as the design draws them: 4.75:1 apart, and
	 * the knob moves between them, so the state reads without the colour. Off, the track is edged
	 * in the faint tone, 4.72:1 against the card these sit on: its fill alone is 1.44:1 there, and
	 * the edge is what gives an unchecked switch the 3:1 boundary WCAG 1.4.11 asks of a control. On,
	 * the edge takes the primary and the track reads as one solid pill, 6.83:1.
	 *
	 * The knob sits 2px inside the 1px edge to land 3px from the outside, as the design places it.
	 * Forced-colors mode is told below what to paint the edge with.
	 */
	.switch {
		appearance: none;
		position: relative;
		flex: none;
		width: 42px;
		height: 24px;
		margin: 0;
		background: var(--color-neutral-300);
		border: 1px solid var(--color-text-faint);
		border-radius: var(--radius-pill);
		cursor: pointer;
		transition:
			background-color 0.15s,
			border-color 0.15s;
	}

	/* White on the primary track is 7.00:1; the shadow is what lifts it off the pale one. */
	.switch::before {
		content: '';
		position: absolute;
		top: 2px;
		left: 2px;
		width: 18px;
		height: 18px;
		background: var(--color-bg);
		border-radius: var(--radius-pill);
		box-shadow: 0 1px 3px rgba(0, 0, 0, 0.25);
		transition: transform 0.15s;
	}

	.switch:checked {
		background: var(--color-accent);
		border-color: var(--color-accent);
	}

	.switch:checked::before {
		transform: translateX(18px);
	}

	/*
	 * Faded as a disabled `.input` is in `admin-theme.css`, to 0.7 rather than a button's 0.45, and
	 * for the same reason: `disabled` is how a row says *inherited*, and an inherited value is the
	 * one a reader is most often here to read. Faded, an inherited "on" track is 3.45:1 against the
	 * card with its knob 3.50:1 on it, and an inherited "off" keeps its edge at 2.71:1. WCAG exempts
	 * an inactive control from 1.4.11; the two still sit 2.69:1 apart, with the knob at either end.
	 */
	.switch:disabled {
		opacity: 0.7;
		cursor: not-allowed;
	}

	@media (prefers-reduced-motion: reduce) {
		.switch,
		.switch::before {
			transition: none;
		}
	}

	/*
	 * Forced-colors mode rewrites colours into the user's own palette, and that is not enough here:
	 * with `appearance: none` there is no platform switch left to recolour, the fills are forced to
	 * `Canvas` and the knob's shadow is dropped, so both states would be the same empty outline. Held
	 * out of the forced palette instead and painted in its system colours: an outlined track with a
	 * `CanvasText` knob when off, the selected-item pair when on - the pair the rail and the profile
	 * tabs use for their own selected state - and `GrayText` when inactive, at full opacity, since
	 * the palette is what says inactive there. Opting out leaves the ring to colour as well.
	 */
	@media (forced-colors: active) {
		.switch {
			forced-color-adjust: none;
			background: Canvas;
			border-color: CanvasText;
		}

		.switch::before {
			background: CanvasText;
			box-shadow: none;
		}

		.switch:checked {
			background: SelectedItem;
			border-color: SelectedItem;
		}

		.switch:checked::before {
			background: SelectedItemText;
		}

		.switch:disabled {
			opacity: 1;
			border-color: GrayText;
		}

		.switch:disabled::before {
			background: GrayText;
		}

		.switch:checked:disabled {
			background: GrayText;
			border-color: GrayText;
		}

		.switch:checked:disabled::before {
			background: Canvas;
		}

		.switch:focus-visible {
			outline-color: CanvasText;
		}
	}
</style>
