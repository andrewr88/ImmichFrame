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
	<p class="text-muted loading">Loading the configuration…</p>
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

				<!-- `aria-pressed` says to assistive technology what the fill says on screen: which of
				     these configurations is the one being edited. -->
				<div class="tabs">
					{#each entries as entry, index (entry.name + index)}
						<button
							type="button"
							class="tab"
							class:is-active={index === selected}
							aria-pressed={index === selected}
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
						<button type="button" class="btn btn-secondary add-profile" onclick={addProfile}>
							Add profile
						</button>
						{#if current && !current.isDefault}
							<button
								type="button"
								class="btn btn-secondary delete-profile"
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
								class="btn btn-primary save-button"
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
		padding: var(--space-4);
	}

	.card.banner {
		margin-bottom: var(--space-4);
	}

	/*
	 * The page gives the editor the whole window, edge to edge. The loading line and the load error
	 * stand where the editor will be, so they keep a page's margins of their own: the gutter at the
	 * sides, and room under the masthead.
	 */
	.loading,
	.card.notice {
		margin: var(--space-6) var(--gutter);
	}

	/*
	 * Failures take the danger role; the accent is for emphasis, not for faults. A callout rather
	 * than a card: the role's tint, ruled all round in the role's own colour, at the callout's
	 * radius. The text is 5.91:1 on the tint. Capped at the gate cards' measure, for the reason they
	 * are: a sentence run across the whole window is a line too long to read.
	 */
	.card.notice {
		max-width: 68ch;
		background: var(--color-danger-100);
		color: var(--color-danger-700);
		border: 1px solid var(--color-danger-700);
		border-radius: var(--radius-callout);
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

	/* The masthead's caption, in the masthead's tone: muted, 7.37:1 on the card. */
	.source-label {
		margin: 0;
		font-size: 10.5px;
		letter-spacing: 0.14em;
		text-transform: uppercase;
		color: var(--color-text-muted);
	}

	.mono {
		font-family: 'Overpass Mono', ui-monospace, monospace;
		font-size: 13px;
	}

	/*
	 * The warning role, used where this banner has always been amber. Read-only and legacy-schema
	 * are conditions to understand before saving, not failures, so they stay off the danger role
	 * the load error above uses. A callout, as that error is: 6.88:1 on the tint.
	 */
	.warning {
		display: flex;
		flex-direction: column;
		gap: var(--space-2);
		padding: var(--space-3) var(--space-4);
		background: var(--color-warning-100);
		color: var(--color-warning-700);
		border: 1px solid var(--color-warning-700);
		border-radius: var(--radius-callout);
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
		gap: var(--space-3) 18px;
		/* The page publishes the gutter too, and the masthead pads by it: their ends line up. */
		padding: 14px var(--gutter);
		background: var(--color-bg);
		border-bottom: 1px solid var(--color-divider);
	}

	/*
	 * The design's faint caption, the tone the rail's group headings wear beside it: 4.83:1 on the
	 * strip's white, which is the only ground it is ever on.
	 */
	.strip-label {
		font-size: 10px;
		letter-spacing: 0.14em;
		text-transform: uppercase;
		color: var(--color-text-faint);
	}

	/*
	 * One segmented control rather than a row of separate buttons: the track belongs to the set and
	 * the tabs sit in it, which is what says that picking one of these is picking among alternatives.
	 *
	 * The edge is transparent, over the track's own fill, and with the 3px of padding inside it makes
	 * the design's 4px. It is there for forced-colors mode, which drops the fill and paints every
	 * border in the user's palette: without an edge of its own, nothing there would hold the tabs
	 * together as one set. Which of them is selected, that mode is told below.
	 */
	.tabs {
		display: flex;
		flex-wrap: wrap;
		gap: 2px;
		padding: 3px;
		background: var(--color-surface-input);
		border: 1px solid transparent;
		border-radius: var(--radius-pill);
	}

	/* Neutral-700 on the track, 9.28:1; the selected tab is white on the primary, 7.00:1. */
	.tab {
		padding: 7px 16px;
		font-family: var(--font-heading);
		font-weight: 600;
		font-size: 13px;
		line-height: 1.2;
		color: var(--color-neutral-700);
		background: transparent;
		border: 0;
		border-radius: var(--radius-pill);
		cursor: pointer;
	}

	.tab.is-active {
		background: var(--color-accent);
		color: var(--color-bg);
	}

	/*
	 * Forced-colors mode drops the primary fill, and with it the only thing on screen that says which
	 * tab is selected, so the palette's own selected-item pair says it there instead. Held out of the
	 * forced palette so that that pair is what paints - which leaves this tab's ring to colour too.
	 * It lies on the track, which the palette grounds in `Canvas`, so it takes `CanvasText`: the
	 * sheet's primary would show at whatever contrast the user's palette happened to give it.
	 */
	@media (forced-colors: active) {
		.tab.is-active {
			forced-color-adjust: none;
			background: SelectedItem;
			color: SelectedItemText;
		}

		.tab.is-active:focus-visible {
			outline-color: CanvasText;
		}
	}

	.strip-end {
		display: flex;
		flex-wrap: wrap;
		align-items: center;
		gap: var(--space-2);
		margin-left: auto;
	}

	/*
	 * Compounded with the component class, like the cards above: `.admin-theme .input` sets every
	 * one of these properties and carries the specificity a bare `.new-profile` would, so which of
	 * them won would come down to the order the bundler emitted the two sheets in. The compact box
	 * the design gives this strip, level with the compact buttons beside it: their line height, and
	 * no floor under it taller than they are.
	 */
	.input.new-profile {
		width: 200px;
		min-height: 0;
		padding: 6px 8px;
		font-size: 13px;
		line-height: 1.2;
	}

	/*
	 * Tailwind's preflight mixes a placeholder at half of `currentColor` - `color-mix(in oklab,
	 * currentcolor 50%, transparent)`, which keeps the colour and halves its alpha - so it composites
	 * `--color-text` over the box's own fill to #81868f: 3.29:1, and 3.38:1 over the white the box
	 * turns when focused, where the text typed into the same box gets 15.97:1. Clear of the 3:1
	 * floor, then, but short of AA at 13px, and AA is the bar here - the `aria-label` names this box
	 * for assistive technology only, which leaves the placeholder the one caption a sighted reader
	 * has for what belongs in it. Muted reads 6.80:1 on the fill and 7.56:1 on white.
	 */
	.input.new-profile::placeholder {
		color: var(--color-text-muted);
	}

	/* The compact size the design gives the two buttons in this strip. */
	.btn.add-profile,
	.btn.delete-profile {
		padding: 6px 10px;
		font-size: 13px;
	}

	/*
	 * Deleting a profile takes every frame on it offline, so it is set apart from Add profile the way
	 * the design sets it apart: the tint's border for an edge rather than the neutral one.
	 * Destructive, but not a fault, so it stays off the danger role - its label is the secondary
	 * button's primary, 7.00:1.
	 */
	.btn.delete-profile {
		border-color: var(--color-accent-300);
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
		padding: var(--space-6) var(--gutter) calc(100vh - var(--masthead-height));
	}

	.save-bar {
		position: sticky;
		bottom: 0;
		z-index: 5;
		display: flex;
		flex-direction: column;
		align-items: flex-start;
		gap: var(--space-2);
		padding: 14px var(--gutter);
		background: var(--color-bg);
		border-top: 1px solid var(--color-divider);
	}

	.save-row {
		display: flex;
		flex-wrap: wrap;
		align-items: center;
		justify-content: space-between;
		gap: var(--space-4);
		width: 100%;
	}

	/* Bounded so the sentence stays a sentence: the pane is as wide as the window. Muted, 7.56:1. */
	.save-note {
		max-width: 640px;
		margin: 0;
		font-size: 12.5px;
		color: var(--color-text-muted);
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

	/*
	 * The design's size for the page's one primary action, a step over the sheet's button.
	 * Compounded with `.btn`, which sets the padding at a bare class's specificity.
	 */
	.btn.save-button {
		padding: 10px 18px;
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
	 * to understand before saving, the way the legacy-schema consent above it is, not a failure. A
	 * callout, as that consent is.
	 */
	.save-deletions {
		margin: 0;
		padding: var(--space-2) var(--space-4);
		font-size: 13px;
		background: var(--color-warning-100);
		color: var(--color-warning-700);
		border: 1px solid var(--color-warning-700);
		border-radius: var(--radius-callout);
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
