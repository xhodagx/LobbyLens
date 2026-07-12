using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Collections.Generic;
using ScryDotNet;

namespace LobbyLens
{
    // Reads the game client's UI memory through HDT's bundled Scry library:
    // the local player's battletag and the Battlegrounds leaderboard rail tiles.
    //
    // Field paths verified against the live client (2026-06, Unity 2022.3.62):
    //   tile name:    m_overlay.m_heroActor.m_playerNameText.m_Text  (hover-populated)
    //   hero card id: m_overlay.m_heroActor.m_entity.m_cardIdInternal
    // Reads of nonexistent fields throw native SEHExceptions — every read is guarded.
    public class GameMemory
    {
        // Last known-good Unity version, used only if reading the real one fails.
        private const string FallbackUnityVersion = "2021.3.25.61228";

        private static string _detectedUnity;
        private MonoImage _image;
        private string _myName;
        private string _lastLog;

        public struct Tile
        {
            public int Team;
            public dynamic Handle;
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
                _myName = presence?["m_myPlayer"]?["m_account"]?["m_battleTag"]?["m_name"];
                LogOnChange($"[mem] own battletag => '{_myName ?? "null"}'");
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
            string fromProcess = null;
            try { fromProcess = Path.GetDirectoryName(proc.MainModule.FileName); }
            catch { /* cross-bitness module access can fail */ }
            if (fromProcess != null) { yield return fromProcess; }
            yield return @"C:\Program Files (x86)\Hearthstone";
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
