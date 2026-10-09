import { describe, expect, it } from 'vitest';
import type { AssetFaceResponseDto } from '$lib/immichFrameApi';
import { PAN_OFFSET, ZOOM_PAN_SCALES, ZOOM_SCALES, zoomOrigin } from './asset';

/** A face of Ada's in a 1000 x 800 photo, its box centred `x` of the way across and `y` down. */
function adaAt(
	x: number,
	y: number,
	face: Partial<AssetFaceResponseDto> = {}
): AssetFaceResponseDto {
	return {
		id: 'face-1',
		person: { id: 'person-1', name: 'Ada', thumbnailPath: '' },
		imageWidth: 1000,
		imageHeight: 800,
		boundingBoxX1: x * 1000 - 50,
		boundingBoxX2: x * 1000 + 50,
		boundingBoxY1: y * 800 - 40,
		boundingBoxY2: y * 800 + 40,
		...face
	};
}

/**
 * Whether a photo still covers its frame along one axis, scaled by `scale` about `origin` and moved
 * by `offset` - each a fraction of the frame, as transform-origin and translate take them.
 *
 * About origin o, a point p of the photo lands at o + offset + scale * (p - o): its near edge at
 * offset - o * (scale - 1), and its far edge at 1 + offset + (1 - o) * (scale - 1). The photo
 * covers the frame while the one is at or before 0 and the other at or past 1. The animation moves
 * the offset and the scale together, each in a straight line from start to end, so a photo that
 * covers the frame at both ends covers it all the way. Within rounding, as the clamp puts an edge
 * exactly on the frame's - 1.15 - 1 is 0.1499999999999999 to a double.
 */
function coversFrame(origin: number, scale: number, offset: number) {
	const near = offset - origin * (scale - 1);
	const far = 1 + offset + (1 - origin) * (scale - 1);
	return near <= 1e-9 && far >= 1 - 1e-9;
}

describe('zoomOrigin: where a zoom closes in on a photo', () => {
	it.each([
		// What loadAsset hands over with zoom and pan both off, and whenever the server will not give
		// the faces up - for an API key without the face scope, say.
		{ faces: 'no faces at all', list: [] },
		{ faces: 'a face of nobody', list: [adaAt(0.25, 0.75, { person: undefined })] },
		{
			faces: 'a face of somebody nobody has named',
			list: [adaAt(0.25, 0.75, { person: { id: 'person-2', name: '', thumbnailPath: '' } })]
		}
	])('$faces, is no origin, and the zoom closes in on the middle', ({ list }) => {
		// Act / Assert: it was 0% 0%, the top left corner, whenever Immich had a name for anyone in
		// the photo and there was no named face to put the origin anywhere else.
		expect(zoomOrigin(list, false)).toBeNull();
	});

	it("a named face, is the middle of its box, in percent of the photo's width and height", () => {
		// Arrange: somebody nobody has named first, who is passed over.
		const faces = [adaAt(0.5, 0.5, { person: undefined }), adaAt(0.25, 0.75)];

		// Act / Assert
		expect(zoomOrigin(faces, false)).toEqual({ x: 25, y: 75 });
	});

	it.each([
		{ has: 'a width of 0', face: { imageWidth: 0 } },
		{ has: 'a height of 0', face: { imageHeight: 0 } },
		{ has: 'no width', face: { imageWidth: undefined } },
		{ has: 'no box', face: { boundingBoxX1: undefined, boundingBoxX2: undefined } }
	])('a named face with $has, is no origin', ({ face }) => {
		// Act / Assert: a missing width was taken for 1 pixel, which put the origin thousands of
		// percent off the photo, and a width of 0 was divided by.
		expect(zoomOrigin([adaAt(0.25, 0.75, face)], false)).toBeNull();
	});

	it('with a pan as well, a face near an edge, is drawn in from it', () => {
		// Arrange: Ada near the left edge and near the bottom.
		const faces = [adaAt(0.1, 0.95)];

		// Act
		const panning = zoomOrigin(faces, true);
		const zooming = zoomOrigin(faces, false);

		// Assert: a third of the way in from the edge, where no 5% pan can uncover the frame at the
		// smaller scale of 1.15. Without a pan she is zoomed into where she is.
		expect(panning?.x).toBeCloseTo(100 / 3);
		expect(panning?.y).toBeCloseTo(200 / 3);
		expect(zooming).toEqual({ x: 10, y: 95 });
	});
});

describe('coversFrame: the photo against its frame, as the slideshow zooms and pans it', () => {
	it.each([
		{ origin: 0.2, scale: 1.15, offset: 0.05 },
		{ origin: 0.9, scale: 1.15, offset: -0.05 },
		{ origin: 0, scale: 1.15, offset: 0.05 },
		{ origin: 0, scale: 1.35, offset: 0.05 }
	])(
		'an origin of $origin, scaled $scale and moved $offset, uncovers the frame',
		({ origin, scale, offset }) => {
			// Act / Assert: the blurred backdrop showed past the photo's edge, which is what the
			// origin was zoomed about with a pan before it was clamped.
			expect(coversFrame(origin, scale, offset)).toBe(false);
		}
	);

	// Faces from edge to edge, at the start and end scales of a zoom in or out, at either end of a
	// pan or across it - a pan moves the photo along one axis and leaves the other where it is.
	const motions = [
		{ pan: true, scales: ZOOM_PAN_SCALES, offsets: [-PAN_OFFSET, 0, PAN_OFFSET] },
		{ pan: false, scales: ZOOM_SCALES, offsets: [0] }
	].flatMap(({ pan, scales, offsets }) =>
		[0, 0.2, 0.5, 0.9, 1].flatMap((at) =>
			[scales.min, scales.max].flatMap((scale) =>
				offsets.map((offset) => ({ pan, at, scale, offset }))
			)
		)
	);

	it.each(motions)(
		'pan $pan, a face at $at, scaled $scale and moved $offset, leaves the frame covered',
		({ pan, at, scale, offset }) => {
			// Act
			const origin = zoomOrigin([adaAt(at, at)], pan);

			// Assert
			expect(origin).not.toBeNull();
			expect(coversFrame(origin!.x / 100, scale, offset)).toBe(true);
			expect(coversFrame(origin!.y / 100, scale, offset)).toBe(true);
		}
	);
});
