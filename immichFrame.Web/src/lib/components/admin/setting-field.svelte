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

	const control =
		'w-full rounded bg-neutral-900 border border-neutral-700 px-2 py-1 text-sm ' +
		'text-neutral-100 disabled:opacity-60 disabled:cursor-not-allowed';

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
		class="h-4 w-4 accent-sky-500 disabled:opacity-60"
		checked={value === true}
		{disabled}
		onchange={(event) => onChange(event.currentTarget.checked)}
	/>
{:else if spec.kind === 'select'}
	<select
		{id}
		class={control}
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
		class={control}
		value={number}
		{disabled}
		onchange={(event) => onChange(toNumber(event.currentTarget.value))}
	/>
{:else if spec.kind === 'date'}
	<input
		{id}
		type="date"
		class={control}
		value={date}
		{disabled}
		onchange={(event) =>
			onChange(event.currentTarget.value ? `${event.currentTarget.value}T00:00:00` : null)}
	/>
{:else if spec.kind === 'lines'}
	<textarea
		{id}
		rows="3"
		class="{control} font-mono"
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
		class={control}
		placeholder={spec.placeholder ?? ''}
		value={text}
		{disabled}
		oninput={(event) => onChange(event.currentTarget.value)}
	/>
{/if}
