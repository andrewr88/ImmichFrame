import { get } from 'svelte/store';
import { afterEach, describe, expect, it, vi } from 'vitest';

// persist.store reads and writes localStorage while it is being imported, so every test installs
// the storage it is about first and only then imports the module - evaluated afresh each time, as
// afterEach resets the module registry. Node has no localStorage of its own, so what a test
// installs is all the module finds.
const originalLocalStorage = Object.getOwnPropertyDescriptor(globalThis, 'localStorage');

afterEach(() => {
	if (originalLocalStorage) {
		Object.defineProperty(globalThis, 'localStorage', originalLocalStorage);
	} else {
		Reflect.deleteProperty(globalThis, 'localStorage');
	}
	vi.resetModules();
});

/** Installs `localStorage` as a getter or a value; a browser's can throw on being read at all. */
function installLocalStorage(descriptor: PropertyDescriptor) {
	Object.defineProperty(globalThis, 'localStorage', { configurable: true, ...descriptor });
}

/** A localStorage that works, over a Map the test can look into afterwards. */
function workingLocalStorage(entries: Record<string, string> = {}) {
	const items = new Map(Object.entries(entries));
	installLocalStorage({
		value: {
			getItem: (key: string) => items.get(key) ?? null,
			setItem: (key: string, value: string) => {
				items.set(key, value);
			}
		}
	});
	return items;
}

/** The shape of the client identifier a frame generates on its first visit. */
const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

describe('persist.store, with a localStorage it cannot rely on', () => {
	it.each([
		{
			// Chrome and Firefox with cookies blocked, and a sandboxed iframe without
			// allow-same-origin. It is reading the global itself that throws, so `?.` never sees it.
			failure: 'reading localStorage throws a SecurityError',
			localStorage: {
				get() {
					throw new DOMException(
						"Failed to read the 'localStorage' property from 'Window': Access is denied for this document.",
						'SecurityError'
					);
				}
			}
		},
		{
			// Safari's private mode before 11: reads work, and find nothing, but every write throws -
			// the first of them while the module is still being imported, as a store runs its
			// subscriber the moment it is subscribed.
			failure: 'setItem throws a QuotaExceededError',
			localStorage: {
				value: {
					getItem: () => null,
					setItem: () => {
						throw new DOMException('The quota has been exceeded.', 'QuotaExceededError');
					}
				}
			}
		}
	])('$failure, still imports, and both stores run on in memory', async ({ localStorage }) => {
		// Arrange
		installLocalStorage(localStorage);

		// Act
		const { clientIdentifierStore, authSecretStore } = await import('./persist.store');
		const onImport = [get(clientIdentifierStore), get(authSecretStore)];

		clientIdentifierStore.set('kitchen-frame');
		authSecretStore.set('s3cret');

		// Assert: the frame starts out as on a first visit, and holds what it is given from then on;
		// only a reload forgets it.
		expect(onImport).toEqual([expect.stringMatching(GUID), null]);
		expect(get(clientIdentifierStore)).toBe('kitchen-frame');
		expect(get(authSecretStore)).toBe('s3cret');
	});

	it('a stored value that is not JSON, falls back to the default instead of failing the import', async () => {
		// Arrange: nothing here has ever written one - every write goes through JSON.stringify - but
		// JSON.parse throws on it all the same.
		workingLocalStorage({ clientIdentifier: 'kitchen-frame', authSecret: 's3cret' });

		// Act
		const { clientIdentifierStore, authSecretStore } = await import('./persist.store');

		// Assert
		expect(get(clientIdentifierStore)).toMatch(GUID);
		expect(get(authSecretStore)).toBeNull();
	});
});

describe('persist.store, with a working localStorage', () => {
	it('a first visit, stores the client identifier it generates, so a reload keeps it', async () => {
		// Arrange
		const items = workingLocalStorage();

		// Act
		const { clientIdentifierStore } = await import('./persist.store');

		// Assert
		expect(get(clientIdentifierStore)).toMatch(GUID);
		expect(items.get('clientIdentifier')).toBe(JSON.stringify(get(clientIdentifierStore)));
	});

	it('a returning frame, reads and writes each value under the key, and in the encoding, it always has', async () => {
		// Arrange: the default profile's secret under the unsuffixed key, and another profile's.
		const items = workingLocalStorage({
			clientIdentifier: '"kitchen-frame"',
			authSecret: '"default-secret"',
			'authSecret:bedroom': '"bedroom-secret"'
		});

		// Act
		const { clientIdentifierStore, authSecretStore } = await import('./persist.store');
		const { profileStore } = await import('./profile.store');
		const onImport = [get(clientIdentifierStore), get(authSecretStore)];

		profileStore.set('bedroom');
		const onBedroom = get(authSecretStore);

		clientIdentifierStore.set('hallway-frame');
		authSecretStore.set('new-bedroom-secret');

		// Assert
		expect(onImport).toEqual(['kitchen-frame', 'default-secret']);
		expect(onBedroom).toBe('bedroom-secret');
		expect(Object.fromEntries(items)).toEqual({
			clientIdentifier: '"hallway-frame"',
			authSecret: '"default-secret"',
			'authSecret:bedroom': '"new-bedroom-secret"'
		});
	});
});
