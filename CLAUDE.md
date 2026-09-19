# KitchenCore

Family app for the week menu (part 1), shopping list (part 2) and other family
tasks later. Data is YAML files on disk, no database.

The full implementation plan lives in `.local/plan.md` -- untracked and local to
this machine, so the canonical data format is restated below rather than only
living there.

## Data format

Identity is the (date, slot) pair. No ids.

```yaml
# data/menu/2026.yaml -- shards are 2026.yaml, 2026-1.yaml, ...; the loader
# globs 2026*.yaml, merges them, and remembers which file each date came from.
year: 2026
days:
  2026-09-23:
    lunch:
      title: Steak / pâtes / pesto
      notes: |
        Sortir la viande le matin.
      links:
        - https://example.com/pesto
    dinner:
      title: Tartiflette
```

A slot holding more than one entry is written as a sequence and flagged in the
UI. The reader also tolerates a literally repeated key, which is what a person
hand-editing the file would produce:

```yaml
  2026-09-23:
    dinner:
      - title: Tartiflette
      - title: Chili
```

Undated "any day" requests live in `data/menu/requests.yaml` as a plain
sequence, addressed by ordinal and guarded by title.

## Running it

Every scenario under `fixtures/` has a launch profile that points the app at it:

```
cd src/KitchenCore.Server
dotnet run --launch-profile basic      # or empty, sharded, duplicates, git, ...
```

Profiles set `KITCHENCORE_DATA_PATH` / `KITCHENCORE_CONFIG_PATH`; the defaults
are the container paths `/app/data` and `/app/config`. Adding a scenario is a
folder plus a profile entry, no code.

Fixture folders are written to on purpose -- `git diff fixtures/` shows exactly
what the app wrote, `git checkout -- fixtures/` resets. Tests copy to a temp
directory instead, so `dotnet test` never dirties the working tree.

## Architecture decisions

These are settled; don't relitigate them without a reason.

- **Entry identity is the (date, slot) pair.** No id field anywhere, so the YAML
  stays hand-editable. One consequence: a day can hold the same slot twice. That
  is tolerated in the file and flagged as an error in the UI, never merged.
- **Slots are config, not data.** `config/app.yaml` defines them; the family can
  add "brunch" without touching a year file.
- **Drop on an occupied slot always asks**: insert / shift-right / overwrite.
  There is no silent overwrite and no swap.
- **Slots are aligned rows.** The week is ONE CSS grid (days = columns, slots =
  rows), not seven per-day stacks -- those misalign the moment a day skips a slot
  or holds two. Every day renders every slot; empty cells are the "add here" target.
- **Git is detected, not configured.** Enabled iff `.git` sits *directly* at the
  data root. Deliberately does not walk up to an ancestor repo: otherwise pointing
  a launch profile at `fixtures/basic/data` would find KitchenCore's own source
  repo and commit menu edits into the codebase. Remote and branch come from the
  repo; config supplies only the token and committer identity.
- **`/app/config` is mounted separately** from `/app/data` and is never git-synced:
  it holds the GitHub token and the device list.

## URLs

```
/                  -> redirects to /menu/week
/menu/week         the main view
/menu/multi-week   drag-and-drop across several weeks
/menu/list         tree/list grouped by month and week
/shopping          part 2
```

Section first, then view. API routes mirror it: `/api/menu/...`, `/api/shopping/...`.

## Shopping (part 2, already shaping part 1)

A shopping trip has **two** moments, each with a date *and* a time:

- **order** -- when the order must be placed
- **delivery** -- when it arrives, or when the store trip happens

Both are **predicted** until the order is actually placed, at which point they
become confirmed. The UI has to show which of the two it is: a predicted date
is a plan, a confirmed one is a commitment.

Both appear on the menu calendar as a horizontal line across the day, positioned
by time of day: **blue for the order deadline, green for the delivery.** They are
markers on the week and multi-week grids, not menu entries, so they draw over the
day column rather than occupying a slot. (Implemented with the week grid in S3/S4;
the model exists now so the home card and the grid agree.)

## Frontend conventions

- Blazor **WebAssembly** + minimal API. Server owns the files; client talks JSON.
- **Fluent UI Blazor v5** (prerelease) for components, **Material You** for colour.
  Do not add `Microsoft.FluentUI.AspNetCore.Components.Icons`: the 5.x package
  still depends on Components 4.14.4. Use small inline SVGs instead (see
  `Components/BrandMark.razor`).
- **Themes are data.** A theme is a map of tokens in `Styles/themes/`. Adding one
  (Nord and Darcula are there already) is: a map file, a line in the `$themes`
  registry in `_themes.scss`, a row in `ThemeCatalog.cs`. `emit-theme` asserts the
  token set is complete at build time, and bridges Fluent's own design tokens to
  ours -- Fluent injects its stylesheet after ours at runtime, so overriding its
  rules does not work; repointing its tokens does.
- **Auto theme writes no `data-theme` attribute**, leaving `prefers-color-scheme`
  in charge. `index.html` applies the stored theme before Blazor boots to avoid a
  flash of the wrong one.
- **Language and theme are per-device** (localStorage), not per-user.
- Slot colours come from the active theme via `SlotTone.cs`, which maps a slot key
  to one of six tones -- stable across restarts, since slots are user-defined.

## Gotchas hit already

- **Fluent v5 injects an adopted stylesheet** containing
  `body { height: 100dvh; overflow: hidden }` -- it assumes an app-shell where an
  inner region scrolls. Adopted sheets are author-level and applied after ours,
  so they win every tie. Beat them with an extra element in the selector
  (`html body`), not `!important`. The same ordering is why Fluent's design
  tokens are repointed at ours rather than its rules being overridden.
- **Drag and drop lives in `wwwroot/js/drag.js`, not in Blazor.** Handling
  pointermove in C# meant an interop call plus a re-render per move and was
  visibly laggy. JS owns the gesture and calls .NET once, on drop. Three things
  there are load-bearing: `setPointerCapture` (without it a drag cannot leave its
  own column), swallowing the `click` that follows a drag (otherwise every drop
  opens the edit dialog), and returning early when the drop cell is the source
  cell (otherwise an imprecise drag asks what to do).

- `.NET 10` serves the WASM client through `app.MapStaticAssets()`. The legacy
  `UseBlazorFrameworkFiles()` + `UseStaticFiles()` pair does not serve `_framework`.
- Singletons in the client must not depend on a scoped `HttpClient`; the client's
  `HttpClient` is registered as a singleton.
- Switching culture at startup needs
  `<BlazorWebAssemblyLoadAllGlobalizationData>true</BlazorWebAssemblyLoadAllGlobalizationData>`.
