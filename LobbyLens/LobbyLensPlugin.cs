using System;
using System.Windows.Controls;
using Hearthstone_Deck_Tracker.Plugins;

namespace LobbyLens
{
    public class LobbyLensPlugin : IPlugin
    {
        public MenuItem MenuItem { get; private set; }
        private LobbyTracker tracker = null;
        private DateTime lastUpdate = DateTime.Now;

        public string Name => "LobbyLens";

        public string Description => "Live Battlegrounds lobby readout: opponent MMR and ladder rank, hero, tier, health, board composition, standings and eliminations.";

        public string Author => "xhodagx";

        // Single source of truth is <Version> in the csproj (assembly version).
        public Version Version => typeof(LobbyLensPlugin).Assembly.GetName().Version;

        public string ButtonText => "Settings";

        public void OnButtonPress()
        {
            SettingsWindow.Open(() => tracker?.ResetLayout(), () => tracker?.ShowPreview());
        }

        public void OnLoad()
        {
            LensLog.Init(Settings.DataDir);
            Settings.Load();
            LensLog.SetDebug(Settings.Instance.verboseLog);
            LensLog.Info($"LobbyLens v{Version} loaded");
            Updater.CleanupStaleFiles();
            CreateMenuItem();
            MenuItem.IsChecked = true;
            _ = Updater.Run();
        }

        public void OnUnload()
        {
            SettingsWindow.CloseOpen();
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
                tracker?.Clean();
                tracker = null;
            };
        }
    }
}
