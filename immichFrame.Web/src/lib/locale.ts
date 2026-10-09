import * as locales from 'date-fns/locale';
import type { Locale } from 'date-fns/locale';

/**
 * date-fns's locales under their export names in lower case. An export name is the locale's
 * BCP 47 code with the separator dropped - `ptBR` for pt-BR, `srLatn` for sr-Latn - so a code
 * written in any case, with `-` or `_`, is found here once lower-cased and without its separator.
 */
const byName = new Map<string, Locale>(
	Object.entries(locales).map(([name, locale]) => [name.toLowerCase(), locale])
);

// ISO 639-1 codes that date-fns has the language for, under other names only: Chinese as `zhCN`,
// `zhTW` and `zhHK`, Persian as `faIR`, and Norwegian as `nb` (Bokmål) and `nn` (Nynorsk). Each
// gets the form most of its readers write. English needs none - English (US) is the fallback.
byName.set('zh', locales.zhCN);
byName.set('no', locales.nb);
byName.set('fa', locales.faIR);

/**
 * The date-fns locale for the frame's language: an ISO 639-1 code (`de`), one with a region
 * (`pt-BR`, `pt_BR` and `PT-br` alike) or a date-fns export name (`ptBR`). A region date-fns has
 * no locale for falls back to the language alone - `de-CH` is German - and English (US) stands in
 * when there is no language, or none date-fns knows - `en` among them, as date-fns has only `enUS`,
 * `enGB` and the like.
 *
 * The one place the language is read, so that the clock, the photo date and the appointments all
 * have their dates in the same one. The appointments had theirs in English whatever it was.
 */
export function resolveLocale(language: string | null | undefined): Locale {
	const code = language?.toLowerCase() ?? '';
	const [bare] = code.split(/[-_]/);

	return byName.get(code.replace(/[-_]/g, '')) ?? byName.get(bare) ?? locales.enUS;
}
