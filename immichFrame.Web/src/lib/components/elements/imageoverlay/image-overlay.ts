import type {
	AlbumResponseDto,
	ExifResponseDto,
	PersonResponseDto,
	TagResponseDto
} from '$lib/immichFrameApi';

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

/**
 * The names of the albums a photo is in, as both overlays list them: an album without one left out.
 *
 * An album need not have a name in Immich, and one without was listed all the same, as nothing -
 * the corner read ", Holiday", and the info overlay had an empty entry. A name of spaces alone is
 * no better, so it goes the same way.
 */
export function albumNames(albums: Pick<AlbumResponseDto, 'albumName'>[] | null | undefined) {
	return named((albums ?? []).map((album) => album.albumName));
}

/** The names of the people in a photo, as both overlays list them: anybody without one left out. */
export function namedPeople(people: Pick<PersonResponseDto, 'name'>[] | null | undefined) {
	return named((people ?? []).map((person) => person.name));
}

/** The names of a photo's tags, as both overlays list them: a tag without one left out. */
export function namedTags(tags: Pick<TagResponseDto, 'name'>[] | null | undefined) {
	return named((tags ?? []).map((tag) => tag.name));
}

/** The names that would show as something: none missing, empty, or of spaces alone. */
function named(names: (string | null | undefined)[]): string[] {
	return names.filter((name): name is string => !!name?.trim());
}
