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
		onToggle: (declared: boolean) => void;
		children: Snippet;
	}

	let { id, label, help, declared, inheritLabel, onToggle, children }: Props = $props();
</script>

<div
	class="grid grid-cols-1 gap-2 border-b border-neutral-800 py-2 sm:grid-cols-[16rem_1fr_11rem] sm:items-start"
>
	<div>
		<label class="text-sm text-neutral-200" for={id}>{label}</label>
		{#if help}
			<p class="text-xs text-neutral-500">{help}</p>
		{/if}
	</div>

	<div class:opacity-60={!declared}>
		{@render children()}
	</div>

	<label class="flex items-center gap-2 text-xs text-neutral-400 sm:justify-end">
		<input
			type="checkbox"
			class="h-3.5 w-3.5 accent-sky-500"
			checked={declared}
			onchange={(event) => onToggle(event.currentTarget.checked)}
		/>
		<span>{declared ? 'Overridden' : inheritLabel}</span>
	</label>
</div>
