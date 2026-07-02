using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LobbyLens
{
    // Remote plugin metadata, published alongside the leaderboards on the backend
    // blob. HDT has no plugin auto-update, so shipped binaries are frozen — this
    // file is the channel that lets every installed copy learn about new releases,
    // stage them (see Updater), show current support links, and stand down cleanly
    // when a game patch breaks memory reads. Absent file or empty fields mean the
    // corresponding feature stays inert. Fetched once per HDT session.
    //
    // Compatibility contract: fields are ADDITIVE-ONLY. Shipped binaries regex-parse
    // this file and ignore unknown fields; never rename or repurpose an existing one.
    public static class Meta
    {
        private const string MetaUrl = "https://stdatayififhlgyqepq.blob.core.windows.net/public/meta.json";

        private static bool loading;
        private static readonly TaskCompletionSource<bool> loaded = new TaskCompletionSource<bool>();

        public static string LatestVersion { get; private set; }
        public static string ReleaseUrl { get; private set; } = "https://github.com/xhodagx/LobbyLens/releases";
        public static string Kofi { get; private set; }
        public static string Lightning { get; private set; }
        public static string Btc { get; private set; }
        public static bool UpdateAvailable { get; private set; }

        // Update package: zip whose RSA signature (over the raw zip bytes) must
        // verify against the key(s) baked into Updater before anything is staged.
        public static string PackageUrl { get; private set; }
        public static string PackageSig { get; private set; }

        // Kill switch: explicit message stops match processing (memory reads) until
        // an update fixes things; minVersion forces it for out-of-date binaries.
        public static string StandDown { get; private set; }
        public static string MinVersion { get; private set; }

        // Endpoint indirection: lets the ingest host move without a plugin release.
        public static string Ingest { get; private set; }

        // Completes when the fetch has finished (successfully or not).
        public static Task LoadCompletion => loaded.Task;

        public static string StandDownMessage
        {
            get
            {
                if (StandDown != null) { return StandDown; }
                if (Version.TryParse(MinVersion ?? "", out Version min)
                    && min > typeof(Meta).Assembly.GetName().Version)
                {
                    return "this version needs an update to keep working";
                }
                return null;
            }
        }

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
                PackageUrl = Field(json, "pkg");
                PackageSig = Field(json, "sig");
                StandDown = Field(json, "standDown");
                MinVersion = Field(json, "minVersion");
                Ingest = Field(json, "ingest");

                Version current = typeof(Meta).Assembly.GetName().Version;
                if (Version.TryParse(LatestVersion ?? "", out Version latest) && latest > current)
                {
                    UpdateAvailable = true;
                    LensLog.Info($"update available: v{LatestVersion} (installed v{current})");
                }
                if (StandDownMessage != null)
                {
                    LensLog.Info($"stand-down active: {StandDownMessage}");
                }
            }
            catch (Exception ex)
            {
                LensLog.Debug($"meta unavailable: {ex.Message}");
            }
            finally
            {
                loaded.TrySetResult(true);
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
