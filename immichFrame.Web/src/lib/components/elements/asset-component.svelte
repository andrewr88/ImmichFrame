<script lang="ts">
	import { untrack } from 'svelte';
	import type { AssetResponseDto } from '$lib/immichFrameApi';
	import * as api from '$lib/index';
	import ErrorElement from './error-element.svelte';
	import Asset from './asset.svelte';
	import type AssetComponent from './asset.svelte';
	import LoadingElement from './LoadingElement.svelte';
	import { fade } from 'svelte/transition';
	import { configStore } from '$lib/stores/config.store';
	import { Confetti } from 'svelte-confetti';
	import { slideshowStore } from '$lib/stores/slideshow.store';

	api.init();

	interface Props {
		assets: [string, AssetResponseDto, api.AssetFaceResponseDto[], api.AlbumResponseDto[]][];
		interval?: number;
		error?: boolean;
		loaded?: boolean;
		split?: boolean;
		hasBday?: boolean;
		showLocation?: boolean;
		showPhotoDate?: boolean;
		showImageDesc?: boolean;
		showPeopleDesc?: boolean;
		showTagsDesc?: boolean;
		showAlbumName?: boolean;
		imageFill?: boolean;
		imageZoom?: boolean;
		imagePan?: boolean;
		showInfo: boolean;
		playAudio?: boolean;
		onVideoWaiting?: () => void;
		onVideoPlaying?: () => void;
		onAssetError?: () => void;
	}

	let {
		assets,
		interval = 20,
		error = false,
		loaded = false,
		split = false,
		hasBday = false,
		showLocation = true,
		showPhotoDate = true,
		showImageDesc = true,
		showPeopleDesc = true,
		showTagsDesc = true,
		showAlbumName = true,
		imageFill = false,
		imageZoom = false,
		imagePan = false,
		showInfo = $bindable(false),
		playAudio = false,
		onVideoWaiting = () => {},
		onVideoPlaying = () => {},
		onAssetError = () => {}
	}: Props = $props();
	let instantTransition = slideshowStore.instantTransition;
	let transitionDuration = $derived(
		$instantTransition ? 0 : ($configStore.transitionDuration ?? 1) * 1000
	);
	let transitionDelay = $derived($instantTransition ? 0 : transitionDuration / 2 + 25);

	let primaryAssetComponent = $state<AssetComponent | undefined>(undefined);
	let secondaryAssetComponent = $state<AssetComponent | undefined>(undefined);

	// What every Asset below is given alike; each has its own asset, binding and showInfo besides.
	// Spread ahead of those rather than after: Svelte looks a prop up from the last attribute back,
	// so spread after them, every read of `asset` would read this object too, and Asset's teardown
	// of its video would run again - unloading it - whenever any of these changed.
	const assetProps = $derived({
		interval,
		showLocation,
		showPhotoDate,
		showImageDesc,
		showPeopleDesc,
		showTagsDesc,
		showAlbumName,
		imageFill,
		imageZoom,
		imagePan,
		split,
		playAudio,
		onVideoWaiting,
		onVideoPlaying,
		onAssetError
	});

	// New assets mean the ones on screen start to fade out, and they are stood down here - see
	// Asset's retire(). Not from the keyed block's `outrostart`: Svelte fires that a frame after the
	// swap, once the fade is under way, and by then the bindings hold the incoming Assets, which
	// would be stood down instead. This runs ahead of the block, while they still hold the outgoing
	// ones - and only for a new list, as nothing else swaps the block, whatever else re-runs this.
	let shownAssets: Props['assets'] | undefined;
	$effect.pre(() => {
		if (assets === shownAssets) return;
		shownAssets = assets;
		untrack(() => {
			primaryAssetComponent?.retire();
			secondaryAssetComponent?.retire();
		});
	});

	export const pause = async () => {
		await primaryAssetComponent?.pause?.();
		await secondaryAssetComponent?.pause?.();
	};

	export const play = async () => {
		await primaryAssetComponent?.play?.();
		await secondaryAssetComponent?.play?.();
	};
</script>

{#if hasBday}
	<div
		class="z-1000 top-[-50px] fixed l-0 h-dvh-safe w-screen flex justify-center overflow-hidden pointer-events-none"
	>
		<Confetti
			x={[-5, 5]}
			y={[0, 0.1]}
			delay={[500, 2000]}
			infinite
			duration={5000}
			amount={200}
			fallDistance="100vh"
		/>
	</div>
{/if}

{#if error}
	<ErrorElement />
{:else if loaded}
	{#key assets}
		<div
			class="grid absolute h-dvh-safe w-screen"
			out:fade={{ duration: transitionDuration / 2 }}
			in:fade={{ duration: transitionDuration / 2, delay: transitionDelay }}
		>
			{#if split}
				<div class="grid grid-cols-2">
					<div
						id="image_portrait_1"
						class="relative grid border-r-2 border-frame-primary h-dvh-safe"
					>
						<Asset
							{...assetProps}
							asset={assets[0]}
							bind:this={primaryAssetComponent}
							bind:showInfo
						/>
					</div>
					<div
						id="image_portrait_2"
						class="relative grid border-l-2 border-frame-primary h-dvh-safe"
					>
						<Asset
							{...assetProps}
							asset={assets[1]}
							bind:this={secondaryAssetComponent}
							bind:showInfo
						/>
					</div>
				</div>
			{:else}
				<div id="image_default" class="relative grid h-dvh-safe w-screen">
					<Asset
						{...assetProps}
						asset={assets[0]}
						bind:this={primaryAssetComponent}
						bind:showInfo
					/>
				</div>
			{/if}
		</div>
	{/key}
{:else}
	<div class="grid absolute h-dvh-safe w-screen">
		<LoadingElement />
	</div>
{/if}
