/** A control of the slideshow's that a key stands in for. */
export type Shortcut = 'next' | 'back' | 'pause' | 'info';

const keys = new Map<string, Shortcut>([
	['ArrowRight', 'next'],
	['ArrowLeft', 'back'],
	[' ', 'pause'],
	['i', 'info']
]);

/**
 * The shortcuts that switch something on and off. A key held down repeats, and each repeat switched
 * it back: holding Space paused and resumed the slideshow over and over.
 */
const toggles: ReadonlySet<Shortcut> = new Set(['pause', 'info']);

/**
 * Which of the slideshow's shortcuts a key press is, and whether to act on it - or null when it is
 * not the slideshow's, and is left to the browser.
 *
 * A repeat of a toggle is not acted on, but is the slideshow's all the same, so that the browser
 * does not act on it either: a Space the browser saw would press whichever button had the focus once
 * let go - the pause button, after a click on it - and switch the slideshow straight back.
 */
export function matchShortcut(
	event: Pick<KeyboardEvent, 'key' | 'repeat' | 'altKey' | 'ctrlKey' | 'metaKey'>
): { shortcut: Shortcut; act: boolean } | null {
	// With Alt, Ctrl or Meta held the key is a browser shortcut, not the slideshow's -
	// Alt+ArrowLeft is Back - so it is left to the browser.
	if (event.altKey || event.ctrlKey || event.metaKey) return null;

	const shortcut = keys.get(event.key);
	if (!shortcut) return null;

	return { shortcut, act: !(event.repeat && toggles.has(shortcut)) };
}
