using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LobbyLens
{
    // Community form: recent average placement per player, served by the LobbyLens
    // backend from the same anonymized reports the plugin already contributes. We ask
    // by battletag HASH only (never the name), so this adds no new data exposure — it
    // reads back an aggregate of consented data. One batched request per lobby, cached.
    public class FormStats
    {
        // meta.json can repoint ingest; the form endpoint lives beside it. Derive from
        // the ingest host so a backend move carries it along, with a compiled default.
        private const string DefaultFormUrl = "https://func-lobbylens-yififhlgyqepq.azurewebsites.net/api/form";

        private static string FormUrl
        {
            get
            {
                string ingest = Meta.Ingest;
                if (!string.IsNullOrEmpty(ingest) && ingest.EndsWith("/match", StringComparison.OrdinalIgnoreCase))
                {
                    return ingest.Substring(0, ingest.Length - "/match".Length) + "/form";
                }
                return DefaultFormUrl;
            }
        }

        private readonly HttpClient _http;
        private Dictionary<string, Entry> _byHash;

        public struct Entry
        {
            public int Games;
            public double AvgPlace;
        }

        public FormStats(HttpClient http) { _http = http; }

        public bool TryGet(string nameHash, out Entry entry)
        {
            entry = default;
            var map = _byHash;
            return map != null && nameHash != null && map.TryGetValue(nameHash, out entry);
        }

        // Fetch form for a lobby's worth of hashes (id = name hash, aid = account hash).
        // Sends every non-null hash; the backend answers whichever it has data for.
        public async Task Load(IEnumerable<string> hashes)
        {
            if (!Settings.Instance.showForm) { return; }
            var ids = hashes.Where(h => !string.IsNullOrEmpty(h)).Distinct().Take(24).ToList();
            if (ids.Count == 0) { _byHash = null; return; }

            try
            {
                string url = FormUrl + "?ids=" + string.Join(",", ids);
                string body = await _http.GetStringAsync(url).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(body)) { return; }

                var map = new Dictionary<string, Entry>();
                // compact shape: {"<hash>":{"n":12,"a":3.87},...}
                foreach (Match m in RowRx.Matches(body))
                {
                    int n = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                    double a = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                    if (n > 0) { map[m.Groups[1].Value] = new Entry { Games = n, AvgPlace = a }; }
                }
                _byHash = map;
                LensLog.Debug($"form stats: {map.Count}/{ids.Count} players have recent history");
            }
            catch (Exception ex)
            {
                LensLog.Debug($"form stats unavailable: {ex.Message}");
            }
        }

        private static readonly Regex RowRx = new Regex(
            "\"([0-9a-f]{1,64})\":\\{\"n\":(\\d+),\"a\":([0-9.]+)\\}",
            RegexOptions.Compiled);
    }
}
