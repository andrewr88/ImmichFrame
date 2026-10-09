import type { IWeather } from '$lib/immichFrameApi';

/**
 * What the clock shows for the weather once the server has answered, given what it shows now - and
 * what to warn about, when the answer is not one the server gives.
 *
 * A 204 is the server saying there is no weather: no API key is set, the coordinates are not
 * usable, or OpenWeatherMap could not be had. getWeather declares only the 200, so the clock took a
 * 204 for something unexpected and warned about it - on mount, and every ten minutes after, on every
 * frame that never asked for weather - and kept whatever weather it already showed up on screen for
 * as long as an outage lasted. A 204 clears it instead, without a word. The server holds on to its
 * answer for five minutes, so the weather comes back by itself once OpenWeatherMap answers again.
 *
 * Anything else - a 500, a proxy's error page, a 200 that is not weather - is not the server saying
 * there is none, so the weather shown stays up and the console says why. Kept rather than cleared on
 * purpose: one failed refresh is no reason to blank the weather for the ten minutes until the next.
 */
export function weatherAfter(
	response: { status: number; data: unknown },
	shown: IWeather | null
): { weather: IWeather | null; warning?: string } {
	// Only 200 is declared, and only weather for it, hence the widening of both.
	const status: number = response.status;
	const data: unknown = response.data;

	if (status === 200 && typeof data === 'object' && data !== null && !Array.isArray(data)) {
		return { weather: data as IWeather };
	}

	if (status === 204) {
		return { weather: null };
	}

	return { weather: shown, warning: `Unexpected weather response: status ${status}` };
}
