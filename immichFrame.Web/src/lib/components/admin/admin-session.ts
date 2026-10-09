import type { AdminSessionDto } from '$lib/immichFrameApi';

/** The admin page's screens. `loading` is the page's own, up while a read is in the air. */
export type AdminView = 'loading' | 'error' | 'unconfigured' | 'signed-out' | 'not-admin' | 'admin';

/**
 * How one read of the session ended: with the generated client's answer, whatever its status, or
 * with the request itself throwing - ImmichFrame down, or the network between.
 */
export type SessionRead = { status: number; data: AdminSessionDto } | { thrown: unknown };

/** Everything the page draws from a read of the session. */
export interface SessionView {
	view: Exclude<AdminView, 'loading'>;
	/** Who the masthead names, with a Sign out beside them while they are signed in. */
	session: AdminSessionDto | undefined;
	/** What the error view says. Empty for every other view. */
	errorMessage: string;
}

/**
 * What the admin page shows for a read of the session: every field of it, from that read alone.
 *
 * `session` is `undefined` for every error, and that is the point of answering all three at once.
 * The masthead names the account and offers its Sign out from `session`, and the page used to set
 * the view and the message on an error and leave `session` as it was - so a read that failed right
 * after a sign-out, or after the editor's 401 while ImmichFrame was down, put the previous account
 * and a working Sign out over a page saying the session could not be read. Nothing here can carry
 * a session over from one read to the next, because nothing here sees the one before.
 *
 * Out here rather than in the page because the suite runs in node with no DOM, and a component
 * cannot be mounted there.
 */
export function sessionView(read: SessionRead): SessionView {
	if ('thrown' in read) {
		return {
			view: 'error',
			session: undefined,
			errorMessage: 'The admin session could not be read. Is ImmichFrame still running?'
		};
	}

	// Only 200 is declared on getAdminSession, hence the widening: any other status carries
	// whatever was sent with it, and so can a 200. With a JSON content type and no body the
	// generated client hands back null, which reading the flags off would throw on - and the page
	// calls this outside its catch - and a proxy that routes /api to the SPA answers with HTML.
	const data: unknown = read.data;

	if (read.status !== 200 || !data || typeof data !== 'object') {
		return {
			view: 'error',
			session: undefined,
			errorMessage: `The admin session could not be read (HTTP ${read.status}).`
		};
	}

	const session = read.data;

	// With the surface switched off the logout endpoint is not routed, so a cookie still held from
	// when it was on would put an account and a Sign out that does nothing over this screen.
	if (!session.configured) {
		return { view: 'unconfigured', session: undefined, errorMessage: '' };
	}

	return {
		view: !session.authenticated ? 'signed-out' : !session.isAdmin ? 'not-admin' : 'admin',
		session,
		errorMessage: ''
	};
}
