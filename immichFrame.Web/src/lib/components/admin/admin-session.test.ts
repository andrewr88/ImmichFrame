import { describe, expect, it } from 'vitest';
import type { AdminSessionDto } from '$lib/immichFrameApi';
import { sessionView, type SessionRead, type SessionView } from './admin-session';

/** Who a session names once someone is signed in, and who it names before. */
const ADA = { subject: 'oidc|ada', email: 'ada@example.com' };
const NOBODY = { subject: null, email: null };

/**
 * What the generated client resolves with. Typed by oazapfts as the one status the OpenAPI
 * document declares, while carrying whatever the server - or a proxy in front of it - answered.
 */
function reply(status: number, data: unknown): SessionRead {
	return { status, data, headers: new Headers() } as never;
}

describe('sessionView', () => {
	it.each<{ case: string; read: SessionRead; errorMessage: string }>([
		{
			case: 'a 401',
			read: reply(401, '<html>401 Authorization Required</html>'),
			errorMessage: 'The admin session could not be read (HTTP 401).'
		},
		{
			case: 'a 500',
			read: reply(500, ''),
			errorMessage: 'The admin session could not be read (HTTP 500).'
		},
		{
			// A JSON content type and no body, which the generated client hands back as null.
			case: 'a 200 with no session in it',
			read: reply(200, null),
			errorMessage: 'The admin session could not be read (HTTP 200).'
		},
		{
			// What a proxy that routes /api to the SPA answers with.
			case: 'a 200 that is a page of HTML',
			read: reply(200, '<!doctype html><html></html>'),
			errorMessage: 'The admin session could not be read (HTTP 200).'
		},
		{
			case: 'a request that threw',
			read: { thrown: new TypeError('Failed to fetch') },
			errorMessage: 'The admin session could not be read. Is ImmichFrame still running?'
		}
	])('$case, is the error view, and names no account', ({ read, errorMessage }) => {
		// Assert: the masthead names the account and offers its Sign out from `session`, so an
		// error that kept the last read's would put a working Sign out over a page saying the
		// session could not be read.
		expect(sessionView(read)).toEqual({ view: 'error', session: undefined, errorMessage });
	});

	it.each<{ case: string; session: AdminSessionDto }>([
		{
			case: 'nobody signed in',
			session: { configured: false, authenticated: false, isAdmin: false, ...NOBODY }
		},
		{
			case: 'a cookie still held from when it was switched on',
			session: { configured: false, authenticated: true, isAdmin: true, ...ADA }
		}
	])(
		'a 200 with no admin surface and $case, is the unconfigured view, and names no account',
		({ session }) => {
			// Assert: the logout endpoint is not routed while the surface is off, so a named account
			// would come with a Sign out that does nothing.
			expect(sessionView({ status: 200, data: session })).toEqual({
				view: 'unconfigured',
				session: undefined,
				errorMessage: ''
			});
		}
	);

	it.each<{ case: string; session: AdminSessionDto; view: SessionView['view'] }>([
		{
			case: 'nobody signed in',
			session: { configured: true, authenticated: false, isAdmin: false, ...NOBODY },
			view: 'signed-out'
		},
		{
			case: 'an account that is not on the allowlist',
			session: { configured: true, authenticated: true, isAdmin: false, ...ADA },
			view: 'not-admin'
		},
		{
			case: 'an administrator',
			session: { configured: true, authenticated: true, isAdmin: true, ...ADA },
			view: 'admin'
		}
	])('a 200 for $case, is the $view view, with that session', ({ session, view }) => {
		// Assert
		expect(sessionView(reply(200, session))).toEqual({ view, session, errorMessage: '' });
	});
});
