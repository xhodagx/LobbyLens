# LobbyLens

A Hearthstone Deck Tracker plugin that turns the Battlegrounds leaderboard rail into a live
tactical readout: every opponent's MMR and ladder rank, hero, tech tier, health, board
composition, live standings order, and eliminations — pinned in one HDT-styled panel.

No Overwolf, no ads, one DLL.

## Features

- **Opponent MMR + ladder rank** (`Prophane 11240 #389`) from the official Blizzard
  leaderboards (players below the 8000 cutoff show as `8000↓`)
- **Guided name resolution** — the panel appears from turn 1 with placeholders and a
  "hover N more portraits" countdown (the game only loads names into memory on mouseover)
- **Hero, tier, health** per player, live (`Jandice · T5 · 31♥+4`)
- **Board composition** of each opponent's last-fought board (`4 Mech · 2 Beast — t9`)
- **Live standings order** — rows reorder as rail places shift; rating sort available
- **Eliminations** — strikethrough + final place, hardened against false positives
- **Duos** — team grouping, shared-pool health, team placements
- Settings window: font size, opacity, panel scale, sort mode, per-feature toggles,
  privacy, verbose logging, position reset. Drag to move, scroll to zoom (0.5×–3×).

## Install

1. Build (see below) or take a built `LobbyLens.dll`
2. Drop it into `%AppData%\HearthstoneDeckTracker\Plugins\`
3. Restart HDT and enable the plugin under Options → Tracker → Plugins
4. The plugin-list button opens Settings

## Updates

HDT has no plugin auto-update, so LobbyLens ships its own. Once per HDT session the plugin
checks a small metadata file on its backend; when a newer release exists it downloads the
release package, **verifies its RSA signature against a key baked into the plugin** (an
unsigned or tampered package is refused — the CDN is just a pipe), and stages it. The
panel shows `vX.Y.Z installed — restart HDT`; the update applies on the next HDT start.

Auto-update is on by default and can be disabled in Settings, which falls back to a
notice + manual download link. A remote stand-down flag lets a broken game patch pause
match processing cleanly ("LobbyLens paused") until a fix ships — the updater keeps
running, since it is the recovery path.

## Build

Requires the .NET SDK and an installed Hearthstone Deck Tracker (the newest
`%LocalAppData%\HearthstoneDeckTracker\app-*` folder is auto-detected for references;
override with `-p:HdtAppDir=<path>` if needed):

```
dotnet build LobbyLens/LobbyLens.csproj -c Release
```

## Releasing (maintainer)

1. Bump `<Version>` in `LobbyLens/LobbyLens.csproj` (single source of truth — the plugin,
   the update check, and the package name all read it)
2. Test in HDT, commit, then run `./release.ps1` — it builds, zips, **signs the package
   with the offline release key**, uploads it, and updates `meta.json` (merging, so
   support links survive). Every installed copy stages the update on its next HDT start.
3. Create the matching GitHub release with the zip attached (manual-download fallback).

`meta.json` is the remote control for shipped binaries — **additive fields only, never
rename or repurpose one**: `latest`/`url`/`pkg`/`sig` (update channel), `kofi`/
`lightning`/`btc` (Settings support links; empty hides), `standDown`/`minVersion`
(kill switch), `ingest` (endpoint override). The private signing key lives outside the
repo (`~\.lobbylens\signing\`) and must never be committed or uploaded; losing it means
old binaries can no longer auto-update (they fall back to the manual notice), so back it
up. Key rotation: add the new public key to `Updater.PublicKeys`, ship a release signed
with the old key, then sign with the new one from the next release on.

## How it gets its data

- **Ratings** come from the **LobbyLens backend** — a small cloud service that mirrors
  Blizzard's official leaderboards into one pre-aggregated, CDN-cached file per region/mode.
  The plugin fetches a single ~50 KB file instead of paging the Blizzard API directly. If
  the backend is ever unreachable, the plugin **automatically falls back** to fetching
  Blizzard's API itself, so ratings keep working regardless. Results are cached on disk
  (6-hour TTL). Backend source: [lobbylens-functions](https://github.com/xhodagx/lobbylens-functions).
- **Everything else** (hero, tier, health, board comps, eliminations, standings) comes from
  HDT's game state and the game client's own UI memory — the same data the native rail
  hover uses. None of it leaves your PC.

## Privacy

LobbyLens can contribute **anonymized** match summaries to the backend to power future
community stats (hero/comp win rates, lobby difficulty). This is controlled by the
**Privacy → "Contribute anonymous match data"** setting.

- Battletags are **one-way SHA-256 hashed** before they ever leave your machine — raw
  player names are never transmitted or stored.
- A summary contains heroes, placements, tiers, ratings, and board comps for the lobby —
  no chat, no personal information, no account identifiers.
- Toggle it off any time in Settings.

Logs and settings live in `%AppData%\HearthstoneDeckTracker\LobbyLens\`.

## Related repositories

- [lobbylens-infra](https://github.com/xhodagx/lobbylens-infra) — the backend's Azure
  infrastructure (Bicep)
- [lobbylens-functions](https://github.com/xhodagx/lobbylens-functions) — the backend
  service (Azure Functions)

## License

MIT — Copyright (c) 2026 xhodagx
