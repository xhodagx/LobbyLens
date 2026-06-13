using System;
using System.Windows.Controls;
using Hearthstone_Deck_Tracker.Plugins;

namespace LobbyLens
{
    public class LobbyLensPlugin : IPlugin
    {
        public MenuItem MenuItem { get; private set; }
        private LobbyTracker tracker = null;
        private SettingsWindow settingsWindow = null;
        private DateTime lastUpdate = DateTime.Now;

        public string Name => "LobbyLens";

        public string Description => "Live Battlegrounds lobby readout: opponent MMR and ladder rank, hero, tier, health, board composition, standings and eliminations.";

        public string Author => "xhodagx";

        public Version Version => new Version(1, 0, 0);

        public string ButtonText => "Settings";

        public void OnButtonPress()
        {
            if (settingsWindow == null || !settingsWindow.IsLoaded)
            {
                settingsWindow = new SettingsWindow(() => tracker?.ResetLayout());
                settingsWindow.Show();
            }
            else
            {
                settingsWindow.Activate();
            }
        }

        public void OnLoad()
        {
            LensLog.Init(Settings.DataDir, true);
            Settings.Load();
            LensLog.SetDebug(Settings.Instance.debugLog);
            LensLog.Info($"LobbyLens v{Version} loaded");
            CreateMenuItem();
            MenuItem.IsChecked = true;
        }

        public void OnUnload()
        {
            if (settingsWindow != null && settingsWindow.IsLoaded) { settingsWindow.Close(); }
            MenuItem.IsChecked = false;
        }

        public void OnUpdate()
        {
            if (tracker != null && (DateTime.Now - lastUpdate).TotalSeconds >= 1)
            {
                lastUpdate = DateTime.Now;
                tracker.OnUpdate();
            }
        }

        private void CreateMenuItem()
        {
            MenuItem = new MenuItem
            {
                Header = "LobbyLens",
                IsCheckable = true
            };

            MenuItem.Checked += (sender, args) => tracker ??= new LobbyTracker();
            MenuItem.Unchecked += (sender, args) =>
            {
                tracker?.Clean(true);
                tracker = null;
            };
        }
    }
}
