// adapter-static prerenders every route by default (`+layout.ts` sets `prerender = true`), and
// nothing here can be rendered at build time: the page is a session gate over an API that only
// answers a live, signed-in browser. The SPA fallback (`fallback: 'index.html'`, and
// MapFallbackToFile on the API) is what serves /admin, exactly as it serves /<profile>.
export const prerender = false;
