import type { AssetFaceResponseDto } from '$lib/immichFrameApi';

/** How far a pan moves the photo either way, as a fraction of the frame. */
export const PAN_OFFSET = 0.05;

/** The scale a pan alone holds the photo at, so that the pan does not uncover the frame. */
export const PAN_SCALE = 1.1;

/** The scales a zoom alone runs between, in either direction. */
export const ZOOM_SCALES = { min: 1, max: 1.3 };

/** The scales a zoom runs between with a pan as well, never small enough for the pan to show. */
export const ZOOM_PAN_SCALES = { min: 1.15, max: 1.35 };

/**
 * Where a zoom closes in on a photo, in percent across and down: the middle of the first named face
 * with a box to go by - or null when there is none, and the zoom closes in on the middle.
 *
 * Only a face says where in the photo somebody is. Whether Immich has named anyone in the photo
 * says nothing of where, and going by that alone put the origin at 0% 0% whenever no named face was
 * loaded - the faces are not loaded at all for an API key without the face scope - so every such
 * photo zoomed into its top left corner.
 *
 * With a pan as well, the origin is kept clear of the edges. About an origin o a scale s keeps the
 * photo's edges o * (s - 1) and (1 - o) * (s - 1) beyond the frame's, and a pan moves them by
 * PAN_OFFSET either way, so o has to be PAN_OFFSET / (s - 1) or more from both edges at the
 * smallest scale the zoom passes through - about a third of the way in. A face nearer an edge than
 * that was zoomed about where it was, and the pan showed the blurred backdrop past the photo's edge.
 */
export function zoomOrigin(
	faces: AssetFaceResponseDto[],
	pan: boolean
): { x: number; y: number } | null {
	// loadAsset already sees to it that this is a list. Checked again all the same, as a throw from
	// here halts the update that brings the photo in.
	const list: AssetFaceResponseDto[] = Array.isArray(faces) ? faces : [];

	const margin = pan ? PAN_OFFSET / (ZOOM_PAN_SCALES.min - 1) : 0;
	const clamp = (fraction: number) => Math.min(Math.max(fraction, margin), 1 - margin) * 100;

	for (const face of list) {
		if (!face?.person?.name) continue;

		const centre = boxCentre(face);
		if (centre) return { x: clamp(centre.x), y: clamp(centre.y) };
	}

	return null;
}

/** The middle of a face's box, as a fraction of the photo - or null with no box or size to go by. */
function boxCentre({
	boundingBoxX1: x1,
	boundingBoxX2: x2,
	boundingBoxY1: y1,
	boundingBoxY2: y2,
	imageWidth: width,
	imageHeight: height
}: AssetFaceResponseDto) {
	if (typeof x1 !== 'number' || typeof x2 !== 'number') return null;
	if (typeof y1 !== 'number' || typeof y2 !== 'number') return null;
	if (typeof width !== 'number' || typeof height !== 'number' || width <= 0 || height <= 0) {
		return null;
	}

	return { x: (x1 + (x2 - x1) / 2) / width, y: (y1 + (y2 - y1) / 2) / height };
}
