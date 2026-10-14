// Reordering a list by dragging a handle -- the shopping list.
//
// Same rule as drag.js: JavaScript owns the gesture and .NET hears about it
// once, on drop, as (from, to). Pointer events rather than HTML5 drag and drop,
// which never fires on touch. Only the handle starts a drag, so on a phone the
// rest of the row still scrolls the list; the handle sets touch-action: none.
//
// Markup contract:
//   [data-sort-list]               the container this module is attached to
//     [data-sort-item="<index>"]   one per row, in display order; the index is
//                                  the row's place in the full list, which
//                                  may have rows filtered out of view
//       [data-sort-handle]         the grip that starts the drag

const lists = new Map();

export function attach(id, list, dotnetRef) {
    detach(id);

    const state = { list, dotnetRef, drag: null };
    const onDown = event => onPointerDown(state, event);

    list.addEventListener('pointerdown', onDown);
    lists.set(id, { list, onDown, state });
}

export function detach(id) {
    const entry = lists.get(id);

    if (entry) {
        entry.list.removeEventListener('pointerdown', entry.onDown);
        finish(entry.state, false);
        lists.delete(id);
    }
}

function onPointerDown(state, event) {
    if (event.button !== 0) {
        return;
    }

    const handle = event.target.closest('[data-sort-handle]');
    const item = handle?.closest('[data-sort-item]');

    if (!item || !state.list.contains(item)) {
        return;
    }

    event.preventDefault();

    const onMove = e => onPointerMove(state, e);
    const onUp = e => onPointerUp(state, e);

    state.drag = {
        item,
        from: parseInt(item.dataset.sortItem, 10),
        to: null,
        pointerId: event.pointerId,
        startY: event.clientY,
        onMove,
        onUp,
    };

    try {
        handle.setPointerCapture(event.pointerId);
    } catch {
        // The document listeners below still see the gesture.
    }

    document.addEventListener('pointermove', onMove, true);
    document.addEventListener('pointerup', onUp, true);
    document.addEventListener('pointercancel', onUp, true);

    item.classList.add('is-sorting');
    document.body.classList.add('is-dragging');
}

function onPointerMove(state, event) {
    const drag = state.drag;

    if (!drag || event.pointerId !== drag.pointerId) {
        return;
    }

    event.preventDefault();

    // The dragged row follows the pointer vertically; the others stay put and a
    // line shows where it would land. Cheaper than reflowing the list per move,
    // and it never lies about the order the server will end up with.
    drag.item.style.transform = `translateY(${event.clientY - drag.startY}px)`;

    const items = [...state.list.querySelectorAll('[data-sort-item]')];
    let to = items.length;

    for (let i = 0; i < items.length; i++) {
        const box = items[i].getBoundingClientRect();

        if (event.clientY < box.top + box.height / 2) {
            to = i;
            break;
        }
    }

    drag.to = to;

    for (const other of items) {
        other.classList.remove('is-sort-before', 'is-sort-after');
    }

    if (to < items.length) {
        items[to].classList.add('is-sort-before');
    } else if (items.length > 0) {
        items[items.length - 1].classList.add('is-sort-after');
    }
}

function onPointerUp(state, event) {
    if (!state.drag || event.pointerId !== state.drag.pointerId) {
        return;
    }

    finish(state, event.type === 'pointerup');
}

function finish(state, commit) {
    const drag = state.drag;

    if (!drag) {
        return;
    }

    state.drag = null;

    document.removeEventListener('pointermove', drag.onMove, true);
    document.removeEventListener('pointerup', drag.onUp, true);
    document.removeEventListener('pointercancel', drag.onUp, true);
    document.body.classList.remove('is-dragging');

    drag.item.classList.remove('is-sorting');
    drag.item.style.transform = '';

    for (const other of state.list.querySelectorAll('[data-sort-item]')) {
        other.classList.remove('is-sort-before', 'is-sort-after');
    }

    if (!commit || drag.to === null) {
        return;
    }

    // Rows carry their position in the full list, which may hide some (ticked
    // off ones), so translate "before the n-th visible row" into a position in
    // the full list: before that row's own index, or just after the last row.
    const items = [...state.list.querySelectorAll('[data-sort-item]')];
    const indexOf = el => parseInt(el.dataset.sortItem, 10);
    const before = drag.to < items.length ? indexOf(items[drag.to]) : indexOf(items[items.length - 1]) + 1;

    // The item's final index once it has been taken out -- shifted by one when it moves down.
    const to = before > drag.from ? before - 1 : before;

    if (to !== drag.from) {
        state.dotnetRef.invokeMethodAsync('HandleSort', drag.from, to).catch(() => { });
    }
}
