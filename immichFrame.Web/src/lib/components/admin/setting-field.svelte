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
	<input
		{id}
		type="checkbox"
		class="check"
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
	 * Compounded with `.input` rather than written alone, as the chrome above does: `.modernist
	 * .input` and `.modernist textarea.input` set several of these properties themselves, so a bare
	 * `.lines` would win or lose on whichever stylesheet the bundler happened to emit second.
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
		/* The three rows the design asks for, restated as a height: `.modernist textarea.input`'s 90px
		   floor beats `rows="3"` and renders every list deeper than it is drawn. Three line boxes at
		   the sheet's 1.55 on this 13px, plus `.input`'s own 6px padding and 1px border on each edge.
		   The system's 90px is right for a textarea with no row count of its own and stays as it is;
		   this is the only one here that has one. */
		min-height: calc(3 * 1.55 * 13px + 2 * 6px + 2 * 1px);
	}

	/*
	 * Tailwind's preflight paints a placeholder at half of `currentColor`, which composites to
	 * 3.10:1 on the input's own surface - clear of the 3:1 floor but short of AA, and these
	 * placeholders are worked examples of a format rather than decoration.
	 *
	 * That AA is an enabled field's, and the claim goes no further. The `opacity: 0.7` below fades
	 * the fill and the hint written on it together, so one of the nine `text` placeholders
	 * (`admin-config.ts:86-108`) on an inherited row reads 2.91:1 against the fill it sits on and
	 * 3.08:1 against the page behind that - short of AA again, in the state most rows on this page
	 * are in. Deliberate, not missed: text in an inactive control is incidental under 1.4.3, and a
	 * tone chosen to clear AA through the fade would land where the faded value lands, so the hint
	 * would read as a value. The trade is the hint's contrast for the two staying told apart.
	 */
	.input::placeholder {
		color: var(--color-neutral-700);
	}

	/*
	 * A real checkbox with the platform's box taken off, not a button wearing `aria-pressed`: the
	 * label association, the space bar and the announced checked state all come with the element and
	 * none of them come with the button. The mark is clipped out of a solid block rather than typed,
	 * so it takes a token like everything else here.
	 *
	 * The rule around it is neutral-900 rather than the `--color-divider` the text boxes beside it
	 * wear: divider reads 2.41:1 on this ground, under 1.4.11, and where a text box is also found by
	 * its fill and its width an empty check box is that line and nothing else. 12.6:1, and it stays
	 * 3.27:1 clear of the neutral-600 an inherited box wears below, so the two do not read alike.
	 */
	.check {
		appearance: none;
		display: grid;
		place-content: center;
		width: 24px;
		height: 24px;
		margin: 0;
		background: var(--color-surface);
		border: 1px solid var(--color-neutral-900);
		border-radius: var(--radius-sm);
		cursor: pointer;
	}

	.check:checked {
		background: var(--color-accent);
		border-color: var(--color-accent);
	}

	.check:checked::after {
		content: '';
		width: 12px;
		height: 12px;
		background: var(--color-bg);
		clip-path: polygon(14% 44%, 0 65%, 50% 100%, 100% 16%, 80% 0%, 43% 62%);
	}

	/*
	 * `disabled` is how a row says *inherited*, which is the state most rows on this page are in, so
	 * the box has to stay readable rather than stand down. The system's own disabled dress - 0.45
	 * opacity, stated once on `.btn` - faded fill and rule together and left an inherited value at
	 * 1.04:1 against the page: unreadable, and unreadable for the value a reader is most often here
	 * to read. Tones instead, at full opacity. Neutral-600 is 3.85:1 on the ground, the same tone as
	 * a fill puts inherited-true 3.55:1 from inherited-false, and against it the tick's `--color-bg`
	 * is 3.85:1 - while staying plainly inactive beside the resting box's near-black rule.
	 */
	.check:disabled {
		border-color: var(--color-neutral-600);
		cursor: not-allowed;
	}

	.check:checked:disabled {
		background: var(--color-neutral-600);
		border-color: var(--color-neutral-600);
	}

	/*
	 * Same reading of `disabled` for the value itself, and the same reason not to take the system's
	 * 0.45: at that figure an inherited value reads 2.66:1 against the fill it sits on, 2.75:1
	 * against the page behind that. 0.7 is 5.49:1 and 5.81:1, clear of AA on either reading, and a
	 * fade is what the value wants where the box wanted tones: the border, the placeholder and the
	 * select's own arrow go down with the text rather than each needing a tone of its own.
	 */
	.input:disabled {
		opacity: 0.7;
		cursor: not-allowed;
	}

	/*
	 * The one escape hatch on this sheet, and the only rule that needs one: forced-colors mode
	 * rewrites `color`, `background-color` and `border-color` into the user's own palette, which is
	 * why every other rule here comes through it working. `appearance: none` is the declaration it
	 * cannot rewrite - it removes the rendering that rewriting relies on, and with `--color-accent`
	 * and `--color-bg` both forced to `Canvas` a checked box would be an empty one. Hand the box
	 * back to the platform, which draws a tick that mode understands and greys it when it is
	 * inactive; drop the clipped mark so it cannot be painted over the platform's, and the fade with
	 * it, since `GrayText` is already saying what the fade was for and `opacity` is not forced.
	 */
	@media (forced-colors: active) {
		.check {
			appearance: auto;
		}

		.check:checked::after {
			content: none;
		}

		.input:disabled {
			opacity: 1;
		}
	}
</style>
