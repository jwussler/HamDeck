// HamDeck v3.0.0 — Service Worker
// Network-first for API/WS, cache-first for static assets

const CACHE = 'hamdeck-v3.0.0';

const STATIC_ASSETS = [
    '/',
    '/index.html',
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

    // Always bypass: API calls, WebSockets, cross-origin
    if (
        url.pathname.startsWith('/api/') ||
        url.pathname.startsWith('/ws')   ||
        url.origin !== self.location.origin
    ) {
        return; // let the browser handle it normally
    }

    // Network-first with cache fallback for navigation requests
    if (request.mode === 'navigate') {
        event.respondWith(
            fetch(request)
                .then(res => {
                    const clone = res.clone();
                    caches.open(CACHE).then(c => c.put(request, clone));
                    return res;
                })
                .catch(() => caches.match('/index.html'))
        );
        return;
    }

    // Cache-first with network update for static assets
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
