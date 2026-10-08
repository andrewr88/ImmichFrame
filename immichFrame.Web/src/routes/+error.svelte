<script lang="ts">
	import { page } from '$app/state';
	import ErrorElement from '$lib/components/elements/error-element.svelte';
	import { reloadSchedule } from '$lib/load-config';

	// At the root, so it is what both / and /<profile> show when their load fails - and all a load
	// does here is fetch the configuration (loadConfig). The page then reloads by itself instead of
	// waiting for someone to; reloadSchedule has how soon. A full reload, as home-page.svelte does,
	// so a frame that waits out an upgrade comes back on the new build.
	const schedule = $derived(reloadSchedule(page.status));

	$effect(() => {
		const timer = window.setTimeout(() => window.location.reload(), schedule.ms);
		return () => window.clearTimeout(timer);
	});
</script>

<svelte:head>
	<title>immichFrame</title>
</svelte:head>
<section class="fixed grid h-dvh-safe w-screen bg-black">
	<ErrorElement
		message={page.error?.message}
		retryNote="The page will automatically refresh every {schedule.every}."
	/>
</section>
