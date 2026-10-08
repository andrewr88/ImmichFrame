import { de } from 'date-fns/locale';
import { afterEach, describe, expect, it, vi } from 'vitest';

// safeFormat remembers the patterns date-fns has refused for as long as the module lives, so every
// test imports it afresh - afterEach resets the module registry - and starts out remembering none.
afterEach(() => {
	vi.restoreAllMocks();
	vi.resetModules();
});

async function importSafeFormat() {
	return (await import('./safe-format')).safeFormat;
}

/** Thursday 8 October 2026, 14:05:09 local time. */
const DATE = new Date(2026, 9, 8, 14, 5, 9);

describe('safeFormat', () => {
	it.each([
		// Moment.js and .NET spellings, the mistakes an administrator is likeliest to type. date-fns
		// throws on each one rather than printing something wrong.
		{ pattern: 'DD.MM.YYYY', fallback: 'dd.MM.yyyy', shown: '08.10.2026' },
		{ pattern: 'dd.MM.YYYY', fallback: 'dd.MM.yyyy', shown: '08.10.2026' },
		{ pattern: 'YYYY-MM-dd', fallback: 'dd.MM.yyyy', shown: '08.10.2026' },
		{ pattern: 'ddd, MMM D', fallback: 'eee, MMM d', shown: 'Thu, Oct 8' },
		{ pattern: 'h:mm A', fallback: 'HH:mm', shown: '14:05' }
	])(
		"'$pattern', which date-fns refuses, falls back to '$fallback' and says why",
		async ({ pattern, fallback, shown }) => {
			// Arrange
			const safeFormat = await importSafeFormat();
			const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});

			// Act
			const formatted = safeFormat(DATE, pattern, fallback);

			// Assert
			expect(formatted).toBe(shown);
			expect(warn).toHaveBeenCalledWith(
				expect.stringContaining(`'${pattern}'`),
				expect.any(RangeError)
			);
		}
	);

	it.each([
		// What an emptied box in the editor saves. date-fns fails on it with a TypeError.
		{ unset: 'the empty string', pattern: '' },
		{ unset: 'null', pattern: null },
		{ unset: 'undefined', pattern: undefined }
	])(
		'$unset, is no pattern at all, and the fallback is used without a warning',
		async ({ pattern }) => {
			// Arrange
			const safeFormat = await importSafeFormat();
			const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});

			// Act / Assert
			expect(safeFormat(DATE, pattern, 'HH:mm')).toBe('14:05');
			expect(warn).not.toHaveBeenCalled();
		}
	);

	it('a pattern date-fns accepts, is used as given, quoted literal text and all', async () => {
		// Arrange
		const safeFormat = await importSafeFormat();

		// Act / Assert
		expect(safeFormat(DATE, "dd.MM.yyyy 'at' HH:mm", 'dd.MM.yyyy')).toBe('08.10.2026 at 14:05');
	});

	it('the locale, is applied to the fallback as well as to the pattern', async () => {
		// Arrange
		const safeFormat = await importSafeFormat();
		vi.spyOn(console, 'warn').mockImplementation(() => {});

		// Act
		const accepted = safeFormat(DATE, 'eeee', 'eee, MMM d', { locale: de });
		const refused = safeFormat(DATE, 'ddd, MMM D', 'eeee', { locale: de });

		// Assert
		expect([accepted, refused]).toEqual(['Donnerstag', 'Donnerstag']);
	});

	it('a refused pattern, is warned about once, however often it is formatted', async () => {
		// Arrange
		const safeFormat = await importSafeFormat();
		const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});

		// Act: two bad patterns, formatted once a second as the clock formats its own.
		safeFormat(DATE, 'h:mm A', 'HH:mm');
		safeFormat(DATE, 'DD.MM.YYYY', 'dd.MM.yyyy');
		const firstSecond = warn.mock.calls.length;

		for (let second = 0; second < 5; second++) {
			safeFormat(DATE, 'h:mm A', 'HH:mm');
			safeFormat(DATE, 'DD.MM.YYYY', 'dd.MM.yyyy');
		}

		// Assert: each was named once, and nothing was warned about after that - not by date-fns
		// either, which warns about `DD` on its own each time it is asked to format it.
		expect(warn).toHaveBeenCalledWith(expect.stringContaining("'h:mm A'"), expect.any(RangeError));
		expect(warn).toHaveBeenCalledWith(
			expect.stringContaining("'DD.MM.YYYY'"),
			expect.any(RangeError)
		);
		expect(warn).toHaveBeenCalledTimes(firstSecond);
	});

	it('an invalid date, still throws, and is not held against the pattern', async () => {
		// Arrange
		const safeFormat = await importSafeFormat();
		const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});

		// Act / Assert: date-fns refuses an invalid date before it reads the pattern, and the
		// fallback cannot format one either.
		expect(() => safeFormat(new Date(Number.NaN), 'HH:mm:ss', 'HH:mm')).toThrow(RangeError);

		// Assert: so the next valid date is still formatted with the pattern, and nobody was told
		// it was bad.
		expect(safeFormat(DATE, 'HH:mm:ss', 'HH:mm')).toBe('14:05:09');
		expect(warn).not.toHaveBeenCalled();
	});
});
