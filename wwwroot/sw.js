// HamDeck v3.4 — Service Worker
// Network-first for API/WS/JS/HTML, cache-first for icons/css

const CACHE = 'hamdeck-v3.4';

const STATIC_ASSETS = [
    '/',
    '/index.html',
    '/ptt.js',
    '/audio.js',
    '/app.js',
    '/style.css',
    '/login.html',
    '/admin.html',
    '/manifest.json',
    '/icons/icon-192.png',
    '/icons/icon-512.png'
];

// ── Install: pre-cache static shell ──────────────────────────────────────────
self.addEventListener('install', event => {
    event.waitUntil(
        caches.open(CACHE)
            .then(cache => cache.addAll(STATIC_ASSETS))
            .then(() => self.skipWaiting())
    );
});

// ── Activate: purge old caches ────────────────────────────────────────────────
self.addEventListener('activate', event => {
    event.waitUntil(
        caches.keys()
            .then(keys => Promise.all(
                keys.filter(k => k !== CACHE).map(k => caches.delete(k))
            ))
            .then(() => self.clients.claim())
    );
});

// ── Fetch strategy ────────────────────────────────────────────────────────────
self.addEventListener('fetch', event => {
    const { request } = event;
    const url = new URL(request.url);

    // Always bypass: API calls, WebSockets, cross-origin, non-GET
    if (
        url.pathname.startsWith('/api/') ||
        url.pathname.startsWith('/ws')   ||
        url.origin !== self.location.origin ||
        request.method !== 'GET'
    ) {
        return;
    }

    // Network-first for JS and HTML — always fetch fresh, cache as offline fallback only
    if (url.pathname.endsWith('.js') || request.mode === 'navigate') {
        event.respondWith(
            fetch(request)
                .then(res => {
                    if (res.ok) {
                        const clone = res.clone();
                        caches.open(CACHE).then(c => c.put(request, clone));
                    }
                    return res;
                })
                .catch(() => caches.match(request).then(c => c || caches.match('/index.html')))
        );
        return;
    }

    // Cache-first for icons, fonts, css — these change rarely
    event.respondWith(
        caches.match(request).then(cached => {
            const networkFetch = fetch(request).then(res => {
                if (res.ok) {
                    const clone = res.clone();
                    caches.open(CACHE).then(c => c.put(request, clone));
                }
                return res;
            });
            return cached || networkFetch;
        })
    );
});
