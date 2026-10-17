// The full-screen picture: opens fitted to the screen, then zooms and pans.
//
// Same rule as drag.js: JavaScript owns the gesture, so a pinch is not an
// interop call per move. The stage is an ordinary scrolling box and zooming only
// resizes the picture, so panning is plain scrolling: scrollbars and the wheel
// work as usual, and a pinch keeps the point under the fingers by adjusting the
// scroll position.
//
// Gestures: pinch, ctrl+wheel (also what a trackpad pinch sends) and the +/-
// buttons zoom; one finger or a mouse drag pans; a double tap toggles between
// fitted and close up. Never smaller than fitted.
//
// Markup contract:
//   [data-zoom-stage]          the scrolling box this module is attached to
//     img                      the picture, sized here
//   [data-zoom="in|out|fit"]   buttons anywhere in the stage's parent
//
// A tap on the stage outside the picture still reaches Blazor (it closes the
// viewer); a tap on the picture, and the click that ends a drag or a pinch, are
// swallowed, like drag.js.

const STEP = 1.5;
const CLOSE_UP = 2.5;
const MAX_OVER_FIT = 8;
const DOUBLE_TAP_MS = 300;
const SLOP = 6;

let current = null;

export function attach(stage) {
    detach();

    const img = stage.querySelector('img');

    if (!img) {
        return;
    }

    const state = {
        stage,
        img,
        scale: 0,
        fit: 1,
        pointers: new Map(),
        pinch: null,
        moved: false,
        lastTap: null,
        swallowClick: false,
        listeners: [],
    };

    const on = (target, type, handler, options) => {
        target.addEventListener(type, handler, options);
        state.listeners.push(() => target.removeEventListener(type, handler, options));
    };

    // photos.js sets src once the picture has been fetched; a new picture means
    // starting fitted again.
    on(img, 'load', () => reset(state));
    on(window, 'resize', () => refit(state));
    on(stage, 'pointerdown', event => onDown(state, event));
    on(stage, 'pointermove', event => onMove(state, event));
    on(stage, 'pointerup', event => onUp(state, event));
    on(stage, 'pointercancel', event => onUp(state, event));
    on(stage, 'wheel', event => onWheel(state, event), { passive: false });
    on(stage, 'click', event => {
        // A tap on the picture itself is for zooming, not for closing.
        if (state.swallowClick || event.target === img) {
            state.swallowClick = false;
            event.stopPropagation();
            event.preventDefault();
        }
    }, true);

    for (const button of stage.parentElement.querySelectorAll('[data-zoom]')) {
        on(button, 'click', event => {
            event.stopPropagation();
            onButton(state, button.dataset.zoom);
        });
    }

    if (img.complete && img.naturalWidth > 0) {
        reset(state);
    }

    current = state;
}

export function detach() {
    if (current) {
        current.listeners.forEach(remove => remove());
        current = null;
    }
}

function fitScale(state) {
    const style = getComputedStyle(state.stage);
    const width = state.stage.clientWidth - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight);
    const height = state.stage.clientHeight - parseFloat(style.paddingTop) - parseFloat(style.paddingBottom);

    return Math.min(width / state.img.naturalWidth, height / state.img.naturalHeight);
}

function reset(state) {
    if (!state.img.naturalWidth) {
        return;
    }

    state.fit = fitScale(state);
    apply(state, state.fit);
    state.img.classList.add('is-ready');
}

function refit(state) {
    if (!state.scale) {
        return;
    }

    const wasFitted = state.scale <= state.fit;
    state.fit = fitScale(state);
    apply(state, wasFitted ? state.fit : state.scale);
}

function clamp(state, scale) {
    return Math.min(Math.max(scale, state.fit), state.fit * MAX_OVER_FIT);
}

function apply(state, scale) {
    state.scale = clamp(state, scale);
    state.img.style.width = `${state.img.naturalWidth * state.scale}px`;
    state.img.style.height = `${state.img.naturalHeight * state.scale}px`;
    state.stage.classList.toggle('is-zoomed', state.scale > state.fit + 1e-6);
}

// Zooms keeping the picture's point under (x, y) -- client coordinates -- where
// it was. Points off the picture are pinned to its nearest edge.
function zoomAt(state, scale, x, y) {
    if (!state.scale) {
        return;
    }

    const before = state.img.getBoundingClientRect();
    const fx = Math.min(Math.max((x - before.left) / before.width, 0), 1);
    const fy = Math.min(Math.max((y - before.top) / before.height, 0), 1);

    apply(state, scale);

    const after = state.img.getBoundingClientRect();
    state.stage.scrollLeft += after.left + fx * after.width - x;
    state.stage.scrollTop += after.top + fy * after.height - y;
}

function centre(state) {
    const rect = state.stage.getBoundingClientRect();
    return { x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 };
}

function onButton(state, action) {
    const { x, y } = centre(state);

    if (action === 'in') {
        zoomAt(state, state.scale * STEP, x, y);
    } else if (action === 'out') {
        zoomAt(state, state.scale / STEP, x, y);
    } else {
        apply(state, state.fit);
    }
}

function onWheel(state, event) {
    // A plain wheel scrolls, as everywhere else.
    if (!event.ctrlKey) {
        return;
    }

    event.preventDefault();

    // Trackpads send many small deltas, mice a few large ones.
    const factor = Math.exp(-event.deltaY * (event.deltaMode === 1 ? 0.05 : 0.0025));
    zoomAt(state, state.scale * factor, event.clientX, event.clientY);
}

function onDown(state, event) {
    if (event.pointerType === 'mouse' && event.button !== 0) {
        return;
    }

    // On a scrollbar: the browser's own drag, not a pan.
    if (event.target === state.stage
        && (event.offsetX >= state.stage.clientWidth || event.offsetY >= state.stage.clientHeight)) {
        return;
    }

    state.pointers.set(event.pointerId, { x: event.clientX, y: event.clientY, startX: event.clientX, startY: event.clientY });
    state.stage.setPointerCapture(event.pointerId);

    if (state.pointers.size === 1) {
        state.moved = false;
    } else if (state.pointers.size === 2) {
        state.pinch = pinchOf(state);
        state.moved = true;
    }
}

function pinchOf(state) {
    const [a, b] = [...state.pointers.values()];
    return {
        distance: Math.hypot(a.x - b.x, a.y - b.y) || 1,
        x: (a.x + b.x) / 2,
        y: (a.y + b.y) / 2,
    };
}

function onMove(state, event) {
    const pointer = state.pointers.get(event.pointerId);

    if (!pointer) {
        return;
    }

    const dx = event.clientX - pointer.x;
    const dy = event.clientY - pointer.y;
    pointer.x = event.clientX;
    pointer.y = event.clientY;

    if (state.pointers.size >= 2 && state.pinch) {
        const next = pinchOf(state);
        zoomAt(state, state.scale * (next.distance / state.pinch.distance), next.x, next.y);
        state.stage.scrollLeft -= next.x - state.pinch.x;
        state.stage.scrollTop -= next.y - state.pinch.y;
        state.pinch = next;
        return;
    }

    if (!state.moved && Math.hypot(event.clientX - pointer.startX, event.clientY - pointer.startY) > SLOP) {
        state.moved = true;
    }

    if (state.moved) {
        state.stage.scrollLeft -= dx;
        state.stage.scrollTop -= dy;
    }
}

function onUp(state, event) {
    if (!state.pointers.delete(event.pointerId)) {
        return;
    }

    if (state.pointers.size >= 2) {
        state.pinch = pinchOf(state);
        return;
    }

    state.pinch = null;

    // Lifting one finger of a pinch leaves the other panning, from where it is.
    if (state.pointers.size === 1) {
        return;
    }

    if (state.moved) {
        state.swallowClick = true;
        // A drag that ends off the stage sends no click to swallow.
        setTimeout(() => { state.swallowClick = false; }, 0);
        state.lastTap = null;
        return;
    }

    if (event.type === 'pointercancel') {
        return;
    }

    const now = performance.now();
    const last = state.lastTap;

    if (last && now - last.time < DOUBLE_TAP_MS && Math.hypot(event.clientX - last.x, event.clientY - last.y) < SLOP * 4) {
        state.lastTap = null;
        state.swallowClick = true;
        setTimeout(() => { state.swallowClick = false; }, 0);

        if (state.scale > state.fit + 1e-6) {
            apply(state, state.fit);
        } else {
            zoomAt(state, state.fit * CLOSE_UP, event.clientX, event.clientY);
        }
        return;
    }

    state.lastTap = { time: now, x: event.clientX, y: event.clientY };
}
