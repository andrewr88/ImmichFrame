<script lang="ts">
	import { untrack } from 'svelte';
	import type { FieldSpec } from './admin-config';
	import {
		personThumbnailUrl,
		pickedValues,
		pickerNouns,
		sourceKey,
		withChoice,
		type PickerItem,
		type PickerKind,
		type PickerList,
		type PickerSource
	} from './immich-picker';
	import { cachedPickerList, dropPickerList, pickerList } from './picker-cache';
	import SettingField from './setting-field.svelte';

	interface Props {
		/** Namespace for this picker's own controls, and the id of the label naming it. */
		id: string;
		kind: PickerKind;
		/** Which credentials this field's account is browsed with. */
		source: PickerSource;
		/** Exactly what the configuration holds for this field, in the order it holds it. */
		values: string[];
		onChange: (values: string[]) => void;
	}

	let { id, kind, source, values, onChange }: Props = $props();

	/**
	 * How many rows one panel draws. A library with five thousand people would otherwise be five
	 * thousand rows of DOM; the search box is what reaches the rest.
	 */
	const MAX_ROWS = 200;

	/** What the manual fallback renders. The label is the row's, and is not drawn again here. */
	const textSpec: FieldSpec = { label: '', kind: 'lines' };

	/**
	 * Which read is the current one. Not reactive: it decides whether an answer still has anybody
	 * waiting for it, and an older answer must not touch the spinner belonging to a newer request.
	 */
	let request = 0;

	let panelOpen = $state(false);
	let manual = $state(false);
	let query = $state('');
	let loading = $state(false);
	let message = $state('');
	/**
	 * What another picker on this page has already read from these same credentials, if anything.
	 * Taken here rather than awaited, so a picker created after the read has landed - a profile
	 * section expanded, an account added back - draws its rows named on their first frame.
	 */
	const alreadyRead = untrack(() => {
		const result = values.length > 0 ? cachedPickerList(kind, source) : undefined;

		return result?.ok ? { list: result.list, source: sourceKey(source) } : null;
	});

	let list = $state<PickerList | null>(alreadyRead?.list ?? null);
	/**
	 * Where the quiet read that names what is already configured has got to.
	 * <p>
	 * Rows need all three: a raw identifier is the honest thing to draw once there is nothing better
	 * coming, and the wrong thing to draw while an answer is on its way - a page of GUIDs that turn
	 * into names a moment later reads as broken. 'idle' covers the cases prime() returns from without
	 * asking anything, which is why it is the state a picker with nothing configured sits in.
	 * </p>
	 */
	let priming = $state<'idle' | 'loading' | 'failed'>('idle');
	/** What the list in hand was read with, so it can be dropped when that stops being true. */
	let listSource = $state<string | null>(alreadyRead?.source ?? null);
	/** The credentials this picker has already seen, so a change to them can be noticed. */
	let seenSource = $state<string | null>(null);

	let noun = $derived(pickerNouns[kind]);
	let key = $derived(sourceKey(source));
	let known = $derived(new Map((list?.items ?? []).map((item) => [item.key, item])));
	let unmatched = $derived(list ? values.filter((value) => !known.has(value)) : []);
	/**
	 * Whether the list in hand is the whole of what Immich holds. A cut-short list cannot support
	 * the conclusion that an entry missing from it is gone: it may simply lie past where the read
	 * stopped, which is the very mistake the proxy reports truncation to prevent.
	 */
	let partial = $derived(list?.truncated === true);
	let matches = $derived(
		(list?.items ?? []).filter((item) => matchesQuery(item, query.trim().toLowerCase()))
	);

	const button =
		'rounded border border-neutral-600 px-2 py-0.5 text-xs text-neutral-200 hover:border-neutral-400';

	// A list read from one server must never be shown as the contents of another, so it - and any
	// message about it - is dropped the moment the credentials behind it change: a URL retyped, a
	// key entered, an account saved.
	$effect(() => {
		const current = key;

		untrack(() => {
			if (seenSource !== null && seenSource !== current) {
				reset();

				// An open panel is a statement about the credentials that were in force when it was
				// filled. Left as it was it reads as "this server has nothing" - including in the
				// moment a mistyped URL is corrected. Not reloaded automatically either: the URL is
				// bound on input, so that would be one request per keystroke.
				if (panelOpen) {
					message =
						source.kind === 'blocked'
							? source.reason
							: 'The credentials for this account changed, so the list was discarded.';
				}

				if (panelOpen && source.kind === 'blocked') panelOpen = false;
			}

			seenSource = current;
		});
	});

	/**
	 * What the quiet read was last started for, as the values themselves. Held so that the effect
	 * below can tell "there is something new here to name" from every other reason it might run.
	 */
	let primedFor: string | null = null;

	// Runs on creation and whenever the configured values change - which is the profile tab strip's
	// doing as much as the administrator's. `EntryEditor` is rendered unkeyed, so selecting another
	// configuration swaps this picker's props rather than building a new picker: switching from a
	// profile with no people to one that has them used to leave the first run's "nothing to name
	// here" verdict standing, and the rows sat on bare identifiers until the panel was opened.
	//
	// The values are the only dependency, deliberately. The credentials are not: the server URL is
	// bound on input, so waking on those would be one read per keystroke - which is why `list` is not
	// a dependency either, since a credential change clears it.
	$effect(() => {
		const signature = values.join('\n');

		if (values.length === 0 || signature === primedFor) return;

		untrack(() => {
			primedFor = signature;
			prime();
		});
	});

	/**
	 * The names for what is already configured, without the administrator opening anything: `list`
	 * was filled only by `load()`, so every configured entry on a freshly loaded page rendered as
	 * the raw identifier the settings file holds.
	 *
	 * Quieter than `load()` on purpose. Nobody asked for this read, so a failed one changes nothing
	 * on screen - no message, no text box opened by itself, and no spinner, which belongs to the
	 * panel - and the page reads exactly as it does today with Immich unreachable.
	 */
	async function prime() {
		// Nothing to put a name to, or nowhere to ask: neither is worth a request. Left 'idle' rather
		// than 'failed' - no read was attempted, so there is nothing to report having gone wrong.
		if (values.length === 0 || source.kind === 'blocked') return;

		// A list already in hand from another picker on these credentials needs no second read, and
		// no placeholder either: its rows are named on this very frame.
		if (list) return;

		priming = 'loading';

		const current = key;
		const started = request;
		const result = await pickerList(kind, source);

		// A read the administrator asked for supersedes this one whichever answers first: it went out
		// later, and it is the one the panel is waiting on. Its own spinner is showing, so this one
		// stops claiming to be loading and says nothing else.
		if (started !== request) {
			priming = 'idle';
			return;
		}

		// Same rule as `load()`: an answer about the server that was named when the request went out
		// says nothing about the one named now.
		if (current !== sourceKey(source)) {
			priming = 'idle';
			return;
		}

		if (!result.ok) {
			// Still quiet - no message, no text box, no panel - but no longer invisible. A row that
			// went on showing a bare identifier with nothing to say why was indistinguishable from one
			// whose names were simply slow, which is what made a failing read here so hard to notice.
			priming = 'failed';
			return;
		}

		priming = 'idle';
		list = result.list;
		listSource = current;
	}

	function reset() {
		list = null;
		listSource = null;
		message = '';
		query = '';

		// The mark belongs to the credentials that produced it. Kept across a change it would report
		// the old server's failure against the new one's rows.
		priming = 'idle';
	}

	function matchesQuery(item: PickerItem, needle: string): boolean {
		if (!needle) return true;

		return (
			item.label.toLowerCase().includes(needle) ||
			item.key.toLowerCase().includes(needle) ||
			(item.detail?.toLowerCase().includes(needle) ?? false)
		);
	}

	async function load() {
		const current = key;
		const token = ++request;

		loading = true;
		message = '';

		// This read supersedes the quiet one, spinner and all: a skeleton left behind would claim a
		// second answer is still coming, and a 'names unavailable' mark left behind would report the
		// previous failure over the top of the read now in flight.
		priming = 'idle';

		// Dropped rather than read through: this is the administrator asking Immich again, and what
		// the cache is for is sparing the *first* read of a list several pickers share, not
		// answering this one. The read still goes through it so that the fresh answer is what any
		// later picker on the page starts from.
		dropPickerList(kind, source);

		const result = await pickerList(kind, source);

		// Only the newest read may speak. A superseded one clearing the spinner would report the
		// request still in flight behind it as finished, and offer to start a third.
		if (token !== request) return;

		loading = false;

		// The account can be edited while a slow server is answering, and an answer about the server
		// that was named when the request went out says nothing about the one named now.
		if (current !== sourceKey(source)) return;

		if (result.ok) {
			list = result.list;
			listSource = current;
			return;
		}

		message = result.message;
		// A picker that cannot answer must not take the field with it: identifiers typed by hand are
		// worse than a list of names, and much better than an unusable configuration.
		manual = true;

		// A failed read says nothing about a list already in hand from this same account, so the
		// names and the marks on the rows outside the panel survive a server that is briefly down.
		if (listSource !== current) {
			list = null;
			listSource = null;
		}
	}

	function toggle() {
		if (source.kind === 'blocked') {
			// Refused rather than opened. There are no credentials here that describe this account,
			// and the ones there are describe some other server.
			message = source.reason;
			manual = true;
			panelOpen = false;
			return;
		}

		panelOpen = !panelOpen;

		// Every open reads the list again rather than redisplaying the one in hand. The proxy caches
		// nothing on purpose - a stale album list is worse than a slow one - and what is retained
		// here is for labelling the rows outside the panel, not for choosing from.
		if (panelOpen) load();
	}

	/** Removing one row, which is the only way an unmatched entry ever leaves the configuration. */
	function removeAt(index: number) {
		onChange(values.filter((_, other) => other !== index));
	}
</script>

<div class="space-y-2" role="group" aria-labelledby="{id}-label">
	{#if values.length === 0}
		<p class="text-xs text-neutral-500">Nothing chosen.</p>
	{:else}
		<ul class="space-y-1">
			<!-- Keyed by position as well as by value: a settings file is free to list the same id
			     twice, and a duplicate key would take the page down rather than the duplicate. -->
			{#each values as value, index (`${value}\n${index}`)}
				{@const item = known.get(value)}
				<li class="flex items-center gap-2 rounded border border-neutral-800 px-2 py-1">
					{#if item?.personId}
						{@const face = personThumbnailUrl(source, item.personId)}
						{#if face}
							<img class="h-8 w-8 shrink-0 rounded object-cover" src={face} alt="" loading="lazy" />
						{/if}
					{/if}
					{#if !item && priming === 'loading'}
						<!-- The name is on its way, so draw the shape of one rather than the identifier it
						     is about to replace. A person id in particular says nothing to a human, and a
						     row of them that turns into names a moment later reads as a page that loaded
						     wrong. -->
						<span
							class="h-4 w-32 animate-pulse rounded bg-neutral-800"
							aria-label="Loading {noun.one} name"
						></span>
					{:else}
						<span class="truncate text-sm text-neutral-200">{item?.label ?? value}</span>
					{/if}
					{#if item?.detail}
						<span class="truncate text-xs text-neutral-500">{item.detail}</span>
					{/if}
					{#if list && !item}
						<span class="shrink-0 rounded bg-amber-950 px-1 text-xs text-amber-300">
							{partial ? 'not in the part read' : 'unmatched'}
						</span>
					{:else if !item && priming === 'failed'}
						<!-- Deliberately not 'unmatched': that is a claim about this entry not being on the
						     account, and a read that failed supports no claim about it at all. All that is
						     known here is that the names could not be fetched. -->
						<span class="shrink-0 rounded bg-neutral-800 px-1 text-xs text-neutral-400">
							names unavailable
						</span>
					{/if}
					<button
						type="button"
						class="{button} ml-auto shrink-0"
						onclick={() => removeAt(index)}
						aria-label="Remove {item?.label ?? value}"
					>
						Remove
					</button>
				</li>
			{/each}
		</ul>
	{/if}

	{#if unmatched.length > 0 && list}
		<!-- Said here rather than only inside the panel, because this is a statement about the rows
		     above it and they stay on screen after the panel is closed. -->
		<p class="text-xs text-amber-400">
			{unmatched.length === 1 ? 'One of these' : `${unmatched.length} of these`}
			{#if partial}
				was not in the {noun.many} read from Immich, which stopped at {list.items.length} of {list.total}
				- so they may simply lie past that point rather than be gone.
			{:else}
				{unmatched.length === 1 ? 'is' : 'are'} not on this Immich account - deleted, or belonging to
				another account.
			{/if}
			They are kept exactly as configured and saved unchanged unless you remove them.
		</p>
	{/if}

	<div class="flex flex-wrap items-center gap-2">
		<button type="button" class={button} onclick={toggle}>
			{panelOpen ? 'Close' : `Choose ${noun.many} from Immich`}
		</button>
		<button type="button" class={button} onclick={() => (manual = !manual)}>
			{manual ? 'Hide text entry' : 'Edit as text'}
		</button>
	</div>

	{#if message}
		<p class="text-xs text-amber-400">{message}</p>
	{/if}

	{#if manual}
		<div>
			<label class="text-xs text-neutral-500" for="{id}-text">
				{kind === 'tags' ? 'One full tag path per line.' : 'One id per line.'}
			</label>
			<SettingField
				id="{id}-text"
				spec={textSpec}
				value={values}
				onChange={(value) => onChange(pickedValues(value))}
			/>
		</div>
	{/if}

	{#if panelOpen}
		<div class="rounded border border-neutral-700 bg-neutral-900/60 p-2">
			{#if loading}
				<p class="text-xs text-neutral-400">Reading {noun.many} from Immich…</p>
			{:else if list}
				<div class="flex flex-wrap items-center gap-2">
					<input
						id="{id}-search"
						type="search"
						class="min-w-0 flex-1 rounded border border-neutral-700 bg-neutral-900 px-2 py-1 text-sm
							text-neutral-100"
						placeholder="Search {noun.many}"
						aria-label="Search {noun.many}"
						bind:value={query}
					/>
					<button type="button" class={button} onclick={load}>Reload</button>
				</div>

				{#if list.truncated}
					<p class="mt-1 text-xs text-amber-400">
						Immich reports {list.total}
						{noun.many}, and this list stops at {list.items.length}. Somebody who is not in it is
						not necessarily absent from Immich - add them with <em>Edit as text</em>.
					</p>
				{/if}

				{#if kind === 'people' && source.kind === 'inline'}
					<p class="mt-1 text-xs text-neutral-400">
						Faces need a saved account, so these are listed by name - and by the start of their id
						where Immich has no name for them, which is most of a real library. Save the
						configuration and reopen this list to pick by face.
					</p>
				{/if}

				{#if matches.length === 0}
					<p class="mt-2 text-xs text-neutral-400">
						{list.items.length === 0
							? `This Immich account has no ${noun.many}.`
							: `No ${noun.many} match that search.`}
					</p>
				{:else}
					<ul class="mt-2 max-h-64 space-y-1 overflow-y-auto">
						<!-- Keyed by position too, for the same reason the rows above are: Immich is what
					     decides these values, and a duplicate key would take the page down. -->
						{#each matches.slice(0, MAX_ROWS) as item, index (`${item.key}\n${index}`)}
							{@const face = item.personId ? personThumbnailUrl(source, item.personId) : null}
							<li>
								<label class="flex items-center gap-2 rounded px-1 py-0.5 hover:bg-neutral-800">
									<input
										type="checkbox"
										class="h-4 w-4 shrink-0 accent-sky-500"
										checked={values.includes(item.key)}
										onchange={(event) =>
											onChange(withChoice(values, item.key, event.currentTarget.checked))}
									/>
									{#if face}
										<img
											class="h-8 w-8 shrink-0 rounded object-cover"
											src={face}
											alt=""
											loading="lazy"
										/>
									{/if}
									<span class="truncate text-sm text-neutral-200">{item.label}</span>
									{#if item.detail}
										<span class="truncate text-xs text-neutral-500">{item.detail}</span>
									{/if}
								</label>
							</li>
						{/each}
					</ul>

					{#if matches.length > MAX_ROWS}
						<p class="mt-1 text-xs text-neutral-500">
							Showing the first {MAX_ROWS} of {matches.length} matches. Search to narrow it.
						</p>
					{/if}
				{/if}
			{:else}
				<p class="text-xs text-neutral-400">
					Nothing has been read from Immich.
					<button type="button" class={button} onclick={load}>Read {noun.many}</button>
				</p>
			{/if}
		</div>
	{/if}
</div>
