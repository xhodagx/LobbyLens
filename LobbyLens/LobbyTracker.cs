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
            public string Name;        // null until the portrait has been moused over
            public int Team;
            public string HeroCardId;
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
        }

        private bool isReset = true;
        private bool failShown = false;
        private bool namesDone = false;
        private int tileErrors = 0;
        private bool statusErrorLogged = false;
        private string lastNameFail = null;
        private string lastRender = null;
        private string lastStateDump = null;
        private DateTime lastStatusSweep = DateTime.MinValue;

        private List<PlayerInfo> players = null;
        private bool reported = false;

        private GameMemory memory;
        private HttpClient http;
        private Leaderboard leaderboard;
        private LobbyPanel panel;

        public LobbyTracker()
        {
            http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "LobbyLens/1.0 (HDT plugin)");
            http.Timeout = TimeSpan.FromSeconds(20);
            memory = new GameMemory();
            leaderboard = new Leaderboard(http);
            panel = new LobbyPanel();
        }

        public void Clean(bool save)
        {
            memory.Reset();
            http.Dispose();
            panel.Clean(save);
            http = null;
            memory = null;
            leaderboard = null;
            panel = null;
        }

        public void ResetLayout()
        {
            panel?.ResetLayout();
        }

        private void Reset()
        {
            failShown = false;
            namesDone = false;
            tileErrors = 0;
            statusErrorLogged = false;
            lastNameFail = null;
            lastRender = null;
            lastStateDump = null;
            lastStatusSweep = DateTime.MinValue;
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
                }

                if (Core.Game.GetTurnNumber() == 0) { return; }

                if (leaderboard.Failed && !leaderboard.Ready)
                {
                    if (!failShown)
                    {
                        failShown = true;
                        panel.DisplayLines(new List<RankLine>
                        {
                            new RankLine("No rating data"),
                            new RankLine("check log for details", dim: true)
                        });
                    }
                    return;
                }

                if (!leaderboard.Ready) { return; }

                if (!namesDone) { ResolveTiles(); }
                UpdateLiveStatus();
                Render();
            }
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
            try
            {
                string myName = memory.MyName;
                if (string.IsNullOrWhiteSpace(myName)) { FailName("own battletag not readable yet"); return; }

                var tiles = memory.ReadLeaderboardTiles();
                if (tiles.Count == 0) { FailName("no leaderboard tiles readable yet"); return; }

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

                // Carry live state across the rebuild.
                if (players != null)
                {
                    foreach (var oldP in players.Where(x => x.FinalPlace != 0 || x.HeroName != null || x.Tier > 0))
                    {
                        var match = tmp.FirstOrDefault(x =>
                            (oldP.Name != null && x.Name == oldP.Name) ||
                            (oldP.HeroCardId != null && x.HeroCardId == oldP.HeroCardId));
                        if (match != null)
                        {
                            match.FinalPlace = oldP.FinalPlace;
                            match.HeroName = oldP.HeroName;
                            match.Tier = oldP.Tier;
                            match.Health = oldP.Health;
                            match.Armor = oldP.Armor;
                            match.Comp = oldP.Comp;
                            match.CompTurn = oldP.CompTurn;
                            match.LivePlace = oldP.LivePlace;
                            match.DeadSweeps = oldP.DeadSweeps;
                        }
                    }
                }
                players = tmp;

                if (unresolved == 0)
                {
                    namesDone = true;
                    lastNameFail = null;
                    LensLog.Info($"all {players.Count} tiles resolved: [{string.Join(", ", players.Select(x => x.Name))}]");
                }
                else
                {
                    var got = players.Where(x => x.Name != null && !x.IsMe).Select(x => x.Name).ToList();
                    FailName($"{unresolved} portrait(s) not yet hovered; resolved: [{string.Join(", ", got)}]");
                }
            }
            catch (Exception ex)
            {
                tileErrors++;
                if (tileErrors < 5) { LensLog.Error("failed to read leaderboard tiles", ex); }
                else if (tileErrors == 5) { LensLog.Error("failed to read leaderboard tiles; suppressing further errors", ex); }
            }
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
                foreach (var entity in canonical)
                {
                    int pid = entity.GetTag(HearthDb.Enums.GameTag.PLAYER_ID);
                    string cardId = entity.CardId;
                    if (string.IsNullOrWhiteSpace(cardId)) { continue; }

                    PlayerInfo p = players.FirstOrDefault(x => x.HeroCardId != null &&
                        (cardId.Equals(x.HeroCardId, StringComparison.OrdinalIgnoreCase) ||
                         cardId.StartsWith(x.HeroCardId, StringComparison.OrdinalIgnoreCase) ||
                         x.HeroCardId.StartsWith(cardId, StringComparison.OrdinalIgnoreCase)));
                    if (p == null) { continue; }

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
                    // the native rail hover shows; exists only for opponents faced).
                    if (!p.IsMe && p.FinalPlace == 0)
                    {
                        var snap = Core.Game.GetBattlegroundsBoardStateFor(entity.Id);
                        if (snap?.Entities != null && snap.Entities.Length > 0 && snap.Turn != p.CompTurn)
                        {
                            string comp = ComputeComp(snap.Entities);
                            if (comp != null)
                            {
                                p.Comp = comp;
                                p.CompTurn = snap.Turn;
                                LensLog.Debug($"comp: {p.Name ?? cardId} t{snap.Turn}: {comp}");
                            }
                        }
                    }
                }

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
                if (duos && !firstTeam) { lines.Add(new RankLine(null, divider: true)); }
                firstTeam = false;

                foreach (var p in team)
                {
                    if (p.IsMe && !duos) { continue; } // solo: hide own row

                    if (p.Name == null)
                    {
                        lines.Add(new RankLine("hover a portrait…", dim: true));
                        continue;
                    }

                    bool markDead = Settings.Instance.showEliminations && p.FinalPlace != 0;
                    string left = p.Name + (p.IsMe ? " (you)" : "");
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
                    if (p.FinalPlace == 0 && Settings.Instance.showComps && p.Comp != null)
                    {
                        line.Sub2 = $"{p.Comp} — t{p.CompTurn}";
                    }
                    lines.Add(line);
                }
            }

            int unresolvedCount = players.Count(p => p.Name == null);
            if (unresolvedCount > 0)
            {
                lines.Add(new RankLine($"hover {unresolvedCount} more portrait{(unresolvedCount == 1 ? "" : "s")}", dim: true));
            }

            string sig = string.Join("|", lines.Select(l => l.Text + "/" + l.Sub + "/" + l.Sub2 + "/" + l.Right + "/" + l.RightDim + (l.Dead ? "D" : "") + (l.Dim ? "~" : "") + (l.Divider ? "=" : "")))
                + $"#{Settings.Instance.showRankNumbers}{Settings.Instance.showHeroInfo}{Settings.Instance.showComps}{Settings.Instance.showEliminations}{Settings.Instance.bestFirst}{Settings.Instance.sortByPlace}";
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
