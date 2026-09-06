<script lang="ts">
	import { onMount } from 'svelte';
	import * as api from '$lib/immichFrameApi';
	import {
		newProfile,
		profileNameError,
		toEditable,
		toUpdate,
		validationErrors,
		type EditableConfig,
		type EditableEntry
	} from './admin-config';
	import EntryEditor from './entry-editor.svelte';

	interface Props {
		/** A 401 from any admin call means the session is gone; the page returns to the sign-in state. */
		onUnauthenticated: () => void;
		/** A 403 means the session is real but the account is not on the allowlist. */
		onForbidden: () => void;
	}

	let { onUnauthenticated, onForbidden }: Props = $props();

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

	const button =
		'rounded border border-neutral-600 px-3 py-1 text-sm text-neutral-200 ' +
		'hover:border-neutral-400 disabled:opacity-50 disabled:cursor-not-allowed';

	onMount(load);

	/**
	 * oazapfts types a response as the single status the OpenAPI document declares but returns
	 * whatever the server actually answered, so every refusal arrives here as a value rather than a
	 * throw. `src/routes/[config]/+page.ts` widens the same way for its 404.
	 */
	function statusOf(response: { status: number }): number {
		return response.status;
	}

	/** The server writes these messages for an operator, so they are shown as they arrive. */
	function detailOf(data: unknown, fallback: string): string {
		if (!data || typeof data !== 'object') return fallback;

		const problem = data as { detail?: unknown; title?: unknown; errors?: unknown };

		if (typeof problem.detail === 'string' && problem.detail.trim()) return problem.detail;

		// A request the controller never sees carries its messages somewhere else. Model binding
		// fails before the action runs and [ApiController] answers with a ValidationProblemDetails,
		// which has a title and an `errors` map and no `detail` at all - and that is exactly what a
		// mistyped album or person UUID produces while those fields are raw text entry.
		const messages =
			problem.errors && typeof problem.errors === 'object'
				? Object.entries(problem.errors as Record<string, unknown>).flatMap(([field, value]) =>
						(Array.isArray(value) ? value : [value])
							.filter((message): message is string => typeof message === 'string')
							.map((message) => (field && field !== '$' ? `${field}: ${message}` : message))
					)
				: [];

		if (messages.length > 0) return messages.join(' ');
		if (typeof problem.title === 'string' && problem.title.trim()) return problem.title;

		return fallback;
	}

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
				loadError = detailOf(
					response.data,
					`The configuration could not be read (HTTP ${status}).`
				);
				return;
			}

			config = toEditable(response.data);
			selected = 0;
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
				// Snapshotted from the reloaded model directly rather than read back off `pending`,
				// so the banner's "this is what was saved" does not depend on when a derived
				// happens to recompute.
				savedPending = JSON.stringify(toUpdate(reloaded));
				return;
			}

			saveStale = status === 409;
			saveError = detailOf(response.data, `The configuration was not saved (HTTP ${status}).`);
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
	<p class="text-neutral-400">Loading the configuration…</p>
{:else if loadError}
	<div class="rounded border border-red-700 bg-red-950/40 p-3">
		<p class="text-sm text-red-300">{loadError}</p>
		<button type="button" class="{button} mt-2" onclick={load}>Try again</button>
	</div>
{:else if config}
	<div class="mb-4 rounded border border-neutral-700 p-3 text-sm text-neutral-300">
		<p>
			<span class="text-neutral-500">Configuration source:</span>
			{config.source.path ?? config.source.format ?? 'unknown'}
		</p>
		{#if readOnly}
			<p class="mt-2 text-amber-300">
				{config.source.notEditableReason ?? 'This configuration cannot be edited from here.'}
			</p>
			<p class="mt-1 text-xs text-neutral-500">The settings below are shown read-only.</p>
		{:else if needsConversion}
			<p class="mt-2 text-amber-300">
				This settings file is written in the old schema. Saving rewrites it in the current one,
				which cannot be undone from here.
			</p>
			<label class="mt-1 flex items-center gap-2 text-sm text-amber-200">
				<input
					type="checkbox"
					class="h-4 w-4 accent-amber-500"
					bind:checked={config.convertLegacySchema}
				/>
				<span>Convert this file to the current schema when I save.</span>
			</label>
		{/if}
	</div>

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
			<button type="button" class={button} onclick={addProfile}>Add profile</button>
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
			<EntryEditor entry={current} inheritFrom={current.isDefault ? null : config.default} />
		</fieldset>
	{/if}

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
					<button type="button" class="{button} mb-2" onclick={load}>
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
