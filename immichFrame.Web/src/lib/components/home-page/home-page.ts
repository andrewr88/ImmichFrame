/**
 * Whether a person's birthday is today, by the calendar where the frame hangs.
 *
 * Immich keeps a birthday as a date alone, but the server passes it on as midnight at its own
 * offset - "1990-10-08T00:00:00+01:00" from a container running in Europe/Berlin. Read as a moment,
 * that midnight is still the 7th anywhere west of the server, so a frame in New York threw the
 * confetti a day early and not at all on the day. The date is read as it was written instead, and
 * only today is read in the frame's own zone.
 */
export function isBirthdayToday(
	birthDate: string | null | undefined,
	today: Date = new Date()
): boolean {
	const written = /^\d{4}-(\d{2})-(\d{2})/.exec(birthDate ?? '');
	if (!written) return false;

	return Number(written[1]) === today.getMonth() + 1 && Number(written[2]) === today.getDate();
}

/**
 * The list a response carries for one of a photo's extras - its albums, its faces - or an empty
 * one, with a warning that says which and why, when it carries none.
 *
 * oazapfts does not throw for a status the spec does not declare: it hands back whatever came, as
 * text when it is not JSON. AssetController answers an upstream failure with a 500 and no body -
 * every time, for an API key without the album or face scope - so loadAsset was handed '', or a
 * proxy's error page, as the list it asked for. Drawing the photo threw on the first `.filter` or
 * `.map` of it, which halted the update bringing it in, and nothing reported the asset as failed.
 * These are extras, so the photo goes up without them instead.
 */
export function okArray<T>(response: { status: number; data: T[] }, what: string, id: string): T[] {
	// Only 200 is declared, and only a list for it, hence the widening of both.
	const status: number = response.status;
	const data: unknown = response.data;

	if (status === 200 && Array.isArray(data)) return data;

	console.warn(`Failed to load ${what} for asset ${id}: status ${status}`);
	return [];
}
