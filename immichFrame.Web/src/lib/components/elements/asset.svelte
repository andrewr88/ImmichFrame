<script lang="ts">
	import { untrack } from 'svelte';
	import {
		type AlbumResponseDto,
		type AssetResponseDto,
		type AssetFaceResponseDto
	} from '$lib/immichFrameApi';
	import { isVideoAsset } from '$lib/constants/asset-type';
	import { decodeBase64 } from '$lib/utils';
	import { thumbHashToDataURL } from 'thumbhash';
	import AssetInfo from '$lib/components/elements/asset-info.svelte';
	import ImageOverlay from '$lib/components/elements/imageoverlay/image-overlay.svelte';
	import { PAN_OFFSET, PAN_SCALE, ZOOM_PAN_SCALES, ZOOM_SCALES, zoomOrigin } from './asset';

	interface Props {
		asset: [
			url: string,
			asset: AssetResponseDto,
			faces: AssetFaceResponseDto[],
			albums: AlbumResponseDto[]
		];
		showLocation: boolean;
		showPhotoDate: boolean;
		showImageDesc: boolean;
		showPeopleDesc: boolean;
		showTagsDesc: boolean;
		showAlbumName: boolean;
		imageFill: boolean;
		imageZoom: boolean;
		imagePan: boolean;
		interval: number;
		split: boolean;
		showInfo: boolean;
		playAudio: boolean;
		onVideoWaiting?: () => void;
		onVideoPlaying?: () => void;
		onAssetLoaded?: () => void;
		onAssetError?: () => void;
	}

	let {
		asset,
		showLocation,
		showPhotoDate,
		showImageDesc,
		showPeopleDesc,
		showTagsDesc,
		showAlbumName,
		imageFill,
		imageZoom,
		imagePan,
		interval,
		split,
		showInfo = $bindable(false),
		playAudio,
		onVideoWaiting = () => {},
		onVideoPlaying = () => {},
		onAssetLoaded = () => {},
		onAssetError = () => {}
	}: Props = $props();

	const isVideo = $derived(isVideoAsset(asset[1]));

	// Re-evaluate only when the asset changes; keep the interval stable for the
	// lifetime of the current asset so zoom/pan animations don't restart.
	const animationDuration = $derived.by(() => {
		void asset[1].id;
		return untrack(() => interval);
	});

	let videoElement = $state<HTMLVideoElement | null>(null);

	$effect(() => {
		void asset[0];
		return () => {
			if (videoElement) {
				videoElement.pause();
				videoElement.src = '';
				videoElement.removeAttribute('src');
				videoElement.srcObject = null;
				videoElement.load();
			}
		};
	});

	let zoomIn = $derived(zoomEffect());
	let panDirection = $derived(panEffect());
	const enableZoom = $derived(imageZoom && !isVideo);
	const enablePan = $derived(imagePan && !isVideo);
	const origin = $derived(zoomOrigin(asset[2], enablePan));

	// PAN_OFFSET either way, as the pan's keyframes take it.
	const panPlus = `${PAN_OFFSET * 100}%`;
	const panMinus = `${-PAN_OFFSET * 100}%`;

	const thumbhashUrl = $derived(getThumbhashUrl());

	function getThumbhashUrl() {
		const hash = asset[1].thumbhash;
		if (!hash) return '';
		try {
			return thumbHashToDataURL(decodeBase64(hash));
		} catch {
			return '';
		}
	}

	function zoomEffect() {
		return 0.5 > Math.random();
	}

	function panEffect() {
		const directions = ['left', 'right', 'up', 'down'];
		return directions[Math.floor(Math.random() * directions.length)];
	}

	function getScaleValues() {
		if (imageZoom && imagePan) {
			// When both zoom and pan are enabled make sure we have minimum zoom to cover pan offset
			return {
				startScale: zoomIn ? ZOOM_PAN_SCALES.min : ZOOM_PAN_SCALES.max,
				endScale: zoomIn ? ZOOM_PAN_SCALES.max : ZOOM_PAN_SCALES.min
			};
		} else if (imageZoom) {
			// Original zoom behavior when only zoom is enabled
			return {
				startScale: zoomIn ? ZOOM_SCALES.min : ZOOM_SCALES.max,
				endScale: zoomIn ? ZOOM_SCALES.max : ZOOM_SCALES.min
			};
		} else {
			// No zoom but pan give pan slight scale to avoid edges
			const panScale = imagePan ? PAN_SCALE : 1;
			return {
				startScale: panScale,
				endScale: panScale
			};
		}
	}

	let scaleValues = $derived(getScaleValues());

	export const pause = async () => {
		if (!asset) return;
		if (isVideo && videoElement) {
			videoElement.pause();
		}
	};

	export const play = async () => {
		if (!asset) return;
		if (isVideo && videoElement) {
			try {
				await videoElement.play();
			} catch {
				// Autoplay might be blocked; ignore.
			}
		}
	};

	// Whether this is still the slideshow's asset, rather than one fading out. See retire().
	let live = true;

	/**
	 * Stands this asset down as the next one replaces it.
	 *
	 * An asset stays mounted for as long as it takes to fade out, and until then its video went on
	 * playing and its events went on reaching the slideshow, which takes them for the new slide's:
	 * with audio on the two were heard over each other, a `playing` from the old one cancelled the
	 * new one's stall skip and reset the count of failures in a row, and a `waiting` or an error
	 * could pause the new one's progress or skip it outright. So the video is stopped and muted, and
	 * the events are no longer passed on.
	 */
	export const retire = () => {
		live = false;
		if (videoElement) {
			videoElement.muted = true;
			videoElement.pause();
		}
	};
</script>

{#if showInfo}
	<ImageOverlay asset={asset[1]} albums={asset[3]} />
{/if}

<div class="immichframe_image relative place-self-center overflow-hidden">
	<!-- Container with zoom-effect -->
	<div
		class="relative w-full h-full {enableZoom ? 'zoom' : ''} {enablePan ? 'pan' : ''}"
		style="
			--interval: {animationDuration + 2}s;
			--originX: {origin ? origin.x + '%' : 'center'};
			--originY: {origin ? origin.y + '%' : 'center'};
			--start-scale: {scaleValues.startScale};
			--end-scale: {scaleValues.endScale};
			--pan-start-x: {panDirection === 'left' ? panPlus : panDirection === 'right' ? panMinus : '0'};
			--pan-end-x: {panDirection === 'left' ? panMinus : panDirection === 'right' ? panPlus : '0'};
			--pan-start-y: {panDirection === 'up' ? panPlus : panDirection === 'down' ? panMinus : '0'};
			--pan-end-y: {panDirection === 'up' ? panMinus : panDirection === 'down' ? panPlus : '0'};"
	>
		{#if isVideo}
			<!-- Autoplay might be blocked by the browser when playAudio is enabled -->
			<video
				bind:this={videoElement}
				class="{imageFill
					? 'w-screen max-h-screen h-dvh-safe object-cover'
					: 'max-h-screen h-dvh-safe max-w-full object-contain'} w-full h-full"
				src={asset[0]}
				autoplay={!playAudio}
				muted={!playAudio}
				playsinline
				preload="metadata"
				disableRemotePlayback
				poster={thumbhashUrl}
				onloadstart={async (e) => {
					e.currentTarget.currentTime = 0;
					if (playAudio) {
						try {
							await play();
						} catch {
							// Autoplay might be blocked; ignore.
						}
					}
				}}
				onerror={() => {
					if (!live) return;
					console.error('Video failed to load:', asset[0]);
					onAssetError();
				}}
				onwaiting={() => {
					if (live) onVideoWaiting();
				}}
				onplaying={() => {
					if (live) onVideoPlaying();
				}}
			></video>
		{:else}
			<img
				class="{imageFill
					? 'w-screen max-h-screen h-dvh-safe object-cover'
					: 'max-h-screen h-dvh-safe max-w-full object-contain'} w-full h-full"
				src={asset[0]}
				alt="data"
				onload={() => {
					if (live) onAssetLoaded();
				}}
				onerror={() => {
					if (!live) return;
					console.error('Image failed to load:', asset[0]);
					onAssetError();
				}}
			/>
		{/if}
	</div>
</div>
<AssetInfo
	asset={asset[1]}
	albums={asset[3]}
	{showLocation}
	{showPhotoDate}
	{showImageDesc}
	{showPeopleDesc}
	{showTagsDesc}
	{showAlbumName}
	{split}
/>
<img class="absolute flex w-full h-full z-[-1]" src={thumbhashUrl} alt="data" />

<style>
	.zoom {
		animation: zoom var(--interval) ease-out forwards;
		transform-origin: var(--originX, center) var(--originY, center);
	}

	.pan {
		animation: pan var(--interval) ease-in-out forwards;
	}

	.zoom.pan {
		animation: zoom-pan var(--interval) ease-in-out forwards;
		transform-origin: var(--originX, center) var(--originY, center);
	}

	@keyframes zoom {
		from {
			transform: scale(var(--start-scale, 1));
		}
		to {
			transform: scale(var(--end-scale, 1.3));
		}
	}

	@keyframes pan {
		from {
			transform: translateX(var(--pan-start-x, 0)) translateY(var(--pan-start-y, 0))
				scale(var(--start-scale, 1));
		}
		to {
			transform: translateX(var(--pan-end-x, 0)) translateY(var(--pan-end-y, 0))
				scale(var(--end-scale, 1));
		}
	}

	@keyframes zoom-pan {
		from {
			transform: translateX(var(--pan-start-x, 0)) translateY(var(--pan-start-y, 0))
				scale(var(--start-scale, 1));
		}
		to {
			transform: translateX(var(--pan-end-x, 0)) translateY(var(--pan-end-y, 0))
				scale(var(--end-scale, 1.3));
		}
	}
</style>
