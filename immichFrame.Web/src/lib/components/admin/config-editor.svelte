<script lang="ts">
	import { onMount } from 'svelte';
	import * as api from '$lib/immichFrameApi';
	import {
		entryLabel,
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
	import AdminRail from './admin-rail.svelte';
	import EntryEditor from './entry-editor.svelte';

	interface Props {
		/** A 401 from any admin call means the session is gone; the page returns to the sign-in state. */
		onUnauthenticated: () => void;
		/** A 403 means the session is real but the account is not on the allowlist. */
		onForbidden: () => void;
		/** Where the configuration was read from, for the page header. Raised on every load. */
		onSource: (label: string) => void;
		/**
		 * How tall the masthead is, measured by the page that owns it. Passed down to the rail, whose
		 * scroll-spy has to know where the sticky chrome ends; the page publishes the same number as
		 * `--masthead-height` for the CSS below to stick to.
		 */
		mastheadHeight: number;
	}

	let { onUnauthenticated, onForbidden, onSource, mastheadHeight }: Props = $props();

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

	// What the server holds, as the same comparable value: set from every read and from every save.
	// The save bar says whether there is anything unsaved and not how much, because that is all the
	// model knows - dirtiness here is a comparison and has never been a count of edits.
	let serverPending: string | null = $state(null);
	let unsaved = $derived(serverPending !== null && serverPending !== pending);

	// Measured rather than assumed, because the strip wraps on a narrow viewport: the rail offsets
	// its scroll-spy by whatever the sticky chrome above the pane currently covers.
	let stripHeight = $state(0);

	onMount(load);

	async function load() {
		loading = true;
		loadError = '';
		saveError = '';
		saveStale = false;
		savedPending = null;
		serverPending = null;
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
			serverPending = JSON.stringify(toUpdate(loaded));
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
				serverPending = savedPending;
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
	<div class="editor">
		<AdminRail
			entry={current}
			accountCount={config.accounts.length}
			{mastheadHeight}
			{stripHeight}
		/>

		<div class="pane">
			<div class="strip" bind:offsetHeight={stripHeight}>
				<span class="strip-label">Editing</span>

				<div class="tabs">
					{#each entries as entry, index (entry.name + index)}
						<button
							type="button"
							class="tab"
							class:is-active={index === selected}
							onclick={() => (selected = index)}
						>
							{entry.isDefault ? 'Default configuration' : entry.name}
						</button>
					{/each}
				</div>

				{#if !readOnly}
					<div class="strip-end">
						<!-- Named by `aria-label` rather than by a visible caption: the strip is one row of
						     chrome, and the only thing an empty box beside an Add profile button can be
						     asking for is the name of one. -->
						<input
							type="text"
							class="input new-profile"
							aria-label="New profile name"
							placeholder="kitchen"
							bind:value={newProfileName}
						/>
						<button type="button" class="btn btn-secondary" onclick={addProfile}>
							Add profile
						</button>
						{#if current && !current.isDefault}
							<button
								type="button"
								class="btn delete-profile"
								onclick={() => removeProfile(current)}
							>
								Delete profile '{current.name}'…
							</button>
						{/if}
					</div>
					{#if newProfileError}
						<p class="strip-error">{newProfileError}</p>
					{/if}
				{/if}
			</div>

			<div class="pane-body">
				<section class="card banner">
					<p class="source">
						<span class="source-label">Configuration source</span>
						<span class="mono">{config.source.path ?? config.source.format ?? 'unknown'}</span>
					</p>
					{#if readOnly}
						<div class="warning">
							<p class="warning-text">
								{config.source.notEditableReason ??
									'This configuration cannot be edited from here.'}
							</p>
							<p class="warning-note">The settings below are shown read-only.</p>
						</div>
					{:else if needsConversion}
						<div class="warning">
							<p class="warning-text">
								This settings file is written in the old schema. Saving rewrites it in the current
								one, which cannot be undone from here.
							</p>
							<label class="warning-consent">
								<input
									type="checkbox"
									class="warning-check"
									bind:checked={config.convertLegacySchema}
								/>
								<span>Convert this file to the current schema when I save.</span>
							</label>
						</div>
					{/if}
				</section>

				<!-- First in the pane, and listed apart from the profile's own sections in the rail,
				     because an Immich account belongs to the configuration as a whole rather than to
				     whichever tab happens to be selected: the same account is used by the default
				     configuration and by any number of profiles, and is one set of credentials in all
				     of them. -->
				<fieldset disabled={readOnly}>
					<AccountsSection {config} />
				</fieldset>

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
				<div class="save-bar">
					{#if problems.length > 0}
						<ul class="save-problems">
							{#each problems as problem (problem)}
								<li>{problem}</li>
							{/each}
						</ul>
					{/if}
					{#if saveError}
						<p class="save-error">{saveError}</p>
						{#if saveStale}
							<button type="button" class="btn btn-secondary" onclick={load}>
								Reload the configuration
							</button>
						{/if}
					{/if}
					{#if pendingDeletions.length > 0}
						<p class="save-deletions">
							{pendingDeletions.length === 1 ? 'Profile' : 'Profiles'}
							{pendingDeletions.map((name) => `'${name}'`).join(', ')}
							will be deleted when you save. Reload to get {pendingDeletions.length === 1
								? 'it'
								: 'them'} back.
						</p>
					{/if}
					<div class="save-row">
						<p class="save-note">
							{#if saved}
								Saved and applied. Frames pick the new configuration up on their next request.
							{:else}
								Editing {entryLabel(current)}. Nothing is written until you save.
							{/if}
						</p>
						<div class="save-actions">
							{#if unsaved}
								<span class="save-unsaved">Unsaved changes</span>
							{/if}
							<button
								type="button"
								class="btn btn-primary"
								disabled={saving || (needsConversion && !config.convertLegacySchema)}
								onclick={save}
							>
								{saving ? 'Saving…' : 'Save configuration'}
							</button>
						</div>
					</div>
				</div>
			{/if}
		</div>
	</div>
{/if}

<style>
	/*
	 * Compounded with `.card` rather than written alone: `.admin-theme .card` from the global sheet
	 * carries the same specificity, so a bare `.notice` would win or lose on whichever stylesheet
	 * the bundler happened to emit second.
	 */
	.card.notice,
	.card.banner {
		margin-bottom: var(--space-4);
		padding: var(--space-4);
	}

	/* Failures take the danger role; the accent is for emphasis, not for faults. */
	.card.notice {
		background: var(--color-danger-100);
		color: var(--color-danger-700);
		border-left: 4px solid var(--color-danger-700);
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
	 * are conditions to understand before saving, not failures, so they stay off the danger role
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
	/*
	 * Two columns: the rail, which stays where it is, and the configuration, which scrolls past it.
	 * `minmax(0, 1fr)` rather than `1fr` because a grid track sized from its contents is widened by
	 * the longest unbroken thing in it - an Immich album id, a webhook URL - and the rail is what
	 * would be pushed off screen.
	 */
	.editor {
		display: grid;
		grid-template-columns: 262px minmax(0, 1fr);
	}

	/*
	 * No padding of its own: the strip and the save bar are the pane's own chrome and their rules
	 * run its full width, so each of them carries its own. `min-width: 0` is the grid item's half of
	 * the `minmax(0, …)` above - a grid item refuses to shrink below its contents whatever the track
	 * it sits in allows.
	 */
	.pane {
		min-width: 0;
	}

	.strip {
		position: sticky;
		/* The admin root publishes the masthead's height; the rail sticks to the same number. */
		top: var(--masthead-height);
		z-index: 5;
		display: flex;
		flex-wrap: wrap;
		align-items: center;
		gap: var(--space-3);
		padding: var(--space-2) var(--space-4);
		background: var(--color-bg);
		border-bottom: 1px solid var(--color-neutral-300);
	}

	/*
	 * The mock's neutral-600 reads 3.85:1 on the page ground, which is under AA for a 10px label.
	 * Neutral-700 is 5.83:1 and is what the masthead's kicker and source label - the two captions
	 * this sits in line with - already use, so matching the mock's tone here means matching them.
	 */
	.strip-label {
		font-size: 10px;
		letter-spacing: 0.14em;
		text-transform: uppercase;
		color: var(--color-neutral-700);
	}

	/*
	 * One segmented control rather than a row of separate buttons: the border belongs to the set and
	 * the tabs divide it, which is what says that picking one of these is picking among alternatives.
	 */
	.tabs {
		display: flex;
		flex-wrap: wrap;
		border: 1px solid var(--color-divider);
	}

	.tab {
		padding: 7px 14px;
		font-family: var(--font-heading);
		font-weight: 800;
		font-size: 13px;
		line-height: 1.2;
		color: var(--color-text);
		background: transparent;
		border: 0;
		cursor: pointer;
	}

	.tab + .tab {
		border-left: 1px solid var(--color-divider);
	}

	.tab.is-active {
		background: var(--color-accent);
		color: var(--color-bg);
	}

	.strip-end {
		display: flex;
		flex-wrap: wrap;
		align-items: center;
		gap: var(--space-2);
		margin-left: auto;
	}

	/*
	 * Compounded with the component class, like the cards above: `.admin-theme .input` sets both of
	 * these properties and carries the specificity a bare `.new-profile` would, so which of them
	 * won would come down to the order the bundler emitted the two sheets in.
	 */
	.input.new-profile {
		width: 200px;
		font-size: 13px;
	}

	/*
	 * Tailwind's preflight mixes a placeholder at half of `currentColor` - `color-mix(in oklab,
	 * currentcolor 50%, transparent)`, which keeps the colour and halves its alpha - so on the light
	 * ground it composites `--color-text` over the input's own surface to #858483: 3.08:1, where the
	 * text typed into the same box gets 13.7:1. Clear of the 3:1 floor, then, but short of AA at
	 * 13px, and AA is the bar here - the `aria-label` names this box for assistive technology only,
	 * which leaves the placeholder the one caption a sighted reader has for what belongs in it.
	 * Neutral-700 reads 5.38:1 on that same surface.
	 */
	.input.new-profile::placeholder {
		color: var(--color-neutral-700);
	}

	/*
	 * Deleting a profile takes every frame on it offline, so it is the one control in this strip
	 * that wears the accent, with the tint's border for an edge. Destructive, but not a fault, so it
	 * stays off the danger role: the design dresses it in the primary.
	 */
	.btn.delete-profile {
		color: var(--color-accent-700);
		border-color: var(--color-accent-300);
	}

	.btn.delete-profile:hover {
		background: var(--color-accent-100);
	}

	/* On its own line under the row, because the input it is about has been pushed to the right. */
	.strip-error {
		flex-basis: 100%;
		margin: 0;
		font-size: 13px;
		color: var(--color-danger-700);
	}

	/*
	 * The room at the foot is what lets the last section reach the chrome line: a section shorter
	 * than the screen has nothing below it to scroll against, so without this the scroll ends with
	 * it still halfway down the page and its rail item could never light up. A viewport less the
	 * masthead is the generous form of "enough" - the exact figure is a viewport less the whole
	 * chrome, less whatever the last section and the save bar already contribute, and three
	 * measurements plumbed into CSS would buy nothing but a shorter blank at the very end of a
	 * scroll. It is what lets the rail decide the last section the same way it decides every other,
	 * with no foot marker and no special case.
	 */
	.pane-body {
		padding: var(--space-6) var(--space-4) calc(100vh - var(--masthead-height));
	}

	.save-bar {
		position: sticky;
		bottom: 0;
		z-index: 5;
		display: flex;
		flex-direction: column;
		align-items: flex-start;
		gap: var(--space-2);
		padding: var(--space-3) var(--space-4);
		background: var(--color-bg);
		border-top: 2px solid var(--color-divider);
	}

	.save-row {
		display: flex;
		flex-wrap: wrap;
		align-items: center;
		justify-content: space-between;
		gap: var(--space-3);
		width: 100%;
	}

	/* Bounded so the sentence stays a sentence: the pane is as wide as the window. */
	.save-note {
		max-width: 640px;
		margin: 0;
		font-size: 12.5px;
		color: var(--color-neutral-700);
	}

	.save-actions {
		display: flex;
		align-items: center;
		gap: var(--space-3);
	}

	.save-unsaved {
		font-size: 12.5px;
		font-weight: 600;
		color: var(--color-accent-700);
	}

	/* Failures take the danger role, as the load error above does. */
	.save-problems,
	.save-error {
		margin: 0;
		font-size: 13px;
		color: var(--color-danger-700);
	}

	/* Tailwind's preflight strips list markers from every `ul`, and these are a list of faults. */
	.save-problems {
		padding-left: var(--space-4);
		list-style: disc;
	}

	/*
	 * The warning role rather than the danger role: a profile queued for deletion is a consequence
	 * to understand before saving, the way the legacy-schema consent above it is, not a failure.
	 */
	.save-deletions {
		margin: 0;
		padding: var(--space-2) var(--space-3);
		font-size: 13px;
		background: var(--color-warning-100);
		color: var(--color-warning-700);
		border-left: 4px solid var(--color-warning-700);
	}

	/*
	 * Below 900px the rail stops being a column and stacks above the pane. `admin-rail.svelte`
	 * carries the other half of this breakpoint, where it also stops being sticky.
	 */
	@media (max-width: 900px) {
		.editor {
			grid-template-columns: minmax(0, 1fr);
		}
	}
</style>
