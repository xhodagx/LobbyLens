using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace LobbyLens
{
    // Self-update. Downloads the release zip named by meta.json, verifies its RSA
    // signature against the keys baked in below, and stages every zip entry next to
    // the running DLL. HDT locks loaded plugin DLLs, but Windows allows renaming
    // them — so the live file is renamed to *.old and the new one takes its place;
    // the update applies on the next HDT restart, and *.old is swept then.
    //
    // Trust model: the blob is a dumb pipe. Only packages signed by the offline
    // release key install; a compromised blob can at worst block updates. The key
    // LIST allows rotation: a transition release ships trusting old + new keys.
    public static class Updater
    {
        private static readonly string[] PublicKeys =
        {
            // key1 (2026-07)
            "<RSAKeyValue><Modulus>qhUI1nAAtQKeBNOMitBlaL80X8GiNSc7uOu/CGxV5EAXXIl7CiSocq2RDSgLjjnJsRoHbR/uNaeV2259sAT8i/L7Re9twZ1CbSICQPveTluu4o9V5waVGpRGsqvNDNot+PckzVs5dzMZc9ojwKiZL6x6lFAOZF6Vs9y3hH2V0GC7bPYy0UeZ9dLJbgzJnnT0ZVF++pnbnaXFVb+7qpaB89lNkO1tVexQ7nPN0/s5aUHPNyUoNfTOuYh8mCsTfxd4ePQJO8MPoaWhCUD6w92oYDwD0gcXYZ7+5dlAAvxdqskvrVWcrP4pKQKGGa1+KzqPENqXXL8waUPVoSQ59TzfgQ==</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>",
        };

        private const int MaxPackageBytes = 20 * 1024 * 1024;

        public static bool Staged { get; private set; }

        // Best-effort sweep of *.old files left by a previous staging (they are
        // unlocked once HDT restarts on the new DLL). Call at plugin load.
        public static void CleanupStaleFiles()
        {
            foreach (string dir in TargetDirs)
            {
                try
                {
                    foreach (string f in Directory.GetFiles(dir, "*.old"))
                    {
                        try { File.Delete(f); } catch { /* still loaded — next time */ }
                    }
                }
                catch { }
            }
        }

        public static async Task Run()
        {
            try
            {
                await Meta.LoadCompletion.ConfigureAwait(false);
                if (!Settings.Instance.autoUpdate || Staged || !Meta.UpdateAvailable) { return; }
                if (Meta.PackageUrl == null || Meta.PackageSig == null) { return; }

                byte[] zip;
                using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2), MaxResponseContentBufferSize = MaxPackageBytes })
                {
                    zip = await http.GetByteArrayAsync(Meta.PackageUrl).ConfigureAwait(false);
                }

                if (!VerifySignature(zip, Meta.PackageSig))
                {
                    LensLog.Error($"update package signature INVALID — not installing (v{Meta.LatestVersion} from {Meta.PackageUrl})");
                    return;
                }

                int files = Stage(zip);
                Staged = files > 0;
                if (Staged)
                {
                    LensLog.Info($"update v{Meta.LatestVersion} staged ({files} file(s)) — applies on next HDT restart");
                }
            }
            catch (Exception ex)
            {
                LensLog.Debug($"self-update skipped: {ex.GetType().Name} - {ex.Message}");
            }
        }

        private static bool VerifySignature(byte[] data, string sigBase64)
        {
            byte[] sig;
            try { sig = Convert.FromBase64String(sigBase64); }
            catch { return false; }
            foreach (string keyXml in PublicKeys)
            {
                try
                {
                    using RSA rsa = RSA.Create();
                    rsa.FromXmlString(keyXml);
                    if (rsa.VerifyData(data, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) { return true; }
                }
                catch { }
            }
            return false;
        }

        private static int Stage(byte[] zipBytes)
        {
            int staged = 0;
            using var archive = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
            string[] dirs = TargetDirs;

            // zip-slip guard: validate EVERY entry against every target dir before
            // writing anything, so a rejected package can never leave partial staging.
            foreach (string dir in dirs)
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (entry.Name.Length == 0) { continue; } // directory entry
                    string target = Path.GetFullPath(Path.Combine(dir, entry.FullName));
                    if (!target.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        LensLog.Error($"update package entry escapes plugin dir, rejected: {entry.FullName}");
                        return 0;
                    }
                }
            }

            foreach (string dir in dirs)
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (entry.Name.Length == 0) { continue; }
                    string target = Path.GetFullPath(Path.Combine(dir, entry.FullName));

                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    string tmp = target + ".new";
                    using (var outStream = File.Create(tmp))
                    using (var inStream = entry.Open())
                    {
                        inStream.CopyTo(outStream);
                    }

                    if (File.Exists(target))
                    {
                        try { File.Delete(target); }
                        catch // locked — the currently loaded DLL; rename it aside
                        {
                            string old = target + ".old";
                            try { File.Delete(old); } catch { }
                            File.Move(target, old);
                        }
                    }
                    File.Move(tmp, target);
                    staged++;
                }
            }
            return staged;
        }

        // HDT's canonical plugin install is Roaming\HDT\Plugins, but at startup it
        // copies that into app-<version>\Plugins and LOADS THE COPY — so the
        // executing directory is the synced one. Staging must hit BOTH: the Roaming
        // canonical (survives HDT self-updates and re-syncs) and the executing copy
        // (so a same-version resync can't resurrect the old build).
        private static string[] TargetDirs
        {
            get
            {
                string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var dirs = new List<string> { exeDir };
                const string marker = @"\Plugins";
                int i = exeDir.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
                // must be a whole path segment ("...\Plugins" or "...\Plugins\..."),
                // not a prefix of e.g. "...\PluginsBackup"
                if (i >= 0 && (i + marker.Length == exeDir.Length || exeDir[i + marker.Length] == '\\'))
                {
                    string rel = exeDir.Substring(i + marker.Length).TrimStart('\\');
                    string roaming = Path.Combine(Hearthstone_Deck_Tracker.Config.AppDataPath, "Plugins", rel)
                        .TrimEnd('\\');
                    if (!dirs.Contains(roaming, StringComparer.OrdinalIgnoreCase)) { dirs.Add(roaming); }
                }
                return dirs.ToArray();
            }
        }
    }
}
