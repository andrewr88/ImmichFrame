import { writable } from 'svelte/store';

export const slideshowStore = {
	/**
	 * Whether the slide going up replaces the one on screen at once, rather than fading over it: so
	 * for a step asked for or a skip, and not for the progress bar running out.
	 */
	instantTransition: writable<boolean>(false)
};
