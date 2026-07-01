using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LobbyLens
{
    // Remote plugin metadata, published alongside the leaderboards on the backend
    // blob. HDT has no plugin auto-update, so shipped binaries are frozen — this
    // lets every already-installed copy learn about new releases and show current
    // support links without shipping a new DLL. Absent file or empty fields mean
    // the corresponding UI stays hidden. Fetched once per HDT session.
    public static class Meta
    {
        private const string MetaUrl = "https://stdatayififhlgyqepq.blob.core.windows.net/public/meta.json";

        private static bool loading;

        public static string LatestVersion { get; private set; }
        public static string ReleaseUrl { get; private set; } = "https://github.com/xhodagx/LobbyLens/releases";
        public static string Kofi { get; private set; }
        public static string Lightning { get; private set; }
        public static string Btc { get; private set; }
        public static bool UpdateAvailable { get; private set; }

        public static async Task Load(HttpClient http)
        {
            if (loading) { return; }
            loading = true;
            try
            {
                string json = await http.GetStringAsync(MetaUrl);
                LatestVersion = Field(json, "latest");
                ReleaseUrl = Field(json, "url") ?? ReleaseUrl;
                Kofi = Field(json, "kofi");
                Lightning = Field(json, "lightning");
                Btc = Field(json, "btc");

                Version current = typeof(Meta).Assembly.GetName().Version;
                if (Version.TryParse(LatestVersion ?? "", out Version latest) && latest > current)
                {
                    UpdateAvailable = true;
                    LensLog.Info($"update available: v{LatestVersion} (installed v{current})");
                }
            }
            catch (Exception ex)
            {
                LensLog.Debug($"meta unavailable: {ex.Message}");
            }
        }

        private static string Field(string json, string name)
        {
            Match m = Regex.Match(json, "\"" + name + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            string v = m.Success ? m.Groups[1].Value.Trim() : null;
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
    }
}
