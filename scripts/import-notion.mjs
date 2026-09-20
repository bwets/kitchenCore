// One-off import of the family's Notion menu into KitchenCore's YAML format.
//
// Two Notion databases, with different shapes:
//
//   "Menu (old)"  Name, Date, light, main   -- 2020..2024, dates carry a year
//   "Menu"        Repas, Date, Plat         -- dates carry NO year
//
// Notion writes the year only when it differs from the current one, so every
// date in the second database falls in the year the export was taken. That year
// is passed in explicitly rather than read from the clock, so re-running this
// next January cannot silently relabel five years of dinners.
//
// The per-page Markdown files carry what the CSV drops -- recipe links, mostly
// -- so they are matched back onto the rows by (slot, date, title).
//
//   node scripts/import-notion.mjs <export-dir> <out-dir> <year-of-export>

import { readFileSync, readdirSync, writeFileSync, mkdirSync } from 'node:fs';
import { join, basename } from 'node:path';

const [exportDir, outDir, yearArg] = process.argv.slice(2);

if (!exportDir || !outDir || !yearArg) {
    console.error('usage: node import-notion.mjs <export-dir> <out-dir> <year-of-export>');
    process.exit(1);
}

const exportYear = Number(yearArg);

// --- reading Notion -------------------------------------------------------

/** Splits CSV text, honouring quoted fields. Notion quotes any value with a comma or a newline. */
function parseCsv(text) {
    const rows = [];
    let row = [], cell = '', quoted = false;

    for (let i = 0; i < text.length; i++) {
        const c = text[i];

        if (quoted) {
            if (c === '"') {
                if (text[i + 1] === '"') { cell += '"'; i++; } else quoted = false;
            } else cell += c;
        } else if (c === '"') quoted = true;
        else if (c === ',') { row.push(cell); cell = ''; }
        else if (c === '\n') { row.push(cell); rows.push(row); row = []; cell = ''; }
        else if (c !== '\r') cell += c;
    }

    if (cell || row.length) { row.push(cell); rows.push(row); }

    // Notion puts a BOM on the first header cell.
    if (rows.length) rows[0][0] = rows[0][0].replace(/^﻿/, '');

    return rows;
}

const MONTHS = {
    jan: 1, feb: 2, mar: 3, apr: 4, may: 5, jun: 6,
    jul: 7, aug: 8, sep: 9, oct: 10, nov: 11, dec: 12,
};

/**
 * "June 16, 2020" and "Mar 25" both appear. The second has no year, which is
 * Notion's way of saying "this year" -- the year of the export.
 */
function parseDate(text) {
    // A few rows hold a range, "July 4, 2024 -> July 4, 2024". A menu entry is
    // for one day, so the first date is the one that matters.
    const first = String(text ?? '').split(/\s*(?:→|->)\s*/)[0];

    const m = /^([A-Za-z]+)\s+(\d{1,2})(?:,\s*(\d{4}))?$/.exec(first.trim());
    if (!m) return null;

    const month = MONTHS[m[1].slice(0, 3).toLowerCase()];
    if (!month) return null;

    const year = m[3] ? Number(m[3]) : exportYear;
    const day = Number(m[2]);
    const date = new Date(Date.UTC(year, month - 1, day));

    // Rejects "Feb 31" rather than letting it roll into March.
    if (date.getUTCMonth() + 1 !== month || date.getUTCDate() !== day) return null;

    return year + '-' + String(month).padStart(2, '0') + '-' + String(day).padStart(2, '0');
}

/**
 * Notion's meal names drifted over five years: Midi, midi, "midi x4", "midi P",
 * "A - midi", "Soi" (a typo), "Midi=>Resto", plus a handful of gouters, one
 * brunch and one dessert. Matched loosely, because the alternative is losing
 * real meals to a stray prefix.
 *
 * Deliberately unmatched: "kids baking", "A - nothing to buy", "AOG". Those are
 * notes to self that happened to live in the meal column, and the run prints
 * them so a person can decide.
 */
function parseSlot(text) {
    const t = String(text ?? '').trim().toLowerCase();

    if (t.includes('midi') || t.includes('brunch')) return 'lunch';
    if (t.includes('soir') || t === 'soi') return 'dinner';
    if (t.includes('gouter') || t.includes('goûter')) return 'gouter';
    if (t.includes('dessert')) return 'special';

    return null;
}

/** Eating out is not a menu entry. */
function isEatingOut(title, rawSlot) {
    return /^(resto|restaurant)\b/i.test(String(title ?? '').trim()) ||
        /resto|restaurant/i.test(String(rawSlot ?? ''));
}

// --- collecting -----------------------------------------------------------

const entries = [];
const skipped = { noDate: 0, noSlot: 0, noTitle: 0, eatingOut: 0 };
const unmapped = [];

function add(rawSlot, rawDate, rawTitle) {
    const date = parseDate(rawDate);
    if (!date) { skipped.noDate++; return; }

    // Some cells hold several lines: a dish, then how to make it. The first line
    // is the meal and the rest is a note -- which is where it belongs, and which
    // also keeps a newline out of a YAML scalar.
    const parts = String(rawTitle ?? '').split(/\r?\n/).map(l => l.trim()).filter(Boolean);
    const title = parts[0] ?? '';
    const notes = parts.slice(1).join('\n');

    if (!title) { skipped.noTitle++; return; }

    if (isEatingOut(title, rawSlot)) { skipped.eatingOut++; return; }

    const slot = parseSlot(rawSlot);

    if (!slot) {
        // Listed rather than silently counted: a meal with no meal name is for a
        // person to look at, not for this script to guess.
        skipped.noSlot++;
        unmapped.push({ slot: rawSlot, date: rawDate, title });
        return;
    }

    entries.push({ date, slot, title, notes, links: [] });
}

for (const file of readdirSync(exportDir).filter(f => f.endsWith('_all.csv'))) {
    const rows = parseCsv(readFileSync(join(exportDir, file), 'utf8'));
    if (rows.length < 2) continue;

    const header = rows[0].map(h => h.trim().toLowerCase());
    const col = name => header.indexOf(name);

    // The old database put the dish in `main`; the new one calls it `Plat`.
    const slotAt = col('name') >= 0 ? col('name') : col('repas');
    const dateAt = col('date');
    const titleAt = col('plat') >= 0 ? col('plat') : col('main');
    const lightAt = col('light');

    if (slotAt < 0 || dateAt < 0 || titleAt < 0) {
        console.error('  ! ' + file + ': unexpected columns (' + header.join(', ') + ') -- skipped');
        continue;
    }

    const before = entries.length;

    for (const row of rows.slice(1)) {
        if (row.length <= dateAt) continue;

        // A few old rows put the dish in `light` instead.
        const title = (row[titleAt] || '').trim() || (lightAt >= 0 ? (row[lightAt] || '').trim() : '');
        add(row[slotAt], row[dateAt], title);
    }

    console.log('  ' + file + ': ' + (entries.length - before) + ' entries');
}

// --- enriching from the page files ---------------------------------------

/**
 * The pages hold what the CSV threw away: recipe links, and in the older
 * database a fair amount of prose -- what went into a brunch, what to buy.
 *
 * The two databases shaped their pages differently. The newer one repeats the
 * dish as a `Plat:` property; the older one names the meal in its heading and
 * leaves the dish to the CSV. So a page is matched on (date, slot), narrowed by
 * title only when the page actually carries one.
 */
function enrich(pagesDir, label) {
    let notesAdded = 0, linksAdded = 0, created = 0;

    for (const file of readdirSync(pagesDir).filter(f => f.endsWith('.md'))) {
        const text = readFileSync(join(pagesDir, file), 'utf8');

        const heading = basename(file).replace(/\s+[0-9a-f]{32}\.md$/, '');
        const date = parseDate((/^Date:\s*(.+)$/m.exec(text) || [])[1]);
        const slot = parseSlot(heading);
        const plat = ((/^Plat:\s*(.+)$/m.exec(text) || [])[1] || '').trim();

        if (!date || !slot) continue;

        // Everything that is not the heading or a Notion property.
        const body = text.split(/\r?\n/)
            .filter(line => line.trim() &&
                !/^#/.test(line) &&
                !/^(Plat|Date|Name|light|main):/i.test(line));

        if (body.length === 0) continue;

        // A line that is nothing but a URL belongs in `links`. A line that wraps
        // one in link text keeps its text, so it stays in the notes as Markdown
        // -- which is how notes are rendered anyway.
        const links = [];
        const notes = [];

        for (const line of body) {
            const bare = /^\s*(https?:\/\/\S+)\s*$/.exec(line);
            const selfTitled = /^\s*\[(https?:\/\/[^\]]+)\]\((https?:\/\/[^)]+)\)\s*$/.exec(line);

            if (bare) links.push(bare[1]);
            else if (selfTitled) links.push(selfTitled[2]);
            else notes.push(line.trim());
        }

        const target = entries.find(e =>
            e.date === date &&
            e.slot === slot &&
            (plat === '' || e.title === plat));

        if (target) {
            if (links.length) {
                const before = target.links.length;
                target.links = [...new Set([...target.links, ...links])];
                if (target.links.length > before) linksAdded++;
            }

            if (notes.length) {
                target.notes = [target.notes, notes.join('\n')].filter(Boolean).join('\n');
                notesAdded++;
            }
        } else if (notes.length) {
            // A page with real content and no row behind it -- the CSV's dish
            // column was empty. The first line is the closest thing to a title
            // it has; losing the rest would be worse than promoting it.
            entries.push({
                date,
                slot,
                title: notes[0],
                notes: notes.slice(1).join('\n'),
                links,
            });
            created++;
        }
    }

    console.log('  ' + label + ': notes on ' + notesAdded + ', links on ' + linksAdded +
        ', ' + created + ' entries the CSV did not have');
}

for (const dir of ['Menu', 'Menu (old)']) {
    try {
        enrich(join(exportDir, dir), dir);
    } catch {
        console.log('  (no ' + dir + '/ pages directory)');
    }
}

// --- writing KitchenCore's format ----------------------------------------

/**
 * Quotes anything YAML would read as something other than text.
 *
 * Five years of real family menus include "? DELHAIZE", a lone "?", and dishes
 * with colons in them. Each of those is an indicator in plain-scalar position,
 * and each produced a file that would not parse.
 */
function scalar(value) {
    const risky =
        /^[-?:,[\]{}#&*!|>'"%@`]/.test(value) ||            // indicator in first position
        /:\s|\s#/.test(value) ||                            // reads as a mapping or a comment
        /^\s|\s$/.test(value) ||                            // leading/trailing space is lost
        /[\n\r]/.test(value) ||                             // never valid in a plain scalar
        value === '' ||
        /^(true|false|null|~|-?\d+(\.\d+)?)$/i.test(value); // would parse as a non-string

    if (!risky) return value;

    return '"' + value
        .replace(/\\/g, '\\\\')
        .replace(/"/g, '\\"')
        .replace(/\r/g, '')
        .replace(/\n/g, '\\n') + '"';
}

const SLOT_ORDER = { lunch: 10, gouter: 20, dinner: 30, special: 40 };

const byYear = new Map();

for (const entry of entries) {
    const year = entry.date.slice(0, 4);
    if (!byYear.has(year)) byYear.set(year, new Map());

    const days = byYear.get(year);
    if (!days.has(entry.date)) days.set(entry.date, []);

    days.get(entry.date).push(entry);
}

mkdirSync(outDir, { recursive: true });

let total = 0, duplicates = 0;

for (const [year, days] of [...byYear].sort((a, b) => a[0].localeCompare(b[0]))) {
    const out = [
        '# Imported from Notion. One file per year; split it by hand',
        '# (' + year + '-1.yaml, ' + year + '-2.yaml...) if it ever gets unwieldy.',
        'year: ' + year,
        'days:',
    ];

    const writeBody = (entry, indent) => {
        out.push(indent + 'title: ' + scalar(entry.title));

        if (entry.notes) {
            out.push(indent + 'notes: |');
            for (const line of entry.notes.split('\n')) out.push(indent + '  ' + line);
        }

        if (entry.links.length) {
            out.push(indent + 'links:');
            for (const link of entry.links) out.push(indent + '  - ' + scalar(link));
        }
    };

    for (const date of [...days.keys()].sort()) {
        out.push('  ' + date + ':');

        const slots = new Map();

        for (const entry of days.get(date)) {
            if (!slots.has(entry.slot)) slots.set(entry.slot, []);
            slots.get(entry.slot).push(entry);
        }

        const ordered = [...slots.keys()].sort(
            (a, b) => (SLOT_ORDER[a] ?? 99) - (SLOT_ORDER[b] ?? 99));

        for (const slot of ordered) {
            const group = slots.get(slot);
            total += group.length;

            out.push('    ' + slot + ':');

            if (group.length === 1) {
                writeBody(group[0], '      ');
                continue;
            }

            // Two meals in one slot: the sequence form, which the app flags for
            // a person to resolve rather than merging silently.
            duplicates++;

            for (const entry of group) {
                out.push('      - title: ' + scalar(entry.title));

                if (entry.notes) {
                    out.push('        notes: |');
                    for (const line of entry.notes.split('\n')) out.push('          ' + line);
                }

                if (entry.links.length) {
                    out.push('        links:');
                    for (const link of entry.links) out.push('          - ' + scalar(link));
                }
            }
        }
    }

    writeFileSync(join(outDir, year + '.yaml'), out.join('\n') + '\n');
    console.log('  ' + year + '.yaml: ' + days.size + ' days');
}

console.log('\n' + total + ' entries across ' + byYear.size + ' years');
console.log('skipped: ' + JSON.stringify(skipped));
console.log('slots holding more than one entry: ' + duplicates);

if (unmapped.length) {
    console.log('\nNot imported -- no recognisable meal name. Add by hand if wanted:');
    for (const row of unmapped) {
        console.log('  [' + (row.slot || '(blank)') + '] ' + row.date + ' -- ' + row.title);
    }
}
