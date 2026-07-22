using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using Microsoft.Win32;
using ScryDotNet;

namespace LobbyLens
{
    // Reads the game client's UI memory through HDT's bundled Scry library:
    // the local player's battletag, the Battlegrounds leaderboard rail tiles, and
    // the GameState lobby roster.
    //
    // Field paths verified against the live client (2026-06, Unity 2022.3.62):
    //   tile name:    m_overlay.m_heroActor.m_playerNameText.m_Text  (hover-populated)
    //   hero card id: m_overlay.m_heroActor.m_entity.m_cardIdInternal
    //   lobby roster: GameState.s_instance.m_playerInfoMap.valueSlots[i]
    //                 .m_name / .m_playerHero.m_cardIdInternal / .m_gameAccountId.EntityId
    //                 (path mirrors HearthMirror's GetBattlegroundsLobbyInfo)
    // Reads of nonexistent fields throw native SEHExceptions — every read is guarded.
    public class GameMemory
    {
        // Last known-good Unity version, used only if reading the real one fails.
        private const string FallbackUnityVersion = "2022.3.62.7762112"; // 2022.3.62f2, current client as of 2026-07

        private static string _detectedUnity;
        private MonoImage _image;
        private string _myName;
        private string _lastLog;

        public struct Tile
        {
            public int Team;
            public dynamic Handle;
        }

        public struct LobbyPlayer
        {
            public string Name;         // discriminator (#1234) stripped, may be empty
            public string HeroCardId;   // join key against the rail tiles
            public string AccountId;    // "hi:lo" — stable, name-change-proof identity
        }

        // Reflected access to HDT's bundled HearthMirror, resolved once. This is the
        // maintained, patch-tracked path HDT itself uses for the lobby roster; we call
        // it by reflection so LobbyLens keeps its single-DLL, no-extra-reference design
        // and never fails to load if a host update reshapes the assembly. Our own Scry
        // walk (ReadLobbyInfoScry) stays as the fallback when this returns nothing.
        private static bool _mirrorProbed;
        private static System.Reflection.PropertyInfo _mirrorClient;
        private static System.Reflection.MethodInfo _mirrorGetLobby;

        private static void ProbeMirror()
        {
            _mirrorProbed = true;
            try
            {
                Type refl = Type.GetType("HearthMirror.Reflection, HearthMirror");
                _mirrorClient = refl?.GetProperty("Client",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                Type iface = _mirrorClient?.PropertyType;
                _mirrorGetLobby = iface?.GetMethod("GetBattlegroundsLobbyInfo", Type.EmptyTypes);
            }
            catch { }
        }

        public void Reset()
        {
            _image?.Dispose();
            _image = null;
            _myName = null;
            _lastLog = null;
        }

        public string MyName
        {
            get
            {
                if (_myName != null) { return _myName; }
                dynamic presence = Image?["BnetPresenceMgr"]?["s_instance"];
                if (presence == null)
                {
                    LogOnChange("[mem] BnetPresenceMgr.s_instance => null");
                    return null;
                }
                string name = presence?["m_myPlayer"]?["m_account"]?["m_battleTag"]?["m_name"];
                // cache only a real value — a transient empty read must not stick for the match
                if (!string.IsNullOrWhiteSpace(name)) { _myName = name; }
                LogOnChange($"[mem] own battletag => '{name ?? "null"}'");
                return _myName;
            }
        }

        // All leaderboard rail tiles in display order, tagged with their team index.
        public List<Tile> ReadLeaderboardTiles()
        {
            var result = new List<Tile>();
            dynamic mgr = Image?["PlayerLeaderboardManager"]?["s_instance"];
            if (mgr == null) { return result; }
            dynamic teams = mgr["m_teams"]?["_items"];
            if (teams == null) { return result; }

            for (uint t = 0; t < teams.size(); t++)
            {
                dynamic team = teams[t];
                dynamic cards = team?["m_playerLeaderboardCards"]?["_items"];
                if (cards == null) { continue; }
                for (uint c = 0; c < cards.size(); c++)
                {
                    dynamic card = cards[c];
                    if (card != null) { result.Add(new Tile { Team = (int)t, Handle = card }); }
                }
            }
            return result;
        }

        // The GameState lobby roster as a data map (not the visual rail). Unlike the
        // hover-gated tile name, this map may hold every player's name + hero + account
        // id from match start — the caller uses it to fill names WITHOUT the hover
        // ritual, falling back to the tiles when it's empty or partial. Best-effort:
        // any miss yields an empty list, never throws into the update loop.
        public List<LobbyPlayer> ReadLobbyInfo()
        {
            // Prefer HDT's maintained HearthMirror roster; fall back to our own Scry
            // walk only when it yields nothing (e.g. an older HDT without the API).
            var mirror = ReadLobbyInfoMirror();
            if (mirror.Count > 0) { return mirror; }
            return ReadLobbyInfoScry();
        }

        // HDT-native roster via reflected HearthMirror.Reflection.Client.GetBattlegroundsLobbyInfo().
        private List<LobbyPlayer> ReadLobbyInfoMirror()
        {
            var result = new List<LobbyPlayer>();
            try
            {
                if (!_mirrorProbed) { ProbeMirror(); }
                if (_mirrorClient == null || _mirrorGetLobby == null) { return result; }

                object client = _mirrorClient.GetValue(null);
                if (client == null) { return result; }
                dynamic info = _mirrorGetLobby.Invoke(client, null);
                var players = info?.Players;
                if (players == null) { return result; }

                foreach (var p in players)
                {
                    string name = p?.Name;
                    if (string.IsNullOrWhiteSpace(name)) { continue; }
                    int hash = name.IndexOf('#');
                    if (hash > 0) { name = name.Substring(0, hash); }

                    string hero = p?.HeroCardId;
                    string acct = null;
                    var aid = p?.AccountId;
                    if (aid != null)
                    {
                        try { acct = aid.Hi.ToString() + ":" + aid.Lo.ToString(); } catch { }
                    }

                    result.Add(new LobbyPlayer
                    {
                        Name = name,
                        HeroCardId = string.IsNullOrWhiteSpace(hero) ? null : hero,
                        AccountId = string.IsNullOrWhiteSpace(acct) ? null : acct
                    });
                }
                if (result.Count > 0) { LogOnChange($"[mem] lobby roster via HearthMirror ({result.Count})"); }
            }
            catch (Exception ex)
            {
                LogOnChange($"[mem] HearthMirror lobby read failed: {ex.GetType().Name}");
            }
            return result;
        }

        // Fallback: direct Scry walk of GameState.m_playerInfoMap (our original path).
        private List<LobbyPlayer> ReadLobbyInfoScry()
        {
            var result = new List<LobbyPlayer>();
            try
            {
                dynamic gs = Image?["GameState"]?["s_instance"];
                dynamic slots = gs?["m_playerInfoMap"]?["valueSlots"];
                if (slots == null) { return result; }

                for (uint i = 0; i < slots.size(); i++)
                {
                    dynamic p = slots[i];
                    if (p == null) { continue; }

                    string name = null;
                    try { name = p["m_name"]; } catch { }
                    if (string.IsNullOrWhiteSpace(name)) { continue; }
                    int hash = name.IndexOf('#');
                    if (hash > 0) { name = name.Substring(0, hash); }

                    string hero = null;
                    try { hero = p["m_playerHero"]?["m_cardIdInternal"]; } catch { }
                    if (string.IsNullOrWhiteSpace(hero))
                    {
                        try { hero = p["m_playerHero"]?["m_staticEntityDef"]?["m_cardIdInternal"]; } catch { }
                    }

                    string acct = null;
                    try
                    {
                        dynamic eid = p["m_gameAccountId"]?["EntityId"];
                        if (eid != null)
                        {
                            object hi = eid["high_"];
                            object lo = eid["low_"];
                            if (hi != null && lo != null) { acct = hi.ToString() + ":" + lo.ToString(); }
                        }
                    }
                    catch { }

                    result.Add(new LobbyPlayer
                    {
                        Name = name,
                        HeroCardId = string.IsNullOrWhiteSpace(hero) ? null : hero,
                        AccountId = acct
                    });
                }
            }
            catch (Exception ex)
            {
                LogOnChange($"[mem] lobby roster read failed: {ex.GetType().Name}");
            }
            return result;
        }

        // Hover-populated; null until the user has moused over the tile.
        public static string TileName(dynamic tile)
        {
            try
            {
                string name = tile?["m_overlay"]?["m_heroActor"]?["m_playerNameText"]?["m_Text"];
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
            catch { return null; }
        }

        public static string TileHeroCardId(dynamic tile)
        {
            try
            {
                string id = tile?["m_overlay"]?["m_heroActor"]?["m_entity"]?["m_cardIdInternal"];
                return string.IsNullOrWhiteSpace(id) ? null : id;
            }
            catch { return null; }
        }

        private MonoImage Image
        {
            get
            {
                if (_image != null) { return _image; }

                Process[] procs = Process.GetProcessesByName("Hearthstone");
                try
                {
                    Process proc = procs.FirstOrDefault();
                    if (proc == null)
                    {
                        LogOnChange("[mem] no Hearthstone process");
                        return null;
                    }

                    string unity = DetectUnityVersion(proc);
                    using MonoScry scry = new MonoScry(Scry.connect(proc.Id));

                    if (unity != null)
                    {
                        _image = scry.getImage(new List<string> { "Blizzard.T5.ServiceLocator" }, unity);
                        LogOnChange($"[mem] attach pid {proc.Id}, unity {unity} (detected) => {(_image == null ? "null" : "ok")}");
                        if (_image != null) { return _image; }
                    }

                    _image = scry.getImage(new List<string> { "Blizzard.T5.ServiceLocator" }, FallbackUnityVersion);
                    LogOnChange($"[mem] attach pid {proc.Id}, unity {FallbackUnityVersion} (fallback) => {(_image == null ? "null — plugin needs an update" : "ok")}");
                    // Total failure: the game may have patched (new Unity) since we cached
                    // the detected version — forget it so the next attempt re-detects.
                    if (_image == null) { _detectedUnity = null; }
                    return _image;
                }
                finally
                {
                    foreach (Process p in procs) { p.Dispose(); }
                }
            }
        }

        // The game's real engine version comes from UnityPlayer.dll metadata, so
        // engine upgrades in game patches don't require a plugin update.
        private static string DetectUnityVersion(Process proc)
        {
            if (_detectedUnity != null) { return _detectedUnity; }
            foreach (string dir in CandidateGameDirs(proc))
            {
                try
                {
                    string dll = Path.Combine(dir, "UnityPlayer.dll");
                    if (!File.Exists(dll)) { continue; }
                    string v = FileVersionInfo.GetVersionInfo(dll).FileVersion;
                    if (!string.IsNullOrWhiteSpace(v))
                    {
                        _detectedUnity = v;
                        break;
                    }
                }
                catch { }
            }
            return _detectedUnity;
        }

        private static IEnumerable<string> CandidateGameDirs(Process proc)
        {
            // HDT is 32-bit and the game is 64-bit, so Process.MainModule throws
            // cross-bitness — QueryFullProcessImageName is the bitness-proof read.
            string fromProcess = null;
            try
            {
                string exe = ProcessImagePath(proc.Id) ?? proc.MainModule.FileName;
                fromProcess = Path.GetDirectoryName(exe);
            }
            catch { }
            if (fromProcess != null) { yield return fromProcess; }

            // Custom install locations (other drive, moved Battle.net library).
            string fromRegistry = RegistryInstallDir();
            if (fromRegistry != null) { yield return fromRegistry; }

            yield return @"C:\Program Files (x86)\Hearthstone";
        }

        // Battle.net's installer is 32-bit, so its uninstall entry lives in the
        // 32-bit registry view regardless of who is asking.
        private static string RegistryInstallDir()
        {
            try
            {
                using RegistryKey hklm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
                using RegistryKey key = hklm32.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Hearthstone");
                string dir = key?.GetValue("InstallLocation") as string;
                return string.IsNullOrWhiteSpace(dir) ? null : dir;
            }
            catch { return null; }
        }

        private const int ProcessQueryLimitedInformation = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder exeName, ref int size);

        private static string ProcessImagePath(int pid)
        {
            IntPtr h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (h == IntPtr.Zero) { return null; }
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
            }
            finally { CloseHandle(h); }
        }

        private void LogOnChange(string msg)
        {
            if (msg != _lastLog)
            {
                _lastLog = msg;
                LensLog.Debug(msg);
            }
        }
    }
}
