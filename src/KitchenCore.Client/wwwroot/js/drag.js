// Drag and drop for the menu views.
//
// JavaScript owns the whole gesture and .NET is called exactly once, on drop.
//
// The first version put the pointer handlers on the Blazor side, which meant a
// JS interop round-trip plus a full component re-render on every pointermove.
// It was visibly laggy. Nothing about tracking a pointer needs to go through
// .NET, so none of it does any more: the ghost, the hit-testing and the
// highlight classes are all manipulated here.
//
// HTML5 drag-and-drop is not used: it never fires on touch, and this app has to
// work on a phone.

const MOVE_THRESHOLD = 5;
const LONG_PRESS_MS = 350;

let dotnet = null;
let attached = false;
let state = null;
let ghost = null;

// A pointerup at the end of a drag is still followed by a click on the element
// the gesture started on. Without swallowing it, every drop also opens the edit
// dialog for the entry that was just moved.
let suppressClick = false;

export function init(dotnetRef) {
    dotnet = dotnetRef;

    if (attached) {
        return;
    }

    document.addEventListener('pointerdown', onPointerDown, true);
    document.addEventListener('pointermove', onPointerMove, true);
    document.addEventListener('pointerup', onPointerUp, true);
    document.addEventListener('pointercancel', cancel, true);
    document.addEventListener('click', onClick, true);
    attached = true;
}

export function dispose() {
    document.removeEventListener('pointerdown', onPointerDown, true);
    document.removeEventListener('pointermove', onPointerMove, true);
    document.removeEventListener('pointerup', onPointerUp, true);
    document.removeEventListener('pointercancel', cancel, true);
    document.removeEventListener('click', onClick, true);
    attached = false;
    dotnet = null;
    cancel();
}

function onPointerDown(event) {
    if (event.button !== 0) {
        return;
    }

    const handle = event.target.closest('[data-drag-date]');

    if (!handle) {
        return;
    }

    state = {
        handle,
        pointerId: event.pointerId,
        touch: event.pointerType === 'touch',
        startX: event.clientX,
        startY: event.clientY,
        dragging: false,
        timer: null,
        source: {
            date: handle.dataset.dragDate,
            slot: handle.dataset.dragSlot,
            index: parseInt(handle.dataset.dragIndex || '0', 10),
            title: handle.dataset.dragTitle || '',
        },
    };

    // Route every later event to the element we started on, so a fast drag that
    // outruns the pointer does not lose the gesture the instant it leaves the
    // entry. Without this a drag cannot leave its own column at all.
    try {
        handle.setPointerCapture(event.pointerId);
    } catch {
        // Capture is a convenience; the document-level listeners still work.
    }

    if (state.touch) {
        // Long press, so an ordinary finger swipe still scrolls the week.
        state.timer = setTimeout(() => begin(event.clientX, event.clientY), LONG_PRESS_MS);
    }
}

function onPointerMove(event) {
    if (!state || event.pointerId !== state.pointerId) {
        return;
    }

    const travelled = Math.abs(event.clientX - state.startX) + Math.abs(event.clientY - state.startY);

    if (!state.dragging) {
        if (state.touch) {
            // Moving before the long press fired means this is a scroll.
            if (travelled > MOVE_THRESHOLD) {
                cancel();
            }

            return;
        }

        if (travelled > MOVE_THRESHOLD) {
            begin(event.clientX, event.clientY);
        }

        return;
    }

    event.preventDefault();
    moveGhost(event.clientX, event.clientY, event.ctrlKey || event.metaKey);
    highlight(targetAt(event.clientX, event.clientY));
}

function onClick(event) {
    if (!suppressClick) {
        return;
    }

    suppressClick = false;
    event.stopPropagation();
    event.preventDefault();
}

function onPointerUp(event) {
    if (!state || event.pointerId !== state.pointerId) {
        return;
    }

    const was = state;

    // Only a real drag swallows the click; a plain tap must still open the entry.
    suppressClick = was.dragging;
    const target = was.dragging ? targetAt(event.clientX, event.clientY) : null;
    const copy = event.ctrlKey || event.metaKey;

    cancel();

    if (!target) {
        return;
    }

    // Dropping an entry back where it started is a no-op, not a question. It is
    // what a slightly imprecise drag looks like, and asking about it is noise.
    if (target.date === was.source.date && target.slot === was.source.slot) {
        return;
    }

    dotnet?.invokeMethodAsync('HandleDrop', {
        fromDate: was.source.date,
        fromSlot: was.source.slot,
        fromIndex: was.source.index,
        toDate: target.date,
        toSlot: target.slot,
        copy,
    });
}

function begin(x, y) {
    if (!state) {
        return;
    }

    clearTimeout(state.timer);
    state.dragging = true;
    document.body.classList.add('is-dragging');
    showGhost(state.source.title);
    moveGhost(x, y, false);
}

function cancel() {
    if (state?.timer) {
        clearTimeout(state.timer);
    }

    if (state?.handle && state.pointerId !== undefined) {
        try {
            state.handle.releasePointerCapture(state.pointerId);
        } catch {
            // Already gone.
        }
    }

    document.body.classList.remove('is-dragging');
    clearHighlight();
    hideGhost();
    state = null;
}

function targetAt(x, y) {
    // The ghost follows the pointer, so it would otherwise be the top element.
    if (ghost) {
        ghost.style.display = 'none';
    }

    const element = document.elementFromPoint(x, y);

    if (ghost) {
        ghost.style.display = '';
    }

    const cell = element?.closest('[data-drop-date]');

    return cell ? { date: cell.dataset.dropDate, slot: cell.dataset.dropSlot, element: cell } : null;
}

let highlighted = [];

function highlight(target) {
    clearHighlight();

    if (!target) {
        return;
    }

    // The whole column, so "which day?" is answerable mid-drag, plus the exact
    // cell so "which meal?" is too.
    for (const cell of document.querySelectorAll(`[data-drop-date="${CSS.escape(target.date)}"]`)) {
        cell.classList.add('is-over-column');
        highlighted.push(cell);
    }

    for (const head of document.querySelectorAll(`[data-day-head="${CSS.escape(target.date)}"]`)) {
        head.classList.add('is-over-column');
        highlighted.push(head);
    }

    target.element.classList.add('is-over');
    highlighted.push(target.element);
}

function clearHighlight() {
    for (const element of highlighted) {
        element.classList.remove('is-over', 'is-over-column');
    }

    highlighted = [];
}

function showGhost(title) {
    if (!ghost) {
        ghost = document.createElement('div');
        ghost.className = 'drag-ghost';
        document.body.appendChild(ghost);
    }

    ghost.textContent = title;
    ghost.hidden = false;
}

function moveGhost(x, y, copy) {
    if (!ghost) {
        return;
    }

    // transform rather than left/top: it stays on the compositor, so the ghost
    // keeps up with the pointer instead of trailing behind it.
    ghost.style.transform = `translate(${x}px, ${y}px) translate(-50%, -140%)`;
    ghost.classList.toggle('is-copy', !!copy);
}

function hideGhost() {
    if (ghost) {
        ghost.hidden = true;
    }
}
