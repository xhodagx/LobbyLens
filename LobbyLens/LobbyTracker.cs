using System;
using System.Linq;
using System.Net.Http;
using System.Collections.Generic;
using Hearthstone_Deck_Tracker;
using Hearthstone_Deck_Tracker.Enums;

namespace LobbyLens
{
    // Core per-match logic: resolves the lobby from game memory, enriches it with
    // ladder ratings and live entity state, and renders it into the panel.
    public class LobbyTracker
    {
        private class PlayerInfo
        {
            public string Name;        // null until moused over OR filled from the lobby roster
            public int Team;
            public string HeroCardId;
            public string AccountId;   // stable "hi:lo" identity from the lobby roster, if available
            public bool IsMe;
            public int FinalPlace;     // 0 = alive, -1 = dead with unknown place
            public string HeroName;
            public int Tier;
            public int Health;
            public int Armor;
            public string Comp;        // tribe counts of last-fought board
            public int CompTurn;
            public int DeadSweeps;     // consecutive sweeps reading health <= 0
            public int LivePlace;      // current rail position
            public bool NextOpponent;  // we fight this player (or their ghost) next
            public bool PrevOpponent;  // most recently fought (last combat)
            public int Encounters;     // completed combats fought against this player
            public string NameHash;    // cached SHA of Name, for form lookups
        }

        private bool isReset = true;
        private bool standShown = false;
        private bool namesDone = false;
        private int tileErrors = 0;
        private bool statusErrorLogged = false;
        private string lastNameFail = null;
        private string lastRender = null;
        private string lastStateDump = null;
        private DateTime lastStatusSweep = DateTime.MinValue;
        private DateTime lastTileSweep = DateTime.MinValue;
        private int lastNextPid = 0;
        private double tileSweepBackoff = 1; // seconds; 1 while resolution progresses, up to 5 when stalled
        private int lastResolvedCount = -1;
        private int lastTileCount = -1;
        private bool formRequested = false;

        private List<PlayerInfo> players = null;
        private bool reported = false;

        private GameMemory memory;
        private HttpClient http;
        private Leaderboard leaderboard;
        private FormStats form;
        private LobbyPanel panel;

        public LobbyTracker()
        {
            http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", $"LobbyLens/{typeof(LobbyTracker).Assembly.GetName().Version} (HDT plugin)");
            http.Timeout = TimeSpan.FromSeconds(20);
            memory = new GameMemory();
            leaderboard = new Leaderboard(http);
            form = new FormStats(http);
            panel = new LobbyPanel();
            _ = Meta.Load(http);
        }

        public void Clean()
        {
            memory.Reset();
            http.Dispose();
            panel.Clean();
            http = null;
            memory = null;
            leaderboard = null;
            form = null;
            panel = null;
        }

        public void ResetLayout()
        {
            panel?.ResetLayout();
        }

        // Position/scale the panel from Settings without a live match.
        public void ShowPreview()
        {
            panel?.ShowPreview();
        }

        private void Reset()
        {
            standShown = false;
            namesDone = false;
            tileErrors = 0;
            statusErrorLogged = false;
            lastNameFail = null;
            lastRender = null;
            lastStateDump = null;
            lastStatusSweep = DateTime.MinValue;
            lastTileSweep = DateTime.MinValue;
            lastNextPid = 0;
            formRequested = false;
            tileSweepBackoff = 1;
            lastResolvedCount = -1;
            lastTileCount = -1;
            players = null;
            reported = false;
            memory.Reset();
            panel.HidePanel();
        }

        // On return to menu, fire one anonymized summary of the match just played
        // (opt-out via Settings.reportMatches). Only matches that actually resolved
        // and ended (someone reached a final place) are sent.
        private void TryReportMatch()
        {
            if (reported || players == null || !Settings.Instance.reportMatches) { return; }
            if (!players.Any(p => p.FinalPlace != 0)) { return; }
            reported = true;

            string region = GetRegionStr();
            bool duos = players.GroupBy(p => p.Team).Any(g => g.Count() > 1);
            var summaries = new List<PlayerSummary>();
            foreach (var p in players)
            {
                if (p.Name == null) { continue; }
                leaderboard.TryGet(p.Name, out int rating, out int rank);
                summaries.Add(new PlayerSummary
                {
                    HeroCardId = p.HeroCardId,
                    FinalPlace = p.FinalPlace,
                    Tier = p.Tier,
                    Rating = rating,
                    Rank = rank,
                    Comp = p.Comp,
                    NameHash = MatchReporter.HashName(p.Name),
                    AccountHash = p.AccountId != null ? MatchReporter.HashName(p.AccountId) : null,
                    IsMe = p.IsMe
                });
            }
            if (summaries.Count > 0)
            {
                _ = MatchReporter.PostAsync(http, region, duos, summaries);
                LensLog.Info($"reported anonymized match summary ({summaries.Count} players)");
            }
        }

        public void OnUpdate()
        {
            if (Core.Game.IsInMenu)
            {
                if (!isReset)
                {
                    RecordSession();
                    TryReportMatch();
                    Reset();
                    isReset = true;
                }
            }
            else if (Core.Game.IsBattlegroundsMatch)
            {
                if (isReset)
                {
                    Reset();
                    isReset = false;
                    bool duos = !Core.Game.IsBattlegroundsSoloMatch;
                    LensLog.Info($"BG match detected (duos={duos})");
                    _ = leaderboard.Load(GetRegionStr(), duos);
                    Session.OnGameStart(SafeBgRating());
                }

                if (Core.Game.GetTurnNumber() == 0) { return; }

                // Remote kill switch: a game patch that breaks memory reads gets a
                // clean "paused" panel instead of a match of SEH exceptions. The
                // updater still runs (it's the recovery path); only match
                // processing stops.
                string standDown = Meta.StandDownMessage;
                if (standDown != null)
                {
                    if (!standShown)
                    {
                        standShown = true;
                        var msg = new List<RankLine>
                        {
                            new RankLine("LobbyLens paused"),
                            new RankLine(standDown, dim: true)
                        };
                        if (Updater.Staged) { msg.Add(new RankLine("update ready — restart HDT", dim: true)); }
                        panel.DisplayLines(msg);
                    }
                    return;
                }

                // Ratings are one feature, not the panel's spine: heroes, tiers, health,
                // comps, standings and eliminations are all local data. A ratings outage
                // (backend + Blizzard down, or CN region) renders with "-" instead of
                // withholding everything; while ratings load they show "…" briefly.
                // Sweep until every name AND stable account id is captured — the roster
                // can populate after the last portrait was hovered, and the account id
                // enriches the match report. NoteSweep's backoff caps the idle cost.
                bool needSweep = !namesDone || players.Any(p => p.AccountId == null);
                if (needSweep && (DateTime.Now - lastTileSweep).TotalSeconds >= tileSweepBackoff) { ResolveTiles(); }
                UpdateLiveStatus();
                TryLoadForm();
                Render();
            }
        }

        // Our own final placement for the just-ended game. FinalPlace once we've been
        // eliminated; the live rail place only when it can be trusted — we died (any
        // dead reading) or we demonstrably won (every rival team eliminated). A player
        // who concedes while alive gets no record: the rail still shows their
        // pre-concede standing (1 for everyone at match start), not a result.
        private void RecordSession()
        {
            var me = players?.FirstOrDefault(p => p.IsMe);
            if (me == null) { return; }
            int place = 0;
            if (me.FinalPlace > 0) { place = me.FinalPlace; }
            else if (me.FinalPlace == -1 || me.DeadSweeps >= 1)
            {
                place = me.LivePlace; // we died; the last rail read is the best estimate
            }
            else
            {
                var rivals = players.Where(p => p.Team != me.Team).ToList();
                if (rivals.Count > 0 && rivals.All(p => p.FinalPlace != 0))
                {
                    place = me.LivePlace; // corroborated win: every rival team is out
                }
            }
            if (place > 0) { Session.OnGameEnd(place); }
        }

        // Once names are resolved, ask the backend for each player's recent form, one
        // batched request per match. By hash only — the same hashes we already report.
        private void TryLoadForm()
        {
            if (formRequested || !Settings.Instance.showForm || players == null) { return; }
            if (players.Any(p => p.Name == null)) { return; } // wait for a full roster
            formRequested = true;
            var hashes = new List<string>();
            foreach (var p in players)
            {
                if (p.IsMe) { continue; }
                if (p.NameHash == null && p.Name != null) { p.NameHash = MatchReporter.HashName(p.Name); }
                if (p.NameHash != null) { hashes.Add(p.NameHash); }
                if (p.AccountId != null) { hashes.Add(MatchReporter.HashName(p.AccountId)); }
            }
            _ = form.Load(hashes);
        }

        private static int? SafeBgRating()
        {
            try { return Core.Game.CurrentBattlegroundsRating; }
            catch { return null; }
        }

        private string GetRegionStr()
        {
            return Core.Game.CurrentRegion switch
            {
                Region.US => "US",
                Region.EU => "EU",
                Region.ASIA => "AP",
                Region.CHINA => "CN",
                _ => "US"
            };
        }

        // Rebuilds the player model from the rail tiles. Stateless by design: once a
        // name has been moused over it stays readable in memory, so re-reading every
        // tick is safe and survives tile reordering.
        private void ResolveTiles()
        {
            lastTileSweep = DateTime.Now;
            try
            {
                string myName = memory.MyName;
                if (string.IsNullOrWhiteSpace(myName)) { FailName("own battletag not readable yet"); NoteSweep(false); return; }

                var tiles = memory.ReadLeaderboardTiles();
                if (tiles.Count == 0) { FailName("no leaderboard tiles readable yet"); NoteSweep(false); return; }

                var tmp = new List<PlayerInfo>();
                int unresolved = 0;

                foreach (GameMemory.Tile tile in tiles)
                {
                    var p = new PlayerInfo { Team = tile.Team };
                    string name = GameMemory.TileName(tile.Handle);
                    if (name != null)
                    {
                        int idx = name.IndexOf('#'); // battletag-mod users
                        if (idx > 0) { name = name.Substring(0, idx); }
                        p.Name = name;
                        p.IsMe = name == myName;
                    }
                    else { unresolved++; }
                    p.HeroCardId = GameMemory.TileHeroCardId(tile.Handle);
                    tmp.Add(p);
                }

                // Fill still-unhovered names from GameState's lobby roster, matched by
                // hero card id — this is what lets names appear without the hover ritual
                // when the roster is populated. Also captures each player's stable account
                // id for match reporting. Purely additive: if the roster is empty or a
                // hero is mirrored (ambiguous), those seats fall back to hover exactly as
                // before.
                var lobby = memory.ReadLobbyInfo();
                if (lobby.Count > 0)
                {
                    foreach (var p in tmp)
                    {
                        if (p.HeroCardId == null) { continue; }
                        var m = lobby.Where(l => l.HeroCardId != null &&
                            l.HeroCardId.Equals(p.HeroCardId, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (m.Count != 1) { continue; } // skip mirror-hero ambiguity
                        if (p.AccountId == null) { p.AccountId = m[0].AccountId; }
                        if (p.Name == null && !string.IsNullOrWhiteSpace(m[0].Name))
                        {
                            p.Name = m[0].Name;
                            p.IsMe = m[0].Name == myName;
                            unresolved--;
                            LensLog.Debug($"lobby roster filled '{m[0].Name}' without hover");
                        }
                    }
                }

                // Carry live state across the rebuild.
                if (players != null)
                {
                    foreach (var oldP in players.Where(x => x.FinalPlace != 0 || x.HeroName != null || x.Tier > 0 || x.NextOpponent || x.Encounters > 0))
                    {
                        var match = tmp.FirstOrDefault(x =>
                            (oldP.Name != null && x.Name == oldP.Name) ||
                            (oldP.HeroCardId != null && x.HeroCardId == oldP.HeroCardId));
                        if (match != null)
                        {
                            if (match.AccountId == null) { match.AccountId = oldP.AccountId; }
                            match.FinalPlace = oldP.FinalPlace;
                            match.HeroName = oldP.HeroName;
                            match.Tier = oldP.Tier;
                            match.Health = oldP.Health;
                            match.Armor = oldP.Armor;
                            match.Comp = oldP.Comp;
                            match.CompTurn = oldP.CompTurn;
                            match.LivePlace = oldP.LivePlace;
                            match.DeadSweeps = oldP.DeadSweeps;
                            match.NextOpponent = oldP.NextOpponent;
                            match.PrevOpponent = oldP.PrevOpponent;
                            match.Encounters = oldP.Encounters;
                            match.NameHash = oldP.NameHash;
                        }
                    }
                }
                players = tmp;

                int resolvedCount = tmp.Count - unresolved;
                NoteSweep(resolvedCount > lastResolvedCount || tmp.Count != lastTileCount);
                lastResolvedCount = resolvedCount;
                lastTileCount = tmp.Count;

                if (unresolved == 0)
                {
                    lastNameFail = null;
                    if (!namesDone) // aid sweeps re-enter here; log the milestone once
                    {
                        namesDone = true;
                        LensLog.Info($"all {players.Count} tiles resolved: [{string.Join(", ", players.Select(x => x.Name))}]");
                    }
                }
                else
                {
                    var got = players.Where(x => x.Name != null && !x.IsMe).Select(x => x.Name).ToList();
                    FailName($"{unresolved} portrait(s) not yet hovered; resolved: [{string.Join(", ", got)}]");
                }
            }
            catch (Exception ex)
            {
                NoteSweep(false);
                tileErrors++;
                if (tileErrors < 5) { LensLog.Error("failed to read leaderboard tiles", ex); }
                else if (tileErrors == 5) { LensLog.Error("failed to read leaderboard tiles; suppressing further errors", ex); }
            }
        }

        // Names appear in memory only when the user mouses over a portrait, so a
        // sweep that finds nothing new backs off (1→2→4→5s) and any progress —
        // a new name or a tile-count change — snaps back to 1s. Keeps the hover
        // countdown responsive while cutting idle memory walks ~5x.
        private void NoteSweep(bool progress)
        {
            tileSweepBackoff = progress ? 1 : Math.Min(tileSweepBackoff * 2, 5);
        }

        // Live opponent state from HDT's entities: hero, tier, health, armor, rail
        // place, elimination, and last-fought board composition.
        private void UpdateLiveStatus()
        {
            if (players == null || (DateTime.Now - lastStatusSweep).TotalSeconds < 5) { return; }
            lastStatusSweep = DateTime.Now;
            try
            {
                // Combat rounds spawn duplicate hero entities per player; prefer the
                // in-play original (lowest id) — it is the one receiving live updates.
                var canonical = Core.Game.Entities.Values.ToList()
                    .Where(e => e != null && e.IsHero && e.GetTag(HearthDb.Enums.GameTag.PLAYER_ID) > 0)
                    .GroupBy(e => e.GetTag(HearthDb.Enums.GameTag.PLAYER_ID))
                    .Select(g => g.OrderByDescending(e => e.IsInPlay ? 1 : 0).ThenBy(e => e.Id).First())
                    .ToList();

                string dump = string.Empty;
                var byPid = new Dictionary<int, PlayerInfo>();
                foreach (var entity in canonical)
                {
                    int pid = entity.GetTag(HearthDb.Enums.GameTag.PLAYER_ID);
                    string cardId = entity.CardId;
                    if (string.IsNullOrWhiteSpace(cardId)) { continue; }

                    // Exact id first; prefix matching (skin/variant suffixes) only as a
                    // fallback, so it can never shadow another player's exact match.
                    PlayerInfo p = players.FirstOrDefault(x => x.HeroCardId != null &&
                            cardId.Equals(x.HeroCardId, StringComparison.OrdinalIgnoreCase))
                        ?? players.FirstOrDefault(x => x.HeroCardId != null &&
                            (cardId.StartsWith(x.HeroCardId, StringComparison.OrdinalIgnoreCase) ||
                             x.HeroCardId.StartsWith(cardId, StringComparison.OrdinalIgnoreCase)));
                    if (p == null) { continue; }
                    byPid[pid] = p;

                    if (p.HeroName == null)
                    {
                        p.HeroName = HeroNameFromCardId(cardId);
                        if (p.HeroName != null) { LensLog.Debug($"{p.Name ?? ("pid" + pid)} is playing {p.HeroName}"); }
                    }

                    int baseHealth = entity.GetTag(HearthDb.Enums.GameTag.HEALTH);
                    int damage = entity.GetTag(HearthDb.Enums.GameTag.DAMAGE);
                    int healthDisplay = entity.GetTag(HearthDb.Enums.GameTag.HEALTH_DISPLAY);
                    int curHealth = healthDisplay > 0 ? healthDisplay : baseHealth - damage;
                    if (baseHealth > 0 || healthDisplay > 0) { p.Health = curHealth; }
                    int tier = entity.GetTag(HearthDb.Enums.GameTag.PLAYER_TECH_LEVEL);
                    if (tier > 0) { p.Tier = tier; }
                    p.Armor = entity.GetTag(HearthDb.Enums.GameTag.ARMOR);

                    int livePlace = entity.GetTag(HearthDb.Enums.GameTag.PLAYER_LEADERBOARD_PLACE);
                    if (livePlace > 0) { p.LivePlace = livePlace; }

                    dump += $"{p.Name ?? ("pid" + pid)}(e{entity.Id}): H{baseHealth} D{damage} A{p.Armor} P{livePlace} | ";

                    // PLAYER_LEADERBOARD_PLACE is the LIVE rail position (everyone reads 1
                    // at match start), so death is detected by health: it must hold for two
                    // consecutive sweeps from turn 2 on, and self-heals if contradicted.
                    bool deadReading = (baseHealth > 0 || healthDisplay > 0) && curHealth <= 0
                                       && Core.Game.GetTurnNumber() >= 2;
                    if (deadReading)
                    {
                        p.DeadSweeps++;
                        if (p.DeadSweeps >= 2 && p.FinalPlace == 0)
                        {
                            p.FinalPlace = livePlace > 0 ? livePlace : -1;
                            LensLog.Info($"standings: {p.Name ?? cardId} eliminated — place {livePlace}");
                        }
                    }
                    else
                    {
                        p.DeadSweeps = 0;
                        if (p.FinalPlace != 0)
                        {
                            LensLog.Info($"standings: {p.Name ?? cardId} un-eliminated — false positive cleared (health {curHealth})");
                            p.FinalPlace = 0;
                        }
                    }

                    // Tribe composition of the last board we fought (the same knowledge
                    // the native rail hover shows; exists only for opponents faced). A
                    // fresh snapshot turn is also proof of a completed combat vs this
                    // player, so it drives the encounter counter + last-fought marker.
                    if (!p.IsMe)
                    {
                        var snap = Core.Game.GetBattlegroundsBoardStateFor(entity.Id);
                        if (snap?.Entities != null && snap.Entities.Length > 0 && snap.Turn != p.CompTurn)
                        {
                            if (p.CompTurn > 0 || snap.Turn > 0) { p.Encounters++; }
                            p.CompTurn = snap.Turn;
                            foreach (var other in players) { other.PrevOpponent = false; }
                            p.PrevOpponent = true;
                            string comp = ComputeComp(snap.Entities);
                            if (comp != null)
                            {
                                p.Comp = comp;
                                LensLog.Debug($"comp: {p.Name ?? cardId} t{snap.Turn} (#{p.Encounters}): {comp}");
                            }
                        }
                    }
                }

                // "(next)" marker. NEXT_OPPONENT_PLAYER_ID lives on the LOCAL player
                // entity (HDT's TagChangeActions gates on PlayerEntity.Id) and names the
                // player we fight next; a dead player's id here means their ghost.
                // Marked per-team so duos flag the whole opposing pair.
                int nextPid = 0;
                try { nextPid = Core.Game.PlayerEntity?.GetTag(HearthDb.Enums.GameTag.NEXT_OPPONENT_PLAYER_ID) ?? 0; }
                catch { }
                byPid.TryGetValue(nextPid, out PlayerInfo next);
                if (nextPid != lastNextPid)
                {
                    lastNextPid = nextPid;
                    LensLog.Debug($"next opponent: pid{nextPid} ({next?.Name ?? next?.HeroCardId ?? "?"})");
                }
                int myTeam = players.FirstOrDefault(x => x.IsMe)?.Team ?? -1;
                bool nextValid = next != null && (myTeam < 0 || next.Team != myTeam);
                foreach (var pl in players) { pl.NextOpponent = nextValid && pl.Team == next.Team; }

                if (dump.Length > 0 && dump != lastStateDump)
                {
                    lastStateDump = dump;
                    LensLog.Debug($"state: {dump}");
                }
            }
            catch (Exception ex)
            {
                if (!statusErrorLogged)
                {
                    statusErrorLogged = true;
                    LensLog.Debug($"live status unavailable: {ex.GetType().Name} - {ex.Message}");
                }
            }
        }

        private void Render()
        {
            if (players == null) { return; }
            string region = GetRegionStr();
            bool duos = players.GroupBy(p => p.Team).Any(g => g.Count() > 1);
            var lines = new List<RankLine>();

            if (Settings.Instance.showSession)
            {
                string sess = Session.Summary();
                if (sess != null) { lines.Add(new RankLine(sess, dim: true)); }
            }

            // Lobby strength: average rating of rated players, and my delta to it — the
            // one-glance "how hard is this table" read. Rated = a real rating we know.
            if (Settings.Instance.showLobbyAvg)
            {
                var rated = new List<int>();
                int mine = 0;
                foreach (var p in players)
                {
                    if (p.Name == null) { continue; }
                    if (leaderboard.TryGet(p.Name, out int r, out _) && r > 0)
                    {
                        rated.Add(r);
                        if (p.IsMe) { mine = r; }
                    }
                }
                if (rated.Count >= 3)
                {
                    int avg = (int)Math.Round(rated.Average());
                    string head = $"Lobby avg {avg}";
                    if (mine > 0) { int d = mine - avg; head += $" · you {(d >= 0 ? "+" : "")}{d}"; }
                    lines.Add(new RankLine(head, dim: true));
                }
            }

            var teamGroups = players.GroupBy(p => p.Team);
            if (Settings.Instance.sortByPlace)
            {
                teamGroups = teamGroups.OrderBy(PlaceSortKey).ThenByDescending(TeamSortKey).ThenBy(g => g.Key);
            }
            else
            {
                teamGroups = Settings.Instance.bestFirst
                    ? teamGroups.OrderByDescending(TeamSortKey).ThenBy(g => g.Key)
                    : teamGroups.OrderBy(TeamSortKey).ThenBy(g => g.Key);
            }

            bool firstTeam = true;
            foreach (var team in teamGroups)
            {
                var teamLines = new List<RankLine>();
                foreach (var p in team)
                {
                    if (p.IsMe && !duos) { continue; } // solo: hide own row
                    if (p.Name == null) { continue; }  // unresolved: the footer counts them

                    bool markDead = Settings.Instance.showEliminations && p.FinalPlace != 0;
                    string marker = "";
                    if (Settings.Instance.showNextOpponent && p.NextOpponent) { marker = " (next)"; }
                    else if (Settings.Instance.showEncounters && p.PrevOpponent && p.FinalPlace == 0) { marker = " (last)"; }
                    string left = p.Name + (p.IsMe ? " (you)" : "") + marker;
                    if (Settings.Instance.showEncounters && !p.IsMe && p.Encounters > 0)
                    {
                        left += $" ×{p.Encounters}";
                    }
                    if (markDead && p.FinalPlace > 0) { left = $"({Ordinal(p.FinalPlace)}) {left}"; }
                    RankLine line = new RankLine(left, dead: markDead)
                    {
                        Right = RatingText(p.Name, region, out int rank),
                        RightDim = Settings.Instance.showRankNumbers && rank > 0 ? $"#{rank}" : null
                    };
                    if (p.FinalPlace == 0 && Settings.Instance.showHeroInfo && (p.HeroName != null || p.Tier > 0))
                    {
                        string sub = p.HeroName ?? "";
                        if (p.Tier > 0) { sub += (sub.Length > 0 ? " · " : "") + $"T{p.Tier}"; }
                        if (p.Health > 0) { sub += (sub.Length > 0 ? " · " : "") + $"{p.Health}♥" + (p.Armor > 0 ? $"+{p.Armor}" : ""); }
                        line.Sub = sub.Length > 0 ? sub : null;
                    }
                    // Recent form (community avg placement) as a dim right-column suffix,
                    // shown when we have history and it isn't crowded out by a rank number.
                    if (Settings.Instance.showForm && !p.IsMe && p.FinalPlace == 0)
                    {
                        string hash = p.NameHash ?? (p.Name != null ? MatchReporter.HashName(p.Name) : null);
                        if (hash != null && form.TryGet(hash, out FormStats.Entry fe))
                        {
                            string avg = $"avg {fe.AvgPlace:0.0}";
                            line.RightDim = line.RightDim == null ? avg : line.RightDim + " · " + avg;
                        }
                    }
                    if (p.FinalPlace == 0 && Settings.Instance.showComps && p.Comp != null)
                    {
                        line.Sub2 = $"{p.Comp} — t{p.CompTurn}";
                    }
                    teamLines.Add(line);
                }

                if (teamLines.Count == 0) { continue; } // fully-unresolved team: no dangling divider
                if (duos && !firstTeam) { lines.Add(new RankLine(null, divider: true)); }
                firstTeam = false;
                lines.AddRange(teamLines);
            }

            int unresolvedCount = players.Count(p => p.Name == null);
            if (unresolvedCount > 0)
            {
                lines.Add(new RankLine($"hover {unresolvedCount} more portrait{(unresolvedCount == 1 ? "" : "s")}", dim: true));
            }

            if (leaderboard.Failed && !leaderboard.Ready)
            {
                lines.Add(new RankLine("ratings unavailable — see log", dim: true));
            }

            if (Updater.Staged)
            {
                lines.Add(new RankLine($"v{Meta.LatestVersion} installed — restart HDT", dim: true));
            }
            else if (Meta.UpdateAvailable)
            {
                lines.Add(new RankLine($"v{Meta.LatestVersion} available — see Settings", dim: true));
            }

            string sig = string.Join("|", lines.Select(l => l.Text + "/" + l.Sub + "/" + l.Sub2 + "/" + l.Right + "/" + l.RightDim + (l.Dead ? "D" : "") + (l.Dim ? "~" : "") + (l.Divider ? "=" : "")))
                + $"#{Settings.Instance.showRankNumbers}{Settings.Instance.showHeroInfo}{Settings.Instance.showComps}{Settings.Instance.showEliminations}{Settings.Instance.bestFirst}{Settings.Instance.sortByPlace}{Settings.Instance.fontSize}{Settings.Instance.showLobbyAvg}{Settings.Instance.showEncounters}{Settings.Instance.showForm}";
            if (sig == lastRender) { return; }
            lastRender = sig;
            panel.DisplayLines(lines);
        }

        private static int PlaceSortKey(IGrouping<int, PlayerInfo> team)
        {
            var places = team.Where(p => p.LivePlace > 0).Select(p => p.LivePlace).ToList();
            return places.Count > 0 ? places.Min() : int.MaxValue;
        }

        private double TeamSortKey(IGrouping<int, PlayerInfo> team)
        {
            double best = -1;
            foreach (var p in team)
            {
                if (p.Name == null) { continue; }
                best = Math.Max(best, leaderboard.TryGet(p.Name, out int rating, out _) ? rating : 0);
            }
            return best;
        }

        private string RatingText(string name, string region, out int rank)
        {
            if (leaderboard.TryGet(name, out int rating, out rank) && rating > 0)
            {
                return rating.ToString();
            }
            rank = 0;
            // Only claim "below the 8000 cutoff" when we actually have a board to
            // check against; otherwise distinguish still-loading from failed.
            if (!leaderboard.Ready) { return leaderboard.Failed ? "-" : "…"; }
            return region == "CN" ? "-" : "8000↓";
        }

        private static readonly Dictionary<int, string> RaceNames = new Dictionary<int, string>
        {
            { 11, "Undead" }, { 14, "Murloc" }, { 15, "Demon" }, { 17, "Mech" },
            { 18, "Elemental" }, { 20, "Beast" }, { 23, "Pirate" }, { 24, "Dragon" },
            { 26, "All" }, { 43, "Quilboar" }, { 92, "Naga" }
        };

        private static void AddRace(Dictionary<string, int> counts, HearthDb.Enums.Race race)
        {
            if (race == HearthDb.Enums.Race.INVALID) { return; }
            string name = RaceNames.TryGetValue((int)race, out string n) ? n : race.ToString();
            counts[name] = counts.TryGetValue(name, out int c) ? c + 1 : 1;
        }

        private static string ComputeComp(Hearthstone_Deck_Tracker.Hearthstone.Entities.Entity[] minions)
        {
            var counts = new Dictionary<string, int>();
            int typeless = 0;
            foreach (var m in minions)
            {
                if (m?.CardId == null) { continue; }
                if (!HearthDb.Cards.All.TryGetValue(m.CardId, out var card) || card == null) { continue; }
                if (card.Race == HearthDb.Enums.Race.INVALID && card.SecondaryRace == HearthDb.Enums.Race.INVALID) { typeless++; continue; }
                AddRace(counts, card.Race);
                AddRace(counts, card.SecondaryRace);
            }
            if (counts.Count == 0 && typeless == 0) { return null; }
            var parts = counts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value} {kv.Key}").ToList();
            if (typeless > 0) { parts.Add($"{typeless} other"); }
            return string.Join(" · ", parts);
        }

        private static string HeroNameFromCardId(string cardId)
        {
            try
            {
                if (HearthDb.Cards.All.TryGetValue(cardId, out var card))
                {
                    return card?.GetLocName(HearthDb.Enums.Locale.enUS);
                }
            }
            catch { }
            return null;
        }

        private static string Ordinal(int n)
        {
            switch (n)
            {
                case 1: return "1st";
                case 2: return "2nd";
                case 3: return "3rd";
                default: return n + "th";
            }
        }

        // Formerly-silent waits report their reason, throttled to state changes.
        private void FailName(string reason)
        {
            if (reason != lastNameFail)
            {
                lastNameFail = reason;
                LensLog.Debug($"resolution waiting: {reason}");
            }
        }
    }
}
