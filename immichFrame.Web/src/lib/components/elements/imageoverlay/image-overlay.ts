import type { ExifResponseDto } from '$lib/immichFrameApi';

/**
 * What the info overlay says beneath a file's name: its size and its dimensions, each only when the
 * asset has it.
 *
 * Immich is not bound to send any of the three, and an asset can reach the overlay with none of
 * them - a memory Immich has no EXIF for arrives with the description the server gives it and
 * nothing more. Printed regardless, they read "Size: 0.0 MB" and "Dimensions: null x null", so a
 * line the asset cannot fill is left out, and with neither the overlay shows the name alone.
 */
export function fileDetails({
	fileSizeInByte,
	exifImageWidth,
	exifImageHeight
}: Pick<ExifResponseDto, 'fileSizeInByte' | 'exifImageWidth' | 'exifImageHeight'>): string[] {
	const details: string[] = [];

	if (fileSizeInByte != null) {
		details.push(`Size: ${(fileSizeInByte / (1024 * 1024)).toFixed(1)} MB`);
	}

	// Both or neither: one dimension alone is no use, and printed the other as null.
	if (exifImageWidth != null && exifImageHeight != null) {
		details.push(`Dimensions: ${exifImageWidth} x ${exifImageHeight}`);
	}

	return details;
}
