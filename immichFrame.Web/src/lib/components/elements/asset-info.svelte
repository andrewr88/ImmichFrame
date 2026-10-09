<script lang="ts">
	import { type AlbumResponseDto, type AssetResponseDto } from '$lib/immichFrameApi';
	import { configStore } from '$lib/stores/config.store';
	import { safeFormat } from '$lib/safe-format';
	import { resolveLocale } from '$lib/locale';
	import Icon from './icon.svelte';
	import { mdiCalendar, mdiMapMarker, mdiAccount, mdiText, mdiImageAlbum, mdiTag } from '@mdi/js';
	import { albumNames, namedPeople, namedTags } from './imageoverlay/image-overlay';

	interface Props {
		asset: AssetResponseDto;
		albums: AlbumResponseDto[];
		showLocation: boolean;
		showPhotoDate: boolean;
		showImageDesc: boolean;
		showPeopleDesc: boolean;
		showTagsDesc: boolean;
		showAlbumName: boolean;
		split: boolean;
	}

	let {
		asset,
		albums,
		showLocation,
		showPhotoDate,
		showImageDesc,
		showPeopleDesc,
		showTagsDesc,
		showAlbumName,
		split,
	}: Props = $props();

	function formatLocation(format: string, city?: string, state?: string, country?: string) {
		const locationParts: Array<string> = [];

		format.split(',').forEach((part) => {
			const trimmedPart = part.trim().toLowerCase();
			if (trimmedPart === 'city' && city) {
				locationParts.push(city);
			} else if (trimmedPart === 'state' && state) {
				locationParts.push(state);
			} else if (trimmedPart === 'country' && country) {
				locationParts.push(country);
			}
		});

		return Array.from(locationParts).join(', ');
	}
	let assetDate = $derived(asset.exifInfo?.dateTimeOriginal);
	let desc = $derived(asset.exifInfo?.description ?? '');
	let time = $derived(assetDate ? new Date(assetDate) : null);
	const localeToUse = resolveLocale($configStore.language);
	let formattedDate = $derived(
		time
			? safeFormat(time, $configStore.photoDateFormat, 'dd.MM.yyyy', { locale: localeToUse })
			: null
	);
	let location = $derived(
		formatLocation(
			$configStore.imageLocationFormat ?? 'City,State,Country',
			asset.exifInfo?.city ?? '',
			asset.exifInfo?.state ?? '',
			asset.exifInfo?.country ?? ''
		)
	);
	let availableAlbums = $derived(albumNames(albums));
	let availablePeople = $derived(namedPeople(asset.people));
	let availableTags = $derived(namedTags(asset.tags));
</script>

{#if showPhotoDate || showLocation || showImageDesc || showPeopleDesc || showTagsDesc || showAlbumName}
	<div
		id="imageinfo"
		class="immichframe_image_metadata absolute bottom-0 right-0 z-100 text-frame-primary p-1 text-right
		{$configStore.style == 'solid' ? 'bg-frame-secondary rounded-tl-2xl' : ''}
		{$configStore.style == 'transition' ? 'bg-linear-to-l from-frame-secondary from-0% pl-10' : ''}
		{$configStore.style == 'blur' ? 'backdrop-blur-lg rounded-tl-2xl' : ''}	"
	>
		{#if showPhotoDate && formattedDate}
			<p id="photodate" class="info-item">
				<Icon path={mdiCalendar} class="info-icon" />
				<span class="info-text" class:short-text={split}>{formattedDate}</span>
			</p>
		{/if}
		{#if showImageDesc && desc}
			<p id="imagedescription" class="info-item">
				<Icon path={mdiText} class="info-icon" />
				<span class="info-text" class:short-text={split}>{desc}</span>
			</p>
		{/if}
		{#if showAlbumName && availableAlbums.length > 0}
			<p id="imagealbums" class="info-item">
				<Icon path={mdiImageAlbum} />
				<span class="info-text" class:short-text={split}>{availableAlbums.join(', ')}</span>
			</p>
		{/if}
		{#if showPeopleDesc && availablePeople.length > 0}
			<p id="peopledescription" class="info-item">
				<Icon path={mdiAccount} />
				<span class="info-text" class:short-text={split}>{availablePeople.join(', ')}</span>
			</p>
		{/if}
		{#if showTagsDesc && availableTags.length > 0}
			<p id="tagsdescription" class="info-item">
				<Icon path={mdiTag} />
				<span class="info-text" class:short-text={split}>{availableTags.join(', ')}</span>
			</p>
		{/if}
		{#if showLocation && location}
			<p id="imagelocation" class="info-item">
				<Icon path={mdiMapMarker} />
				<span class="info-text" class:short-text={split}>{location}</span>
			</p>
		{/if}
	</div>
{/if}

<style>
	.info-item {
		display: flex;
		align-items: center;
		gap: 0.5rem;
		margin: 0.2rem 0.5rem;
	}
	.info-text {
		max-width: 40vw;
		overflow: hidden;
		text-wrap: nowrap;
		text-overflow: ellipsis;
	}
	.short-text {
		max-width: 22vw;
	}
</style>
