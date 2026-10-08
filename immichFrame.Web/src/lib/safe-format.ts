import { format, isValid, type FormatOptions } from 'date-fns';

/** Patterns date-fns has refused, each warned about once and never tried again. */
const refused = new Set<string>();

/**
 * date-fns's `format`, for a pattern an administrator typed rather than one written here.
 *
 * ClockFormat, ClockDateFormat and PhotoDateFormat are free text, and date-fns throws on the commonest
 * mistakes - Moment.js's `DD.MM.YYYY`, or `h:mm A` - rather than printing something wrong. Thrown
 * from a template, that took down everything around it: a bad photo date format kept the photo itself
 * off the screen, slide after slide. So a pattern that is missing, empty or refused is formatted with
 * `fallback` instead, which is the default the caller would have used had none been set.
 *
 * A refused pattern is warned about the first time and not tried again, as it can only be refused
 * again: the clock formats every second, and date-fns warns about some of these itself before it
 * throws. That is sound because the locale is the only option taken, and no refusal depends on it -
 * the two options that do decide one, `useAdditionalWeekYearTokens` and
 * `useAdditionalDayOfYearTokens`, are not accepted here.
 *
 * Only the pattern is forgiven. An invalid date is refused before the pattern is even read, so it
 * says nothing about the pattern, and the fallback cannot format it either: it throws, as `format`
 * always has.
 */
export function safeFormat(
	date: Date,
	pattern: string | null | undefined,
	fallback: string,
	options?: Pick<FormatOptions, 'locale'>
): string {
	if (pattern && !refused.has(pattern)) {
		try {
			return format(date, pattern, options);
		} catch (error) {
			if (!isValid(date)) throw error;

			refused.add(pattern);
			console.warn(`Date format '${pattern}' cannot be used, falling back to the default:`, error);
		}
	}

	return format(date, fallback, options);
}
