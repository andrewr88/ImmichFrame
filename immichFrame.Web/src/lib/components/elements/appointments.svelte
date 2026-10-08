<script lang="ts">
	import * as api from '$lib/index';
	import { onMount } from 'svelte';
	import { configStore } from '$lib/stores/config.store';
	import { clientIdentifierStore } from '$lib/stores/persist.store';
	import { profileStore } from '$lib/stores/profile.store';
	import { formatDates, loadAppointments } from './appointments';

	api.init();

	let appointments: api.IAppointment[] = $state() as api.IAppointment[];

	onMount(() => {
		GetAppointments();
		const appointmentInterval = setInterval(() => GetAppointments(), 10 * 60 * 1000); //every 10 minutes

		return () => {
			clearInterval(appointmentInterval);
		};
	});

	async function GetAppointments() {
		const loaded = await loadAppointments($clientIdentifierStore, $profileStore);
		if (loaded) {
			appointments = loaded;
		}
	}
</script>

{#if appointments}
	<div
		id="appointments"
		class="fixed top-0 right-0 w-auto z-10 text-center text-frame-primary m-5 max-w-[20%] hidden lg:block md:min-w-[10%]"
	>
		<!-- <div class="text-4xl mx-8 font-bold">Appointments</div> -->
		<div class="">
			<!-- Keyed by position, because nothing else will do. An appointment has no id, and a key
			     built from its fields can repeat - the same event arrives once from each calendar it is
			     on - and a repeated key throws, in production as well. Keyed by the object, every
			     refresh would remount every row, since each fetch hands back new ones. -->
			{#each appointments as appointment, index (index)}
				<div class="bg-gray-600/90 mb-2 text-left rounded-md p-3">
					<p class="text-xs">
						{formatDates(appointment.startTime ?? '', appointment.endTime ?? '', $configStore)}
					</p>
					{appointment.summary}
					{#if appointment.description}
						<p class="text-xs font-light">{appointment.description}</p>
					{/if}
				</div>
			{/each}
		</div>
	</div>
{/if}
