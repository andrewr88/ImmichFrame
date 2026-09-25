<script lang="ts">
	import type { SecretEdit } from './admin-config';

	interface Props {
		id: string;
		/** The setting's own name, for wording that has to be unambiguous about which one it means. */
		label: string;
		secret: SecretEdit;
		/** Whether a value is stored for this setting. Never the value itself - it is never sent. */
		hasValue: boolean;
		/** Whether this configuration declares the setting rather than inheriting it. */
		declared: boolean;
		/** Whether "keep what is stored" is available: only a key this configuration already declares
		 * has a stored value of its own to keep. */
		canKeep: boolean;
		/** Whether an undeclared value comes from another configuration rather than a built-in default. */
		inherits: boolean;
		/** What choosing "no secret" exposes here, when that is worth saying before it is chosen. */
		noSecretWarning?: string;
	}

	let { id, label, secret, hasValue, declared, canKeep, inherits, noSecretWarning }: Props =
		$props();

	// On a profile the two are genuinely different outcomes and the wording has to keep them apart:
	// no secret is an override that beats the default configuration's value, while inheriting is
	// what the row's own "Overridden" checkbox turns back on.
	let noSecretLabel = $derived(inherits ? 'Use no secret' : 'Clear');

	function chooseNoSecret() {
		secret.mode = 'none';
		secret.value = '';
	}
</script>

{#snippet status()}
	<!-- Emphasis is for a secret this configuration writes. An inherited row is saying what is
	     stored somewhere else, and there is no control beside it to put that in proportion, so an
	     emphasised "Set" there would be the loudest thing in the column over rows that declare
	     nothing - and would read the same as a declared key that is keeping its stored value. -->
	<span class="status" class:is-set={hasValue && declared}>
		{hasValue ? 'Set' : 'Not set'}
	</span>
{/snippet}

{#if !declared}
	<!-- Whether the inherited secret exists is the one thing worth saying here, and the mode this
	     configuration would use if it were declared is not: an undeclared secret defaults to 'set',
	     whose empty box would otherwise read as "not set" for a secret that is. -->
	{@render status()}
{:else if secret.mode === 'keep'}
	<div class="row">
		{@render status()}
		<button type="button" class="btn btn-secondary" onclick={() => (secret.mode = 'set')}>
			{hasValue ? 'Replace' : 'Set a value'}
		</button>
		{#if hasValue}
			<button type="button" class="btn btn-secondary" onclick={chooseNoSecret}>
				{noSecretLabel}
			</button>
		{/if}
	</div>
{:else if secret.mode === 'none'}
	<div class="row">
		<span class="no-secret">
			{inherits
				? `No ${label}, overriding the default configuration`
				: `${label} will be cleared when you save`}
		</span>
		<button
			type="button"
			class="btn btn-secondary"
			onclick={() => (secret.mode = canKeep ? 'keep' : 'set')}
		>
			Undo
		</button>
	</div>
	{#if inherits}
		<p class="note">
			This is an override, not a return to inheriting: this profile will have no {label} even though the
			default configuration has one. To use the default's value instead, turn
			<em>Overridden</em> off.
		</p>
	{/if}
	{#if noSecretWarning}
		<p class="note warning">{noSecretWarning}</p>
	{/if}
{:else}
	<div class="row">
		<input
			{id}
			type="password"
			autocomplete="new-password"
			placeholder="Enter a new value"
			class="input secret"
			bind:value={secret.value}
		/>
		{#if canKeep}
			<button type="button" class="btn btn-secondary" onclick={() => (secret.mode = 'keep')}>
				Keep stored
			</button>
		{/if}
		<button type="button" class="btn btn-secondary" onclick={chooseNoSecret}>
			{noSecretLabel}
		</button>
	</div>
	<p class="note warning">
		A value is required. Choose <em>{noSecretLabel}</em> to have none, or turn the override off to leave
		this setting alone.
	</p>
{/if}

<style>
	.row {
		display: flex;
		flex-wrap: wrap;
		align-items: center;
		gap: 10px;
	}

	/* The design's compact secondary button. Compounded with `.btn`, which sets both properties. */
	.row .btn {
		padding: 4px 9px;
		font-size: 12.5px;
	}

	/*
	 * The admin theme has no success role, so "Set" is not a green: it is the primary, 6.83:1 on
	 * the card the rows sit in, against a muted "Not set" at 7.37:1 - the same pair of tones the
	 * override pill beside it tells its two states apart with. Both are set at 600, as the design
	 * sets them, so the subordinate state is read, not dimmed.
	 */
	.status {
		font-size: 13.5px;
		font-weight: 600;
		color: var(--color-text-muted);
	}

	.status.is-set {
		color: var(--color-accent-700);
	}

	.note {
		margin: var(--space-1) 0 0;
		font-size: 12px;
		color: var(--color-text-muted);
	}

	/*
	 * What saving will do to the secret, said in the primary as the design says it - the colour this
	 * pane gives what is written into a configuration: 6.83:1 on the card.
	 */
	.no-secret {
		font-size: 13.5px;
		font-weight: 600;
		color: var(--color-accent-700);
	}

	/*
	 * The warning role, not the danger role. Having no secret and being asked for one are conditions
	 * to understand before saving, the way the banner's legacy-schema consent is; the danger role is
	 * what this pane spends on failures. Compounded so it beats `.note`'s own colour on specificity
	 * rather than on which rule came second. 7.13:1 on the card.
	 */
	.note.warning {
		color: var(--color-warning-700);
	}

	/*
	 * Shares its row with the buttons beside it, so it is sized by the flex line rather than by
	 * `.input`'s own `width: 100%`, and `min-width: 0` is what lets it shrink past the width a text
	 * box carries intrinsically. Compounded with `.input` as the chrome above does, so the two
	 * rules cannot be separated by whichever stylesheet the bundler emits second.
	 */
	.input.secret {
		flex: 1;
		min-width: 0;
		max-width: 280px;
	}
</style>
