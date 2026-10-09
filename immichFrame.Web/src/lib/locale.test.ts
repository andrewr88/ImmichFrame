import { describe, expect, it } from 'vitest';
import { resolveLocale } from './locale';

describe('resolveLocale', () => {
	it.each([
		// An ISO 639-1 code, and a date-fns export name.
		{ language: 'de', code: 'de' },
		{ language: 'ptBR', code: 'pt-BR' },
		// ISO 639-1 codes that date-fns has the language for, but under other names only.
		{ language: 'zh', code: 'zh-CN' },
		{ language: 'no', code: 'nb' },
		{ language: 'fa', code: 'fa-IR' },
		// A code with a region, written either way and in any case - OpenWeatherMap, which the
		// setting is also passed to, writes its own as `pt_br`.
		{ language: 'pt-BR', code: 'pt-BR' },
		{ language: 'pt_BR', code: 'pt-BR' },
		{ language: 'PT-br', code: 'pt-BR' },
		{ language: 'zh-TW', code: 'zh-TW' },
		{ language: 'sr-Latn', code: 'sr-Latn' },
		// A region date-fns has no locale for, falls back to the language alone.
		{ language: 'de-CH', code: 'de' },
		{ language: 'no-NO', code: 'nb' },
		// English (US) for no language, and for one date-fns has never heard of - `en` among them, as
		// date-fns has only `enUS`, `enGB` and the like.
		{ language: undefined, code: 'en-US' },
		{ language: null, code: 'en-US' },
		{ language: '', code: 'en-US' },
		{ language: 'en', code: 'en-US' },
		{ language: 'tlh', code: 'en-US' },
		{ language: 'tlh-QO', code: 'en-US' }
	])("the language '$language', is date-fns's $code", ({ language, code }) => {
		// Act
		const locale = resolveLocale(language);

		// Assert
		expect(locale.code).toBe(code);
	});
});
