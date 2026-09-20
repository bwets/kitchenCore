# KitchenCore

The family week menu, and later the shopping list.

Plan the week's meals on a calendar the whole family can open — the tablet in the
kitchen, a phone in the supermarket, a laptop. No database: the menu is a folder
of YAML files you can read, hand-edit, and keep in git.

![the week view](resources/icon_512.png)

## Why it works this way

**The menu is files, not rows.** A year is one `2026.yaml`, split by hand into
`2026-1.yaml` when it gets unwieldy. You can read it, edit it in any text editor,
diff it, and restore last March from git history. The app never becomes the only
thing that can open your data.

**Meals are identified by where they sit** — the (date, slot) pair — so there are
no ids cluttering the file. One consequence is deliberate: a day *can* end up
with two dinners, and rather than silently merging them the app flags it and lets
you resolve it.

**Nothing is decided silently.** Dropping a meal onto an occupied slot always
asks: keep both, shift the rest along, or replace. Shifting shows you exactly
which other meals it would push before you agree.

**Git sync is detected, not configured.** If the data folder is a git repo, the
app commits and pushes, attributing each commit to whoever made the change. If it
isn't, nothing syncs and the header says so.

## Running it

### Server, with Docker

```bash
docker run -d --name kitchencore -p 8080:8080 \
  -v /srv/kitchencore/data:/app/data \
  -v /srv/kitchencore/config:/app/config \
  ghcr.io/bwets/kitchencore:latest
```

Two mounts, and the distinction matters: `/app/data` holds the menu and is the
folder you sync to git; `/app/config` holds the GitHub token and the list of
approved devices, and is deliberately **outside** it.

Or with `docker-compose.yml` from this repo:

```bash
docker compose up -d
```

### Desktop

Download the latest [release](https://github.com/bwets/kitchenCore/releases):

- **`.msix`** — installs properly, with a Start menu entry. It is self-signed, so
  import the accompanying `.cer` into *Local Machine → Trusted People* first.
- **`.zip`** — portable, no install.

On first run it asks where the menu should live:

- **On this computer** — the server runs inside the app and the menu is a folder
  on that machine. Nothing else to set up.
- **On a server** — a window onto a KitchenCore the family already runs.

Either way it asks for your name, because a standalone menu folder is often a git
clone shared with the family server, and those commits need attributing.

Settings live in `config.yaml` under `%APPDATA%\bwets\KitchenCore` (or
`~/.config/bwets/KitchenCore`).

## Access

There are no accounts and no passwords. A device asks for access with a name, an
admin approves it, and each device gets a role per section:

| Role | Can |
| --- | --- |
| **Viewer** | Read the menu |
| **Requestor** | Ask for a meal, on a day or on no particular day |
| **Editor** | Everything |

The first admin exists via a one-time `adminBootstrapCode` in `config/app.yaml`.
Only the SHA-256 of a device token is ever stored.

## The data format

```yaml
# data/menu/2026.yaml
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

Meal slots are defined in `config/app.yaml`, not in the data, so adding "brunch"
never means touching a year file. Notes are Markdown. Undated requests live in
`data/menu/requests.yaml`.

The reader is deliberately more forgiving than the writer: it copes with a
repeated slot key, a stray date format, or a file somebody made a mess of, and
reports the problem in the UI rather than refusing to start. The writer only ever
emits valid, deterministically ordered YAML, so a git diff shows what actually
changed.

## Development

```bash
dotnet run --project src/KitchenCore.Server --launch-profile basic
```

`fixtures/` holds ten scenarios — an empty first run, a sharded year, a week
spanning New Year, deliberately broken files, a git-backed folder — each with its
own launch profile. Adding one is a folder and a profile entry.

```
./scripts/dev.ps1 <scenario>        # stop any server, build, run
./scripts/reset-fixtures.ps1        # put fixtures/ back after a dev run
./scripts/init-git-fixture.ps1      # build the git scenario's repo and remote
./scripts/make-icons.ps1            # regenerate the app icon
```

Dev runs write into the scenario they are pointed at, on purpose:
`git diff fixtures/` shows exactly what the app wrote. Use the reset script
rather than `git checkout`, because the app also creates files that were never
committed.

```bash
dotnet test
```

`CLAUDE.md` records the architecture decisions and the traps already hit — worth
reading before changing the storage layer or the desktop build.

## Built with

.NET 10, Blazor WebAssembly over a minimal API, Fluent UI components on a
Material You palette, [PhotinoX](https://github.com/tryphotino) for the desktop
window, YamlDotNet, Markdig.

## Licence

MIT. See [LICENSE](LICENSE).
