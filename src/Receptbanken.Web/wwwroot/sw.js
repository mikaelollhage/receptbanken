// Minimal service worker for PWA installability and Web Push notifications.
// Intentionally omits Cache API usage and fetch interception so Blazor Interactive Server,
// SignalR circuits, API endpoints, and uploaded images always bypass the service worker.

self.addEventListener('install', () => {
    self.skipWaiting();
});

self.addEventListener('activate', (event) => {
    event.waitUntil(self.clients.claim());
});

function sanitizeInternalUrl(rawUrl) {
    if (typeof rawUrl !== 'string') {
        return '/kalender';
    }

    const trimmed = rawUrl.trim();
    if (!trimmed.startsWith('/') || trimmed.startsWith('//')) {
        return '/kalender';
    }

    return trimmed;
}

self.addEventListener('push', (event) => {
    let payload = {};
    if (event.data) {
        try {
            payload = event.data.json() || {};
        } catch {
            try {
                payload = { body: event.data.text() };
            } catch {
                payload = {};
            }
        }
    }

    const title = typeof payload.title === 'string' && payload.title.trim().length > 0
        ? payload.title.trim()
        : 'Kalenderpåminnelse';
    const body = typeof payload.body === 'string' && payload.body.trim().length > 0
        ? payload.body.trim()
        : 'Du har en kommande händelse i familjekalendern.';
    const tag = typeof payload.tag === 'string' && payload.tag.trim().length > 0
        ? payload.tag.trim()
        : 'receptbanken-calendar';
    const url = sanitizeInternalUrl(payload.url);

    event.waitUntil(
        self.registration.showNotification(title, {
            body: body,
            icon: '/icons/icon-192.png',
            badge: '/icons/icon-192.png',
            tag: tag,
            renotify: false,
            data: { url: url }
        })
    );
});

self.addEventListener('notificationclick', (event) => {
    event.notification.close();

    const targetUrl = sanitizeInternalUrl(event.notification?.data?.url);

    event.waitUntil((async () => {
        const allClients = await self.clients.matchAll({
            type: 'window',
            includeUncontrolled: true
        });

        for (const client of allClients) {
            if ('focus' in client) {
                try {
                    const clientUrl = new URL(client.url);
                    if (clientUrl.origin === self.location.origin) {
                        await client.focus();
                        if ('navigate' in client) {
                            await client.navigate(targetUrl);
                        }
                        return;
                    }
                } catch {
                    // Ignore malformed client URL and continue
                }
            }
        }

        if (self.clients.openWindow) {
            await self.clients.openWindow(targetUrl);
        }
    })());
});
