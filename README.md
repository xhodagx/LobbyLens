# LobbyLens

A Hearthstone Deck Tracker plugin that turns the Battlegrounds leaderboard rail into a live
tactical readout: every opponent's MMR and ladder rank, hero, tech tier, health, board
composition, live standings order, and eliminations — pinned in one HDT-styled panel.

## Features

- **Opponent MMR + ladder rank** (`Prophane 11240 #389`) from Blizzard's official
  leaderboard API, cached locally (players below the 8000 cutoff show as `8000↓`)
- **Guided name resolution** — the panel appears from turn 1 with placeholders and a
  "hover N more portraits" countdown (the game only loads names into memory on mouseover)
- **Hero, tier, health** per player, live (`Jandice · T5 · 31♥+4`)
- **Board composition** of each opponent's last-fought board (`4 Mech · 2 Beast — t9`)
- **Live standings order** — rows reorder as rail places shift; rating sort available
- **Eliminations** — strikethrough + final place, hardened against false positives
- **Duos** — team grouping, shared-pool health, team placements
- Settings window: font size, opacity, panel scale, sort mode, per-feature toggles,
  verbose logging, position reset. Drag to move, scroll to zoom (0.5×–3×).

## Install

1. Build (see below) or take a built `LobbyLens.dll`
2. Drop it into `%AppData%\HearthstoneDeckTracker\Plugins\`
3. Restart HDT and enable the plugin under Options → Tracker → Plugins
4. The plugin-list button opens Settings

## Build

Requires the .NET SDK and an installed Hearthstone Deck Tracker (references are resolved
from the HDT install; set `HdtAppDir` if your HDT app folder differs):

```
dotnet build LobbyLens/LobbyLens.csproj -c Release
```

## Data sources

- Ratings: `hearthstone.blizzard.com/en-us/api/community/leaderboardsData` (official),
  fetched gently and cached on disk with a 6-hour TTL
- Everything else: HDT's game state and the game client's own UI memory (same data the
  native rail hover uses)

Logs and settings live in `%AppData%\HearthstoneDeckTracker\LobbyLens\`.

## License

MIT — Copyright (c) 2026 xhodagx
