<script lang="ts">
	import { onMount } from 'svelte';
	import * as api from '$lib/immichFrameApi';
	import {
		newProfile,
		problemDetail,
		profileNameError,
		statusOf,
		toEditable,
		toUpdate,
		validationErrors,
		type EditableConfig,
		type EditableEntry
	} from './admin-config';
	import AccountsSection from './accounts-section.svelte';
	import EntryEditor from './entry-editor.svelte';

	interface Props {
		/** A 401 from any admin call means the session is gone; the page returns to the sign-in state. */
		onUnauthenticated: () => void;
		/** A 403 means the session is real but the account is not on the allowlist. */
		onForbidden: () => void;
		/** Where the configuration was read from, for the page header. Raised on every load. */
		onSource: (label: string) => void;
	}

	let { onUnauthenticated, onForbidden, onSource }: Props = $props();

	let config = $state<EditableConfig | null>(null);
	let loading = $state(true);
	let saving = $state(false);
	let loadError = $state('');
	let saveError = $state('');
	let saveStale = $state(false);
	let problems: string[] = $state([]);
	let selected = $state(0);
	let newProfileName = $state('');
	let newProfileError = $state('');
	let pendingDeletions: string[] = $state([]);

	let entries: EditableEntry[] = $derived(config ? [config.default, ...config.profiles] : []);
	let current = $derived(entries[Math.min(selected, entries.length - 1)]);
	let readOnly = $derived(config?.source.editable === false);
	let needsConversion = $derived(config?.source.legacySchema === true);

	// What a save would send, as a value that can be compared. Recomputed only when something in
	// the model actually changes, and the model is a few kilobytes of settings.
	let pending = $derived(config ? JSON.stringify(toUpdate(config)) : '');

	// The banner is a statement about the model rather than a flag set once: left as a flag it
	// went on saying "Saved and applied" over edits made after the save it referred to.
	let savedPending: string | null = $state(null);
	let saved = $derived(savedPending !== null && savedPending === pending);

	onMount(load);

	async function load() {
		loading = true;
		loadError = '';
		saveError = '';
		saveStale = false;
		savedPending = null;
		pendingDeletions = [];
		problems = [];

		try {
			const response = await api.getAdminConfig();
			const status = statusOf(response);

			if (status === 401) return onUnauthenticated();
			if (status === 403) return onForbidden();

			if (status !== 200) {
				loadError = problemDetail(
					response.data,
					`The configuration could not be read (HTTP ${status}).`
				);
				return;
			}

			const loaded = toEditable(response.data);

			config = loaded;
			selected = 0;
			onSource(loaded.source.path ?? loaded.source.format ?? 'unknown');
		} catch {
			loadError = 'The configuration could not be read. Is ImmichFrame still running?';
		} finally {
			loading = false;
		}
	}

	async function save() {
		if (!config) return;

		savedPending = null;
		saveError = '';
		saveStale = false;
		problems = validationErrors(config);

		if (problems.length > 0) return;

		saving = true;

		try {
			const response = await api.saveAdminConfig(toUpdate(config));
			const status = statusOf(response);

			if (status === 401) return onUnauthenticated();
			if (status === 403) return onForbidden();

			if (status === 200) {
				const reloaded = toEditable(response.data);

				config = reloaded;
				pendingDeletions = [];
				// Raised here as well as in `load`: a save can rewrite the file it read from - converting a
				// legacy schema changes its format - and the header pill is the only place that is shown.
				onSource(reloaded.source.path ?? reloaded.source.format ?? 'unknown');
				// Snapshotted from the reloaded model directly rather than read back off `pending`,
				// so the banner's "this is what was saved" does not depend on when a derived
				// happens to recompute.
				savedPending = JSON.stringify(toUpdate(reloaded));
				return;
			}

			saveStale = status === 409;
			saveError = problemDetail(response.data, `The configuration was not saved (HTTP ${status}).`);
		} catch {
			saveError = 'The configuration was not saved. Is ImmichFrame still running?';
		} finally {
			saving = false;
		}
	}

	function addProfile() {
		if (!config) return;

		const name = newProfileName.trim();
		newProfileError =
			profileNameError(
				name,
				config.profiles.map((profile) => profile.name)
			) ?? '';

		if (newProfileError) return;

		config.profiles = [...config.profiles, newProfile(name)];
		selected = config.profiles.length;
		newProfileName = '';
	}

	function removeProfile(entry: EditableEntry) {
		if (!config) return;

		// A profile name is a frame's URL path and its localStorage auth-secret key, so deleting one
		// takes every frame on it offline as well as discarding its settings. Too much for a bare
		// click on a button that looks like all the others.
		const confirmed = confirm(
			`Delete the configuration profile '${entry.name}'?\n\n` +
				'Its settings are discarded and any frame using /' +
				entry.name +
				' stops working. Nothing changes until you save.'
		);

		if (!confirmed) return;

		config.profiles = config.profiles.filter((profile) => profile !== entry);
		pendingDeletions = [...pendingDeletions, entry.name];
		selected = 0;
	}
</script>

{#if loading}
	<p class="text-muted">Loading the configuration…</p>
{:else if loadError}
	<section class="card notice">
		<p class="notice-text">{loadError}</p>
		<div class="notice-actions">
			<button type="button" class="btn btn-secondary" onclick={load}>Try again</button>
		</div>
	</section>
{:else if config}
	<section class="card banner">
		<p class="source">
			<span class="source-label">Configuration source</span>
			<span class="mono">{config.source.path ?? config.source.format ?? 'unknown'}</span>
		</p>
		{#if readOnly}
			<div class="warning">
				<p class="warning-text">
					{config.source.notEditableReason ?? 'This configuration cannot be edited from here.'}
				</p>
				<p class="warning-note">The settings below are shown read-only.</p>
			</div>
		{:else if needsConversion}
			<div class="warning">
				<p class="warning-text">
					This settings file is written in the old schema. Saving rewrites it in the current one,
					which cannot be undone from here.
				</p>
				<label class="warning-consent">
					<input type="checkbox" class="warning-check" bind:checked={config.convertLegacySchema} />
					<span>Convert this file to the current schema when I save.</span>
				</label>
			</div>
		{/if}
	</section>

	<!-- Everything from here to the save bar is still on Tailwind palette classes, and
	     `.modernist` redefines `--color-neutral-100` through `-900` - the very variables Tailwind v4
	     resolves `text-neutral-*` and `border-neutral-*` through. So the light shell above did not
	     just recolour these components, it inverted their ground: the inactive profile tabs, the
	     only route to a profile, came out at 1.33:1. Containing them on the dark surface they were
	     written for costs one element instead of a class revert at every site, and on that ground
	     the Modernist ramp reads as well as Tailwind's own or better (those tabs, 13.3:1). Strictly
	     temporary: task 002 lifts the tab strip and the save bar out of it, and it goes when task
	     005 converts the last child left inside. -->
	<div class="bg-neutral-950 p-4 text-neutral-100">
		<!-- Above the tab strip, because an Immich account belongs to the configuration as a whole rather
		     than to whichever tab happens to be selected: the same account is used by the default
		     configuration and by any number of profiles, and is one set of credentials in all of them. -->
		<fieldset disabled={readOnly}>
			<AccountsSection {config} />
		</fieldset>

		<div class="mb-4 flex flex-wrap items-center gap-2">
			{#each entries as entry, index (entry.name + index)}
				<button
					type="button"
					class="rounded px-3 py-1 text-sm {index === selected
						? 'bg-sky-700 text-white'
						: 'border border-neutral-700 text-neutral-300 hover:border-neutral-500'}"
					onclick={() => (selected = index)}
				>
					{entry.isDefault ? 'Default configuration' : entry.name}
				</button>
			{/each}
		</div>

		{#if !readOnly}
			<div class="mb-4 flex flex-wrap items-end gap-2">
				<div>
					<label class="text-xs text-neutral-400" for="new-profile">New profile</label>
					<input
						id="new-profile"
						type="text"
						class="block rounded border border-neutral-700 bg-neutral-900 px-2 py-1 text-sm
							text-neutral-100"
						placeholder="kitchen"
						bind:value={newProfileName}
					/>
				</div>
				<!-- Still on the old dark classes, like the Reload button below: it sits inside the
				     containment wrapper, which task 002 converts, and `.btn` would paint `--color-text` on
				     near-black at 1.19:1 over a 1.06:1 border. It is the only way to add a profile, so it
				     cannot go illegible in the meantime. -->
				<button
					type="button"
					class="rounded border border-neutral-600 px-3 py-1 text-sm text-neutral-200
						hover:border-neutral-400"
					onclick={addProfile}
				>
					Add profile
				</button>
				{#if current && !current.isDefault}
					<button
						type="button"
						class="rounded border border-red-700 px-3 py-1 text-sm text-red-300
							hover:border-red-500 hover:text-red-200"
						onclick={() => removeProfile(current)}
					>
						Delete profile '{current.name}'…
					</button>
				{/if}
			</div>
			{#if newProfileError}
				<p class="mb-4 text-sm text-red-300">{newProfileError}</p>
			{/if}
		{/if}

		{#if current}
			<fieldset disabled={readOnly}>
				<EntryEditor
					entry={current}
					accounts={config.accounts}
					inheritFrom={current.isDefault ? null : config.default}
					version={config.version}
				/>
			</fieldset>
		{/if}
	</div>

	{#if !readOnly}
		<div class="sticky bottom-0 border-t border-neutral-700 bg-neutral-950 py-3">
			{#if problems.length > 0}
				<ul class="mb-2 list-disc pl-5 text-sm text-red-300">
					{#each problems as problem (problem)}
						<li>{problem}</li>
					{/each}
				</ul>
			{/if}
			{#if saveError}
				<p class="mb-2 text-sm text-red-300">{saveError}</p>
				{#if saveStale}
					<!-- Still on the old dark classes, like the Add profile button above: it sits inside
					     the save bar, which task 002 converts, and `.btn` would paint `--color-text` on
					     near-black at 1.19:1 over a 1.06:1 border. It is the only way out of a stale save,
					     so it cannot go illegible in the meantime. -->
					<button
						type="button"
						class="mb-2 rounded border border-neutral-600 px-3 py-1 text-sm text-neutral-200
							hover:border-neutral-400"
						onclick={load}
					>
						Reload the configuration
					</button>
				{/if}
			{/if}
			{#if pendingDeletions.length > 0}
				<p class="mb-2 text-sm text-red-300">
					{pendingDeletions.length === 1 ? 'Profile' : 'Profiles'}
					{pendingDeletions.map((name) => `'${name}'`).join(', ')}
					will be deleted when you save. Reload to get {pendingDeletions.length === 1
						? 'it'
						: 'them'} back.
				</p>
			{/if}
			{#if saved}
				<p class="mb-2 text-sm text-emerald-400">
					Saved and applied. Frames pick the new configuration up on their next request.
				</p>
			{/if}
			<button
				type="button"
				class="rounded bg-sky-700 px-4 py-2 text-sm text-white hover:bg-sky-600
					disabled:opacity-50 disabled:cursor-not-allowed"
				disabled={saving || (needsConversion && !config.convertLegacySchema)}
				onclick={save}
			>
				{saving ? 'Saving…' : 'Save configuration'}
			</button>
		</div>
	{/if}
{/if}

<style>
	/*
	 * Compounded with `.card` rather than written alone: `.modernist .card` from the global sheet
	 * carries the same specificity, so a bare `.notice` would win or lose on whichever stylesheet
	 * the bundler happened to emit second.
	 */
	.card.notice,
	.card.banner {
		margin-bottom: var(--space-4);
		padding: var(--space-4);
	}

	/* Failures take the accent ramp at 700: the bare accent is tuned to 3:1 and is not body copy. */
	.card.notice {
		background: var(--color-accent-100);
		color: var(--color-accent-700);
		border-left: 4px solid var(--color-accent);
	}

	.notice-text {
		margin: 0;
		font-size: 14px;
	}

	.notice-actions {
		display: flex;
		margin-top: var(--space-2);
	}

	.source {
		display: flex;
		flex-wrap: wrap;
		align-items: center;
		gap: var(--space-2);
		margin: 0;
	}

	.source-label {
		margin: 0;
		font-size: 10.5px;
		letter-spacing: 0.14em;
		text-transform: uppercase;
		color: var(--color-neutral-700);
	}

	.mono {
		font-family: 'Overpass Mono', ui-monospace, monospace;
		font-size: 13px;
	}

	/*
	 * The warning role, used where this banner has always been amber. Read-only and legacy-schema
	 * are conditions to understand before saving, not failures, so they stay off the accent ramp
	 * the load error above uses.
	 */
	.warning {
		display: flex;
		flex-direction: column;
		gap: var(--space-2);
		padding: var(--space-3);
		background: var(--color-warning-100);
		color: var(--color-warning-700);
		border-left: 4px solid var(--color-warning-700);
	}

	.warning-text {
		margin: 0;
		font-size: 14px;
	}

	.warning-note {
		margin: 0;
		font-size: 12px;
	}

	.warning-consent {
		display: flex;
		align-items: center;
		gap: var(--space-2);
		font-size: 14px;
	}

	.warning-check {
		width: 16px;
		height: 16px;
		accent-color: var(--color-warning-700);
	}
</style>
