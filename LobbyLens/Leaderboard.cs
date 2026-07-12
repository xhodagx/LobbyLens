using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LobbyLens
{
    // Battlegrounds ladder ratings straight from Blizzard's official community
    // leaderboard API, with a gentle parallel fetch and an on-disk cache so a
    // typical match start costs zero network requests.
    public class Leaderboard
    {
        private const string BaseUrl = "https://hearthstone.blizzard.com/en-us/api/community/leaderboardsData";
        private const int MaxPages = 200;          // safety cap; boards run ~25-60 pages
        private const int Concurrency = 4;         // be polite to Blizzard
        private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

        // LobbyLens backend: one pre-aggregated, CDN-cached file per region/mode — the
        // primary source. Direct Blizzard (BaseUrl above) is the fallback if it's down.
        private const string LensBaseUrl = "https://stdatayififhlgyqepq.blob.core.windows.net/public/";

        private readonly HttpClient _http;
        private Dictionary<string, Entry> _byName;

        public bool Ready { get; private set; }
        public bool Failed { get; private set; }

        private struct Entry
        {
            public int Rating;
            public int Rank;
        }

        public Leaderboard(HttpClient http)
        {
            _http = http;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        public bool TryGet(string name, out int rating, out int rank)
        {
            rating = 0;
            rank = 0;
            var map = _byName;
            if (map != null && name != null && map.TryGetValue(name, out Entry e))
            {
                rating = e.Rating;
                rank = e.Rank;
                return true;
            }
            return false;
        }

        public async Task Load(string region, bool duos)
        {
            Ready = false;
            Failed = false;
            _byName = null;

            if (region == "CN")
            {
                LensLog.Warn("CN region is not served by the official leaderboard API");
                Failed = true;
                return;
            }

            string cachePath = CachePath(region, duos);
            var cached = ReadCache(cachePath, out DateTime fetchedUtc);
            if (cached != null && cached.Count > 0)
            {
                _byName = cached;
                Ready = true;
                if (DateTime.UtcNow - fetchedUtc < CacheTtl)
                {
                    LensLog.Info($"leaderboard from cache: {cached.Count} players ({region}{(duos ? " duos" : "")}, {(DateTime.UtcNow - fetchedUtc).TotalMinutes:F0}m old)");
                    return;
                }
                LensLog.Info("leaderboard cache is stale — refreshing in background");
            }

            // Primary source: the LobbyLens backend (one small cached file).
            try
            {
                var lens = await FetchFromLens(region, duos);
                if (lens != null && lens.Count > 0)
                {
                    _byName = lens;
                    Ready = true;
                    WriteCache(cachePath, lens);
                    LensLog.Info($"leaderboard from LobbyLens backend: {lens.Count} players ({region}{(duos ? " duos" : "")})");
                    return;
                }
            }
            catch (Exception ex)
            {
                LensLog.Info($"LobbyLens backend unreachable, falling back to Blizzard direct: {ex.Message}");
            }

            // Fallback: fetch directly from Blizzard's API (paged).
            try
            {
                var fresh = await FetchAll(region, duos);
                if (fresh.Count > 0)
                {
                    _byName = fresh;
                    Ready = true;
                    WriteCache(cachePath, fresh);
                    LensLog.Info($"leaderboard fetched direct from Blizzard: {fresh.Count} players ({region}{(duos ? " duos" : "")})");
                }
                else if (!Ready)
                {
                    Failed = true;
                    LensLog.Warn("leaderboard fetch returned no rows and no cache exists");
                }
            }
            catch (Exception ex)
            {
                LensLog.Error("leaderboard fetch failed (backend + Blizzard both)", ex);
                if (!Ready) { Failed = true; }
            }
        }

        // Fetch the pre-aggregated file from the LobbyLens backend and parse the compact
        // {"ts":<unix>,"players":[{"n":name,"r":rating,"k":rank},...]} shape it publishes.
        private async Task<Dictionary<string, Entry>> FetchFromLens(string region, bool duos)
        {
            string url = $"{LensBaseUrl}leaderboard_{region}{(duos ? "_duo" : "")}.json";
            string body = await _http.GetStringAsync(url);
            if (string.IsNullOrWhiteSpace(body)) { return null; }

            Match ts = LensTsRx.Match(body);
            if (ts.Success)
            {
                var fetched = DateTimeOffset.FromUnixTimeSeconds(long.Parse(ts.Groups[1].Value, CultureInfo.InvariantCulture));
                TimeSpan age = DateTimeOffset.UtcNow - fetched;
                if (age > MaxLensAge)
                {
                    LensLog.Warn($"backend leaderboard is {age.TotalHours:F0}h stale — treating as unavailable, using Blizzard direct");
                    return null;
                }
            }

            var result = new Dictionary<string, Entry>();
            foreach (Match m in LensRowRx.Matches(body))
            {
                string name = Unescape(m.Groups[1].Value);
                int rating = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                int rank = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(name) || rating <= 0) { continue; }
                if (!result.ContainsKey(name)) { result.Add(name, new Entry { Rating = rating, Rank = rank }); }
            }
            return result;
        }

        // Deliberately dependency-free parsing: rows are flat objects
        // ({"rank":1,"accountid":"name","rating":12345}), so a compiled regex
        // avoids carrying a JSON library reference in a plugin that must
        // survive host updates.
        private static readonly Regex RowRx = new Regex(
            "\\{\"rank\":(\\d+),\"accountid\":\"((?:[^\"\\\\]|\\\\.)*)\",\"rating\":(\\d+)",
            RegexOptions.Compiled);
        private static readonly Regex TotalPagesRx = new Regex("\"totalPages\":(\\d+)", RegexOptions.Compiled);

        // LobbyLens backend row shape: {"n":name,"r":rating,"k":rank}
        private static readonly Regex LensRowRx = new Regex(
            "\"n\":\"((?:[^\"\\\\]|\\\\.)*)\",\"r\":(\\d+),\"k\":(\\d+)",
            RegexOptions.Compiled);
        private static readonly Regex LensTsRx = new Regex("\"ts\":(\\d+)", RegexOptions.Compiled);

        // The blob keeps returning 200 even if the backend's refresh timer dies, so
        // "unreachable" alone can't trigger the Blizzard fallback — staleness must too.
        private static readonly TimeSpan MaxLensAge = TimeSpan.FromHours(24);

        private async Task<Dictionary<string, Entry>> FetchAll(string region, bool duos)
        {
            string first = await FetchPage(region, duos, 1);
            var pages = new List<string> { first };

            int totalPages = 1;
            Match tp = TotalPagesRx.Match(first ?? string.Empty);
            if (tp.Success) { totalPages = int.Parse(tp.Groups[1].Value, CultureInfo.InvariantCulture); }
            totalPages = Math.Min(totalPages, MaxPages);

            if (totalPages > 1)
            {
                var gate = new SemaphoreSlim(Concurrency);
                var tasks = new List<Task<string>>();
                for (int p = 2; p <= totalPages; p++)
                {
                    int page = p;
                    tasks.Add(Task.Run(async () =>
                    {
                        await gate.WaitAsync();
                        try { return await FetchPage(region, duos, page); }
                        finally { gate.Release(); }
                    }));
                }
                pages.AddRange(await Task.WhenAll(tasks));
            }

            var result = new Dictionary<string, Entry>();
            foreach (string body in pages)
            {
                if (body == null) { continue; }
                foreach (Match m in RowRx.Matches(body))
                {
                    string name = Unescape(m.Groups[2].Value);
                    int rank = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    int rating = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                    if (string.IsNullOrWhiteSpace(name) || rating <= 0) { continue; }
                    if (!result.ContainsKey(name)) { result.Add(name, new Entry { Rating = rating, Rank = rank }); }
                }
            }
            return result;
        }

        // Minimal JSON string unescape (battletags can carry \uXXXX sequences).
        private static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) { return s; }
            try { return Regex.Unescape(s); }
            catch { return s; }
        }

        private async Task<string> FetchPage(string region, bool duos, int page)
        {
            string board = duos ? "battlegroundsduo" : "battlegrounds";
            string url = $"{BaseUrl}?region={region}&leaderboardId={board}&page={page}";
            return await _http.GetStringAsync(url);
        }

        private static string CachePath(string region, bool duos)
        {
            return Path.Combine(Settings.DataDir, $"leaderboard_{region}{(duos ? "_duo" : "")}.tsv");
        }

        // Cache format: first line = UTC ticks of the fetch; then "rank \t name \t rating".
        private static Dictionary<string, Entry> ReadCache(string path, out DateTime fetchedUtc)
        {
            fetchedUtc = DateTime.MinValue;
            try
            {
                if (!File.Exists(path)) { return null; }
                string[] lines = File.ReadAllLines(path);
                if (lines.Length < 2 || !long.TryParse(lines[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks)) { return null; }
                fetchedUtc = new DateTime(ticks, DateTimeKind.Utc);
                var result = new Dictionary<string, Entry>();
                for (int i = 1; i < lines.Length; i++)
                {
                    string[] parts = lines[i].Split('\t');
                    if (parts.Length != 3) { continue; }
                    if (!int.TryParse(parts[0], out int rank) || !int.TryParse(parts[2], out int rating)) { continue; }
                    if (!result.ContainsKey(parts[1])) { result.Add(parts[1], new Entry { Rating = rating, Rank = rank }); }
                }
                return result;
            }
            catch (Exception ex)
            {
                LensLog.Error("failed to read leaderboard cache", ex);
                return null;
            }
        }

        private static void WriteCache(string path, Dictionary<string, Entry> data)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using var writer = new StreamWriter(path);
                writer.WriteLine(DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
                foreach (var kv in data.OrderBy(kv => kv.Value.Rank))
                {
                    writer.WriteLine($"{kv.Value.Rank}\t{kv.Key}\t{kv.Value.Rating}");
                }
            }
            catch (Exception ex)
            {
                LensLog.Error("failed to write leaderboard cache", ex);
            }
        }
    }
}
