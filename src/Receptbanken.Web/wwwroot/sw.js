// Minimal service worker for PWA installability.
// Intentionally omits Cache API usage and fetch interception so Blazor Interactive Server,
// SignalR circuits, API endpoints, and uploaded images always bypass the service worker.

self.addEventListener('install', () => {
    self.skipWaiting();
});

self.addEventListener('activate', (event) => {
    event.waitUntil(self.clients.claim());
});
