import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vitest/config';

/**
 * Vitest's configuration, deliberately in a file of its own rather than a `test` block in
 * `vite.config.ts`.
 *
 * Vite's CLI only ever looks for `vite.config.*`, so `npm run dev`, `npm run build` and
 * `npm run check` never read this file and cannot change behaviour because a test runner arrived.
 * Vitest reads this one in preference to `vite.config.ts`, so the SvelteKit plugin does not run
 * under the suite either - the tests are over plain TypeScript modules, and loading the plugin
 * would drag the whole app pipeline in for nothing.
 *
 * What the plugin would have provided is `$lib`, which the modules under test reach through
 * `immich-picker.ts`'s `import * as api from '$lib/immichFrameApi'`. It is declared here instead,
 * pointing where SvelteKit's `files.lib` default leaves it - `svelte.config.js` does not move it.
 */
export default defineConfig({
	resolve: {
		alias: {
			$lib: fileURLToPath(new URL('./src/lib', import.meta.url))
		}
	},
	test: {
		include: ['src/**/*.test.ts'],
		// No DOM on purpose: there is no jsdom here, and the suite covers model logic only.
		environment: 'node'
	}
});
