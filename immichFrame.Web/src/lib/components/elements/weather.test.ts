import { describe, expect, it } from 'vitest';
import * as api from '$lib/immichFrameApi';
import { weatherAfter } from './weather';

/** What the clock shows as each answer below arrives. */
const shown: api.IWeather = {
	location: 'Berlin',
	temperature: 12.5,
	description: 'Overcast clouds',
	iconId: '04d'
};

const fresh: api.IWeather = {
	location: 'Berlin',
	temperature: 14.2,
	description: 'Clear sky',
	iconId: '01d'
};

/**
 * The weather as the generated client hands it back when the server answers with this - through the
 * client and its runtime rather than as a hand-made response, since a 204 is a status the client
 * was never told it could get.
 */
function weatherAnswered(status: number, body: string | null = null, contentType?: string) {
	return api.getWeather(
		{},
		{
			fetch: async () =>
				new Response(body, {
					status,
					headers: contentType ? { 'Content-Type': contentType } : {}
				})
		}
	);
}

describe('weatherAfter: what the clock shows once the server has answered', () => {
	it.each([
		{
			answer: 'a 200 with weather',
			status: 200,
			body: JSON.stringify(fresh),
			contentType: 'application/json; charset=utf-8',
			outcome: 'is that weather',
			expected: { weather: fresh }
		},
		{
			// How WeatherController says there is no weather - no API key, coordinates it cannot
			// use, OpenWeatherMap not answering - with no body and no content type.
			answer: 'a 204',
			status: 204,
			body: null,
			outcome: 'is no weather, and nothing is said',
			expected: { weather: null }
		},
		{
			answer: 'a 500 with no body',
			status: 500,
			body: null,
			outcome: 'keeps the weather shown, and says why',
			expected: { weather: shown, warning: 'Unexpected weather response: status 500' }
		},
		{
			answer: "a reverse proxy's 502 page",
			status: 502,
			body: '<html>Bad Gateway</html>',
			contentType: 'text/html',
			outcome: 'keeps the weather shown, and says why',
			expected: { weather: shown, warning: 'Unexpected weather response: status 502' }
		},
		{
			// An authenticating proxy's sign-in page, which the generated client hands back as text.
			answer: 'a 200 that is not weather',
			status: 200,
			body: '<html>Sign in</html>',
			contentType: 'text/html',
			outcome: 'keeps the weather shown, and says why',
			expected: { weather: shown, warning: 'Unexpected weather response: status 200' }
		}
	])('$answer, $outcome', async ({ status, body, contentType, expected }) => {
		// Act
		const next = weatherAfter(await weatherAnswered(status, body, contentType), shown);

		// Assert: a 204 was warned about on mount and every ten minutes after, on every frame without
		// weather, and left whatever weather was up there for as long as an outage lasted.
		expect(next).toEqual(expected);
	});
});
