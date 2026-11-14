(function () {
    const boundButtons = new WeakMap();

    function urlBase64ToUint8Array(base64String) {
        const padding = '='.repeat((4 - (base64String.length % 4)) % 4);
        const base64 = (base64String + padding)
            .replace(/-/g, '+')
            .replace(/_/g, '/');

        const rawData = window.atob(base64);
        const outputArray = new Uint8Array(rawData.length);
        for (let i = 0; i < rawData.length; ++i) {
            outputArray[i] = rawData.charCodeAt(i);
        }
        return outputArray;
    }

    function arrayBufferToBase64Url(buffer) {
        if (!buffer) return '';
        const bytes = new Uint8Array(buffer);
        let binary = '';
        for (let i = 0; i < bytes.byteLength; i++) {
            binary += String.fromCharCode(bytes[i]);
        }
        return window.btoa(binary)
            .replace(/\+/g, '-')
            .replace(/\//g, '_')
            .replace(/=+$/, '');
    }

    function isIosDevice() {
        const ua = navigator.userAgent || '';
        const isClassicIos = /iPad|iPhone|iPod/.test(ua);
        const isIpadOs = navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1;
        return isClassicIos || isIpadOs;
    }

    function isStandalonePwa() {
        return (window.matchMedia && window.matchMedia('(display-mode: standalone)').matches) ||
            window.navigator.standalone === true;
    }

    function detectDeviceLabel() {
        const ua = navigator.userAgent || '';
        if (/iPhone/.test(ua)) return 'iPhone';
        if (/iPad/.test(ua) || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1)) return 'iPad';
        if (/Android/.test(ua)) return 'Android';
        if (/Windows/.test(ua)) return 'Windows';
        if (/Macintosh/.test(ua)) return 'Mac';
        return 'Webbläsare';
    }

    async function getEnvironmentState() {
        const isSupported = 'serviceWorker' in navigator &&
            'PushManager' in window &&
            'Notification' in window;
        const isIos = isIosDevice();
        const isStandalone = isStandalonePwa();
        const permission = typeof Notification !== 'undefined' ? Notification.permission : 'unsupported';

        let endpoint = null;
        if (isSupported) {
            try {
                const reg = await navigator.serviceWorker.getRegistration('/') || await navigator.serviceWorker.ready;
                if (reg && reg.pushManager) {
                    const sub = await reg.pushManager.getSubscription();
                    if (sub) {
                        endpoint = sub.endpoint;
                    }
                }
            } catch {
                // Ignore if service worker is not ready yet
            }
        }

        return {
            isSupported: isSupported,
            isIos: isIos,
            isStandalone: isStandalone,
            permission: permission,
            endpoint: endpoint
        };
    }

    function bindEnableButton(buttonEl, dotNetRef, vapidPublicKey) {
        if (!buttonEl || !dotNetRef || !vapidPublicKey) {
            return;
        }

        unbindEnableButton(buttonEl);

        const handler = async function () {
            // IMPORTANT (iOS Safari requirement):
            // Notification.requestPermission() MUST be invoked directly inside the user click gesture
            // without any preceding async/SignalR roundtrip.
            let permissionPromise;
            try {
                if (typeof Notification === 'undefined') {
                    await dotNetRef.invokeMethodAsync('OnPushSubscribeErrorFromJs', 'Web Push stöds inte i den här webbläsaren.');
                    return;
                }
                permissionPromise = Notification.requestPermission();
            } catch (err) {
                await dotNetRef.invokeMethodAsync('OnPushSubscribeErrorFromJs', 'Kunde inte begära notisbehörighet.');
                return;
            }

            try {
                const permission = await permissionPromise;
                if (permission !== 'granted') {
                    await dotNetRef.invokeMethodAsync('OnPushPermissionDeniedFromJs', permission);
                    return;
                }

                const registration = await navigator.serviceWorker.ready;
                let subscription = await registration.pushManager.getSubscription();
                if (!subscription) {
                    subscription = await registration.pushManager.subscribe({
                        userVisibleOnly: true,
                        applicationServerKey: urlBase64ToUint8Array(vapidPublicKey)
                    });
                }

                const p256dh = arrayBufferToBase64Url(subscription.getKey('p256dh'));
                const auth = arrayBufferToBase64Url(subscription.getKey('auth'));

                await dotNetRef.invokeMethodAsync('OnPushSubscribedFromJs', {
                    endpoint: subscription.endpoint,
                    p256dh: p256dh,
                    auth: auth,
                    deviceLabel: detectDeviceLabel()
                });
            } catch (err) {
                const message = err && err.message ? err.message : 'Okänt fel vid aktivering av pushnotiser.';
                try {
                    await dotNetRef.invokeMethodAsync('OnPushSubscribeErrorFromJs', message);
                } catch {
                    // Circuit may be disposed
                }
            }
        };

        buttonEl.addEventListener('click', handler);
        boundButtons.set(buttonEl, handler);
    }

    function unbindEnableButton(buttonEl) {
        if (!buttonEl) return;
        const existing = boundButtons.get(buttonEl);
        if (existing) {
            buttonEl.removeEventListener('click', existing);
            boundButtons.delete(buttonEl);
        }
    }

    async function unsubscribeCurrentDevice() {
        if (!('serviceWorker' in navigator)) {
            return null;
        }

        const registration = await navigator.serviceWorker.getRegistration('/') || await navigator.serviceWorker.ready;
        if (!registration || !registration.pushManager) {
            return null;
        }

        const subscription = await registration.pushManager.getSubscription();
        if (!subscription) {
            return null;
        }

        const endpoint = subscription.endpoint;
        await subscription.unsubscribe();
        return endpoint;
    }

    window.receptbankenPush = {
        getEnvironmentState: getEnvironmentState,
        bindEnableButton: bindEnableButton,
        unbindEnableButton: unbindEnableButton,
        unsubscribeCurrentDevice: unsubscribeCurrentDevice
    };
})();
