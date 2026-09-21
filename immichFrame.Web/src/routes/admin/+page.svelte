<script lang="ts">
	import { onMount } from 'svelte';
	import * as api from '$lib/immichFrameApi';
	import ConfigEditor from '$lib/components/admin/config-editor.svelte';
	// Imported here rather than from app.css so the slideshow bundle never carries the admin
	// design system. Every rule in it is nested under `.modernist`, which only this page sets.
	//
	// After the component import, and not arbitrarily so: the bundler emits each module's CSS in
	// the order the modules are pulled in, so the scoped rules of every component imported above
	// land ahead of this sheet, and a tie between the two - the (0,3,0) that
	// `.modernist .input:disabled` and a component's own `.input:disabled` both carry - is settled
	// in the sheet's favour. That is the reverse of the usual base-first layering, and four of the
	// admin components reason about it in their own comments. Swapping these two lines flips every
	// such tie, and nothing fails. (This file's own style block is compiled after both and still
	// comes last, so its rules win a tie either way.)
	import '$lib/components/admin/modernist.css';

	type View = 'loading' | 'error' | 'unconfigured' | 'signed-out' | 'not-admin' | 'admin';

	let view = $state<View>('loading');
	let session = $state<api.AdminSessionDto | undefined>(undefined);
	let errorMessage = $state('');
	let configSource = $state('');

	/**
	 * The masthead's height, measured rather than assumed: it wraps on a narrow viewport and grows
	 * when the configuration source appears in it. Everything the editor hangs below it needs this
	 * one number - the rail and the profile strip stick to it, and the rail's scroll-spy offsets by
	 * it - so it is published downwards twice, as the custom property those two sticky rules read
	 * and as a prop for the arithmetic. Nothing that decides the masthead's own height reads either,
	 * so measuring it and then publishing it cannot feed back into the measurement.
	 */
	let mastheadHeight = $state(0);

	onMount(loadSession);

	async function loadSession() {
		view = 'loading';
		// Cleared with the session it belonged to, so signing out does not leave the previous
		// account's settings-file path sitting in the header.
		configSource = '';

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

<!-- The custom property is left unset until the header has been laid out and measured, so that
     the declaration in the stylesheet below stands in meanwhile, not a zero overriding it. -->
<div
	class="modernist min-h-screen"
	style:--masthead-height={mastheadHeight ? `${mastheadHeight}px` : null}
>
	<header class="masthead" bind:offsetHeight={mastheadHeight}>
		<div class="masthead-inner">
			<h1 class="brand">IMMICHFRAME</h1>
			<p class="kicker">Configuration editor</p>

			<div class="masthead-end">
				{#if configSource}
					<div class="source">
						<span class="source-label">Configuration source</span>
						<span
							class="mono source-value"
							title="Populated from the settings file on first run, and the single source of truth from then on."
						>
							{configSource}
						</span>
					</div>
				{/if}
				{#if session?.authenticated}
					<span class="account">{session.email || session.subject}</span>
					<button type="button" class="btn btn-secondary" onclick={signOut}>Sign out</button>
				{/if}
			</div>
		</div>
	</header>

	<div class="page" class:page-editor={view === 'admin'}>
		{#if view === 'loading'}
			<p class="text-muted">Checking your session…</p>
		{:else if view === 'error'}
			<section class="card gate gate-error">
				<p class="gate-text">{errorMessage}</p>
				<div class="gate-actions">
					<button type="button" class="btn btn-secondary" onclick={loadSession}>Try again</button>
				</div>
			</section>
		{:else if view === 'unconfigured'}
			<section class="card gate">
				<h2 class="card-title">There is no administration surface on this installation</h2>
				<p class="gate-text">
					The configuration editor needs an OpenID Connect provider
					<em>and</em> a non-empty list of administrators. It is off unless all four are present: the
					provider's authority, a client id, a client secret, and at least one administrator on the allowlist.
					An installation with the first three and an empty allowlist is off too, because a sign-in that
					can only mint a session nobody may use is not worth offering.
				</p>
				<p class="gate-text gate-note">
					They are read from environment variables only - never from the settings file this editor
					writes, since these are what guard it - so turning the surface on takes a restart. See
					<a href="https://immichframe.dev">immichframe.dev</a>
					for the variable names.
				</p>
			</section>
		{:else if view === 'signed-out'}
			<section class="card gate">
				<h2 class="card-title">Sign in to edit the configuration</h2>
				<p class="gate-text">
					You will be sent to this installation's identity provider and back again.
				</p>
				<div class="gate-actions">
					<button type="button" class="btn btn-primary" onclick={signIn}>Sign in</button>
				</div>
			</section>
		{:else if view === 'not-admin'}
			<section class="card gate gate-warning">
				<h2 class="card-title">You are signed in, but not an administrator</h2>
				<p class="gate-text">
					Your identity provider authenticated
					<span class="mono">{session?.email || session?.subject}</span>, but that account is not on
					this installation's administrator allowlist. Ask whoever runs it to add you, or sign in
					with a different account.
				</p>
				<div class="gate-actions">
					<button type="button" class="btn btn-secondary" onclick={signOut}>Sign out</button>
				</div>
			</section>
		{:else}
			<!-- Re-read rather than assigned: `session` still holds the account that has just stopped
			     being one, and setting the view alone would show its address and a Sign out button
			     above a Sign in panel. -->
			<ConfigEditor
				onUnauthenticated={loadSession}
				onForbidden={() => {
					configSource = '';
					view = 'not-admin';
				}}
				onSource={(label) => (configSource = label)}
				{mastheadHeight}
			/>
		{/if}
	</div>
</div>

<style>
	/*
	 * The masthead at its usual height, its 59px of content plus the rule under it. The measurement
	 * above replaces this inline the moment the header has been laid out; this is the floor under
	 * it, and not a line to delete once the measurement works. `top: var(--masthead-height)` with
	 * nothing behind the variable is invalid at computed-value time, which is to say `top: auto`:
	 * the rail and the profile strip would both quietly stop sticking.
	 */
	.modernist {
		--masthead-height: 61px;
	}

	.masthead {
		position: sticky;
		top: 0;
		z-index: 10;
		background: var(--color-bg);
		border-bottom: 2px solid var(--color-divider);
	}

	/*
	 * Full width, not the 1100px the page's prose keeps: the editor below takes the window, so a
	 * capped masthead would leave the brand floating well to the right of the rail it heads. This
	 * and `.page` pad by the same `var(--space-4)` and the rail has no padding on its left, so the
	 * brand's left edge lands on the rail items'. The gate views keep their measure - that is
	 * `.page`'s doing, not the masthead's.
	 */
	.masthead-inner {
		display: flex;
		flex-wrap: wrap;
		align-items: center;
		gap: var(--space-3);
		padding: var(--space-3) var(--space-4);
	}

	.brand {
		margin: 0;
		font-family: var(--font-heading);
		font-weight: 800;
		font-size: 19px;
		line-height: 1.2;
		letter-spacing: -0.02em;
	}

	.kicker,
	.source-label {
		margin: 0;
		font-size: 10.5px;
		letter-spacing: 0.14em;
		text-transform: uppercase;
		color: var(--color-neutral-700);
	}

	.masthead-end {
		display: flex;
		flex-wrap: wrap;
		align-items: center;
		gap: var(--space-3);
		margin-left: auto;
	}

	.source {
		display: flex;
		align-items: center;
		gap: var(--space-2);
	}

	.mono {
		font-family: 'Overpass Mono', ui-monospace, monospace;
		font-size: 13px;
	}

	.source-value {
		font-size: 12px;
		padding: 2px 8px;
		background: var(--color-neutral-200);
		border: 1px solid var(--color-neutral-300);
	}

	/*
	 * `--color-neutral-700` rather than `.text-muted`: the retuned mix does clear AA on this ground
	 * (4.95:1), but this is 13px and it sits between the kicker and the source label, which are on
	 * neutral-700 already. Matching them, and the headroom that comes with it (5.83:1), beats sharing
	 * a token with the page body.
	 */
	.account {
		font-size: 13px;
		color: var(--color-neutral-700);
	}

	.page {
		max-width: 1100px;
		margin: 0 auto;
		padding: var(--space-6) var(--space-4);
	}

	/*
	 * The gate views are prose and stay in the 1100px measure; the editor is a two-column workspace
	 * and takes the window. Its foot goes with the width: the save bar is sticky at the bottom of
	 * the pane, and page padding under it would hold it that far off the bottom of the screen at the
	 * end of the scroll.
	 */
	.page-editor {
		max-width: none;
		padding-bottom: 0;
	}

	/*
	 * Compounded with `.card` rather than written alone: `.modernist .card` from the global sheet
	 * carries the same specificity, so a bare `.gate` would win or lose on whichever stylesheet
	 * the bundler happened to emit second.
	 */
	.card.gate {
		max-width: 68ch;
		gap: var(--space-3);
		padding: var(--space-6);
	}

	.gate-text {
		margin: 0;
		font-size: 14px;
	}

	/*
	 * Not `.text-muted`, for the same reason `.account` is not: the retuned mix clears AA on a card
	 * (4.78:1), but this paragraph is the only place the page says how to turn the admin surface on,
	 * and neutral-700 gives it 5.38:1.
	 */
	.gate-note {
		color: var(--color-neutral-700);
	}

	.gate a {
		color: var(--color-accent-700);
		text-decoration: underline;
	}

	.gate-actions {
		display: flex;
		gap: var(--space-2);
		margin-top: var(--space-2);
	}

	/* Failures take the accent ramp at 700: the bare accent is tuned to 3:1 and is not body copy. */
	.card.gate-error {
		background: var(--color-accent-100);
		color: var(--color-accent-700);
		border-left: 4px solid var(--color-accent);
	}

	/* The warning role, used where this view has always been amber: a refused account is not a fault. */
	.card.gate-warning {
		background: var(--color-warning-100);
		color: var(--color-warning-700);
		border-left: 4px solid var(--color-warning-700);
	}
</style>
