import { describe, expect, it } from 'vitest';
import { reloadSchedule } from './load-config';

describe('reloadSchedule: the error page reloading itself', () => {
	it.each([500, 502, 503])(
		'a %i, is retried every 30 seconds, as the slideshow retries its own errors',
		(status) => {
			// Assert: the delay, and the words the page uses for it, agree.
			expect(reloadSchedule(status)).toEqual({ ms: 30 * 1000, every: '30 seconds' });
		}
	);

	it('a 404, is still retried, but not at that pace', () => {
		// Assert: an unknown profile can be added without a restart, so the page does not give up
		// on it - but until someone does, every attempt is a warning in the server's log.
		expect(reloadSchedule(404)).toEqual({ ms: 5 * 60 * 1000, every: '5 minutes' });
	});
});
