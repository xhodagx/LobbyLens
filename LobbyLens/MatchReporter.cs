using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LobbyLens
{
    public struct PlayerSummary
    {
        public string HeroCardId;
        public int FinalPlace;
        public int Tier;
        public int Rating;
        public int Rank;
        public string Comp;
        public string NameHash;
        public bool IsMe;
    }

    // Sends an anonymized match summary to the LobbyLens backend for aggregate stats
    // (hero/comp winrates, lobby difficulty, etc). Opt-out via Settings.reportMatches.
    // Battletags are one-way hashed — raw identities are never transmitted or stored.
    public static class MatchReporter
    {
        private const string IngestUrl = "https://func-lobbylens-yififhlgyqepq.azurewebsites.net/api/match";

        // Stable per-player pseudonym: lets future features count repeat encounters
        // without the backend ever holding a real battletag.
        public static string HashName(string name)
        {
            if (string.IsNullOrEmpty(name)) { return ""; }
            using var sha = SHA256.Create();
            byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes("lobbylens:" + name));
            var sb = new StringBuilder(16);
            for (int i = 0; i < 8; i++) { sb.Append(h[i].ToString("x2")); }
            return sb.ToString();
        }

        public static async Task PostAsync(HttpClient http, string region, bool duos, List<PlayerSummary> players)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("{\"schema\":1,\"region\":\"").Append(region).Append("\",\"duos\":").Append(duos ? "true" : "false").Append(",\"players\":[");
                for (int i = 0; i < players.Count; i++)
                {
                    PlayerSummary p = players[i];
                    if (i > 0) { sb.Append(','); }
                    sb.Append("{\"h\":").Append(JsonStr(p.HeroCardId))
                      .Append(",\"p\":").Append(p.FinalPlace)
                      .Append(",\"t\":").Append(p.Tier)
                      .Append(",\"r\":").Append(p.Rating)
                      .Append(",\"k\":").Append(p.Rank)
                      .Append(",\"c\":").Append(JsonStr(p.Comp))
                      .Append(",\"id\":").Append(JsonStr(p.NameHash))
                      .Append(",\"me\":").Append(p.IsMe ? "true" : "false").Append('}');
                }
                sb.Append("]}");

                using var content = new StringContent(sb.ToString(), Encoding.UTF8, "application/json");
                using HttpResponseMessage resp = await http.PostAsync(IngestUrl, content);
                LensLog.Debug($"match report -> {(int)resp.StatusCode}");
            }
            catch (Exception ex)
            {
                LensLog.Debug($"match report failed: {ex.Message}");
            }
        }

        private static string JsonStr(string s)
        {
            if (s == null) { return "null"; }
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) { sb.Append("\\u").Append(((int)c).ToString("x4")); }
                        else { sb.Append(c); }
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
