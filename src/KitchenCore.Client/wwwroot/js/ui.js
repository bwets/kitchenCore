// Small browser affordances that Blazor cannot express on its own.

// Escape closes whatever is open. Registered once on the document rather than
// per dialog: a dialog only receives key events while something inside it has
// focus, and clicking the page behind it silently breaks that.
let escapeHandlers = [];

export function registerEscape(dotnetRef) {
    if (escapeHandlers.length === 0) {
        document.addEventListener('keydown', onKeyDown, true);
    }

    escapeHandlers.push(dotnetRef);
}

export function unregisterEscape(dotnetRef) {
    escapeHandlers = escapeHandlers.filter(h => h !== dotnetRef);

    if (escapeHandlers.length === 0) {
        document.removeEventListener('keydown', onKeyDown, true);
    }
}

function onKeyDown(event) {
    if (event.key !== 'Escape' || escapeHandlers.length === 0) {
        return;
    }

    // Only the most recently opened one closes, so a dialog stacked over
    // another does not dismiss both at once.
    const handler = escapeHandlers[escapeHandlers.length - 1];

    event.preventDefault();
    event.stopPropagation();
    handler.invokeMethodAsync('HandleEscape');
}

// Focus a field and select what is in it. Opening an existing entry should let
// you type a replacement straight away; opening a new one should just be ready.
export function focusAndSelect(element) {
    if (!element) {
        return;
    }

    element.focus();

    if (typeof element.select === 'function' && element.value) {
        element.select();
    }
}

// A pop-up menu hangs below its button, which near the bottom of the screen puts
// half of it out of sight. Open it upwards instead when there is more room above.
// (On a phone it is a bottom sheet, fixed in place, and this changes nothing.)
export function placeMenu(menu) {
    if (!menu) {
        return;
    }

    menu.classList.remove('is-up');

    const margin = 8;
    const rect = menu.getBoundingClientRect();
    const anchor = menu.parentElement.getBoundingClientRect();

    if (rect.bottom > window.innerHeight - margin && anchor.top > window.innerHeight - anchor.bottom) {
        menu.classList.add('is-up');
    }
}

// Brings an element into view. Used to open the list view at today rather than
// at the 1st of the month, which is rarely where anyone wants to start reading.
export function scrollIntoView(id) {
    const element = document.getElementById(id);

    if (element) {
        element.scrollIntoView({ block: 'center', behavior: 'smooth' });
    }
}

// The week opens on Monday -- which on a Friday means scrolling past four days
// you have already eaten. Bring today to where you land: the top of the page on
// a phone, the start of the strip wherever the grid scrolls sideways.
export function scrollDayIntoView(date) {
    // Phone: the week is a list of day cards, so today goes to the top of the
    // page. A hidden card (the desktop grid is showing) has no offsetParent.
    const card = document.querySelector(`[data-day-card="${CSS.escape(date)}"]`);

    if (card && card.offsetParent !== null) {
        card.scrollIntoView({ block: 'start', behavior: 'instant' });
        return;
    }

    const cell = document.querySelector(`[data-day-head="${CSS.escape(date)}"]`);
    const scroller = cell?.closest('.menu-grid-scroll');

    if (!cell || !scroller) {
        return;
    }

    // Only when the strip actually scrolls; on a desktop the whole week is
    // visible and moving it would be meddling.
    if (scroller.scrollWidth <= scroller.clientWidth) {
        return;
    }

    scroller.scrollTo({
        left: cell.offsetLeft - scroller.offsetLeft,
        behavior: 'instant',
    });
}

// Keeps a view current while it sits open on the kitchen tablet. Polls on a
// timer and again the moment the tab comes back into view, which is when stale
// data is most likely and most noticed. A tick is skipped while the page is
// hidden (nobody is looking) and mid-drag (a re-render under the pointer would
// pull the drop target away).
const refreshers = new Map();

// Keyed by an id from .NET: the same object reference is not guaranteed to
// arrive as the same JS object on two different calls.
export function startAutoRefresh(id, dotnetRef, intervalMs) {
    stopAutoRefresh(id);

    const tick = () => {
        if (document.hidden || document.body.classList.contains('is-dragging')) {
            return;
        }

        dotnetRef.invokeMethodAsync('HandleAutoRefresh').catch(() => { });
    };

    const onVisibility = () => {
        if (!document.hidden) {
            tick();
        }
    };

    const timer = setInterval(tick, intervalMs);
    document.addEventListener('visibilitychange', onVisibility);
    refreshers.set(id, { timer, onVisibility });
}

export function stopAutoRefresh(id) {
    const existing = refreshers.get(id);

    if (existing) {
        clearInterval(existing.timer);
        document.removeEventListener('visibilitychange', existing.onVisibility);
        refreshers.delete(id);
    }
}
