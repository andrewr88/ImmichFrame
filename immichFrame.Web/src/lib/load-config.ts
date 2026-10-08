import { error } from '@sveltejs/kit';
import * as api from '$lib/immichFrameApi';
import { configStore } from '$lib/stores/config.store';

/**
 * Fetches the client settings for a profile - `undefined` for the default configuration - into
 * configStore, or fails the page load with the status that came back instead.
 *
 * oazapfts does not throw for a status the spec does not declare: it hands the response back with
 * its status, and with the body as text when that is not JSON. Stored unchecked, a reverse proxy's
 * 502 page or the empty body of a 500 became the frame's configuration, and the slideshow ran on
 * every default with nothing on screen to say so - for good, once the assets loaded. A failed load
 * puts the error page up instead, and that reloads by itself (reloadSchedule).
 */
export async function loadConfig({
	clientIdentifier,
	profile
}: {
	clientIdentifier: string;
	profile: string | undefined;
}) {
	const response = await api.getConfig({ clientIdentifier, profile });

	// Only 200 is declared on getConfig, hence the widening.
	const status: number = response.status;

	if (status === 200) {
		configStore.ps(response.data);
		return;
	}

	// UnknownProfileMiddleware's answer to a profile it does not know. A request without a profile
	// cannot have had that answer, so a 404 for the default configuration - some proxy's - gets the
	// message every other status does.
	if (status === 404 && profile) {
		error(404, `No configuration profile named '${profile}' is configured.`);
	}

	// error() takes 400-599 only, and any other status that is not a 200 - a 204, say - is no more
	// a configuration than a 500 is.
	error(
		status >= 400 && status <= 599 ? status : 500,
		`The configuration could not be loaded (HTTP ${status}).`
	);
}

/**
 * How soon the error page reloads itself, by the status it is showing - as a delay, and in the
 * words the page uses to say so. A frame usually has nobody in front of it to press reload, and an
 * error page left waiting for someone would outlast the outage that put it up.
 *
 * Every 30 seconds, as the slideshow retries its own errors, for anything that may clear by
 * itself - the backend restarting behind a proxy, say. A 404 is retried too, since a profile can
 * be added from the admin editor without a restart, but only every 5 minutes: until someone does,
 * there is nothing to wait for, and every attempt is a warning in the server's log.
 */
export function reloadSchedule(status: number): { ms: number; every: string } {
	return status === 404
		? { ms: 5 * 60 * 1000, every: '5 minutes' }
		: { ms: 30 * 1000, every: '30 seconds' };
}
