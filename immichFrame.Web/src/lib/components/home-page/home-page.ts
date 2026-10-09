import type { AssetResponseDto } from '$lib/immichFrameApi';
import { isImageAsset, isVideoAsset } from '$lib/constants/asset-type';

/**
 * Whether a person's birthday is today, by the calendar where the frame hangs.
 *
 * Immich keeps a birthday as a date alone, but the server passes it on as midnight at its own
 * offset - "1990-10-08T00:00:00+01:00" from a container running in Europe/Berlin. Read as a moment,
 * that midnight is still the 7th anywhere west of the server, so a frame in New York threw the
 * confetti a day early and not at all on the day. The date is read as it was written instead, and
 * only today is read in the frame's own zone.
 */
export function isBirthdayToday(
	birthDate: string | null | undefined,
	today: Date = new Date()
): boolean {
	const written = /^\d{4}-(\d{2})-(\d{2})/.exec(birthDate ?? '');
	if (!written) return false;

	return Number(written[1]) === today.getMonth() + 1 && Number(written[2]) === today.getDate();
}

/**
 * The list a response carries for one of a photo's extras - its albums, its faces - or an empty
 * one, with a warning that says which and why, when it carries none.
 *
 * oazapfts does not throw for a status the spec does not declare: it hands back whatever came, as
 * text when it is not JSON. AssetController answers an upstream failure with a 500 and no body -
 * every time, for an API key without the album or face scope - so loadAsset was handed '', or a
 * proxy's error page, as the list it asked for. Drawing the photo threw on the first `.filter` or
 * `.map` of it, which halted the update bringing it in, and nothing reported the asset as failed.
 * These are extras, so the photo goes up without them instead.
 */
export function okArray<T>(response: { status: number; data: T[] }, what: string, id: string): T[] {
	// Only 200 is declared, and only a list for it, hence the widening of both.
	const status: number = response.status;
	const data: unknown = response.data;

	if (status === 200 && Array.isArray(data)) return data;

	console.warn(`Failed to load ${what} for asset ${id}: status ${status}`);
	return [];
}

/** How many slides Back can go back through. Past that the oldest are let go. */
export const HISTORY_LIMIT = 250;

/**
 * Whether a photo stands taller than it is wide, as it is shown. A video is never one, as a video is
 * never paired.
 */
function isPortrait(asset: AssetResponseDto): boolean {
	if (!isImageAsset(asset)) return false;

	let height = asset.exifInfo?.exifImageHeight ?? 0;
	let width = asset.exifInfo?.exifImageWidth ?? 0;
	// EXIF orientations 5 to 8 turn the photo a quarter, so the width it gives is the height the photo
	// is shown at. The server sends the orientation as text, and text that is no number turns nothing.
	if ([5, 6, 7, 8].includes(Number(asset.exifInfo?.orientation ?? 0))) {
		[height, width] = [width, height];
	}
	return height > width;
}

/**
 * The slide a step forward puts up, off the front of the backlog, and what is left of the backlog
 * after it.
 *
 * In the split view layout two portraits in a row go up side by side, as one slide. Anything else
 * goes up alone: a landscape, a video, and a portrait with no portrait after it to pair with.
 */
export function takeNext(
	backlog: readonly AssetResponseDto[],
	layout: string | null | undefined
): { group: AssetResponseDto[]; backlog: AssetResponseDto[] } {
	const paired =
		layout?.trim().toLowerCase() === 'splitview' &&
		backlog.length > 1 &&
		isPortrait(backlog[0]) &&
		isPortrait(backlog[1]);
	const size = paired ? 2 : 1;

	return { group: backlog.slice(0, size), backlog: backlog.slice(size) };
}

/**
 * The slide a step back puts up again, as it went up, and what is left of history before it - or
 * null at the start of history, where there is nothing to go back to.
 *
 * History keeps each slide as it went up, rather than its assets one after another for Back to pair
 * up again, which paired them differently whenever a run of portraits had one over: in split view
 * three portraits and a landscape go up as the first two side by side, the third alone and then the
 * landscape, and Back from the landscape put the second and third up side by side, then the first.
 */
export function takePrevious(
	history: readonly AssetResponseDto[][]
): { group: AssetResponseDto[]; history: AssetResponseDto[][] } | null {
	if (!history.length) return null;

	return { group: history[history.length - 1], history: history.slice(0, -1) };
}

/**
 * History with the slide just gone by added to it, as it went up - and only the last HISTORY_LIMIT
 * slides, so that a frame left running does not hold on to everything it has shown. A pair is one
 * slide.
 */
export function recordShown(
	history: readonly AssetResponseDto[][],
	group: AssetResponseDto[]
): AssetResponseDto[][] {
	return [...history, group].slice(-HISTORY_LIMIT);
}

/**
 * How long a slide stays up, in seconds: for as long as its longest video runs, and for the interval
 * - 20 seconds when none is set - for a photo, a video of no known length, or a slide of nothing.
 */
export function slideDuration(
	assets: readonly AssetResponseDto[],
	interval: number | undefined
): number {
	const fallback = interval ?? 20;
	const durations = assets
		.map((asset) =>
			// The server sends a video's length in milliseconds.
			isVideoAsset(asset) && asset.duration && asset.duration > 0 ? asset.duration / 1000 : fallback
		)
		.filter((seconds) => seconds > 0);

	return durations.length ? Math.max(...durations) : fallback;
}

/**
 * What of a slide loaded, in the slide's order: the half of a pair that loaded without the half
 * that did not, and nothing when none of it did. Each failure is logged, and the slideshow skips a
 * slide that comes to nothing.
 *
 * A load can fail - the server answers 404 for an asset it cannot place - and one failing failed
 * the whole slide: an error went up for as long as the slide was to stay up, and the half of a pair
 * that had loaded went with it.
 */
export async function whatLoaded<T>(
	assets: readonly AssetResponseDto[],
	load: (asset: AssetResponseDto) => Promise<T>
): Promise<T[]> {
	const results = await Promise.allSettled(assets.map((asset) => load(asset)));

	return results.flatMap((result, i) => {
		if (result.status === 'fulfilled') return [result.value];

		console.error(`Asset ${assets[i].id} failed to load, and is skipped:`, result.reason);
		return [];
	});
}

/**
 * Slides in a row that failed - would not load, or would not draw - and whether there have been more
 * than the limit, when the slideshow stops skipping them and puts an error up instead.
 *
 * Only a slide seen to work ends the run: an image that has loaded, a video that is playing. The
 * slideshow used to start the count over at every step, before the slide the step put up had loaded
 * or failed, so it never got past one - and a library none of which would load was skipped through
 * for good, the server asked for one list of assets after another.
 */
export class FailureStreak {
	private readonly limit: number;
	private failures = 0;

	constructor(limit: number) {
		this.limit = limit;
	}

	/** Counts another failure in a row: whether that makes more than the limit. */
	recordFailure(): boolean {
		this.failures++;
		return this.failures > this.limit;
	}

	/** Ends the run, as a slide has worked. */
	recordSuccess(): void {
		this.failures = 0;
	}
}
