<script lang="ts">
	import { ACCOUNTS_KEY, declaredKeyOf, generalSections, type EditableEntry } from './admin-config';

	interface Props {
		/** The configuration on screen. What it declares is what the override badges count. */
		entry: EditableEntry;
		/** Every Immich account in the document, which the installation item reports. */
		accountCount: number;
		/**
		 * How tall the masthead is at this moment. The page publishes the same measurement as
		 * `--masthead-height` for the sticky rules that read it in CSS, but it arrives here as a prop
		 * rather than through `getComputedStyle`: a property read inside the effect below is read
		 * once per run of it, and nothing in it would re-run when the masthead re-wraps, so the CSS
		 * would follow the new height while the arithmetic went on using the old one.
		 */
		mastheadHeight: number;
		/**
		 * How tall the profile strip is at this moment. It is sticky and it wraps on a narrow
		 * viewport, so it is reported rather than assumed: together with the masthead it is what
		 * covers the top of the pane, and a highlight computed without it would name a section
		 * sitting behind the chrome instead of the one being read.
		 */
		stripHeight: number;
	}

	let { entry, accountCount, mastheadHeight, stripHeight }: Props = $props();

	/** The id on `accounts-section.svelte`'s section. */
	const ACCOUNTS_ID = 'section-accounts';

	/** The id on `entry-editor.svelte`'s trailing section, which the rail names for what it does. */
	const PHOTO_SELECTION_ID = 'section-photo-selection';

	/** The id `entry-editor.svelte` derives for each of `generalSections`, spelled the same way. */
	function sectionId(title: string): string {
		return `section-${title.toLowerCase()}`;
	}

	let groups = $derived([
		{
			// Its own group, above the configuration rather than inside it: the same Immich
			// credentials serve the default configuration and every profile, so the accounts section
			// is a property of the installation and not of whichever tab is selected.
			title: 'Installation',
			items: [
				{
					id: ACCOUNTS_ID,
					label: 'Immich accounts',
					badge: `${accountCount} ${accountCount === 1 ? 'account' : 'accounts'}`,
					override: false
				}
			]
		},
		{
			title: entry.isDefault ? 'Default configuration' : `Profile '${entry.name}'`,
			items: [
				...generalSections.map((section) => {
					// Counted through `declaredKeyOf`, because `declared` holds the settings file's
					// spelling of a key (`General.ShowClock`) and never the bare property name: asking
					// it about the property would report every section as overriding nothing.
					const overridden = section.props.filter((prop) =>
						entry.declared.includes(declaredKeyOf(prop))
					).length;

					return {
						id: sectionId(section.title),
						label: section.title,
						badge: overridden > 0 ? `${overridden} overridden` : '',
						override: true
					};
				}),
				{
					id: PHOTO_SELECTION_ID,
					label: 'Photo selection',
					// Never on the default configuration, which always declares accounts and so would
					// always carry the badge: `entry-editor.svelte` deliberately offers it no override
					// toggle - "the default configuration always uses at least one of the accounts
					// above" - so a badge there would mark an override the pane says cannot exist.
					badge: !entry.isDefault && entry.declared.includes(ACCOUNTS_KEY) ? 'own list' : '',
					override: true
				}
			]
		}
	]);

	/** Every item's section, in the order the pane renders them. */
	let ids = $derived(groups.flatMap((group) => group.items.map((item) => item.id)));

	let active = $state('');

	/**
	 * Where the chrome line falls: how much of the top of the pane the masthead and the profile
	 * strip cover between them. One value for both jobs, because a click has to scroll to exactly
	 * the line the scroll-spy watches, or the item a reader jumps to is not the item that lights up.
	 */
	let offset = $derived(mastheadHeight + stripHeight);

	/**
	 * The viewport, read for its dependency rather than for its value: a resize moves the detection
	 * band's lower edge, and this binding reporting is what re-runs the effect below to rebuild the
	 * observer against the new height. The band's own arithmetic measures the root itself - see there.
	 */
	let viewportHeight = $state(0);

	$effect(() => {
		const sections = ids
			.map((id) => document.getElementById(id))
			.filter((section): section is HTMLElement => section !== null);

		// Zero is the binding's value before the window has reported, which is to say before there is
		// a laid-out page to measure. The binding reports on mount and on every resize, and this
		// re-runs then.
		if (sections.length === 0 || viewportHeight === 0) return;

		/**
		 * The decision, written once because two things below run it. Measured across every section
		 * rather than read out of an observer's records, which only ever speak for whatever just
		 * crossed: the section being read is the last one to have passed under the chrome, and a pixel
		 * of tolerance keeps a fractional layout from missing the one sitting exactly on the line. This
		 * also carries the foot of the pane - the pane keeps a viewport of room below its last section,
		 * so the last section reaches the line like any other and needs no case of its own.
		 */
		function decide() {
			const passed = sections.filter(
				(section) => section.getBoundingClientRect().top <= offset + 1
			);

			active = (passed[passed.length - 1] ?? sections[0]).id;
		}

		// The primary path, and an observer rather than a scroll handler: the browser reports the
		// crossings itself, including the ones no scroll caused - a section growing or collapsing
		// carries every section under it across the line with the page standing still. The root is a
		// 1px band sitting exactly on the chrome line - the top margin pushes its top edge down past
		// the masthead and the strip, the bottom margin pulls its bottom edge back up to meet it. A
		// margin that only inset the top would leave a root half the page tall, and the browser only
		// reports its edges: a section would be announced as it appeared at the foot of the screen and
		// again as it left under the chrome, and nothing at all in between. Sections are 24px apart, so
		// the last of those reports lands while the section is still 24px below the line and the rule
		// above rejects it - a click-scroll then finishes in the silence that follows and leaves the
		// previous section lit. Against a band, a crossing is reported exactly when a section's top
		// passes the line, in both directions and at the end of a scroll. The band stays one pixel for
		// that reason: widen it and the crossing is reported early, while the section is still short of
		// the line, and the rule above answers with the previous section and is not asked again.
		//
		// The height is the document element's and not `window.innerHeight`. The implicit root is the
		// initial containing block, whose height is `documentElement.clientHeight` and excludes a
		// horizontal scrollbar, where `innerHeight` includes it; the two differ by a scrollbar's width
		// exactly when the page scrolls sideways, and a band measured back from the larger of them
		// would have its lower edge ~15px above its upper one. An inside-out root intersects nothing
		// ever, so that is not a band an inch out of place, it is an observer that has stopped.
		const observer = new IntersectionObserver(decide, {
			rootMargin: `${-offset}px 0px ${-(document.documentElement.clientHeight - offset - 1)}px 0px`
		});

		for (const section of sections) observer.observe(section);

		// The safety net, and only that: the observer above is the mechanism, and this re-runs its
		// decision for the moments it cannot see. A band reports nothing while no section spans it, and
		// both ends of the page are such a place by construction - at the top the first section starts
		// the page's own padding and the source banner below the chrome line, at the foot the pane's
		// trailing room has carried the last section above it. Home and End move between those two in a
		// single frame, nothing under `src` setting `scroll-behavior`, so the observer finds nothing on
		// the line either side of the jump, has no crossing to report, and leaves the highlight on
		// whichever section the reader was looking at before the key. Re-deciding when the scroll
		// position changes is what covers that, and it is not the polling an observer is chosen over:
		// that reads every section on a timer whether the page moved or not, where this reads them at
		// most once on a frame the page actually scrolled and never on any other.
		let queued = 0;

		function onScroll() {
			if (queued) return;

			queued = requestAnimationFrame(() => {
				queued = 0;
				decide();
			});
		}

		window.addEventListener('scroll', onScroll, { passive: true });

		return () => {
			observer.disconnect();
			window.removeEventListener('scroll', onScroll);
			cancelAnimationFrame(queued);
		};
	});

	function show(id: string) {
		const section = document.getElementById(id);

		if (!section) return;

		window.scrollTo({
			top: section.getBoundingClientRect().top + window.scrollY - offset,
			behavior: window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth'
		});
	}
</script>

<svelte:window bind:innerHeight={viewportHeight} />

<nav class="rail" aria-label="Configuration sections">
	{#each groups as group (group.items[0].id)}
		<h2 class="rail-heading">{group.title}</h2>
		<ul class="rail-list">
			{#each group.items as item (item.id)}
				<li>
					<button
						type="button"
						class="rail-item"
						class:is-active={active === item.id}
						aria-current={active === item.id ? 'location' : undefined}
						onclick={() => show(item.id)}
					>
						<span>{item.label}</span>
						{#if item.badge}
							<span class="rail-badge" class:is-override={item.override}>{item.badge}</span>
						{/if}
					</button>
				</li>
			{/each}
		</ul>
	{/each}

	<p class="rail-note">
		Rows marked <strong class="rail-note-mark">Overridden</strong> are written into this configuration.
		Everything else follows the value shown beside it.
	</p>
</nav>

<style>
	/*
	 * `align-self: start` is what gives the rail somewhere to stick to: a grid item is stretched to
	 * the height of its row by default, and a box as tall as the thing it scrolls beside never moves.
	 * Bounded to what is left of the screen below the masthead and scrolled beyond that: pinned at
	 * full height on a short viewport, its last items and the note under them would sit below the
	 * bottom of the screen with no scroll of their own able to reach them.
	 */
	.rail {
		position: sticky;
		top: var(--masthead-height);
		align-self: start;
		max-height: calc(100vh - var(--masthead-height));
		overflow: auto;
		padding: 20px var(--space-4) 40px 0;
		border-right: 1px solid var(--color-divider);
	}

	/*
	 * The design's faint caption, 4.83:1 on the rail's white, at its regular weight rather than the
	 * sheet's heading weight. Indented by the 24px the item labels below it are: the items run flush
	 * to the rail's left edge, and their padding is what sets the labels in.
	 */
	.rail-heading {
		margin: 0 0 10px;
		padding: 0 24px;
		font-size: 10px;
		font-weight: 400;
		letter-spacing: 0.14em;
		text-transform: uppercase;
		color: var(--color-text-faint);
	}

	.rail-list + .rail-heading {
		margin-top: 22px;
	}

	.rail-list {
		display: flex;
		flex-direction: column;
		gap: 6px;
		margin: 0;
		padding: 0;
		list-style: none;
	}

	/*
	 * Flush to the rail's left edge, which is the window's, and rounded at the other end only, so the
	 * item being read is a tab of tint pulled out from the edge of the screen. A floor rather than a
	 * height, so a label that wraps under a larger text size grows the item instead of spilling out
	 * of it. Neutral-700 at rest: 10.31:1, and 9.37:1 on the hover's fill.
	 */
	.rail-item {
		display: flex;
		align-items: center;
		justify-content: space-between;
		gap: 10px;
		width: 100%;
		min-height: 44px;
		padding: 0 var(--space-4) 0 24px;
		font-family: var(--font-heading);
		font-weight: 500;
		font-size: 14px;
		line-height: 1.2;
		text-align: left;
		color: var(--color-neutral-700);
		background: transparent;
		border: 0;
		border-radius: 0 var(--radius-pill) var(--radius-pill) 0;
		cursor: pointer;
	}

	/*
	 * The sheet's ring, drawn inside the item rather than 2px outside it: the items run flush to the
	 * rail's left edge, and past that edge the rail's own scrolling box - the window's, below 900px -
	 * would cut that side of the ring off.
	 */
	.rail-item:focus-visible {
		outline-offset: -2px;
	}

	.rail-item:hover {
		background: var(--color-neutral-100);
	}

	/*
	 * The primary on its tint, 6.17:1. After `:hover`, which it ties with, so the item being read
	 * keeps its tint under the pointer.
	 */
	.rail-item.is-active {
		background: var(--color-accent-100);
		color: var(--color-accent-700);
	}

	/*
	 * The design sets the account count in its faint tone, but faint is 4.26:1 on the active item's
	 * tint, and the item this count sits in is the active one whenever the page is at its top. Muted
	 * is 6.65:1 there, and 6.87:1 on the hover's fill.
	 */
	.rail-badge {
		flex-shrink: 0;
		font-family: var(--font-body);
		font-weight: 400;
		font-size: 11px;
		color: var(--color-text-muted);
	}

	/*
	 * The accent marks the same thing here as it does in the pane: a value written into this
	 * configuration rather than inherited. 7.00:1, 6.17:1 on the tint and 6.36:1 on the hover's fill.
	 */
	.rail-badge.is-override {
		color: var(--color-accent-700);
	}

	/*
	 * Forced-colors mode drops the tint, and with it the only thing on screen that says which section
	 * is being read - `aria-current` says it to assistive technology alone - so the palette's own
	 * selected-item pair says it there instead. Held out of the forced palette so that that pair is
	 * what paints, which leaves the rest of the item to colour as well, since opting out is
	 * inherited: the count takes the item's text colour, and so does the ring, drawn inside the item
	 * and so on the selected fill.
	 */
	@media (forced-colors: active) {
		.rail-item.is-active {
			forced-color-adjust: none;
			background: SelectedItem;
			color: SelectedItemText;
		}

		.rail-item.is-active .rail-badge {
			color: inherit;
		}

		.rail-item.is-active:focus-visible {
			outline-color: SelectedItemText;
		}
	}

	/* Ruled off from the items and set in to their labels, as the design sets it. Muted, 7.56:1. */
	.rail-note {
		margin: 22px 0 0 24px;
		padding: 14px 0 0;
		border-top: 1px solid var(--color-divider);
		font-size: 11.5px;
		line-height: 1.5;
		color: var(--color-text-muted);
	}

	.rail-note-mark {
		font-weight: 600;
		color: var(--color-accent-700);
	}

	/*
	 * Below 900px `config-editor.svelte` drops to one column and the rail lands above the pane. It
	 * stops being sticky there and stays the same list it always was: a viewport that narrow has no
	 * room to hold a 262px column open beside the settings, and pinning the rail to the top of it
	 * would spend a third of the screen on navigation. Every item still jumps to its section.
	 */
	@media (max-width: 900px) {
		.rail {
			position: static;
			/* Nothing pins it here, so the page's own scroll reaches the note: a box of its own would
			   be a second scrollbar inside the first. */
			max-height: none;
			overflow: visible;
			/* The items' round ends kept off the window's edge, as the column keeps them off its rule. */
			padding: var(--space-4) var(--space-4) var(--space-4) 0;
			border-right: 0;
			border-bottom: 1px solid var(--color-divider);
		}
	}
</style>
