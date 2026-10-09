<script lang="ts">
	import {
		mdiChevronRight,
		mdiPlay,
		mdiPause,
		mdiChevronLeft,
		mdiInformationOutline
	} from '@mdi/js';
	import Icon from './icon.svelte';
	import { ProgressBarStatus } from './progress-bar.types';
	import { matchShortcut, type Shortcut } from './overlay-controls';

	interface Props {
		status: ProgressBarStatus;
		overlayVisible: boolean;
		infoVisible: boolean;
		next: () => void;
		back: () => void;
		pause: () => void;
		showInfo: () => void;
	}

	let {
		status = $bindable(),
		overlayVisible,
		infoVisible = $bindable(),
		next,
		back,
		pause,
		showInfo
	}: Props = $props();

	function shortcuts(_node: Window, actions: Record<Shortcut, () => void>) {
		function handleKeyDown(event: KeyboardEvent) {
			const match = matchShortcut(event);
			if (!match) return;

			// Held back from the browser even when it is not acted on - see matchShortcut.
			event.preventDefault();
			if (match.act) actions[match.shortcut]();
		}

		window.addEventListener('keydown', handleKeyDown);

		return {
			destroy() {
				window.removeEventListener('keydown', handleKeyDown);
			}
		};
	}

	const shortcutActions = $derived({ next, back, pause, info: showInfo });

	const iconClass = 'max-h-[min(10rem,33vh)] max-w-[min(10rem,33vh)] h-[33vh] w-[33vw]';
</script>

<svelte:window use:shortcuts={shortcutActions} />

{#if overlayVisible}
	<div class="inset-0 z-100 grid grid-cols-3 gap-2 {infoVisible ? 'hidden' : ''}">
		<div id="overlayback" class="group grid place-items-center">
			<button class="opacity-0 group-hover:opacity-100 text-frame-primary" onclick={back}
				><Icon title="Back" class={iconClass} path={mdiChevronLeft} size="" /></button
			>
		</div>

		<div class="grid grid-rows-3 gap-2">
			<div id="overlayInfo" class="group grid place-items-center">
				<button class="opacity-0 hover:opacity-100 text-frame-primary" onclick={showInfo}
					><Icon title="Info" class={iconClass} path={mdiInformationOutline} size="" /></button
				>
			</div>

			<div id="overlaypause" class="group grid place-items-center">
				<button onclick={pause} class="opacity-0 group-hover:opacity-100 text-frame-primary">
					<Icon
						class={iconClass}
						title={status == ProgressBarStatus.Paused ? 'Play' : 'Pause'}
						path={status == ProgressBarStatus.Paused ? mdiPlay : mdiPause}
						size=""
					/>
				</button>
			</div>

			<div class="group grid place-items-center">
				<!-- <button class="opacity-0 hover:opacity-100 text-frame-primary"> </button> -->
			</div>
		</div>

		<div id="overlaynext" class="group grid place-items-center">
			<button class="opacity-0 group-hover:opacity-100 text-frame-primary" onclick={next}
				><Icon title="Next" class={iconClass} path={mdiChevronRight} size="" />
			</button>
		</div>
	</div>
{/if}
