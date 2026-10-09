import * as locales from 'date-fns/locale';
import type { Locale } from 'date-fns/locale';

/**
 * The date-fns locale for the frame's language: English (US) when there is none, or none date-fns
 * knows by that name - `en` among them, as date-fns has only `enUS`, `enGB` and the like.
 *
 * The one place the language is read, so that the clock, the photo date and the appointments all
 * have their dates in the same one. The appointments had theirs in English whatever it was.
 */
export function resolveLocale(language: string | null | undefined): Locale {
	return (language && locales[language as keyof typeof locales]) || locales.enUS;
}
