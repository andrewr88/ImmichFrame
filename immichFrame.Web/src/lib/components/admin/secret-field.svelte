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

	const button =
		'rounded border border-neutral-600 px-2 py-0.5 text-xs text-neutral-200 ' +
		'hover:border-neutral-400 disabled:opacity-60 disabled:cursor-not-allowed';

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
	<span class="text-sm {hasValue ? 'text-emerald-400' : 'text-neutral-400'}">
		{hasValue ? 'Set' : 'Not set'}
	</span>
{/snippet}

{#if !declared}
	<!-- Whether the inherited secret exists is the one thing worth saying here, and the mode this
	     configuration would use if it were declared is not: an undeclared secret defaults to 'set',
	     whose empty box would otherwise read as "not set" for a secret that is. -->
	{@render status()}
{:else if secret.mode === 'keep'}
	<div class="flex flex-wrap items-center gap-2">
		{@render status()}
		<button type="button" class={button} onclick={() => (secret.mode = 'set')}>
			{hasValue ? 'Replace' : 'Set a value'}
		</button>
		{#if hasValue}
			<button type="button" class={button} onclick={chooseNoSecret}>{noSecretLabel}</button>
		{/if}
	</div>
{:else if secret.mode === 'none'}
	<div class="flex flex-wrap items-center gap-2">
		<span class="text-sm text-amber-400">
			{inherits
				? `No ${label}, overriding the default configuration`
				: `${label} will be cleared when you save`}
		</span>
		<button type="button" class={button} onclick={() => (secret.mode = canKeep ? 'keep' : 'set')}>
			Undo
		</button>
	</div>
	{#if inherits}
		<p class="mt-1 text-xs text-neutral-400">
			This is an override, not a return to inheriting: this profile will have no {label} even though the
			default configuration has one. To use the default's value instead, turn
			<em>Overridden</em> off.
		</p>
	{/if}
	{#if noSecretWarning}
		<p class="mt-1 text-xs text-amber-400">{noSecretWarning}</p>
	{/if}
{:else}
	<div class="flex flex-wrap items-center gap-2">
		<input
			{id}
			type="password"
			autocomplete="new-password"
			placeholder="Enter a new value"
			class="min-w-0 flex-1 rounded border border-neutral-700 bg-neutral-900 px-2 py-1 text-sm
				text-neutral-100 disabled:opacity-60"
			bind:value={secret.value}
		/>
		{#if canKeep}
			<button type="button" class={button} onclick={() => (secret.mode = 'keep')}>
				Keep stored
			</button>
		{/if}
		<button type="button" class={button} onclick={chooseNoSecret}>{noSecretLabel}</button>
	</div>
	<p class="mt-1 text-xs text-amber-400">
		A value is required. Choose <em>{noSecretLabel}</em> to have none, or turn the override off to leave
		this setting alone.
	</p>
{/if}
