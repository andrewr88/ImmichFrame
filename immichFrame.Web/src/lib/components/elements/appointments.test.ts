import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { formatDates, loadAppointments } from './appointments';

// Local times, with no offset: the panel draws an appointment in the frame's own time zone, so
// these read the same whatever zone the suite runs in.
describe('formatDates', () => {
	it('an appointment that starts and ends on one day, shows only the times', () => {
		// Act
		const shown = formatDates('2026-10-08T09:00:00', '2026-10-08T10:30:00', {});

		// Assert
		expect(shown).toBe('09:00 - 10:30');
	});

	it('an appointment ending on the same day of a later month, shows both dates', () => {
		// Arrange: 8 October to 8 November - a month apart, on the same day of the month.
		const start = '2026-10-08T09:00:00';
		const end = '2026-11-08T10:30:00';

		// Act
		const shown = formatDates(start, end, {});

		// Assert: times alone would read as a ninety-minute appointment today.
		expect(shown).toBe('Thu, Oct 8 09:00 - Sun, Nov 8 10:30');
	});

	it('a clock format date-fns refuses, falls back to the default rather than emptying the panel', () => {
		// Arrange: a Moment.js time format beside a date format date-fns accepts.
		vi.spyOn(console, 'warn').mockImplementation(() => {});
		const settings = { clockFormat: 'h:mm A', clockDateFormat: 'd MMM' };

		// Act
		const oneDay = formatDates('2026-10-08T09:00:00', '2026-10-08T10:30:00', settings);
		const twoDays = formatDates('2026-10-08T09:00:00', '2026-10-09T10:30:00', settings);

		// Assert: the time falls back to its default on its own, and the date keeps its format.
		expect([oneDay, twoDays]).toEqual(['09:00 - 10:30', '8 Oct 09:00 - 9 Oct 10:30']);
	});

	it.each([
		{ language: 'de', shown: 'Fr., Okt. 9 10:00 - Sa., Okt. 10 11:30' },
		// date-fns has no `en` - only `enUS`, `enGB` and the like - and English (US) is what the
		// clock falls back on for it, for no language, and for one date-fns has never heard of.
		{ language: 'en', shown: 'Fri, Oct 9 10:00 - Sat, Oct 10 11:30' },
		{ language: '', shown: 'Fri, Oct 9 10:00 - Sat, Oct 10 11:30' },
		{ language: 'tlh', shown: 'Fri, Oct 9 10:00 - Sat, Oct 10 11:30' }
	])(
		"an appointment over two days, in the language '$language', has its dates as the clock does",
		({ language, shown }) => {
			// Act
			const formatted = formatDates('2026-10-09T10:00:00', '2026-10-10T11:30:00', { language });

			// Assert: the dates were in English whatever the language, under a clock in German.
			expect(formatted).toBe(shown);
		}
	);
});

const fetchMock = vi.fn<typeof fetch>();

beforeEach(() => {
	// fetch is stubbed rather than getAppointments mocked, as in routes/load.test.ts, so what the
	// generated client makes of each answer is under test as well.
	vi.stubGlobal('fetch', fetchMock);
});

afterEach(() => {
	vi.unstubAllGlobals();
	vi.restoreAllMocks();
	fetchMock.mockReset();
});

function answer(status: number, body: string, contentType: string) {
	fetchMock.mockImplementation(
		async () => new Response(body, { status, headers: { 'Content-Type': contentType } })
	);
}

describe('loadAppointments', () => {
	it('a 200, is the appointments soonest first, asked for on behalf of this frame and profile', async () => {
		// Arrange
		answer(
			200,
			JSON.stringify([
				{ summary: 'Dentist', startTime: '2026-10-08T15:00:00' },
				{ summary: 'Standup', startTime: '2026-10-08T09:00:00' }
			]),
			'application/json; charset=utf-8'
		);

		// Act
		const loaded = await loadAppointments('frame', 'kitchen');

		// Assert
		expect(loaded?.map((appointment) => appointment.summary)).toEqual(['Standup', 'Dentist']);
		expect(fetchMock).toHaveBeenCalledWith(
			'/api/Calendar?clientIdentifier=frame&profile=kitchen',
			expect.anything()
		);
	});

	it.each([
		{
			failure: 'the network is down',
			arrange: () => fetchMock.mockRejectedValue(new TypeError('Failed to fetch'))
		},
		{
			failure: "a reverse proxy's 502 page",
			arrange: () => answer(502, '<html>Bad Gateway</html>', 'text/html')
		},
		{
			// An authenticating proxy's sign-in page, which the generated client hands back as text.
			failure: 'a page that is not appointments, served with a 200',
			arrange: () => answer(200, '<html>Sign in</html>', 'text/html')
		}
	])('$failure, resolves to null rather than rejecting', async ({ arrange }) => {
		// Arrange
		arrange();
		vi.spyOn(console, 'warn').mockImplementation(() => {});
		vi.spyOn(console, 'error').mockImplementation(() => {});

		// Act / Assert: null is the panel's cue to keep the list it already shows. A rejection was
		// unhandled, every ten minutes for as long as the outage lasted.
		await expect(loadAppointments('frame', undefined)).resolves.toBeNull();
	});
});
