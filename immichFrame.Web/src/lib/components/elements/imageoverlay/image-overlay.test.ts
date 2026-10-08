import { describe, expect, it } from 'vitest';
import { fileDetails } from './image-overlay';

describe('fileDetails: what the info overlay says under the file name', () => {
	it.each([
		{
			has: 'a size and both dimensions',
			exif: { fileSizeInByte: 5_242_880, exifImageWidth: 4000, exifImageHeight: 3000 },
			shown: ['Size: 5.0 MB', 'Dimensions: 4000 x 3000']
		},
		{
			// What the server sends for a memory Immich has no EXIF for: a description, and null for
			// everything else.
			has: 'none of them, each sent as null',
			exif: { fileSizeInByte: null, exifImageWidth: null, exifImageHeight: null },
			shown: []
		},
		{
			// An asset with no exifInfo at all, which the overlay reads as an empty one.
			has: 'none of them, each left out',
			exif: {},
			shown: []
		},
		{
			has: 'a size and no dimensions',
			exif: { fileSizeInByte: 1_572_864 },
			shown: ['Size: 1.5 MB']
		},
		{
			has: 'a size and a width, but no height',
			exif: { fileSizeInByte: 1_572_864, exifImageWidth: 4000, exifImageHeight: null },
			shown: ['Size: 1.5 MB']
		},
		{
			has: 'a height, and nothing else',
			exif: { exifImageHeight: 3000 },
			shown: []
		}
	])('an asset with $has, gets a line for what it has and none for the rest', ({ exif, shown }) => {
		// Act / Assert: a line the asset cannot fill is left out. Each was filled in regardless, as
		// "Size: 0.0 MB" and "Dimensions: null x null" - or "undefined x undefined".
		expect(fileDetails(exif)).toEqual(shown);
	});
});
