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
    },

    getClientLocalDate: function () {
        try {
            const now = new Date();
            const year = now.getFullYear();
            const month = String(now.getMonth() + 1).padStart(2, '0');
            const day = String(now.getDate()).padStart(2, '0');
            return `${year}-${month}-${day}`;
        } catch (err) {
            return null;
        }
    },

    registerShoppingSyncListeners: function (dotNetRef) {
        try {
            this.unregisterShoppingSyncListeners();
            const notifyIfActive = () => {
                if (document.visibilityState === 'hidden') {
                    return;
                }
                try {
                    dotNetRef.invokeMethodAsync('OnClientResumedAsync');
                } catch (e) {
                    console.debug('OnClientResumedAsync kunde inte anropas:', e);
                }
            };

            const onReconnectStateChanged = (event) => {
                if (event && event.detail && event.detail.state === 'hide') {
                    notifyIfActive();
                }
            };

            this._shoppingVisibilityHandler = notifyIfActive;
            this._shoppingFocusHandler = notifyIfActive;
            this._shoppingOnlineHandler = notifyIfActive;
            this._shoppingReconnectHandler = onReconnectStateChanged;

            document.addEventListener('visibilitychange', this._shoppingVisibilityHandler);
            window.addEventListener('pageshow', this._shoppingFocusHandler);
            window.addEventListener('online', this._shoppingOnlineHandler);
            document.addEventListener('components-reconnect-state-changed', this._shoppingReconnectHandler);

            this._initShoppingTouchDrag(dotNetRef);
        } catch (err) {
            console.debug('registerShoppingSyncListeners misslyckades:', err);
        }
    },

    _initShoppingTouchDrag: function (dotNetRef) {
        const LONG_PRESS_MS = 350;
        const MOVE_THRESHOLD_PX = 9;

        // Touch State Machine: 'idle' | 'pendingLongPress' | 'dragging' | 'cancelled'
        let touchState = {
            mode: 'idle',
            timer: null,
            touchId: null,
            startX: 0,
            startY: 0,
            sourceRow: null,
            sourceItemId: 0,
            targetItemId: null,
            targetElement: null,
            handleElement: null
        };

        let isTouchActive = false;
        let suppressClickTarget = null;
        let suppressClickTimer = null;

        const clearPreviewClasses = () => {
            document.querySelectorAll('.shopping-unchecked-list .shopping-item-row').forEach(row => {
                row.classList.remove('is-dragging', 'drag-over', 'drag-over-above', 'drag-over-below');
            });
            document.body.classList.remove('shopping-touch-reordering');
        };

        const suppressNextClick = (element) => {
            if (suppressClickTimer) {
                clearTimeout(suppressClickTimer);
            }
            suppressClickTarget = element;
            suppressClickTimer = setTimeout(() => {
                suppressClickTarget = null;
                suppressClickTimer = null;
            }, 400);
        };

        const onTouchStart = (e) => {
            if (e.touches.length !== 1) {
                if (touchState.mode !== 'idle') {
                    cleanupTouchGesture(true);
                }
                return;
            }

            const touch = e.touches[0];
            const target = touch.target;

            // Only start touch drag from the dedicated drag handle (DEL B)
            const handle = target && target.closest ? target.closest('.shopping-drag-handle') : null;
            if (!handle) {
                return;
            }

            // The drag handle must belong to an unchecked row that is not in edit mode
            const row = handle.closest('.shopping-unchecked-list .shopping-item-row[data-item-id]');
            if (!row || row.classList.contains('is-editing')) {
                return;
            }

            const itemId = parseInt(row.getAttribute('data-item-id'), 10);
            if (!itemId) {
                return;
            }

            // Temporarily disable HTML5 draggable so mobile browsers never detach the button as a drag ghost (DEL E)
            handle.draggable = false;

            isTouchActive = true;
            if (touchState.timer) {
                clearTimeout(touchState.timer);
            }
            clearPreviewClasses();

            touchState = {
                mode: 'pendingLongPress',
                timer: null,
                touchId: touch.identifier,
                startX: touch.clientX,
                startY: touch.clientY,
                sourceRow: row,
                sourceItemId: itemId,
                targetItemId: null,
                targetElement: target,
                handleElement: handle
            };

            // Start long-press timer (350 ms). If finger remains still, row enters 'dragging' state.
            touchState.timer = setTimeout(() => {
                if (touchState.mode !== 'pendingLongPress') {
                    return;
                }

                // Long-press activated! Transition to 'dragging' state
                touchState.mode = 'dragging';
                touchState.timer = null;
                touchState.sourceRow.classList.add('is-dragging');
                document.body.classList.add('shopping-touch-reordering');

                try {
                    if (navigator.vibrate) {
                        navigator.vibrate(25);
                    }
                } catch (_) {}

                try {
                    dotNetRef.invokeMethodAsync('OnTouchDragStartAsync', touchState.sourceItemId);
                } catch (_) {}
            }, LONG_PRESS_MS);
        };

        const onTouchMove = (e) => {
            if (touchState.mode === 'idle') {
                return;
            }

            const touch = Array.from(e.touches).find(t => t.identifier === touchState.touchId);
            if (!touch) {
                return;
            }

            if (touchState.mode === 'pendingLongPress') {
                const deltaX = Math.abs(touch.clientX - touchState.startX);
                const deltaY = Math.abs(touch.clientY - touchState.startY);
                const distance = Math.hypot(deltaX, deltaY);

                if (distance > MOVE_THRESHOLD_PX) {
                    // Finger moved > 9px before 350 ms: user is swiping vertically across the handle!
                    if (touchState.timer) {
                        clearTimeout(touchState.timer);
                        touchState.timer = null;
                    }
                    touchState.mode = 'cancelled';
                    suppressNextClick(touchState.handleElement);
                    // Do NOT call e.preventDefault(); let browser scroll smoothly
                }
                return;
            }

            if (touchState.mode === 'dragging') {
                // Drag is active! Prevent browser scroll
                if (e.cancelable) {
                    e.preventDefault();
                }

                // Smooth auto-scroll when dragging near top or bottom of mobile viewport
                const edgeThreshold = 65;
                if (touch.clientY < edgeThreshold) {
                    window.scrollBy(0, -10);
                } else if (touch.clientY > window.innerHeight - edgeThreshold) {
                    window.scrollBy(0, 10);
                }

                const elUnderTouch = document.elementFromPoint(touch.clientX, touch.clientY);
                const overRow = elUnderTouch && elUnderTouch.closest
                    ? elUnderTouch.closest('.shopping-unchecked-list .shopping-item-row[data-item-id]')
                    : null;

                document.querySelectorAll('.shopping-unchecked-list .shopping-item-row').forEach(r => {
                    r.classList.remove('drag-over', 'drag-over-above', 'drag-over-below');
                });

                if (overRow && overRow !== touchState.sourceRow) {
                    const overId = parseInt(overRow.getAttribute('data-item-id'), 10);
                    if (overId) {
                        touchState.targetItemId = overId;
                        const rows = Array.from(document.querySelectorAll('.shopping-unchecked-list .shopping-item-row[data-item-id]'));
                        const sourceIdx = rows.indexOf(touchState.sourceRow);
                        const targetIdx = rows.indexOf(overRow);
                        overRow.classList.add('drag-over');
                        if (sourceIdx > targetIdx) {
                            overRow.classList.add('drag-over-above');
                        } else {
                            overRow.classList.add('drag-over-below');
                        }
                    }
                } else if (overRow === touchState.sourceRow) {
                    touchState.targetItemId = touchState.sourceItemId;
                }
            }
        };

        const onTouchEnd = (e) => {
            isTouchActive = false;
            if (touchState.mode === 'idle') {
                return;
            }

            const state = touchState;

            if (state.timer) {
                clearTimeout(state.timer);
                state.timer = null;
            }

            if (state.handleElement) {
                state.handleElement.draggable = true;
            }

            clearPreviewClasses();

            if (state.mode === 'pendingLongPress') {
                // Short tap (< 350ms without movement):
                // Do NOT suppress click! Native click event fires and toggles Flytta upp / Flytta ned menu.
                touchState = { mode: 'idle', timer: null, touchId: null, startX: 0, startY: 0, sourceRow: null, sourceItemId: 0, targetItemId: null, targetElement: null, handleElement: null };
                return;
            }

            if (state.mode === 'cancelled') {
                // User was scrolling over handle: suppress accidental click on finger release
                suppressNextClick(state.handleElement);
                touchState = { mode: 'idle', timer: null, touchId: null, startX: 0, startY: 0, sourceRow: null, sourceItemId: 0, targetItemId: null, targetElement: null, handleElement: null };
                return;
            }

            if (state.mode === 'dragging') {
                // Completed drag: suppress trailing click event so menu is not opened upon release
                suppressNextClick(state.handleElement);
                touchState = { mode: 'idle', timer: null, touchId: null, startX: 0, startY: 0, sourceRow: null, sourceItemId: 0, targetItemId: null, targetElement: null, handleElement: null };

                try {
                    if (state.targetItemId && state.targetItemId !== state.sourceItemId) {
                        dotNetRef.invokeMethodAsync('OnTouchDropAsync', state.sourceItemId, state.targetItemId);
                    } else {
                        dotNetRef.invokeMethodAsync('OnTouchDragCancelAsync');
                    }
                } catch (_) {}
            }
        };

        const onTouchCancel = () => {
            isTouchActive = false;
            cleanupTouchGesture(true);
        };

        const cleanupTouchGesture = (notifyBlazorCancel) => {
            const wasDragging = touchState.mode === 'dragging';
            if (touchState.timer) {
                clearTimeout(touchState.timer);
            }
            if (touchState.handleElement) {
                touchState.handleElement.draggable = true;
            }
            clearPreviewClasses();
            touchState = { mode: 'idle', timer: null, touchId: null, startX: 0, startY: 0, sourceRow: null, sourceItemId: 0, targetItemId: null, targetElement: null, handleElement: null };

            if (wasDragging && notifyBlazorCancel) {
                try {
                    dotNetRef.invokeMethodAsync('OnTouchDragCancelAsync');
                } catch (_) {}
            }
        };

        // Capturing click blocker: suppresses synthetic click after drag or scroll
        const onClickCapture = (e) => {
            if (suppressClickTarget) {
                const target = e.target;
                if (suppressClickTarget === target ||
                    (suppressClickTarget.contains && suppressClickTarget.contains(target)) ||
                    (target && target.closest && target.closest('.shopping-drag-handle') === suppressClickTarget)) {
                    e.stopPropagation();
                    e.preventDefault();
                    suppressClickTarget = null;
                    if (suppressClickTimer) {
                        clearTimeout(suppressClickTimer);
                        suppressClickTimer = null;
                    }
                }
            }
        };

        // On mouse drag: set drag image to the entire varurad so the whole row follows the cursor
        // On touch: prevent mobile browser from detaching buttons as HTML5 drag ghosts (DEL E)
        const onDragStartCapture = (e) => {
            if (isTouchActive || touchState.mode !== 'idle') {
                e.preventDefault();
                return false;
            }

            // Real mouse drag from desktop:
            const handle = e.target && e.target.closest ? e.target.closest('.shopping-drag-handle') : null;
            if (handle) {
                const row = handle.closest('.shopping-item-row');
                if (row && e.dataTransfer) {
                    e.dataTransfer.effectAllowed = 'move';
                    try {
                        e.dataTransfer.setData('text/plain', row.getAttribute('data-item-id') || '');
                    } catch (_) {}

                    if (e.dataTransfer.setDragImage) {
                        const rect = row.getBoundingClientRect();
                        const handleRect = handle.getBoundingClientRect();
                        const offsetX = (handleRect.left + handleRect.width / 2) - rect.left;
                        const offsetY = (handleRect.top + handleRect.height / 2) - rect.top;
                        e.dataTransfer.setDragImage(row, offsetX, offsetY);
                    }
                }
            }
        };

        // Prevent long-press context menu on rows (DEL E)
        const onContextMenuCapture = (e) => {
            if (touchState.mode === 'dragging' || touchState.mode === 'pendingLongPress') {
                e.preventDefault();
                return false;
            }
        };

        const onMouseDownCapture = () => {
            isTouchActive = false;
        };

        this._shoppingTouchStart = onTouchStart;
        this._shoppingTouchMove = onTouchMove;
        this._shoppingTouchEnd = onTouchEnd;
        this._shoppingTouchCancel = onTouchCancel;
        this._shoppingClickCapture = onClickCapture;
        this._shoppingDragStartCapture = onDragStartCapture;
        this._shoppingContextMenuCapture = onContextMenuCapture;
        this._shoppingMouseDownCapture = onMouseDownCapture;

        document.addEventListener('touchstart', this._shoppingTouchStart, { passive: true });
        document.addEventListener('touchmove', this._shoppingTouchMove, { passive: false });
        document.addEventListener('touchend', this._shoppingTouchEnd, { passive: false });
        document.addEventListener('touchcancel', this._shoppingTouchCancel, { passive: true });
        document.addEventListener('click', this._shoppingClickCapture, true);
        document.addEventListener('dragstart', this._shoppingDragStartCapture, true);
        document.addEventListener('contextmenu', this._shoppingContextMenuCapture, true);
        window.addEventListener('mousedown', this._shoppingMouseDownCapture, true);
    },

    unregisterShoppingSyncListeners: function () {
        try {
            if (this._shoppingVisibilityHandler) {
                document.removeEventListener('visibilitychange', this._shoppingVisibilityHandler);
                this._shoppingVisibilityHandler = null;
            }
            if (this._shoppingFocusHandler) {
                window.removeEventListener('pageshow', this._shoppingFocusHandler);
                this._shoppingFocusHandler = null;
            }
            if (this._shoppingOnlineHandler) {
                window.removeEventListener('online', this._shoppingOnlineHandler);
                this._shoppingOnlineHandler = null;
            }
            if (this._shoppingReconnectHandler) {
                document.removeEventListener('components-reconnect-state-changed', this._shoppingReconnectHandler);
                this._shoppingReconnectHandler = null;
            }
            if (this._shoppingTouchStart) {
                document.removeEventListener('touchstart', this._shoppingTouchStart);
                this._shoppingTouchStart = null;
            }
            if (this._shoppingTouchMove) {
                document.removeEventListener('touchmove', this._shoppingTouchMove);
                this._shoppingTouchMove = null;
            }
            if (this._shoppingTouchEnd) {
                document.removeEventListener('touchend', this._shoppingTouchEnd);
                this._shoppingTouchEnd = null;
            }
            if (this._shoppingTouchCancel) {
                document.removeEventListener('touchcancel', this._shoppingTouchCancel);
                this._shoppingTouchCancel = null;
            }
            if (this._shoppingClickCapture) {
                document.removeEventListener('click', this._shoppingClickCapture, true);
                this._shoppingClickCapture = null;
            }
            if (this._shoppingDragStartCapture) {
                document.removeEventListener('dragstart', this._shoppingDragStartCapture, true);
                this._shoppingDragStartCapture = null;
            }
            if (this._shoppingContextMenuCapture) {
                document.removeEventListener('contextmenu', this._shoppingContextMenuCapture, true);
                this._shoppingContextMenuCapture = null;
            }
            if (this._shoppingMouseDownCapture) {
                window.removeEventListener('mousedown', this._shoppingMouseDownCapture, true);
                this._shoppingMouseDownCapture = null;
            }
        } catch (err) {
            console.debug('unregisterShoppingSyncListeners misslyckades:', err);
        }
    }
};

