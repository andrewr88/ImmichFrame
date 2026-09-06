<script lang="ts">
	import { onMount } from 'svelte';
	import * as api from '$lib/immichFrameApi';
	import ConfigEditor from '$lib/components/admin/config-editor.svelte';

	type View = 'loading' | 'error' | 'unconfigured' | 'signed-out' | 'not-admin' | 'admin';

	let view = $state<View>('loading');
	let session = $state<api.AdminSessionDto | undefined>(undefined);
	let errorMessage = $state('');

	const button =
		'rounded bg-sky-700 px-4 py-2 text-sm text-white hover:bg-sky-600 disabled:opacity-50';
	const secondaryButton =
		'rounded border border-neutral-600 px-3 py-1 text-sm text-neutral-200 hover:border-neutral-400';

	onMount(loadSession);

	async function loadSession() {
		view = 'loading';

		try {
			const response = await api.getAdminSession();
			const status: number = response.status;

			if (status !== 200) {
				errorMessage = `The admin session could not be read (HTTP ${status}).`;
				view = 'error';
				return;
			}

			session = response.data;
			view = !session.configured
				? 'unconfigured'
				: !session.authenticated
					? 'signed-out'
					: !session.isAdmin
						? 'not-admin'
						: 'admin';
		} catch {
			errorMessage = 'The admin session could not be read. Is ImmichFrame still running?';
			view = 'error';
		}
	}

	/**
	 * A full-page navigation, deliberately not a fetch through the generated client: the login
	 * endpoint answers with a 302 to the identity provider, which XHR cannot follow - it would
	 * either fail on CORS or silently return the provider's login page as data.
	 */
	function signIn() {
		window.location.href = `/api/admin/login?returnUrl=${encodeURIComponent('/admin')}`;
	}

	async function signOut() {
		try {
			// Signing out needs authentication only, not the allowlist, so it works from the
			// "not an administrator" state too.
			await api.adminLogout();
		} finally {
			await loadSession();
		}
	}
</script>

<svelte:head>
	<title>ImmichFrame configuration</title>
</svelte:head>

<div class="min-h-screen bg-neutral-950 text-neutral-100">
	<div class="mx-auto max-w-5xl px-4 py-6">
		<header class="mb-6 flex flex-wrap items-center justify-between gap-3">
			<h1 class="text-xl font-semibold">ImmichFrame configuration</h1>
			{#if session?.authenticated}
				<div class="flex items-center gap-3 text-sm text-neutral-400">
					<span>{session.email || session.subject}</span>
					<button type="button" class={secondaryButton} onclick={signOut}>Sign out</button>
				</div>
			{/if}
		</header>

		{#if view === 'loading'}
			<p class="text-neutral-400">Checking your session…</p>
		{:else if view === 'error'}
			<div class="rounded border border-red-700 bg-red-950/40 p-3">
				<p class="text-sm text-red-300">{errorMessage}</p>
				<button type="button" class="{secondaryButton} mt-2" onclick={loadSession}>
					Try again
				</button>
			</div>
		{:else if view === 'unconfigured'}
			<div class="rounded border border-neutral-700 p-4">
				<h2 class="mb-2 text-lg">There is no administration surface on this installation</h2>
				<p class="text-sm text-neutral-300">
					The configuration editor needs an OpenID Connect provider
					<em>and</em> a non-empty list of administrators. It is off unless all four are present: the
					provider's authority, a client id, a client secret, and at least one administrator on the allowlist.
					An installation with the first three and an empty allowlist is off too, because a sign-in that
					can only mint a session nobody may use is not worth offering.
				</p>
				<p class="mt-2 text-sm text-neutral-400">
					They are read from environment variables only - never from the settings file this editor
					writes, since these are what guard it - so turning the surface on takes a restart. See
					<a class="text-sky-400 underline" href="https://immichframe.dev">immichframe.dev</a>
					for the variable names.
				</p>
			</div>
		{:else if view === 'signed-out'}
			<div class="rounded border border-neutral-700 p-4">
				<h2 class="mb-2 text-lg">Sign in to edit the configuration</h2>
				<p class="mb-3 text-sm text-neutral-300">
					You will be sent to this installation's identity provider and back again.
				</p>
				<button type="button" class={button} onclick={signIn}>Sign in</button>
			</div>
		{:else if view === 'not-admin'}
			<div class="rounded border border-amber-700 bg-amber-950/30 p-4">
				<h2 class="mb-2 text-lg">You are signed in, but not an administrator</h2>
				<p class="text-sm text-amber-200">
					Your identity provider authenticated
					<span class="font-mono">{session?.email || session?.subject}</span>, but that account is
					not on this installation's administrator allowlist. Ask whoever runs it to add you, or
					sign in with a different account.
				</p>
				<button type="button" class="{secondaryButton} mt-3" onclick={signOut}>Sign out</button>
			</div>
		{:else}
			<!-- Re-read rather than assigned: `session` still holds the account that has just stopped
			     being one, and setting the view alone would show its address and a Sign out button
			     above a Sign in panel. -->
			<ConfigEditor onUnauthenticated={loadSession} onForbidden={() => (view = 'not-admin')} />
		{/if}
	</div>
</div>
