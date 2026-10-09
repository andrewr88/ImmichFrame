import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import * as api from '$lib/immichFrameApi';
import {
	FailureStreak,
	HISTORY_LIMIT,
	isBirthdayToday,
	okArray,
	recordShown,
	slideDuration,
	takeNext,
	takePrevious,
	whatLoaded
} from './home-page';

/** An asset of the kind `type`, with nothing to it but what the slideshow goes by, and `extra`. */
function asset(
	id: string,
	type: api.AssetTypeEnum,
	extra: Partial<api.AssetResponseDto> = {}
): api.AssetResponseDto {
	return {
		id,
		type,
		checksum: '',
		createdAt: '',
		fileCreatedAt: '',
		fileModifiedAt: '',
		localDateTime: '',
		originalFileName: id,
		originalPath: '',
		ownerId: 'owner-1',
		updatedAt: '',
		visibility: api.AssetVisibility.Timeline,
		...extra
	};
}

/** A photo `width` across and `height` down, as its EXIF has them, turned as `orientation` says. */
function photo(id: string, width: number, height: number, orientation: string | null = null) {
	return asset(id, api.AssetTypeEnum.Image, {
		exifInfo: { exifImageWidth: width, exifImageHeight: height, orientation }
	});
}

const portrait = (id: string) => photo(id, 3000, 4000);
const landscape = (id: string) => photo(id, 4000, 3000);

/** A video `duration` milliseconds long, as the server sends it, and standing upright. */
function video(id: string, duration: number | null) {
	return asset(id, api.AssetTypeEnum.Video, {
		duration,
		exifInfo: { exifImageWidth: 1080, exifImageHeight: 1920 }
	});
}

describe('isBirthdayToday: a frame in New York, west of the server sending the birthdays', () => {
	beforeEach(() => {
		// The frame's zone is the browser's. A TZ set at runtime takes in vitest's default forks pool,
		// where each test file has a process of its own, and not in its threads pool - hence the
		// first test, which fails rather than let the rest pass in whatever zone the suite runs in.
		vi.stubEnv('TZ', 'America/New_York');
	});

	afterEach(() => {
		vi.unstubAllEnvs();
	});

	/** Thursday 8 October 2026, mid-morning in New York - built once the zone is set. */
	const october8 = () => new Date(2026, 9, 8, 9, 30);

	it("the server's midnight, read as a moment, is the evening before in New York", () => {
		// Assert: the zone took, so the cases below are read as a frame there reads them.
		expect(new Date('1990-10-08T00:00:00+00:00').getDate()).toBe(7);
	});

	it.each([
		// Immich keeps a birthday as a date alone; the server sends it on as midnight at the offset
		// of whatever zone its container runs in.
		{ sent: 'midnight in UTC', birthDate: '1990-10-08T00:00:00+00:00' },
		{ sent: 'midnight in Berlin', birthDate: '1990-10-08T00:00:00+01:00' },
		{ sent: 'midnight in New York', birthDate: '1990-10-08T00:00:00-04:00' },
		{ sent: 'the date alone', birthDate: '1990-10-08' }
	])('8 October, sent as $sent, is a birthday on 8 October', ({ birthDate }) => {
		// Act / Assert: read as moments, all but New York's own midnight fell on the 7th here, and
		// there was no confetti on the day.
		expect(isBirthdayToday(birthDate, october8())).toBe(true);
	});

	it('9 October, sent as midnight in UTC, is no birthday on 8 October', () => {
		// Act / Assert: read as a moment, it is 8 p.m. on the 8th here - the confetti came a day early.
		expect(isBirthdayToday('1990-10-09T00:00:00+00:00', october8())).toBe(false);
	});

	it.each([
		{ unset: 'null', birthDate: null },
		{ unset: 'undefined', birthDate: undefined },
		{ unset: 'the empty string', birthDate: '' },
		{ unset: 'text that is not a date', birthDate: 'unknown' }
	])('$unset, is no birthday', ({ birthDate }) => {
		// Act / Assert
		expect(isBirthdayToday(birthDate, october8())).toBe(false);
	});
});

describe('okArray: the albums and faces loadAsset fetches beside a photo', () => {
	afterEach(() => {
		vi.restoreAllMocks();
	});

	/**
	 * The faces of asset-1, as the generated client hands them back when the server answers with
	 * this - through the client and its runtime rather than as a hand-made response, since what
	 * they make of a body that is not a list is the whole of the bug.
	 */
	function facesAnswered(status: number, body: string | null = null, contentType?: string) {
		return api.getAssetFaces(
			'asset-1',
			{},
			{
				fetch: async () =>
					new Response(body, {
						status,
						headers: contentType ? { 'Content-Type': contentType } : {}
					})
			}
		);
	}

	it.each([
		{
			// An upstream failure AssetController does not catch: no body and no content type, which
			// the generated client reads as ''.
			answer: 'a 500 with no body',
			status: 500,
			body: null
		},
		{
			answer: "a reverse proxy's 502 page",
			status: 502,
			body: '<html>Bad Gateway</html>',
			contentType: 'text/html'
		},
		{
			answer: 'a 500 problem+json',
			status: 500,
			body: JSON.stringify({ title: 'An error occurred', status: 500 }),
			contentType: 'application/problem+json'
		},
		{
			// An authenticating proxy's sign-in page, which the generated client hands back as text.
			answer: 'a 200 that is not a list',
			status: 200,
			body: '<html>Sign in</html>',
			contentType: 'text/html'
		}
	])('$answer, is no faces, and the console says why', async ({ status, body, contentType }) => {
		// Arrange
		const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});

		// Act
		const faces = okArray(await facesAnswered(status, body, contentType), 'faces', 'asset-1');

		// Assert: an empty list, which the photo goes up without. What came back was taken for the
		// list as it was, and the first `.filter` or `.map` on it threw.
		expect(faces).toEqual([]);
		expect(warn).toHaveBeenCalledWith(`Failed to load faces for asset asset-1: status ${status}`);
	});

	it('a 200 with a list, is that list, and nothing is said', async () => {
		// Arrange
		const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
		const face = { id: 'face-1', boundingBoxX1: 10, boundingBoxX2: 20, person: { name: 'Ada' } };

		// Act
		const faces = okArray(
			await facesAnswered(200, JSON.stringify([face]), 'application/json; charset=utf-8'),
			'faces',
			'asset-1'
		);

		// Assert
		expect(faces).toEqual([face]);
		expect(warn).not.toHaveBeenCalled();
	});
});

describe('takeNext: the slide a step forward puts up, off the front of the backlog', () => {
	it.each(['splitview', 'SplitView', ' splitView\t'])(
		"two portraits, in the layout '%s', go up side by side",
		(layout) => {
			// Arrange
			const backlog = [portrait('a'), portrait('b'), portrait('c')];

			// Act
			const next = takeNext(backlog, layout);

			// Assert: the third is left for the step after, and the backlog given is left as it was.
			expect(next.group).toEqual([backlog[0], backlog[1]]);
			expect(next.backlog).toEqual([backlog[2]]);
			expect(backlog).toHaveLength(3);
		}
	);

	it.each([
		{ layout: "'single'", value: 'single' },
		{ layout: "'split view'", value: 'split view' },
		{ layout: "'split-view'", value: 'split-view' },
		{ layout: 'null', value: null },
		{ layout: 'undefined', value: undefined }
	])('two portraits, in the layout $layout, go up one at a time', ({ value }) => {
		// Arrange
		const backlog = [portrait('a'), portrait('b')];

		// Act / Assert
		expect(takeNext(backlog, value).group).toEqual([backlog[0]]);
	});

	it.each([
		{
			slide: 'a portrait with a landscape after it',
			backlog: () => [portrait('a'), landscape('b')]
		},
		{ slide: 'a portrait with nothing after it', backlog: () => [portrait('a')] },
		{
			slide: 'a landscape with a portrait after it',
			backlog: () => [landscape('a'), portrait('b')]
		},
		{ slide: 'a square photo', backlog: () => [photo('a', 3000, 3000), portrait('b')] },
		{
			slide: 'a photo with no EXIF',
			backlog: () => [asset('a', api.AssetTypeEnum.Image), portrait('b')]
		},
		// A video is never paired, however it stands.
		{ slide: 'an upright video', backlog: () => [video('a', 5000), portrait('b')] },
		{
			slide: 'a portrait with an upright video after it',
			backlog: () => [portrait('a'), video('b', 5000)]
		}
	])('$slide, in split view, goes up alone', ({ backlog }) => {
		// Arrange
		const assets = backlog();

		// Act
		const next = takeNext(assets, 'splitview');

		// Assert
		expect(next.group).toEqual([assets[0]]);
		expect(next.backlog).toEqual(assets.slice(1));
	});

	it.each([
		// Orientations 5 to 8 turn the photo a quarter, so the width EXIF gives is the height it is
		// shown at, and the other way about. The server sends the orientation as text.
		{ stored: 'stored as a landscape', orientation: '5', width: 4000, height: 3000, pairs: true },
		{ stored: 'stored as a landscape', orientation: '6', width: 4000, height: 3000, pairs: true },
		{ stored: 'stored as a landscape', orientation: '7', width: 4000, height: 3000, pairs: true },
		{ stored: 'stored as a landscape', orientation: '8', width: 4000, height: 3000, pairs: true },
		{ stored: 'stored as a portrait', orientation: '6', width: 3000, height: 4000, pairs: false },
		{ stored: 'stored as a portrait', orientation: '1', width: 3000, height: 4000, pairs: true },
		{ stored: 'stored as a portrait', orientation: '3', width: 3000, height: 4000, pairs: true },
		{ stored: 'stored as a portrait', orientation: null, width: 3000, height: 4000, pairs: true },
		// Text that is not a number turns nothing.
		{
			stored: 'stored as a landscape',
			orientation: 'Rotate 90 CW',
			width: 4000,
			height: 3000,
			pairs: false
		},
		{
			stored: 'stored as a portrait',
			orientation: 'Rotate 90 CW',
			width: 3000,
			height: 4000,
			pairs: true
		}
	])(
		'a photo $stored, with the orientation $orientation, pairs with a portrait: $pairs',
		({ orientation, width, height, pairs }) => {
			// Arrange
			const backlog = [photo('a', width, height, orientation), portrait('b')];

			// Act / Assert
			expect(takeNext(backlog, 'splitview').group).toHaveLength(pairs ? 2 : 1);
		}
	);
});

describe('takePrevious and recordShown: the slides Back steps back through', () => {
	it('Back at the start of history, has nothing to go back to', () => {
		// Act / Assert
		expect(takePrevious([])).toBeNull();
	});

	it('Back puts the slides up again as they went up, pairs and all', () => {
		// Arrange: in split view, three portraits and a landscape go up as the first two side by
		// side, then the third alone - the landscape is no pair for it - then the landscape.
		const [a, b, c, d] = [portrait('a'), portrait('b'), portrait('c'), landscape('d')];
		let backlog: api.AssetResponseDto[] = [a, b, c, d];
		let history: api.AssetResponseDto[][] = [];
		let onScreen: api.AssetResponseDto[] = [];
		const wentUp: api.AssetResponseDto[][] = [];
		while (backlog.length) {
			if (onScreen.length) history = recordShown(history, onScreen);
			({ group: onScreen, backlog } = takeNext(backlog, 'splitview'));
			wentUp.push(onScreen);
		}

		// Act: Back from the landscape, for as long as there is anything to go back to.
		const wentBack: api.AssetResponseDto[][] = [];
		for (let back = takePrevious(history); back; back = takePrevious(back.history)) {
			wentBack.push(back.group);
		}

		// Assert: Back put the second and third up side by side, and then the first alone.
		expect(wentUp).toEqual([[a, b], [c], [d]]);
		expect(wentBack).toEqual([[c], [a, b]]);
	});

	it(`keeps the last ${HISTORY_LIMIT} slides, a pair counting as one`, () => {
		// Arrange: a pair more than that.
		const pairs = Array.from({ length: HISTORY_LIMIT + 1 }, (_, i) => [
			portrait(`${i}a`),
			portrait(`${i}b`)
		]);

		// Act
		let history: api.AssetResponseDto[][] = [];
		for (const pair of pairs) history = recordShown(history, pair);

		// Assert: the first pair is let go, and every other pair is kept whole.
		expect(history).toEqual(pairs.slice(1));
	});
});

describe('slideDuration: how long a slide stays up, in seconds', () => {
	it.each([
		{ slide: 'a photo', assets: () => [portrait('a')], seconds: 30 },
		{ slide: 'a pair of photos', assets: () => [portrait('a'), portrait('b')], seconds: 30 },
		{ slide: 'a video of 12.5 seconds', assets: () => [video('a', 12500)], seconds: 12.5 },
		{ slide: 'a video longer than the interval', assets: () => [video('a', 90000)], seconds: 90 },
		{ slide: 'a video of no length', assets: () => [video('a', 0)], seconds: 30 },
		{ slide: 'a video whose length is not known', assets: () => [video('a', null)], seconds: 30 },
		{
			slide: 'a video of 5 seconds with a photo',
			assets: () => [video('a', 5000), portrait('b')],
			seconds: 30
		},
		{ slide: 'two videos', assets: () => [video('a', 5000), video('b', 12500)], seconds: 12.5 },
		{ slide: 'nothing', assets: () => [], seconds: 30 }
	])('$slide, with an interval of 30, stays up for $seconds', ({ assets, seconds }) => {
		// Act / Assert
		expect(slideDuration(assets(), 30)).toBe(seconds);
	});

	it.each([
		{ slide: 'a photo', assets: () => [portrait('a')] },
		{ slide: 'nothing', assets: () => [] }
	])('$slide, with no interval set, stays up for 20', ({ assets }) => {
		// Act / Assert
		expect(slideDuration(assets(), undefined)).toBe(20);
	});
});

describe('whatLoaded: what of a slide loaded', () => {
	afterEach(() => {
		vi.restoreAllMocks();
	});

	/**
	 * Loads each asset as its id - or fails to, for the ids in `failing`, as the server's 404 for an
	 * asset it cannot place does.
	 */
	const loadAllBut =
		(...failing: string[]) =>
		async (asset: api.AssetResponseDto) => {
			if (failing.includes(asset.id)) {
				throw new Error(`Failed to load asset ${asset.id}: status 404`);
			}
			return asset.id;
		};

	it.each([
		{ failed: 'neither half', failing: [], loaded: ['a', 'b'] },
		{ failed: 'the first half', failing: ['a'], loaded: ['b'] },
		{ failed: 'the second half', failing: ['b'], loaded: ['a'] },
		{ failed: 'both halves', failing: ['a', 'b'], loaded: [] }
	])(
		'a pair of which $failed failed to load, is what of it did, and each failure is logged',
		async ({ failing, loaded }) => {
			// Arrange
			const error = vi.spyOn(console, 'error').mockImplementation(() => {});

			// Act
			const result = await whatLoaded([portrait('a'), portrait('b')], loadAllBut(...failing));

			// Assert: one failure took the whole pair with it, and put an error up for as long as
			// the pair was to stay up.
			expect(result).toEqual(loaded);
			expect(error).toHaveBeenCalledTimes(failing.length);
		}
	);
});

describe('FailureStreak: slides in a row that would not load or draw, before the slideshow gives up', () => {
	/** Ten failures in a row and then an eleventh: whether the streak said to give up at each. */
	const elevenFailures = (streak: FailureStreak) =>
		Array.from({ length: 11 }, () => streak.recordFailure());

	it('with a limit of 10, gives up at the eleventh in a row, and not before', () => {
		// Act / Assert
		expect(elevenFailures(new FailureStreak(10))).toEqual([...Array(10).fill(false), true]);
	});

	it('starts over once a slide has worked, and only then', () => {
		// Arrange: one short of giving up.
		const streak = new FailureStreak(10);
		for (let i = 0; i < 10; i++) streak.recordFailure();

		// Act
		streak.recordSuccess();

		// Assert: ten more to go before it gives up again.
		expect(elevenFailures(streak)).toEqual([...Array(10).fill(false), true]);
	});
});
