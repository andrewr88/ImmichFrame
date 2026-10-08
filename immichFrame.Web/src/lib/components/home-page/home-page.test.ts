import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import * as api from '$lib/immichFrameApi';
import { isBirthdayToday, okArray } from './home-page';

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
