import { describe, expect, it } from 'vitest';
import { albumNames, cameraDetails, fileDetails, namedPeople, namedTags } from './image-overlay';

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

describe('cameraDetails: what the info overlay says about the camera', () => {
	it.each([
		{
			has: 'a make, a model and every setting',
			exif: {
				make: 'Canon',
				model: 'EOS R5',
				fNumber: 2.8,
				exposureTime: '1/250',
				focalLength: 50,
				iso: 100
			},
			shown: { header: 'Canon EOS R5', settings: ['ƒ/2.8', '1/250 s', '50 mm', 'ISO 100'] }
		},
		{
			has: 'a model, and no make or settings',
			exif: { make: null, model: 'Pixel 8' },
			shown: { header: 'Pixel 8', settings: [] }
		},
		{
			// An edited export or a scan: the exposure kept, and what took it lost.
			has: 'settings, and no make or model',
			exif: { fNumber: 1.8, exposureTime: '1/60', focalLength: null, iso: 400 },
			shown: { header: 'Camera', settings: ['ƒ/1.8', '1/60 s', 'ISO 400'] }
		},
		{
			has: 'a setting, and a make and model of spaces alone',
			exif: { make: ' ', model: '\t', iso: 200 },
			shown: { header: 'Camera', settings: ['ISO 200'] }
		}
	])('an asset with $has, gets a header that names something', ({ exif, shown }) => {
		// Act / Assert: the header was the make and model alone, and without them an empty line
		// beside the camera icon.
		expect(cameraDetails(exif)).toEqual(shown);
	});

	it.each([
		{
			has: 'none of them, each sent as null',
			exif: {
				make: null,
				model: null,
				fNumber: null,
				exposureTime: null,
				focalLength: null,
				iso: null
			}
		},
		{ has: 'none of them, each left out', exif: {} },
		{ has: 'a make of spaces alone, and nothing else', exif: { make: '  ' } }
	])('an asset with $has, gets no camera line', ({ exif }) => {
		// Act / Assert
		expect(cameraDetails(exif)).toBeUndefined();
	});
});

describe('albumNames: the albums the overlays say a photo is in', () => {
	it.each([
		{
			in: 'two albums',
			albums: [{ albumName: 'Holiday' }, { albumName: 'Family' }],
			shown: ['Holiday', 'Family']
		},
		{
			in: 'an album with no name, and one with',
			albums: [{ albumName: '' }, { albumName: 'Holiday' }],
			shown: ['Holiday']
		},
		{
			in: 'albums named with spaces alone, and one with a name',
			albums: [{ albumName: '   ' }, { albumName: 'Holiday' }, { albumName: '\t' }],
			shown: ['Holiday']
		}
	])('a photo in $in, is said to be in those with a name', ({ albums, shown }) => {
		// Act / Assert: one without a name was listed all the same, as nothing - ", Holiday" in the
		// corner, and an empty entry in the info overlay.
		expect(albumNames(albums)).toEqual(shown);
	});

	it.each([
		// What loadAsset hands over with the album name turned off: the albums are never asked for.
		{ albums: 'never loaded', list: null },
		{ albums: 'none', list: [] }
	])('albums $albums, are no names', ({ list }) => {
		// Act / Assert
		expect(albumNames(list)).toEqual([]);
	});
});

describe('namedPeople and namedTags: the people and tags the overlays list', () => {
	it('somebody Immich has no name for, is left out, as is a name of spaces alone', () => {
		// Arrange: Immich names a person it has found no name for with the empty string.
		const people = [{ name: '' }, { name: 'Ada' }, { name: '  ' }, { name: 'Grace' }];

		// Act / Assert
		expect(namedPeople(people)).toEqual(['Ada', 'Grace']);
	});

	it('a tag named with spaces alone, is left out', () => {
		// Act / Assert
		expect(namedTags([{ name: 'Beach' }, { name: ' ' }, { name: '' }])).toEqual(['Beach']);
	});

	it.each([
		{ none: 'null', list: null },
		{ none: 'undefined', list: undefined }
	])('$none, is nobody and no tags', ({ list }) => {
		// Act / Assert
		expect([namedPeople(list), namedTags(list)]).toEqual([[], []]);
	});
});
