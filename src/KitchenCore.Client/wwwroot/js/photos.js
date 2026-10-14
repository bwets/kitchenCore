// Shopping item pictures: uploading them, and showing them.
//
// The API authenticates by the X-Device-Token header, which an <img src> cannot
// send, so pictures are fetched here with the header and shown from a blob URL,
// kept per URL so a list re-render does not fetch them again. Photo names are
// random and never reused, so a cached one is never stale.
//
// Uploads are shrunk first: a phone camera's 4000px original is several MB and
// slow on a kitchen's wifi, and nobody needs more than a screen's worth to see
// which yoghurt it was.

const TOKEN_KEY = 'kitchencore.token';
const MAX_EDGE = 1600;
const QUALITY = 0.85;

const cache = new Map();

function headers() {
    let token = null;

    try {
        token = localStorage.getItem(TOKEN_KEY);
    } catch {
        // No storage: single-user mode needs no token anyway.
    }

    return token ? { 'X-Device-Token': token } : {};
}

export function show(img, url) {
    if (!img || img.dataset.photoUrl === url) {
        return;
    }

    img.dataset.photoUrl = url;
    img.classList.remove('is-broken');

    let pending = cache.get(url);

    if (!pending) {
        pending = fetch(url, { headers: headers() })
            .then(response => response.ok ? response.blob() : Promise.reject(response.status))
            .then(blob => URL.createObjectURL(blob));

        cache.set(url, pending);
        pending.catch(() => cache.delete(url));
    }

    pending
        .then(src => {
            // The element may have been reused for another picture meanwhile.
            if (img.dataset.photoUrl === url) {
                img.src = src;
            }
        })
        .catch(() => img.classList.add('is-broken'));
}

// Sends every picture chosen in the input, one after the other, and answers
// with the last response: { status, body } where body is the session as JSON.
export async function upload(input, url, title) {
    const files = [...(input.files ?? [])];

    // Cleared at once so choosing the same file again still fires a change.
    input.value = '';

    let last = null;

    for (const file of files) {
        const body = new FormData();
        const picture = await shrink(file);

        body.append('photo', picture, picture === file ? file.name : 'photo.jpg');

        if (title) {
            body.append('title', title);
            title = null;   // One typed title, for the first picture only.
        }

        try {
            const response = await fetch(url, { method: 'POST', headers: headers(), body });
            last = { status: response.status, body: response.ok ? await response.text() : null };
        } catch {
            last = { status: 0, body: null };
        }
    }

    return last;
}

async function shrink(file) {
    if (!file.type.startsWith('image/') || file.type === 'image/gif') {
        return file;
    }

    let bitmap;

    try {
        bitmap = await createImageBitmap(file, { imageOrientation: 'from-image' });
    } catch {
        // A format the browser cannot decode (HEIC on most): send it as it is.
        return file;
    }

    try {
        const scale = Math.min(1, MAX_EDGE / Math.max(bitmap.width, bitmap.height));

        if (scale === 1 && file.size < 1_500_000) {
            return file;
        }

        const canvas = document.createElement('canvas');
        canvas.width = Math.round(bitmap.width * scale);
        canvas.height = Math.round(bitmap.height * scale);
        canvas.getContext('2d').drawImage(bitmap, 0, 0, canvas.width, canvas.height);

        const blob = await new Promise(resolve => canvas.toBlob(resolve, 'image/jpeg', QUALITY));
        return blob ?? file;
    } finally {
        bitmap.close();
    }
}
