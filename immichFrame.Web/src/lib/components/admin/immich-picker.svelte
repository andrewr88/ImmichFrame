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

	/** What the keyboard may reach, for the trap. `[tabindex="-1"]` is excluded by the selector
	    rather than by a filter, so the dialog itself - which carries one so it can be focused on
	    open - is never a stop on the way round. */
	const FOCUSABLE =
		'a[href], button:not(:disabled), input:not(:disabled), select:not(:disabled), ' +
		'textarea:not(:disabled), [tabindex]:not([tabindex="-1"])';

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
	/** The dialog itself: what takes focus on open, and what the trap measures the keyboard against. */
	let dialog = $state<HTMLDivElement>();
	/**
	 * The control that opened it. Bound rather than read from `document.activeElement` at the
	 * moment of opening, because a click does not focus a button on every platform - Safari leaves
	 * it on the body - and this is the one element focus must come back to.
	 */
	let opener = $state<HTMLButtonElement>();
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

	// Everything that makes this a modal rather than a panel that merely floats, gathered in one
	// effect because it all begins and ends on the same instant.
	//
	// In an effect and not in `toggle()`, which is the whole of why it works: on the tick the button
	// is clicked the dialog does not exist yet, so `focus()` there would be called on nothing at all
	// and the keyboard would be left behind the backdrop. An effect runs after Svelte has put the
	// dialog in the document, which is the earliest moment there is anything to focus.
	$effect(() => {
		if (!panelOpen || !dialog) return;

		const held = dialog;
		const back = opener;
		const behind = document.body.style.overflow;

		// What is behind the backdrop must not move under it. `body` rather than `html`: the root's
		// own overflow is `visible`, so the used value propagates from here to the viewport, and
		// restoring exactly what was there beats assuming it was nothing.
		document.body.style.overflow = 'hidden';

		// The dialog itself rather than the first control inside it: every open starts on the
		// "reading…" line, where there is no search box yet, and a container carrying the role and
		// the name is what a screen reader should be handed on entry.
		held.focus();

		return () => {
			document.body.style.overflow = behind;

			// Back where it came from, and not only when a button closed it - the credentials effect
			// above closes this dialog too. Focus left on a node that has just been removed falls to
			// the document body, which loses the keyboard's place on a page this long.
			back?.focus();
		};
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

	/**
	 * Closing, which is one action however it is asked for - Close, Done, Escape, the backdrop, and
	 * the button that opened it. There is nothing to commit and nothing to discard: every tick was
	 * written through `onChange` as it was made, so the four cannot differ.
	 */
	function close() {
		panelOpen = false;
	}

	/** A click on the ground, and not one that reached here from inside the dialog it wraps. */
	function onBackdrop(event: MouseEvent) {
		if (event.target === event.currentTarget) close();
	}

	// On `window` rather than on the dialog, so that Escape answers wherever the keyboard happens to
	// be - including the instant after opening, before anything inside has been tabbed to, and the
	// case where a browser has left focus on the body. Every picker on the page has one of these and
	// all but an open one return on the first line.
	function onKeydown(event: KeyboardEvent) {
		if (!panelOpen || !dialog) return;

		if (event.key === 'Escape') {
			event.preventDefault();
			close();
			return;
		}

		if (event.key !== 'Tab') return;

		// Asked on the keystroke rather than held from the open, because what is inside the dialog
		// changes underneath it: an open starts on the "reading…" line where Close is the only
		// control, and a search takes two hundred rows down to none. A list taken once would send
		// the keyboard at controls that are no longer in the document.
		const focusable = [...dialog.querySelectorAll<HTMLElement>(FOCUSABLE)];

		// `keydown` is the last moment this can be refused: moving focus is this event's default
		// action, and by `keyup` the browser has already done it - out of the dialog and on to the
		// page behind the backdrop.
		event.preventDefault();

		if (focusable.length === 0) {
			dialog.focus();
			return;
		}

		const active = document.activeElement;
		const at = active instanceof HTMLElement ? focusable.indexOf(active) : -1;
		// From the dialog itself - where focus starts - and from anywhere outside it, forwards means
		// the first control and backwards the last.
		const next =
			at === -1
				? event.shiftKey
					? focusable.length - 1
					: 0
				: (at + (event.shiftKey ? -1 : 1) + focusable.length) % focusable.length;

		focusable[next].focus();
	}

	/** Removing one chip, which is the only way an unmatched entry ever leaves the configuration. */
	function removeAt(index: number) {
		onChange(values.filter((_, other) => other !== index));
	}
</script>

<svelte:window onkeydown={onKeydown} />

<div class="picker" role="group" aria-labelledby="{id}-label">
	{#if values.length === 0}
		<p class="note">Nothing chosen.</p>
	{:else}
		<ul class="chips">
			<!-- Keyed by position as well as by value: a settings file is free to list the same id
			     twice, and a duplicate key would take the page down rather than the duplicate. -->
			{#each values as value, index (`${value}\n${index}`)}
				{@const item = known.get(value)}
				<li class="chip">
					{#if item?.personId}
						{@const face = personThumbnailUrl(source, item.personId)}
						{#if face}
							<img class="face" src={face} alt="" loading="lazy" />
						{/if}
					{/if}
					{#if !item && priming === 'loading'}
						<!-- The name is on its way, so draw the shape of one rather than the identifier it
						     is about to replace. A person id in particular says nothing to a human, and a
						     row of them that turns into names a moment later reads as a page that loaded
						     wrong. -->
						<span class="skeleton animate-pulse" aria-label="Loading {noun.one} name"></span>
					{:else}
						<span class="label">{item?.label ?? value}</span>
					{/if}
					{#if item?.detail}
						<span class="detail">{item.detail}</span>
					{/if}
					{#if list && !item}
						<span class="tag pill pill-warn">
							{partial ? 'not in the part read' : 'unmatched'}
						</span>
					{:else if !item && priming === 'failed'}
						<!-- Deliberately not 'unmatched': that is a claim about this entry not being on the
						     account, and a read that failed supports no claim about it at all. All that is
						     known here is that the names could not be fetched. -->
						<span class="tag pill pill-unknown">names unavailable</span>
					{/if}
					<!-- The icon carries no meaning of its own, so the name is the whole of what tells
					     thirty of these apart. It goes with the glyph rather than being replaced by it. -->
					<button
						type="button"
						class="btn remove"
						onclick={() => removeAt(index)}
						aria-label="Remove {item?.label ?? value}"
					>
						×
					</button>
				</li>
			{/each}
		</ul>
	{/if}

	{#if unmatched.length > 0 && list}
		<!-- Said here rather than only inside the dialog, because this is a statement about the chips
		     above it and they stay on screen after the dialog is closed. -->
		<p class="note warn">
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

	<div class="controls">
		<button type="button" class="btn btn-secondary" bind:this={opener} onclick={toggle}>
			{panelOpen ? 'Close' : `Choose ${noun.many} from Immich`}
		</button>
		<button type="button" class="btn btn-secondary" onclick={() => (manual = !manual)}>
			{manual ? 'Hide text entry' : 'Edit as text'}
		</button>
	</div>

	{#if message}
		<p class="note warn">{message}</p>
	{/if}

	{#if manual}
		<div>
			<label class="note text-label" for="{id}-text">
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
		<!-- `role="presentation"` because the ground is chrome and nothing else: it exists to darken
		     the page and to be clicked on, and the dialog inside it carries all of the semantics. -->
		<div class="dialog-backdrop backdrop" role="presentation" onclick={onBackdrop}>
			<div
				class="dialog picker-dialog"
				role="dialog"
				aria-modal="true"
				aria-labelledby="{id}-dialog-title"
				tabindex="-1"
				bind:this={dialog}
			>
				<header class="dialog-head">
					<div>
						<h2 class="dialog-title" id="{id}-dialog-title">
							Choose {noun.many} from Immich
						</h2>
						<p class="dialog-body sub">
							Every tick is made as you make it, so there is nothing here to confirm.
						</p>
					</div>
					<button type="button" class="btn btn-secondary" onclick={close}>Close</button>
				</header>

				{#if message}
					<!-- The same line the block outside renders, said again in here, because while this is
					     open that one cannot be read: `aria-modal="true"` takes the whole page behind the
					     backdrop out of the accessibility tree, and the tint takes it to 2.96:1 for
					     everyone else - 6.17:1 here. A Reload that answers 502 would otherwise change
					     nothing the administrator can see.

					     A live region, because the case it exists for is the one a silent paragraph
					     misses: Reload answers 502, focus stays on the Reload button, the rows already in
					     hand survive, and this paragraph appearing is the only thing that changed. Meeting
					     it as the first thing inside the dialog covers entering, and a Reload never
					     re-enters. `status` rather than `alert` because the credentials effect writes here
					     too and nothing either of them says is worth an interruption: polite waits for a
					     pause instead of cutting across the row being read. -->
					<p class="note warn" role="status">{message}</p>
				{/if}

				{#if loading}
					<p class="note">Reading {noun.many} from Immich…</p>
				{:else if list}
					<div class="search">
						<input
							id="{id}-search"
							type="search"
							class="input search-input"
							placeholder="Search {noun.many}"
							aria-label="Search {noun.many}"
							bind:value={query}
						/>
						<button type="button" class="btn btn-secondary" onclick={load}>Reload</button>
					</div>

					{#if list.truncated}
						<p class="note warn">
							Immich reports {list.total}
							{noun.many}, and this list stops at {list.items.length}. Somebody who is not in it is
							not necessarily absent from Immich - add them with <em>Edit as text</em>.
						</p>
					{/if}

					{#if kind === 'people' && source.kind === 'inline'}
						<p class="note">
							Faces need a saved account, so these are listed by name - and by the start of their id
							where Immich has no name for them, which is most of a real library. Save the
							configuration and reopen this list to pick by face.
						</p>
					{/if}

					{#if matches.length === 0}
						<p class="note">
							{list.items.length === 0
								? `This Immich account has no ${noun.many}.`
								: `No ${noun.many} match that search.`}
						</p>
					{:else}
						<ul class="rows">
							<!-- Keyed by position too, for the same reason the chips above are: Immich is what
						     decides these values, and a duplicate key would take the page down. -->
							{#each matches.slice(0, MAX_ROWS) as item, index (`${item.key}\n${index}`)}
								{@const face = item.personId ? personThumbnailUrl(source, item.personId) : null}
								<li>
									<label class="row">
										<input
											type="checkbox"
											class="check"
											checked={values.includes(item.key)}
											onchange={(event) =>
												onChange(withChoice(values, item.key, event.currentTarget.checked))}
										/>
										{#if face}
											<img class="row-face" src={face} alt="" loading="lazy" />
										{/if}
										<span class="row-label">{item.label}</span>
										{#if item.detail}
											<span class="row-detail">{item.detail}</span>
										{/if}
									</label>
								</li>
							{/each}
						</ul>

						{#if matches.length > MAX_ROWS}
							<p class="note">
								Showing the first {MAX_ROWS} of {matches.length} matches. Search to narrow it.
							</p>
						{/if}
					{/if}
				{:else}
					<p class="note">
						{#if !message}
							<!-- A fallback for a failure that arrives without words, not a state anybody
							     reaches: no route gets here with `message` empty. Every path into this branch
							     sets it first, and none of them can set it to nothing - `problemDetail` trims
							     each candidate and ends in a non-empty literal, `loadPickerList`'s other
							     failures are literals, and both `blocked` reasons are non-empty constants. So
							     do not go hunting for the way to trigger it, and do not delete it as dead
							     either: saying this beats an empty paragraph if a wordless failure ever does
							     turn up. The button is what this and the message case both need, so it is
							     outside the condition rather than repeated inside it. -->
							Nothing has been read from Immich.
						{/if}
						<button type="button" class="btn btn-secondary" onclick={load}>Read {noun.many}</button>
					</p>
				{/if}

				<footer class="dialog-actions foot">
					<p class="chosen">
						{values.length === 1 ? `1 ${noun.one} chosen` : `${values.length} ${noun.many} chosen`}
					</p>
					<button type="button" class="btn btn-primary" onclick={close}>Done</button>
				</footer>
			</div>
		</div>
	{/if}
</div>

<style>
	/*
	 * Every rule that lands on an element already wearing an `admin-theme.css` component class is
	 * compounded with it. The two carry equal specificity and this page imports the components
	 * before the sheet, so an uncompounded rule would lose the tie silently - `routes/admin`'s own
	 * import comment is where that order is written down.
	 */

	.picker {
		display: flex;
		flex-direction: column;
		gap: var(--space-2);
	}

	/* Captions, notices and refusals alike: played down, and raised to the warning role where the
	   line is something to put right rather than something being reported. */
	.note {
		margin: 0;
		font-size: 12px;
		color: var(--color-neutral-700);
	}

	.note.warn {
		color: var(--color-warning-700);
	}

	.note.text-label {
		display: block;
		margin-bottom: var(--space-1);
	}

	/* Chips */

	.chips {
		display: flex;
		flex-wrap: wrap;
		gap: var(--space-2);
		margin: 0;
		padding: 0;
		list-style: none;
	}

	/*
	 * `color` here is defensive rather than a fix for anything present: with the containment wrapper
	 * gone nothing between `.admin-theme` and a chip sets one, so this `<li>` already inherits the
	 * sheet's own `--color-text` and the declaration is a no-op today. Kept because the chip is what
	 * would suffer most if something overhead ever did set one - it carries its own near-white fill,
	 * so near-white text would leave it blank. The system's own `.btn` and `.input` state theirs for
	 * a reason that does not reach an `<li>`: the UA stylesheet gives a form control a colour of its
	 * own to override, and gives a list item none.
	 */
	.chip {
		display: inline-flex;
		align-items: center;
		gap: var(--space-2);
		max-width: 100%;
		padding: 4px 4px 4px 9px;
		color: var(--color-text);
		background: var(--color-neutral-100);
		border: 1px solid var(--color-divider);
	}

	.face {
		flex: none;
		width: 22px;
		height: 22px;
		object-fit: cover;
	}

	/*
	 * A shape and not a word, so it is sized like the name it stands in for rather than like a box:
	 * 13px tall is this chip's own line box, and the width is a plausible name.
	 *
	 * 600 and not 400 because this shape has to survive being pulsed. `animate-pulse` takes the
	 * whole element to opacity 0.5 at the trough, so the fill is measured composited: neutral-400
	 * is 1.84:1 on the chip at rest and 1.33:1 there, which is a blank gap - the very "page that
	 * loaded wrong" the placeholder was added to prevent. Neutral-600 is 3.94:1 and 1.83:1, so the
	 * shape is still a shape at the bottom of the pulse.
	 */
	.skeleton {
		flex: none;
		width: 96px;
		height: 13px;
		background: var(--color-neutral-600);
	}

	.label,
	.detail {
		min-width: 0;
		overflow: hidden;
		text-overflow: ellipsis;
		white-space: nowrap;
	}

	.label {
		font-size: 13px;
		font-weight: 600;
	}

	.detail {
		font-size: 11px;
		color: var(--color-neutral-700);
	}

	.tag.pill {
		flex: none;
	}

	/*
	 * What makes both of these a pill is the edge, not the fill - the same answer the chip around
	 * them gives, and `.rows` below, and for the same reason: on a ground this light no fill pale
	 * enough to carry 11px text at AA is far enough from it to be a shape. The ceiling is
	 * neutral-300, 1.36:1 against the chip fill, and it already drops the text to 4.39:1. So the
	 * fill stays the one the role asks for and the border draws the pill.
	 *
	 * Something to look into rather than something that failed, on both counts: the warning role.
	 * Fill 1.01:1 against the chip and text 6.80:1 on it; the edge is the role's own 700, which is
	 * 6.85:1 against the chip - `.tag-outline`'s construction, in the warning role.
	 */
	.tag.pill-warn {
		background: var(--color-warning-100);
		color: var(--color-warning-700);
		border: 1px solid var(--color-warning-700);
	}

	/*
	 * Neither a warning nor an error but an absence of information, and dressed as one: the played
	 * down tone on a neutral fill. Neutral-200 rather than the `.tag-neutral` the system offers,
	 * because that fill is the chip's own - though at 1.13:1 against it the fill is not what is
	 * seen either way; text on it is 5.30:1. The edge is a step lighter than the text at
	 * neutral-600, 3.94:1 against the chip: enough to be a graphic object, and quieter than the
	 * warning beside it, which is the order these two belong in.
	 */
	.tag.pill-unknown {
		background: var(--color-neutral-200);
		color: var(--color-neutral-700);
		border: 1px solid var(--color-neutral-600);
	}

	/*
	 * Destructive, so it takes the accent ramp at 700 - the dress `accounts-section.svelte` gives
	 * its own Remove. Sized to the chip rather than to the page: `.btn`'s own padding is drawn for
	 * a control that carries a word, and this one carries a glyph 22px tall beside a face.
	 */
	.btn.remove {
		flex: none;
		width: 20px;
		height: 20px;
		padding: 0;
		font-size: 15px;
		line-height: 1;
		color: var(--color-accent-700);
	}

	.btn.remove:hover {
		background: var(--color-accent-100);
	}

	.controls {
		display: flex;
		flex-wrap: wrap;
		align-items: center;
		gap: var(--space-2);
	}

	/* The dialog */

	/*
	 * A tint of the text colour, which is where the design takes it from, rather than the sheet's
	 * neutral-900 - and z-index 20, which the sheet has no need to state and this component does:
	 * the masthead sticks at 10 and the profile strip and save bar at 5, so a backdrop left at
	 * `auto` would be painted under all three and the page would go on being clickable through it.
	 */
	.dialog-backdrop.backdrop {
		z-index: 20;
		/*
		 * Below roughly 370px of viewport height the dialog's fixed parts - header, search row, the
		 * truncation notice and the people note, footer - are taller than the 82vh it is allowed,
		 * `.rows` has already given up everything `min-height: 0` lets it give, and the prose lands
		 * outside the 2px edge. `auto` here rather than `hidden` on the dialog: the backdrop's
		 * scrollable region covers what overflows, so the sentence is still reachable. Safe to
		 * scroll from the top - grid tracks are packed from the start edge, so `place-items: center`
		 * centres the dialog inside its own track and never above the container.
		 */
		overflow: auto;
		background: color-mix(in srgb, var(--color-text) 55%, transparent);
	}

	/*
	 * The heavy edge the design gives a dialog; the elevation is already the system's. `color` is the
	 * chip's guard again and just as redundant: this is a `<div role="dialog">` rather than a
	 * `<dialog>` element, so there is no UA `CanvasText` on it to beat and it inherits `--color-text`
	 * like everything else on the page.
	 */
	.dialog.picker-dialog {
		width: min(700px, 100%);
		max-height: 82vh;
		color: var(--color-text);
		border: 2px solid var(--color-text);
	}

	.dialog-head {
		display: flex;
		align-items: flex-start;
		justify-content: space-between;
		gap: var(--space-3);
		padding-bottom: var(--space-3);
		border-bottom: 1px solid var(--color-divider);
	}

	/*
	 * The system fades its dialog prose to 0.85, which is right for a sentence and wrong for the
	 * band a list of choices sits in - so `.dialog-body` is worn by the one line that is prose.
	 */
	.dialog-body.sub {
		margin: var(--space-1) 0 0;
		font-size: 12px;
	}

	.search {
		display: flex;
		flex-wrap: wrap;
		align-items: center;
		gap: var(--space-2);
	}

	/* Shares its line with Reload, so it is sized by the flex line rather than by `.input`'s own
	   `width: 100%`, and `min-width: 0` is what lets it shrink past a text box's intrinsic width. */
	.input.search-input {
		flex: 1;
		min-width: 0;
	}

	/*
	 * Tailwind's preflight paints a placeholder at half of `currentColor`, which composites to
	 * 3.10:1 on the input's own surface - clear of the 3:1 floor but short of AA. Neutral-700 is
	 * the retune `setting-field.svelte` and `accounts-section.svelte` already make.
	 */
	.input::placeholder {
		color: var(--color-neutral-700);
	}

	/*
	 * The one part of the dialog that scrolls, so it is the one that may shrink: `min-height: 0`
	 * is what lets a flex item go below its content, and without it the list would push the footer
	 * out of the 82vh instead of scrolling inside it. The rule around it says where the scrollable
	 * region ends, which a row cut off mid-height otherwise does not.
	 */
	.rows {
		flex: 1;
		min-height: 0;
		overflow-y: auto;
		margin: 0;
		padding: 0;
		list-style: none;
		border: 1px solid var(--color-divider);
	}

	.row {
		display: flex;
		align-items: center;
		gap: var(--space-2);
		padding: var(--space-1) var(--space-2);
		cursor: pointer;
	}

	.row:hover {
		background: color-mix(in srgb, var(--color-text) 6%, transparent);
	}

	.row-face {
		flex: none;
		width: 32px;
		height: 32px;
		object-fit: cover;
	}

	.row-label,
	.row-detail {
		min-width: 0;
		overflow: hidden;
		text-overflow: ellipsis;
		white-space: nowrap;
	}

	.row-label {
		font-size: 14px;
	}

	.row-detail {
		font-size: 12px;
		color: var(--color-neutral-700);
	}

	/*
	 * 003's box, copied rather than shared: `setting-field.svelte` scopes its own to itself and
	 * `admin-theme.css` is not this task's to extend. A real check box with the platform's rendering
	 * taken off, and not a button wearing `aria-pressed` - the label association, the space bar and
	 * the announced state all come with the element and none of them come with the button.
	 *
	 * Fill and ground swap places against the reference: there the box sits on `--color-bg` and
	 * fills with `--color-surface`, here the dialog is the surface, so the box takes the page
	 * colour to stay the lighter of the two.
	 */
	.check {
		appearance: none;
		display: grid;
		place-content: center;
		flex: none;
		width: 24px;
		height: 24px;
		margin: 0;
		background: var(--color-bg);
		border: 1px solid var(--color-neutral-900);
		border-radius: var(--radius-sm);
		cursor: pointer;
	}

	.check:checked {
		background: var(--color-accent);
		border-color: var(--color-accent);
	}

	.check:checked::after {
		content: '';
		width: 12px;
		height: 12px;
		background: var(--color-bg);
		clip-path: polygon(14% 44%, 0 65%, 50% 100%, 100% 16%, 80% 0%, 43% 62%);
	}

	.dialog-actions.foot {
		align-items: center;
		justify-content: space-between;
		margin-top: 0;
		padding-top: var(--space-3);
		border-top: 1px solid var(--color-divider);
	}

	.chosen {
		margin: 0;
		font-size: 12px;
		color: var(--color-neutral-700);
	}

	/*
	 * The escape hatch that travels with `appearance: none`, and the reason 003 needed one:
	 * forced-colors mode rewrites colour, background and border into the user's own palette, which
	 * is why every other rule here comes through it working. `appearance: none` is the declaration
	 * it cannot rewrite - it removes the rendering that rewriting relies on, and with
	 * `--color-accent` and `--color-bg` both forced to `Canvas` a ticked box would be an empty one.
	 * Hand the box back to the platform and drop the clipped mark, so it cannot be painted over the
	 * platform's own tick.
	 */
	@media (forced-colors: active) {
		.check {
			appearance: auto;
		}

		.check:checked::after {
			content: none;
		}
	}
</style>
