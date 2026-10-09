import { describe, expect, it } from 'vitest';
import { matchShortcut } from './overlay-controls';

/** A key pressed once, with no modifier held - or as `held` says. */
function press(
	key: string,
	held: { repeat?: boolean; altKey?: boolean; ctrlKey?: boolean; metaKey?: boolean } = {}
) {
	return { key, repeat: false, altKey: false, ctrlKey: false, metaKey: false, ...held };
}

describe('matchShortcut: what a key press is to the slideshow', () => {
	it.each([
		{ key: 'ArrowRight', shortcut: 'next' },
		{ key: 'ArrowLeft', shortcut: 'back' },
		{ key: ' ', shortcut: 'pause' },
		{ key: 'i', shortcut: 'info' }
	])("'$key', is $shortcut, and is acted on", ({ key, shortcut }) => {
		// Act / Assert
		expect(matchShortcut(press(key))).toEqual({ shortcut, act: true });
	});

	it.each([
		{ key: ' ', shortcut: 'pause' },
		{ key: 'i', shortcut: 'info' }
	])(
		"'$key' held down, is still $shortcut, but its repeats are not acted on",
		({ key, shortcut }) => {
			// Act / Assert: each repeat toggled it again, so holding Space paused and resumed the
			// slideshow over and over. Still the slideshow's key, so the browser does not act on it
			// either.
			expect(matchShortcut(press(key, { repeat: true }))).toEqual({ shortcut, act: false });
		}
	);

	it.each([
		{ key: 'ArrowRight', shortcut: 'next' },
		{ key: 'ArrowLeft', shortcut: 'back' }
	])("'$key' held down, goes on to the $shortcut photo with every repeat", ({ key, shortcut }) => {
		// Act / Assert
		expect(matchShortcut(press(key, { repeat: true }))).toEqual({ shortcut, act: true });
	});

	it.each([
		{ held: 'Alt', modifier: { altKey: true } },
		{ held: 'Ctrl', modifier: { ctrlKey: true } },
		{ held: 'Meta', modifier: { metaKey: true } }
	])("with $held held, no key is the slideshow's, held down or not", ({ modifier }) => {
		// Act / Assert: each is left to the browser - Alt+ArrowLeft is its Back.
		for (const key of ['ArrowRight', 'ArrowLeft', ' ', 'i']) {
			expect(matchShortcut(press(key, modifier))).toBeNull();
			expect(matchShortcut(press(key, { ...modifier, repeat: true }))).toBeNull();
		}
	});

	it.each(['Enter', 'ArrowUp', 'Escape'])("'%s', is not the slideshow's", (key) => {
		// Act / Assert
		expect(matchShortcut(press(key))).toBeNull();
	});
});
