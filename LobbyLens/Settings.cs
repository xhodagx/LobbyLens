using System;
using System.IO;
using Hearthstone_Deck_Tracker;

namespace LobbyLens
{
    public class Settings
    {
        public bool ifLoad = false;
        public double scaleRatio = 1.0;
        public double positionLeft = 0.0;
        public double positionTop = 0.0;
        public double panelWidth = 0.0;   // explicit width in unscaled units; 0 = auto (content-sized)
        public double fontSize = 20.0;
        public double opacity = 1.0;
        public bool bestFirst = true;
        public bool sortByPlace = true;
        // Renamed from debugLog in v1.5.2 so the always-on-era saved value is dropped:
        // verbose logging is OFF for every install unless re-enabled in Settings.
        public bool verboseLog = false;
        public bool showRankNumbers = true;
        public bool showHeroInfo = true;
        public bool showComps = true;
        public bool showEliminations = true;
        public bool showNextOpponent = true;  // "(next)" marker on the upcoming combat opponent
        public bool showSession = true;   // running session line (games / avg place / net MMR)
        public bool reportMatches = true; // contribute anonymized match summaries to the backend
        public bool autoUpdate = true;    // stage signed releases automatically; they apply on HDT restart
        private static Settings _settings;

        // Raised whenever the settings window changes a value, so the live
        // panel can re-apply appearance without a match restart.
        public static event Action Changed;

        public static Settings Instance => _settings ??= new Settings();

        public static string DataDir => Path.Combine(Config.AppDataPath, "LobbyLens");

        private static string FilePath => Path.Combine(DataDir, "LobbyLensSettings.xml");

        // Settings file of the HDT_BGrank-era build, migrated on first run.
        private static string LegacyPath => Path.Combine(Config.AppDataPath, "BGrank", "BGrankSettings.xml");

        public static void NotifyChanged()
        {
            Changed?.Invoke();
        }

        public static void Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    _settings = XmlManager<Settings>.Load(FilePath);
                }
                else if (File.Exists(LegacyPath))
                {
                    _settings = XmlManager<Settings>.Load(LegacyPath);
                    Save();
                    LensLog.Info("migrated settings from BGrank");
                }
            }
            catch (Exception ex)
            {
                LensLog.Error("failed to load settings", ex);
            }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                XmlManager<Settings>.Save(FilePath, Instance);
            }
            catch (Exception ex)
            {
                LensLog.Error("failed to save settings", ex);
            }
        }
    }
}
