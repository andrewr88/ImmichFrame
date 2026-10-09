import { isSameDay } from 'date-fns';
import * as api from '$lib/immichFrameApi';
import type { ClientSettingsDto, IAppointment } from '$lib/immichFrameApi';
import { resolveLocale } from '$lib/locale';
import { safeFormat } from '$lib/safe-format';

/** When an appointment is, as the appointments panel shows it: the times alone within one day. */
export function formatDates(
	startTime: string,
	endTime: string,
	{
		clockFormat,
		clockDateFormat,
		language
	}: Pick<ClientSettingsDto, 'clockFormat' | 'clockDateFormat' | 'language'>
) {
	const startDate = new Date(startTime);
	const endDate = new Date(endTime);
	const time = (date: Date) => safeFormat(date, clockFormat, 'HH:mm');

	if (isSameDay(startDate, endDate)) {
		return time(startDate) + ' - ' + time(endDate);
	}

	// The date and the time are formatted apart rather than as one pattern, so that a format
	// date-fns refuses falls back on its own and the other keeps the administrator's format.
	// Each as the clock formats its own: the date in the frame's language, the time without one.
	const locale = resolveLocale(language);
	const full = (date: Date) =>
		safeFormat(date, clockDateFormat, 'eee, MMM d', { locale }) + ' ' + time(date);

	return full(startDate) + ' - ' + full(endDate);
}

/**
 * The calendar's appointments, soonest first - or null when they could not be had, which leaves the
 * panel showing the list it already has until the next attempt.
 *
 * Kept rather than cleared on purpose: a frame on a wall has nobody in front of it to notice a fetch
 * failing, and a calendar a few minutes stale is more use to whoever walks past than one that blanks
 * for ten minutes at every network blip. Never a rejection either - nothing awaits the panel's
 * interval, so one went unhandled every time it fired for as long as the outage lasted.
 */
export async function loadAppointments(
	clientIdentifier: string,
	profile: string | undefined
): Promise<IAppointment[] | null> {
	try {
		const appointmentRequest = await api.getAppointments({ clientIdentifier, profile });
		if (appointmentRequest.status === 200) {
			return appointmentRequest.data.sort((a, b) => {
				return new Date(a.startTime ?? '').getTime() - new Date(b.startTime ?? '').getTime();
			});
		}

		console.warn('Unexpected appointments status:', appointmentRequest.status);
	} catch (err) {
		console.error('Error fetching appointments:', err);
	}

	return null;
}
