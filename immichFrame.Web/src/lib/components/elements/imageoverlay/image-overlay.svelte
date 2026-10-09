<script lang="ts">
	import {
		type AlbumResponseDto,
		type AssetResponseDto,
		type ExifResponseDto
	} from '$lib/immichFrameApi';
	import {
		mdiAccount,
		mdiCamera,
		mdiClose,
		mdiFile,
		mdiImageAlbum,
		mdiStar,
		mdiTag,
		mdiText
	} from '@mdi/js';
	import OverlayItem from './overlay-item.svelte';
	import OverlayQr from './overlay-qr.svelte';
	import Icon from '../icon.svelte';
	import { getContext } from 'svelte';
	import { albumNames, cameraDetails, fileDetails, namedPeople, namedTags } from './image-overlay';

	interface Props {
		asset: AssetResponseDto;
		albums: AlbumResponseDto[];
	}
	let { asset, albums }: Props = $props();

	let availablePeople = $derived(namedPeople(asset.people));
	let availableTags = $derived(namedTags(asset.tags));
	let availableAlbums = $derived(albumNames(albums));
	let exif: ExifResponseDto = $derived(asset.exifInfo ?? ({} as ExifResponseDto));
	let camera = $derived(cameraDetails(exif));

	let close = getContext<() => void>('close');
</script>

<div class="p-0 absolute w-full h-full z-200">
	<div
		class="info-overlay-background bg-black/70 w-full h-full relative items-center justify-center flex pt-32 pb-8 max-h-full overflow-auto"
	>
		<button class="absolute top-0 right-0 m-4 text-frame-primary" onclick={close}>
			<Icon
				path={mdiClose}
				size={30}
				class="info-overlay-close hover:scale-110 transition-transform duration-200 text-frame-primary"
			/>
		</button>
		<div class="flex h-full flex-col gap-5">
			<div class="w-fit flex grow flex-col gap-3">
				{#if asset.originalFileName}
					<OverlayItem icon={mdiFile} header={asset.originalFileName} items={fileDetails(exif)} />
				{/if}
				{#if camera}
					<OverlayItem icon={mdiCamera} header={camera.header} items={camera.settings} />
				{/if}
				{#if exif.description}
					<OverlayItem icon={mdiText} header={exif.description} />
				{/if}
				{#if exif.rating}
					<OverlayItem icon={mdiStar} header="Rating" items={[exif.rating.toString()]} />
				{/if}
				{#if availablePeople.length > 0}
					<OverlayItem icon={mdiAccount} header="People" items={availablePeople} />
				{/if}
				{#if availableTags.length > 0}
					<OverlayItem icon={mdiTag} header="Tags" items={availableTags} />
				{/if}
				{#if availableAlbums.length > 0}
					<OverlayItem icon={mdiImageAlbum} header="Album" items={availableAlbums} />
				{/if}
			</div>

			<OverlayQr id={asset.id} />
		</div>
	</div>
</div>
