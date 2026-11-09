// Focus and scroll helper for RecipePickerModal.
// FluentDialog (fluent-dialog) traps focus and handles Escape, but does not
// return focus to the element that opened it, nor lock document body scrolling.

let opener = null;
let activePickers = 0;

export function captureOpener() {
    const active = document.activeElement;
    opener = active && active !== document.body ? active : null;

    activePickers++;
    document.documentElement.classList.add("rb-modal-open");
    document.body.classList.add("rb-modal-open");
}

export function restoreOpener() {
    if (activePickers > 0) {
        activePickers--;
    }
    if (activePickers === 0) {
        document.documentElement.classList.remove("rb-modal-open");
        document.body.classList.remove("rb-modal-open");
    }

    const element = opener;
    opener = null;
    if (element && element.isConnected && typeof element.focus === "function") {
        // Wait one frame so the dialog's focus trap has been released.
        requestAnimationFrame(() => element.focus());
    }
}

