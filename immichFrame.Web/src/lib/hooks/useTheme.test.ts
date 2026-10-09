import { afterEach, describe, expect, it, vi } from 'vitest';
import { applyFrameColors } from './useTheme';

afterEach(() => {
	vi.unstubAllGlobals();
});

/**
 * A `document` whose root element has an inline style, which is all applyFrameColors touches - the
 * suite has no DOM. Its setProperty does as a browser's does: null and '' remove the property, and
 * any other value is stored as text, `undefined` as 'undefined'.
 */
function installRootStyle() {
	const style = new Map<string, string>();
	vi.stubGlobal('document', {
		documentElement: {
			style: {
				setProperty: (name: string, value: unknown) => {
					if (value === null || value === '') style.delete(name);
					else style.set(name, String(value));
				},
				removeProperty: (name: string) => style.delete(name)
			}
		}
	});
	return style;
}

describe('applyFrameColors', () => {
	it.each([
		{ given: 'a primary colour', config: { primaryColor: '#ff0000' }, primary: '#ff0000' },
		// What a configuration without the setting has. Set, it would be 'undefined', which as an
		// inline value overrides app.css's default with no colour at all.
		{ given: 'no primary colour', config: {}, primary: undefined }
	])('$given in the configuration, is what the root has for one', ({ config, primary }) => {
		// Arrange
		const style = installRootStyle();

		// Act
		applyFrameColors(config);

		// Assert
		expect(style.get('--primary-color')).toBe(primary);
	});

	it("its cleanup, takes the colours off the root, so the admin UI keeps @immich/ui's look", () => {
		// Arrange
		const style = installRootStyle();
		const cleanup = applyFrameColors({ primaryColor: '#ff0000', secondaryColor: '#00ff00' });

		// Act
		cleanup();

		// Assert
		expect(Object.fromEntries(style)).toEqual({});
	});
});
