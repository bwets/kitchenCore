# Bundled fonts

Self-hosted rather than linked from Google Fonts on purpose: this app runs on a
kitchen tablet and, in standalone desktop mode, on a machine that may have no
internet at all. Type that silently falls back to something else whenever the
house Wi-Fi is down is not much of a design.

Both are the Latin subsets published by [Fontsource](https://fontsource.org),
which covers French and English.

- `fraunces-600.woff2` — [Fraunces](https://github.com/undercasetype/Fraunces),
  one weight (600), 18 KB. SIL Open Font License 1.1 (`Fraunces-OFL.txt`).
  From `@fontsource/fraunces@5.3.0`, `files/fraunces-latin-600-normal.woff2`.
  The display face: the app name, page and card headings, day numbers, dish
  names in the details.
- `inter-latin-var.woff2` — [Inter](https://github.com/rsms/inter), variable
  weight 100–900, 48 KB. SIL Open Font License 1.1 (`Inter-OFL.txt`).
  From `@fontsource-variable/inter@5.3.0`, `files/inter-latin-wght-normal.woff2`.
  Everything else.
