<script lang="ts">
	import Icon from '../icon.svelte';
	import { mdiTestTube } from '@mdi/js';

	interface Props {
		icon?: string;
		header: string;
		items?: string[];
	}
	let { icon = mdiTestTube, header, items = [] }: Props = $props();
</script>

<!-- A header or an item can be one run with nowhere to break - a long file name, a link in a
     description - and this box, sized to its content, grew as wide as the run, out past the edge of
     the screen. `break-words` breaks a run only where it overflows its box, and a box sized to its
     content is never narrower than the run; `wrap-anywhere` lets the box be narrower, and
     `min-w-0 max-w-full` hold it to the column it is in. -->
<div class="text-xl text-frame-primary text-left w-fit min-w-0 max-w-full flex flex-col">
	<span class="flex gap-2 items-center wrap-anywhere"
		><Icon path={icon} class="info-icon" />{header}</span
	>
	{#if items.length > 0}
		<span class="wrap-anywhere">{items.map((x) => x).join(', ')}</span>
	{/if}
</div>
