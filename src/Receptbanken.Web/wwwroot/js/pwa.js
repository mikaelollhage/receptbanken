// Minimal PWA service worker registration and troubleshooting helpers.
(function () {
    if (!('serviceWorker' in navigator)) {
        return;
    }

    window.addEventListener('load', function () {
        navigator.serviceWorker.register('/sw.js', { scope: '/' })
            .catch(function (err) {
                console.debug('Service worker registration failed:', err);
            });
    });

    window.receptbankenPwa = {
        getServiceWorkerStatus: async function () {
            const registrations = await navigator.serviceWorker.getRegistrations();
            return registrations.map(function (reg) {
                return {
                    scope: reg.scope,
                    active: Boolean(reg.active),
                    waiting: Boolean(reg.waiting),
                    installing: Boolean(reg.installing)
                };
            });
        },

        unregisterServiceWorkers: async function () {
            const registrations = await navigator.serviceWorker.getRegistrations();
            const results = await Promise.all(registrations.map(function (reg) {
                return reg.unregister();
            }));
            return results.every(Boolean);
        }
    };
})();
