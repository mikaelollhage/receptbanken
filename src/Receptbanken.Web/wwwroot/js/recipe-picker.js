// Minimal focus helper for RecipePickerModal.
// FluentDialog (fluent-dialog) traps focus and handles Escape, but does not
// return focus to the element that opened it. These two functions do just that.

let opener = null;

export function captureOpener() {
    const active = document.activeElement;
    opener = active && active !== document.body ? active : null;
}

export function restoreOpener() {
    const element = opener;
    opener = null;
    if (element && element.isConnected && typeof element.focus === "function") {
        // Wait one frame so the dialog's focus trap has been released.
        requestAnimationFrame(() => element.focus());
    }
}
