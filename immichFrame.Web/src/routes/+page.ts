import * as api from '$lib/immichFrameApi';
import { configStore } from '$lib/stores/config.store.js';
import { clientIdentifierStore } from '$lib/stores/persist.store';
import { profileStore } from '$lib/stores/profile.store';
import { get } from 'svelte/store';
import type { PageLoad } from './$types';

export const load: PageLoad = async ({ url }) => {
  // Reset rather than assume: a client-side navigation back from /<profile> does not reload
  // this module, so a stale profile would otherwise leak into the default route.
  profileStore.set(undefined);

  const clientParam = url.searchParams.get('client');
  if (clientParam) {
    clientIdentifierStore.set(clientParam);
  }

  const configRequest = await api.getConfig({
    clientIdentifier: get(clientIdentifierStore),
    profile: get(profileStore)
  });

  configStore.ps(configRequest.data);
};
