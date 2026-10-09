<script lang="ts">
	import * as api from '$lib/index';
	import ProgressBar from '$lib/components/elements/progress-bar.svelte';
	import { slideshowStore } from '$lib/stores/slideshow.store';
	import { clientIdentifierStore, authSecretStore } from '$lib/stores/persist.store';
	import { profileStore } from '$lib/stores/profile.store';
	import { onDestroy, onMount, setContext, tick } from 'svelte';
	import OverlayControls from '../elements/overlay-controls.svelte';
	import AssetComponent from '../elements/asset-component.svelte';
	import type AssetComponentInstance from '../elements/asset-component.svelte';
	import { configStore } from '$lib/stores/config.store';
	import { applyBaseFontSize, applyFrameColors } from '$lib/hooks/useTheme';
	import ErrorElement from '../elements/error-element.svelte';
	import Clock from '../elements/clock.svelte';
	import Appointments from '../elements/appointments.svelte';
	import { page } from '$app/state';
	import { ProgressBarLocation, ProgressBarStatus } from '../elements/progress-bar.types';
	import { isImageAsset, isVideoAsset } from '$lib/constants/asset-type';
	import {
		FailureStreak,
		isBirthdayToday,
		okArray,
		recordShown,
		slideDuration,
		takeNext,
		takePrevious,
		whatLoaded
	} from './home-page';

	interface AssetsState {
		assets: [string, api.AssetResponseDto, api.AssetFaceResponseDto[], api.AlbumResponseDto[]][];
		loaded: boolean;
		split: boolean;
		hasBday: boolean;
	}

	api.init();

	const PRELOAD_ASSETS = 5;
	const TRANSITION_WATCHDOG_MS = 10000;
	const VIDEO_STALL_MS = 15000;
	const CURSOR_HIDE_MS = 2000;
	const RELOAD_ON_ERROR_MS = 30000;
	const MAX_FAILURES_IN_A_ROW = 10;

	// The slides shown before the one on screen, oldest first, each as it went up - see takePrevious.
	let assetHistory: api.AssetResponseDto[][] = $state([]);
	let assetBacklog: api.AssetResponseDto[] = $state([]);

	// The slide on screen, as it went up. A step moves it on as the step starts - into history going
	// forward, back onto the backlog going back - and this is empty until the step puts its own slide
	// up, so that a step the watchdog gives up on cannot move it on twice, and a slide that came to
	// nothing is never moved on at all.
	let displayingAssets: api.AssetResponseDto[] = $state([]);

	const { instantTransition } = slideshowStore;

	let progressBar: ProgressBar = $state() as ProgressBar;
	let assetComponent: AssetComponentInstance = $state() as AssetComponentInstance;
	let currentDuration: number = $state($configStore.interval ?? 20);

	const failureStreak = new FailureStreak(MAX_FAILURES_IN_A_ROW);
	let errorSkipScheduled = false;
	let watchdogTimer: number | undefined;
	let videoStallTimeout: number | undefined;
	let timeoutId: number | undefined;

	// Whether the user has paused the slideshow, with the pause button or the info. A video that
	// stalls pauses the progress bar as well, so it is this, not the bar, that says what to resume.
	let userPaused: boolean = $state(false);

	let error: boolean = $state(false);
	let infoVisible: boolean = $state(false);
	let authError: boolean = $state(false);
	let errorMessage: string = $state('');
	let assetsState: AssetsState = $state({
		assets: [],
		loaded: false,
		split: false,
		hasBday: false
	});
	let assetPromisesDict: Record<
		string,
		Promise<[string, api.AssetResponseDto, api.AssetFaceResponseDto[], api.AlbumResponseDto[]]>
	> = {};

	let refreshInterval: number;

	let cursorVisible = $state(true);

	const authsecret = page.url.searchParams.get('authsecret');

	if (authsecret && authsecret != $authSecretStore) {
		authSecretStore.set(authsecret);
		api.init();
	}

	const hideCursor = () => {
		cursorVisible = false;
	};

	setContext('close', provideClose);

	async function provideClose() {
		infoVisible = false;
		await setPaused(false);
	}

	/** Pauses the slide on screen for the user - its video, and its timer - or resumes it. */
	async function setPaused(paused: boolean) {
		userPaused = paused;
		if (paused) {
			await assetComponent?.pause?.();
			await progressBar.pause();
		} else {
			await assetComponent?.play?.();
			await progressBar.play();
		}
	}

	const showCursor = () => {
		cursorVisible = true;
		clearTimeout(timeoutId);
		timeoutId = window.setTimeout(hideCursor, CURSOR_HIDE_MS);
	};

	/** Starts loading the slide about to go up and the next few after it, and lets go of the rest. */
	async function updateAssetPromises(upcoming: api.AssetResponseDto[]) {
		for (let asset of upcoming) {
			if (!(asset.id in assetPromisesDict)) {
				assetPromisesDict[asset.id] = loadAsset(asset);
			}
		}
		for (let i = 0; i < PRELOAD_ASSETS; i++) {
			if (i >= assetBacklog.length) {
				break;
			}
			if (!(assetBacklog[i].id in assetPromisesDict)) {
				assetPromisesDict[assetBacklog[i].id] = loadAsset(assetBacklog[i]);
			}
		}
		// Collect keys to remove first to avoid modifying dict during async iteration
		const keysToRemove = Object.keys(assetPromisesDict).filter(
			(key) =>
				!upcoming.find((item) => item.id === key) && !assetBacklog.find((item) => item.id === key)
		);

		keysToRemove.forEach((key) => {
			const promise = assetPromisesDict[key];
			delete assetPromisesDict[key];
			promise
				.then(([url]) => revokeObjectUrl(url))
				.catch((err) => console.warn('Failed to resolve asset during cleanup:', err));
		});
	}

	async function loadAssets(epoch: number) {
		try {
			let assetRequest = await api.getAssets({
				clientIdentifier: $clientIdentifierStore,
				profile: $profileStore
			});

			// The watchdog has given up on this step and started another, which the slideshow is now.
			if (epoch !== transitionEpoch) return;

			if (assetRequest.status != 200) {
				if (assetRequest.status == 401) {
					authError = true;
				}
				error = true;
				return;
			}

			error = false;
			assetBacklog = assetRequest.data.filter(
				(asset) => isImageAsset(asset) || isVideoAsset(asset)
			);
		} catch {
			if (epoch === transitionEpoch) error = true;
		}
	}

	let isHandlingAssetTransition = $state(false);
	let transitionEpoch = 0;
	let pendingTransition: { previous: boolean; instant: boolean } | null = $state(null);

	/**
	 * Steps the slideshow on, or back with `previous` - or queues the step behind the one under way.
	 * Whether it went anywhere, or is to: Back at the start of history has nowhere to go, and is a
	 * no-op. getPreviousAssets says so before anything has been touched, so that the slide on screen
	 * is left as it was - its timer, a pause, a stalled video's skip - and this returns false.
	 */
	const handleDone = async (previous: boolean = false, instant: boolean = false) => {
		if (isHandlingAssetTransition) {
			pendingTransition = { previous, instant };
			return true;
		}

		const previousAssets = previous ? getPreviousAssets() : null;
		if (previous && !previousAssets) return false;

		await runTransition(previousAssets, instant);
		return true;
	};

	/** The step itself: to `assets` - where Back leads - or else on to the next slide. */
	async function runTransition(assets: api.AssetResponseDto[] | null, instant: boolean) {
		const currentEpoch = ++transitionEpoch;
		isHandlingAssetTransition = true;

		clearTimeout(watchdogTimer);
		clearTimeout(videoStallTimeout);
		// Watchdog: If the transition (fetching/loading assets) hangs, force-release the lock.
		watchdogTimer = window.setTimeout(() => {
			if (currentEpoch === transitionEpoch && isHandlingAssetTransition) {
				console.error('Transition watchdog triggered: Force-resetting lock due to hang');
				isHandlingAssetTransition = false;

				// Bump the epoch so the original (still-awaiting) transition becomes a no-op
				// when/if it eventually resolves, and force a fresh advance.
				transitionEpoch++;
				const next = recoveryStep();
				handleDone(next.previous, next.instant).catch((err) => {
					console.error('handleDone failed:', err);
					isHandlingAssetTransition = false;
				});
			}
		}, TRANSITION_WATCHDOG_MS);

		try {
			progressBar.restart(false);
			$instantTransition = instant;

			const group = assets ?? (await getNextAssets(currentEpoch));
			if (!group) return;

			const shown = await showAssets(group, currentEpoch);
			if (currentEpoch !== transitionEpoch) return;
			if (!shown) {
				// None of it loaded: skipped, and counted, as onAssetError skips and counts a slide
				// that will not draw.
				if (!giveUpAfterFailure()) pendingTransition = recoveryStep();
				return;
			}

			await tick();
			if (currentEpoch !== transitionEpoch) return;

			userPaused = false;
			await assetComponent?.play?.();
			if (currentEpoch !== transitionEpoch) return;
			progressBar.play();
		} finally {
			if (currentEpoch === transitionEpoch) {
				isHandlingAssetTransition = false;
				clearTimeout(watchdogTimer);

				if (pendingTransition) {
					const next = pendingTransition;
					pendingTransition = null;
					handleDone(next.previous, next.instant).catch((err) => {
						console.error('handleDone failed:', err);
						isHandlingAssetTransition = false;
					});
				}
			}
		}
	}

	/**
	 * The step to take when the slideshow has to move on by itself - the step under way hung, or put
	 * nothing up - with the slide timer stopped: the step asked for meanwhile, if any, or else the
	 * next slide. Back at the start of history would be a no-op, and would leave the timer stopped for
	 * good, so then it is the next slide as well.
	 */
	function recoveryStep(): { previous: boolean; instant: boolean } {
		const asked = pendingTransition;
		pendingTransition = null;
		if (asked && (!asked.previous || assetHistory.length)) return asked;
		return { previous: false, instant: true };
	}

	/**
	 * Counts a slide that failed - it would not load, or would not draw - and once too many have in a
	 * row, stops skipping them: the error goes up instead, and a step asked for meanwhile, which would
	 * only go on trying, is dropped. Whether it gave up.
	 */
	function giveUpAfterFailure(): boolean {
		if (!failureStreak.recordFailure()) return false;

		error = true;
		errorMessage =
			'Too many consecutive asset load failures. Please check your network or server connection.';
		pendingTransition = null;
		return true;
	}

	/**
	 * Steps forward to the next slide off the backlog, fetching more once it has run out - or returns
	 * null when there are none to be had, or the watchdog has given up on this step.
	 */
	async function getNextAssets(epoch: number): Promise<api.AssetResponseDto[] | null> {
		if (!assetBacklog.length) {
			await loadAssets(epoch);
			if (epoch !== transitionEpoch) return null;
		}

		if (!assetBacklog.length) {
			if (!error) {
				error = true;
				errorMessage = 'No assets were found! Check your configuration.';
			}
			return null;
		}

		const next = takeNext(assetBacklog, $configStore.layout);
		assetBacklog = next.backlog;
		if (displayingAssets.length) assetHistory = recordShown(assetHistory, displayingAssets);
		displayingAssets = [];
		return next.group;
	}

	/**
	 * Steps back to the slide before the one on screen, as it went up - or, at the start of history,
	 * returns null, having changed nothing.
	 */
	function getPreviousAssets(): api.AssetResponseDto[] | null {
		const previous = takePrevious(assetHistory);
		if (!previous) return null;

		assetHistory = previous.history;
		assetBacklog.unshift(...displayingAssets);
		displayingAssets = [];
		return previous.group;
	}

	/**
	 * Loads a slide and puts up what of it loaded: a pair whose one half would not load goes up as the
	 * other half alone. Whether anything went up - nothing does when none of it loaded, nor once the
	 * watchdog has given up on the step: by then a blob URL of the slide may have been revoked, or the
	 * slideshow have moved on, and it would replace whatever is up now.
	 */
	async function showAssets(assets: api.AssetResponseDto[], epoch: number): Promise<boolean> {
		await updateAssetPromises(assets);
		const loaded = await whatLoaded(assets, (asset) => assetPromisesDict[asset.id]);
		if (epoch !== transitionEpoch || !loaded.length) return false;

		const shown = loaded.map(([, asset]) => asset);
		displayingAssets = shown;
		currentDuration = slideDuration(shown, $configStore.interval);
		assetsState = {
			assets: loaded,
			loaded: true,
			split: shown.length == 2 && shown.every(isImageAsset),
			hasBday: hasBirthday(shown)
		};
		return true;
	}

	function hasBirthday(assets: api.AssetResponseDto[]) {
		let today = new Date();
		let hasBday: boolean = false;

		for (let asset of assets) {
			for (let person of asset.people ?? []) {
				if (isBirthdayToday(person.birthDate, today)) {
					hasBday = true;
					break;
				}
			}
			if (hasBday) break;
		}

		return hasBday;
	}

	async function loadAsset(assetResponse: api.AssetResponseDto) {
		let assetUrl: string;

		if (isVideoAsset(assetResponse)) {
			// Stream videos directly instead of preloading
			assetUrl = api.getAssetStreamUrl(
				assetResponse.id,
				$clientIdentifierStore,
				assetResponse.type,
				$profileStore
			);
		} else {
			// Preload images as blobs
			const req = await api.getAsset(assetResponse.id, {
				clientIdentifier: $clientIdentifierStore,
				profile: $profileStore,
				assetType: assetResponse.type
			});
			if (req.status != 200) {
				throw new Error(`Failed to load asset ${assetResponse.id}: status ${req.status}`);
			}
			assetUrl = getObjectUrl(req.data);
		}

		let album: api.AlbumResponseDto[] | null = null;
		if ($configStore.showAlbumName) {
			const albumReq = await api.getAlbumInfo(assetResponse.id, {
				clientIdentifier: $clientIdentifierStore,
				profile: $profileStore
			});
			album = okArray(albumReq, 'albums', assetResponse.id);
		}

		// if the people array is already populated, there is no need to call the API again
		if ($configStore.showPeopleDesc && (assetResponse.people ?? []).length == 0) {
			const assetInfoRequest = await api.getAssetInfo(assetResponse.id, {
				clientIdentifier: $clientIdentifierStore,
				profile: $profileStore
			});
			// Only 200 is declared, hence the widening. Anything but a 200 with a list is warned about,
			// as okArray warns for the albums and faces, and leaves the asset's people as they were.
			const status: number = assetInfoRequest.status;
			const people = status === 200 ? assetInfoRequest.data?.people : undefined;
			if (Array.isArray(people)) {
				assetResponse.people = people;
			} else {
				console.warn(`Failed to load people for asset ${assetResponse.id}: status ${status}`);
			}
		}

		let faces: api.AssetFaceResponseDto[] = [];
		if (!isVideoAsset(assetResponse) && ($configStore.imageZoom || $configStore.imagePan)) {
			const facesRequest = await api.getAssetFaces(assetResponse.id, {
				clientIdentifier: $clientIdentifierStore,
				profile: $profileStore
			});
			faces = okArray(facesRequest, 'faces', assetResponse.id);
		}

		return [assetUrl, assetResponse, faces, album] as [
			string,
			api.AssetResponseDto,
			api.AssetFaceResponseDto[],
			api.AlbumResponseDto[]
		];
	}

	function getObjectUrl(image: Blob) {
		return URL.createObjectURL(image);
	}

	function revokeObjectUrl(url: string) {
		// Only revoke blob URLs, not streaming URLs
		if (!url.startsWith('blob:')) return;
		try {
			URL.revokeObjectURL(url);
		} catch {
			console.warn('Failed to revoke object URL:', url);
		}
	}

	// The configured theme applies to the slideshow only — the admin UI keeps
	// the @immich/ui defaults.
	$effect(() => applyFrameColors($configStore));
	$effect(() => applyBaseFontSize($configStore.baseFontSize));

	onMount(() => {
		window.addEventListener('mousemove', showCursor);
		window.addEventListener('click', showCursor);

		// 30 second reload on error
		refreshInterval = window.setInterval(() => {
			if (error) window.location.reload();
		}, RELOAD_ON_ERROR_MS);

		// The first slide goes up as every other does: under the lock, so that a key pressed or the
		// progress bar running out meanwhile waits its turn, and with its timer started once it is up.
		handleDone(false, true);

		return () => {
			window.removeEventListener('mousemove', showCursor);
			window.removeEventListener('click', showCursor);
			window.clearInterval(refreshInterval);
			window.clearTimeout(timeoutId);
			window.clearTimeout(videoStallTimeout);
			window.clearTimeout(watchdogTimer);
		};
	});

	onDestroy(async () => {
		const revokes = Object.values(assetPromisesDict).map(async (p) => {
			try {
				const [url] = await p;
				revokeObjectUrl(url);
			} catch (err) {
				console.warn('Failed to resolve asset during destroy cleanup:', err);
			}
		});
		await Promise.allSettled(revokes);
		assetPromisesDict = {};
	});
</script>

<section class="fixed grid h-dvh-safe w-screen bg-black" class:cursor-none={!cursorVisible}>
	{#if error}
		<ErrorElement {authError} message={errorMessage} />
	{:else}
		<div class="absolute h-screen w-screen">
			<AssetComponent
				showLocation={$configStore.showImageLocation}
				interval={currentDuration}
				showPhotoDate={$configStore.showPhotoDate}
				showImageDesc={$configStore.showImageDesc}
				showPeopleDesc={$configStore.showPeopleDesc}
				showTagsDesc={$configStore.showTagsDesc}
				showAlbumName={$configStore.showAlbumName}
				{...assetsState}
				imageFill={$configStore.imageFill}
				imageZoom={$configStore.imageZoom}
				imagePan={$configStore.imagePan}
				bind:this={assetComponent}
				bind:showInfo={infoVisible}
				playAudio={$configStore.playAudio}
				onVideoWaiting={async () => {
					// Armed before the bar is paused, not after: the pause takes until the next frame, and
					// a `playing` meanwhile found no timer yet to clear, so the one armed after it skipped
					// a video that was playing.
					clearTimeout(videoStallTimeout);
					if (!userPaused) {
						videoStallTimeout = window.setTimeout(
							() => {
								if (!userPaused) {
									console.warn('Video stalled, skipping...');
									handleDone(false, true);
								}
							},
							Math.max(5000, Math.min(VIDEO_STALL_MS, currentDuration * 1000))
						);
					}
					await progressBar.pause();
				}}
				onVideoPlaying={async () => {
					failureStreak.recordSuccess();
					clearTimeout(videoStallTimeout);
					if (!userPaused) {
						await progressBar.play();
					}
				}}
				onAssetLoaded={() => failureStreak.recordSuccess()}
				onAssetError={async () => {
					if (errorSkipScheduled) return;
					errorSkipScheduled = true;

					if (!giveUpAfterFailure()) await handleDone(false, true);
					errorSkipScheduled = false;
				}}
			/>
		</div>

		{#if $configStore.showClock}
			<Clock />
		{/if}

		<Appointments />

		<OverlayControls
			next={async () => {
				await handleDone(false, true);
				infoVisible = false;
			}}
			back={async () => {
				// Back at the start of history is a no-op, and leaves the info up as well.
				if (await handleDone(true, true)) infoVisible = false;
			}}
			pause={async () => {
				infoVisible = false;
				await setPaused(!userPaused);
			}}
			showInfo={async () => {
				infoVisible = !infoVisible;
				await setPaused(infoVisible);
			}}
			status={userPaused ? ProgressBarStatus.Paused : ProgressBarStatus.Playing}
			bind:infoVisible
			overlayVisible={cursorVisible}
		/>

		<ProgressBar
			autoplay
			duration={currentDuration}
			hidden={!$configStore.showProgressBar}
			location={ProgressBarLocation.Bottom}
			bind:this={progressBar}
			onDone={async () => {
				// Not handleDone itself: what it returns is for the Back button, and onDone returns nothing.
				await handleDone();
			}}
		/>
	{/if}
</section>
