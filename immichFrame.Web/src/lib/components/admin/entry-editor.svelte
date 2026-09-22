<script lang="ts">
	import type { AdminGeneralSettingsDto } from '$lib/immichFrameApi';
	import {
		ACCOUNTS_KEY,
		accountName,
		declaredKeyOf,
		entryLabel,
		generalFields,
		generalSections,
		newSelection,
		setAccountUse,
		usedSelections,
		type AccountSelection,
		type EditableAccount,
		type EditableEntry,
		type FieldValue,
		type GeneralProp,
		type SecretProp
	} from './admin-config';
	import AccountEditor from './account-editor.svelte';
	import OverrideRow from './override-row.svelte';
	import SecretField from './secret-field.svelte';
	import SettingField from './setting-field.svelte';

	interface Props {
		entry: EditableEntry;
		/**
		 * Every Immich account in the configuration. This section assigns them rather than describing
		 * them: the credentials belong to the account, and are edited once, above the tabs.
		 */
		accounts: EditableAccount[];
		/** The configuration this one inherits from, or null for the default configuration itself. */
		inheritFrom: EditableEntry | null;
		/** The loaded configuration's version token, which the Immich pickers ask against. */
		version: string;
	}

	let { entry, accounts, inheritFrom, version }: Props = $props();

	// The default configuration has nothing above it, so its unset keys fall back to the setting's
	// built-in default rather than to another configuration.
	let inheritLabel = $derived(inheritFrom ? 'Inherited' : 'Built-in default');
	// The same fact read from the other end, for the section headers: what the reader is looking at
	// is either the values every profile starts from, or a profile that falls back to them.
	let inheritNote = $derived(
		inheritFrom
			? 'Anything not overridden follows the default configuration'
			: 'Every profile inherits what is not overridden here'
	);
	/** How a row's override control names this configuration when it offers to write into it. */
	let editing = $derived(entryLabel(entry));
	let accountsDeclared = $derived(entry.declared.includes(ACCOUNTS_KEY));
	let inherited = $derived(inheritFrom ? usedSelections(inheritFrom) : []);
	/** The accounts this profile is inheriting, in the order the section above lists them. */
	let inheritedAccounts = $derived(
		accounts
			.map((account, index) => ({ account, index }))
			.filter((row) => inherited.some((selection) => selection.accountKey === row.account.key))
	);

	function declares(prop: GeneralProp): boolean {
		return entry.declared.includes(declaredKeyOf(prop));
	}

	/** The configuration a setting's displayed value comes from while it is not declared here. */
	function valuesFor(prop: GeneralProp): AdminGeneralSettingsDto {
		return declares(prop) ? entry.general : (inheritFrom?.general ?? entry.general);
	}

	function hasSecret(values: AdminGeneralSettingsDto, prop: SecretProp): boolean {
		if (prop === 'weatherApiKey') return values.hasWeatherApiKey === true;
		if (prop === 'webhook') return values.hasWebhook === true;
		return values.hasAuthenticationSecret === true;
	}

	function toggleGeneral(prop: GeneralProp, declared: boolean) {
		const key = declaredKeyOf(prop);

		if (!declared) {
			entry.declared = entry.declared.filter((other) => other !== key);
			return;
		}

		if (entry.declared.includes(key)) return;

		// An override starts from what was being inherited, so adding one and saving without further
		// edits changes nothing but which configuration the value is written in. The default
		// configuration needs no seeding: what it shows for an undeclared key is already the
		// setting's built-in default, straight off the merged read.
		if (inheritFrom) Object.assign(entry.general, { [prop]: inheritFrom.general[prop] });

		entry.declared = [...entry.declared, key];
	}

	function toggleAccounts(declared: boolean) {
		if (!declared) {
			entry.declared = entry.declared.filter((other) => other !== ACCOUNTS_KEY);
			return;
		}

		// A declared account list replaces the inherited one outright, so the profile starts from what
		// it was inheriting: the same accounts, ticked, showing the same photos. Each keeps the handle
		// the read issued for it under the default configuration, so their stored API keys come across
		// on save without the administrator retyping a key they were already using.
		//
		// Only when this profile has nothing of its own, which is the first time the override is
		// turned on and no other. Seeding again would overwrite whatever it had been given since -
		// including selections it is holding unticked - with the default configuration's, and turning
		// an override off and on again is not a request to discard the override's contents.
		if (!entry.accountsWereDeclared && entry.accounts.length === 0) {
			entry.accounts = inherited.map((selection) =>
				newSelection(selection.accountKey, selection.values)
			);
		}

		entry.declared = [...entry.declared, ACCOUNTS_KEY];
	}

	/** This entry's selection for an account, ticked or not: an unticked one is kept, not dropped. */
	function selectionFor(account: EditableAccount): AccountSelection | undefined {
		return entry.accounts.find((selection) => selection.accountKey === account.key);
	}

	function setGeneral(prop: GeneralProp, value: FieldValue) {
		Object.assign(entry.general, { [prop]: value });
	}

	/**
	 * A profile with no authentication secret is a documented capability - it is how a frame on a
	 * trusted network skips the prompt - and it is also the one choice here that makes a URL
	 * publicly readable. Said at the point of choosing rather than left to the documentation.
	 */
	function noSecretWarning(prop: SecretProp): string | undefined {
		if (prop !== 'authenticationSecret' || inheritFrom === null) return undefined;

		return (
			`Anyone who can reach /${entry.name} will then see everything this profile is ` +
			'configured to show, without a secret.'
		);
	}
</script>

{#each generalSections as section (section.title)}
	<section class="section" id="section-{section.title.toLowerCase()}">
		<header class="head">
			<div>
				<h2 class="title">{section.title}</h2>
				<p class="blurb">{section.blurb}</p>
			</div>
			<p class="inherit-note">{inheritNote}</p>
		</header>

		<div class="columns">
			<span>Setting</span>
			<span>Value</span>
			<span>Where it comes from</span>
		</div>

		{#each section.props as prop (prop)}
			{@const spec = generalFields[prop]}
			{@const id = `${entry.name}-${prop}`}
			<OverrideRow
				{id}
				label={spec.label}
				help={spec.help}
				declared={declares(prop)}
				{inheritLabel}
				entryName={editing}
				onToggle={(declared) => toggleGeneral(prop, declared)}
			>
				{#if spec.kind === 'secret'}
					<SecretField
						{id}
						label={spec.label}
						secret={entry.secrets[prop as SecretProp]}
						hasValue={hasSecret(valuesFor(prop), prop as SecretProp)}
						declared={declares(prop)}
						canKeep={entry.originallyDeclared.includes(declaredKeyOf(prop))}
						inherits={inheritFrom !== null}
						noSecretWarning={noSecretWarning(prop as SecretProp)}
					/>
				{:else}
					<SettingField
						{id}
						{spec}
						value={valuesFor(prop)[prop]}
						disabled={!declares(prop)}
						onChange={(value) => setGeneral(prop, value)}
					/>
				{/if}
			</OverrideRow>
		{/each}
	</section>
{/each}

<section class="section" id="section-photo-selection">
	<header class="head">
		<div>
			<h2 class="title">Photo selection</h2>
			<p class="blurb">
				Which of those accounts this configuration shows photos from, and what it shows from each.
			</p>
		</div>
		<p class="inherit-note">{inheritNote}</p>
	</header>

	<div class="banner">
		{#if entry.isDefault}
			<p class="banner-note">
				The default configuration always uses at least one of the accounts above.
			</p>
		{:else}
			<!-- 003's override control, declared again here because Svelte scopes a component's styles
			     to the component that declares them. The same control in every other respect: the
			     accounts list is a declared key like any other, and this says the same thing about it
			     that the third column says about a setting - written here, or followed from there. -->
			<label
				class="override"
				class:is-declared={accountsDeclared}
				title={accountsDeclared
					? 'This configuration writes this setting. Click to stop overriding it.'
					: `Click to override this setting in ${editing}.`}
			>
				<!-- Named for the same reason a row's box is: on its own this is a thirty-first
				     "Overridden, checkbox" among the sections above, and the one that is not a setting.
				     The visible word leads the name so a voice user can still ask for what they see. -->
				<input
					type="checkbox"
					class="box"
					aria-label="{accountsDeclared ? 'Overridden' : inheritLabel}: Photo selection"
					checked={accountsDeclared}
					onchange={(event) => toggleAccounts(event.currentTarget.checked)}
				/>
				<span>{accountsDeclared ? 'Overridden' : inheritLabel}</span>
			</label>
		{/if}
	</div>

	{#if accountsDeclared}
		{#if !entry.isDefault && !entry.accountsWereDeclared}
			<p class="warning">
				This profile now declares its own accounts instead of inheriting them. The accounts ticked
				below keep the API keys already stored for them, but changes to which accounts the default
				configuration uses no longer reach this profile.
			</p>
		{/if}

		{#if accounts.length === 0}
			<p class="warning">
				There are no Immich accounts to choose from. Add one in the Immich accounts section above.
			</p>
		{/if}

		<!-- Keyed by the account's own key rather than its position: position is deliberately not an
		     identity anywhere in this feature, and reusing a DOM node across a removal would put one
		     account's photo selection under another. -->
		{#each accounts as account, index (account.key)}
			{@const selection = selectionFor(account)}
			{@const uses = selection?.uses === true}
			<article class="account">
				<!-- The whole strip is the label, so the name and the state beside the box toggle the
				     account too: what the strip says is what the box answers. -->
				<label class="account-head" class:is-used={uses}>
					<input
						type="checkbox"
						class="check"
						checked={uses}
						onchange={(event) =>
							setAccountUse(entry, account, event.currentTarget.checked, inheritFrom)}
					/>
					<span class="account-name">{accountName(account, index)}</span>
					{#if account.label.trim() && account.serverUrl.trim()}
						<!-- Only beside a name that is not already it: an account with no label is named by
						     its server URL, and `accountName` is what decides that. -->
						<span class="account-url">{account.serverUrl.trim()}</span>
					{/if}
					<span class="account-state">{uses ? 'Shows photos from this account' : 'Not used'}</span>
				</label>

				{#if selection?.uses}
					<div class="account-body">
						<AccountEditor
							id="{entry.name}-{account.key}"
							{account}
							{selection}
							{version}
							title="What {entryLabel(entry)} shows from this account"
						/>
					</div>
				{/if}
			</article>
		{/each}
	{:else}
		<p class="inherited-note">
			Inherited from the default configuration. Override to choose which accounts this profile shows
			and what it shows from each.
		</p>
		<ul class="inherited">
			{#each inheritedAccounts as row (row.account.key)}
				<li>{accountName(row.account, row.index)}</li>
			{/each}
		</ul>
	{/if}
</section>

<style>
	/*
	 * The track list is declared here rather than in `override-row.svelte` so that the column
	 * headings and the rows they head read one declaration between them, and so that the breakpoint
	 * below governs both at once. 24px between sections is what the rail's scroll-spy reasons
	 * against when it argues for a one-pixel detection band.
	 */
	.section {
		--settings-grid: minmax(190px, 250px) minmax(0, 1fr) 210px;

		margin: var(--space-6) 0;
	}

	.head {
		display: flex;
		flex-wrap: wrap;
		align-items: flex-end;
		justify-content: space-between;
		gap: var(--space-3);
		padding-bottom: var(--space-2);
		border-bottom: 2px solid var(--color-divider);
	}

	/* 26px against the sheet's 32px: five of these head one page and none of them is its title. */
	.title {
		margin: 0;
		font-size: 26px;
	}

	.blurb {
		margin: 2px 0 0;
		font-size: 13px;
		color: var(--color-neutral-700);
	}

	.inherit-note {
		margin: 0;
		font-size: 11.5px;
		text-align: right;
		color: var(--color-neutral-700);
	}

	/*
	 * The mock's neutral-600 reads 3.85:1 on the page ground, under AA for a 10px label - the same
	 * figure and the same answer as the profile strip's caption, which took neutral-700 for it.
	 * That is 5.83:1 and is the tone every other played down caption on this page already wears.
	 */
	.columns {
		display: grid;
		grid-template-columns: var(--settings-grid);
		gap: 20px;
		padding: var(--space-3) 0 var(--space-1);
		font-size: 10px;
		letter-spacing: 0.13em;
		text-transform: uppercase;
		color: var(--color-neutral-700);
		border-bottom: 1px solid var(--color-neutral-300);
	}

	/*
	 * Photo selection, from here down. The wrapper that used to sit around this body was hiding
	 * every colour in it, and taking it away is the whole of this section's conversion - so these
	 * bands are read against the page's own `--color-bg`, save for the one fill the section keeps:
	 * a used account's header strip paints `--color-neutral-200`. That is not a rare state either,
	 * and since the strip's `is-used` and the box's `checked` are one variable read twice, a used
	 * account's URL and status are never seen on anything else.
	 *
	 * Both grounds clear AA. Neutral-700 is 5.30:1 on neutral-200 against 5.83:1 on
	 * `--color-bg`, for the 12px URL and the 11px status alike; the name inherits `--color-text`,
	 * 13.51:1 and 14.86:1. The box's own figures are with the box, below.
	 */

	.banner {
		display: flex;
		flex-wrap: wrap;
		align-items: center;
		/*
		 * The control is pushed to the end rather than parted from something by `space-between`: on
		 * a profile it is the only thing in this row, and a lone item in a `space-between` row sits
		 * at the start - which is where it has been sitting since 003 took away the heading it used
		 * to share the row with.
		 */
		justify-content: flex-end;
		gap: var(--space-3);
		margin-top: var(--space-3);
		padding: 12px 16px;
		border: 1px solid var(--color-divider);
	}

	/* Capped at a readable measure rather than run across the page, and kept to the left end of the
	   row by its own margin so the control opposite it stays at the right. */
	.banner-note {
		max-width: 620px;
		margin: 0 auto 0 0;
		font-size: 12.5px;
		color: var(--color-neutral-700);
	}

	/* 003's control, restated: `override-row.svelte` is where these three rules are explained. */
	.override {
		display: flex;
		align-items: center;
		gap: var(--space-2);
		padding: 6px 8px;
		font-size: 12.5px;
		font-weight: 400;
		color: var(--color-neutral-700);
		border: 1px solid var(--color-neutral-300);
		cursor: pointer;
	}

	.override.is-declared {
		font-weight: 600;
		color: var(--color-accent-700);
		background: var(--color-accent-100);
		border-color: var(--color-accent);
	}

	.box {
		flex-shrink: 0;
		width: 16px;
		height: 16px;
		margin: 0;
		accent-color: var(--color-accent);
	}

	/*
	 * Both are things to know or to put right before saving rather than actions that failed, so
	 * they take the warning role - the same role, and the same reasoning, as the empty accounts
	 * section in `accounts-section.svelte`. The accent ramp on this page means refused.
	 */
	.warning {
		margin: var(--space-3) 0 0;
		font-size: 13px;
		color: var(--color-warning-700);
	}

	.account {
		margin-top: var(--space-3);
		border: 1px solid var(--color-divider);
	}

	/*
	 * The strip is a `<label>` around the box, so a click anywhere along it ticks the account: the
	 * name and the state it carries are the question the box answers, and neither is a control of
	 * its own that a click could be meant for instead.
	 */
	.account-head {
		display: flex;
		flex-wrap: wrap;
		align-items: center;
		gap: var(--space-2) var(--space-3);
		padding: 12px 16px;
		cursor: pointer;
		border-bottom: 1px solid var(--color-neutral-300);
	}

	/*
	 * The rule under the strip parts it from the body below, so it goes when there is no body: the
	 * body renders only for an account this configuration uses, so on one it does not the strip is
	 * the panel's only child, and its own light rule lands on the panel's own `--color-divider`
	 * edge - 2px of doubled line, light over dark, on the default state of every account a
	 * configuration has not ticked.
	 */
	.account-head:last-child {
		border-bottom: 0;
	}

	/* The fill is the section's answer at a glance: which of these accounts this configuration is
	   showing photos from, readable without reading a word of any strip. */
	.account-head.is-used {
		background: var(--color-neutral-200);
	}

	/*
	 * 003's box at 20px, and it travels with the escape hatch its `appearance: none` needs, for the
	 * reason `setting-field.svelte` gives at length: forced-colors mode cannot rewrite that
	 * declaration, so a ticked box would be an empty one. The fill and the rule are that box's too,
	 * and neither had to be retuned - but not because the ground is the same as there. It is not:
	 * `checked` and the strip's `is-used` are one variable read twice, so a ticked box is always on
	 * the strip's `--color-neutral-200` and an empty one always on `--color-bg`.
	 *
	 * Both clear 1.4.11 on the ground they get, and would on the other. The ticked fill is 3.42:1
	 * on neutral-200 and 3.76:1 on `--color-bg`; ticked and inactive, 3.50:1 and 3.85:1. An empty
	 * box is its rule and nothing else - `--color-surface` is 1.08:1 on the page ground - and that
	 * rule is 12.60:1 there, 11.45:1 on neutral-200.
	 */
	.check {
		appearance: none;
		display: grid;
		place-content: center;
		flex: none;
		width: 20px;
		height: 20px;
		margin: 0;
		background: var(--color-surface);
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
		width: 10px;
		height: 10px;
		background: var(--color-bg);
		clip-path: polygon(14% 44%, 0 65%, 50% 100%, 100% 16%, 80% 0%, 43% 62%);
	}

	/* Read-only configurations disable the fieldset around this editor, and the platform's own box
	   said so before this rule took its rendering away. 003's tones, which say it at full opacity. */
	.check:disabled {
		border-color: var(--color-neutral-600);
		cursor: not-allowed;
	}

	.check:checked:disabled {
		background: var(--color-neutral-600);
		border-color: var(--color-neutral-600);
	}

	/*
	 * `min-width: 0` on both, so a long name or a long URL is truncated rather than widening the
	 * panel: a flex item refuses to shrink below its own content until it is told it may, and an
	 * account's name is whatever was typed into the box in the section above.
	 */
	.account-name,
	.account-url {
		min-width: 0;
		overflow: hidden;
		text-overflow: ellipsis;
		white-space: nowrap;
	}

	/* 15px in the heading face: a panel's name, and one rank below the card titles in the accounts
	   section, which is where this account is described rather than assigned. */
	.account-name {
		font-family: var(--font-heading);
		font-weight: 800;
		font-size: 15px;
	}

	.account-url {
		font-family: 'Overpass Mono', ui-monospace, monospace;
		font-size: 12px;
		color: var(--color-neutral-700);
	}

	/* At the far end of the strip whatever else is in it, including a strip whose name has wrapped
	   onto a line of its own. */
	.account-state {
		margin-left: auto;
		font-size: 11px;
		letter-spacing: 0.1em;
		text-transform: uppercase;
		color: var(--color-neutral-700);
	}

	.account-body {
		padding: 18px 16px 22px;
	}

	/*
	 * The one read-only state this section has, and so the one place the design's read-only note
	 * belongs: an inheriting profile is shown the default configuration's accounts and cannot
	 * choose among them. A rule down its left rather than a colour of its own, because nothing
	 * here is wrong - following the default configuration is where a profile starts.
	 */
	.inherited-note {
		max-width: 620px;
		margin: var(--space-3) 0 0;
		padding-left: 10px;
		font-size: 12px;
		color: var(--color-neutral-700);
		border-left: 3px solid var(--color-neutral-400);
	}

	.inherited {
		margin: var(--space-3) 0 0;
		padding: 0;
		list-style: none;
	}

	/* The strip above without its box: these are accounts this profile shows and cannot choose. */
	.inherited li {
		padding: 10px 16px;
		font-size: 14px;
		border: 1px solid var(--color-divider);
	}

	.inherited li + li {
		margin-top: var(--space-2);
	}

	/*
	 * Below 900px a row is one column, where `config-editor.svelte` drops the rail out of a column
	 * of its own. The headings go with it: three column names over a single column head nothing.
	 */
	@media (max-width: 900px) {
		.section {
			--settings-grid: minmax(0, 1fr);
		}

		.columns {
			display: none;
		}
	}

	/*
	 * The escape hatch that travels with `appearance: none`, and the only rule here that needs one:
	 * forced-colors mode rewrites colour, background and border into the user's own palette, so
	 * every other rule above comes through it working. Hand the box back to the platform, which
	 * draws a tick that mode understands, and drop the clipped mark so it cannot be painted over
	 * the platform's own.
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
