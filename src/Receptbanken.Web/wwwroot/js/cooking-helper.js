// Screen Wake Lock API & File Download helper for Receptbanken
window.receptbanken = {
    wakeLockSentinel: null,

    requestWakeLock: async function () {
        try {
            if ('wakeLock' in navigator) {
                this.wakeLockSentinel = await navigator.wakeLock.request('screen');
                return true;
            }
        } catch (err) {
            console.debug('Screen Wake Lock kunde inte aktiveras:', err);
        }
        return false;
    },

    releaseWakeLock: async function () {
        try {
            if (this.wakeLockSentinel) {
                await this.wakeLockSentinel.release();
                this.wakeLockSentinel = null;
            }
        } catch (err) {
            console.debug('Screen Wake Lock kunde inte frigöras:', err);
        }
    },

    downloadFileFromBytes: function (fileName, contentType, base64Data) {
        const link = document.createElement('a');
        link.download = fileName;
        link.href = 'data:' + contentType + ';base64,' + base64Data;
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
    },

    copyToClipboard: async function (text, elementIdToSelectOnFailure) {
        let copied = false;

        // 1. Try navigator.clipboard (available on HTTPS or localhost)
        if (navigator.clipboard && typeof navigator.clipboard.writeText === 'function') {
            try {
                await navigator.clipboard.writeText(text);
                copied = true;
            } catch (err) {
                console.debug('navigator.clipboard.writeText misslyckades:', err);
            }
        }

        // 2. Fallback for HTTP (non-secure context) or if writeText was rejected
        if (!copied) {
            try {
                const textArea = document.createElement('textarea');
                textArea.value = text;
                textArea.style.position = 'fixed';
                textArea.style.top = '0';
                textArea.style.left = '-9999px';
                textArea.style.opacity = '0';
                textArea.setAttribute('readonly', '');
                document.body.appendChild(textArea);

                textArea.focus();
                textArea.select();
                textArea.setSelectionRange(0, text.length);

                copied = document.execCommand('copy');
                document.body.removeChild(textArea);
            } catch (fallbackErr) {
                console.debug('document.execCommand fallback misslyckades:', fallbackErr);
                copied = false;
            }
        }

        // 3. If automatic copying failed, select the element text so the user can copy manually with Ctrl+C
        if (!copied && elementIdToSelectOnFailure) {
            try {
                const el = document.getElementById(elementIdToSelectOnFailure);
                if (el) {
                    const range = document.createRange();
                    range.selectNodeContents(el);
                    const selection = window.getSelection();
                    if (selection) {
                        selection.removeAllRanges();
                        selection.addRange(range);
                    }
                }
            } catch (selectErr) {
                console.debug('Kunde inte markera koden manuellt:', selectErr);
            }
        }

        return copied;
    },

    focusElement: function (elementId) {
        try {
            const el = document.getElementById(elementId);
            if (el) {
                el.focus();
                el.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
            }
        } catch (err) {
            console.debug('focusElement misslyckades:', err);
        }
    },

    readClipboardContent: async function () {
        try {
            if (navigator.clipboard && typeof navigator.clipboard.read === 'function') {
                const items = await navigator.clipboard.read();
                for (const item of items) {
                    if (item.types.includes('text/html')) {
                        const blob = await item.getType('text/html');
                        return await blob.text();
                    }
                }
                for (const item of items) {
                    if (item.types.includes('text/plain')) {
                        const blob = await item.getType('text/plain');
                        return await blob.text();
                    }
                }
            } else if (navigator.clipboard && typeof navigator.clipboard.readText === 'function') {
                return await navigator.clipboard.readText();
            }
        } catch (err) {
            console.debug('readClipboardContent misslyckades:', err);
        }
        return null;
    }
};

