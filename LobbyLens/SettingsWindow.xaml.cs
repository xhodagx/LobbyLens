using System;
using System.Diagnostics;
using System.Windows;

namespace LobbyLens
{
    public partial class SettingsWindow : Window
    {
        // Singleton opener shared by HDT's plugin-list button and the panel's ⚙.
        private static SettingsWindow open;

        public static void Open(Action onResetLayout, Action onPreview = null)
        {
            if (open == null || !open.IsLoaded)
            {
                open = new SettingsWindow(onResetLayout, onPreview);
                open.Show();
            }
            open.Activate();
        }

        public static void CloseOpen()
        {
            if (open != null && open.IsLoaded) { open.Close(); }
            open = null;
        }

        private readonly Action onResetLayout;
        private readonly Action onPreview;
        private bool ready = false;

        public SettingsWindow(Action onResetLayout, Action onPreview = null)
        {
            InitializeComponent();
            this.onResetLayout = onResetLayout;
            this.onPreview = onPreview;
            ApplyMeta();
            if (!Meta.LoadCompletion.IsCompleted)
            {
                // Window opened before the once-per-session meta fetch finished —
                // refresh the update banner + support section when it lands.
                _ = Meta.LoadCompletion.ContinueWith(_ =>
                {
                    try { Dispatcher.Invoke(ApplyMeta); } catch { }
                });
            }

            FontSlider.Value = Settings.Instance.fontSize;
            OpacitySlider.Value = Settings.Instance.opacity;
            ScaleSlider.Value = Math.Max(0.5, Math.Min(Settings.Instance.scaleRatio, 3.0));
            SortByPlaceBox.IsChecked = Settings.Instance.sortByPlace;
            BestFirstBox.IsChecked = Settings.Instance.bestFirst;
            DebugBox.IsChecked = Settings.Instance.verboseLog;
            RankNumbersBox.IsChecked = Settings.Instance.showRankNumbers;
            HeroInfoBox.IsChecked = Settings.Instance.showHeroInfo;
            CompsBox.IsChecked = Settings.Instance.showComps;
            EliminationsBox.IsChecked = Settings.Instance.showEliminations;
            NextOpponentBox.IsChecked = Settings.Instance.showNextOpponent;
            EncountersBox.IsChecked = Settings.Instance.showEncounters;
            LobbyAvgBox.IsChecked = Settings.Instance.showLobbyAvg;
            FormBox.IsChecked = Settings.Instance.showForm;
            SessionBox.IsChecked = Settings.Instance.showSession;
            ReportMatchesBox.IsChecked = Settings.Instance.reportMatches;
            AutoUpdateBox.IsChecked = Settings.Instance.autoUpdate;
            UpdateLabels();

            ready = true;
            Closing += (s, e) => Settings.Save();
        }

        private void UpdateLabels()
        {
            FontLabel.Text = $"Font size: {(int)FontSlider.Value}";
            OpacityLabel.Text = $"Opacity: {(int)(OpacitySlider.Value * 100)}%";
            ScaleLabel.Text = $"Panel scale: {ScaleSlider.Value:F2}×";
        }

        private void FontSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!ready) { return; }
            Settings.Instance.fontSize = FontSlider.Value;
            UpdateLabels();
            Settings.NotifyChanged();
        }

        private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!ready) { return; }
            Settings.Instance.opacity = OpacitySlider.Value;
            UpdateLabels();
            Settings.NotifyChanged();
        }

        private void ScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!ready) { return; }
            Settings.Instance.scaleRatio = ScaleSlider.Value;
            UpdateLabels();
            Settings.NotifyChanged();
        }

        private void SortByPlaceBox_Changed(object sender, RoutedEventArgs e)
        {
            if (!ready) { return; }
            Settings.Instance.sortByPlace = SortByPlaceBox.IsChecked == true;
            Settings.NotifyChanged();
        }

        private void BestFirstBox_Changed(object sender, RoutedEventArgs e)
        {
            if (!ready) { return; }
            Settings.Instance.bestFirst = BestFirstBox.IsChecked == true;
            Settings.NotifyChanged();
        }

        private void DebugBox_Changed(object sender, RoutedEventArgs e)
        {
            if (!ready) { return; }
            Settings.Instance.verboseLog = DebugBox.IsChecked == true;
            LensLog.SetDebug(Settings.Instance.verboseLog);
            Settings.NotifyChanged();
        }

        private void FeatureBox_Changed(object sender, RoutedEventArgs e)
        {
            if (!ready) { return; }
            Settings.Instance.showRankNumbers = RankNumbersBox.IsChecked == true;
            Settings.Instance.showHeroInfo = HeroInfoBox.IsChecked == true;
            Settings.Instance.showComps = CompsBox.IsChecked == true;
            Settings.Instance.showEliminations = EliminationsBox.IsChecked == true;
            Settings.Instance.showNextOpponent = NextOpponentBox.IsChecked == true;
            Settings.Instance.showEncounters = EncountersBox.IsChecked == true;
            Settings.Instance.showLobbyAvg = LobbyAvgBox.IsChecked == true;
            Settings.Instance.showForm = FormBox.IsChecked == true;
            Settings.Instance.showSession = SessionBox.IsChecked == true;
            Settings.NotifyChanged();
        }

        private void ReportBox_Changed(object sender, RoutedEventArgs e)
        {
            if (!ready) { return; }
            Settings.Instance.reportMatches = ReportMatchesBox.IsChecked == true;
            Settings.NotifyChanged();
        }

        private void AutoUpdateBox_Changed(object sender, RoutedEventArgs e)
        {
            if (!ready) { return; }
            Settings.Instance.autoUpdate = AutoUpdateBox.IsChecked == true;
            Settings.NotifyChanged();
        }

        private void PreviewButton_Click(object sender, RoutedEventArgs e)
        {
            onPreview?.Invoke();
        }

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            Settings.Instance.scaleRatio = 1.0;
            Settings.Instance.positionLeft = 0.0;
            Settings.Instance.positionTop = 0.0;
            Settings.Instance.panelWidth = 0.0;
            Settings.Instance.ifLoad = false;
            onResetLayout?.Invoke(); // may recompute scaleRatio from the overlay size
            Settings.Save();
            ScaleSlider.Value = Math.Max(0.5, Math.Min(Settings.Instance.scaleRatio, 3.0));
            Settings.NotifyChanged();
        }

        // Sections light up only for values present in the remote meta.json, so
        // shipped binaries gain/lose links without an update (HDT can't auto-update).
        private void ApplyMeta()
        {
            StagedText.Visibility = Updater.Staged ? Visibility.Visible : Visibility.Collapsed;
            UpdateText.Visibility = Meta.UpdateAvailable && !Updater.Staged ? Visibility.Visible : Visibility.Collapsed;

            bool any = false;
            if (Meta.Kofi != null) { KofiButton.Visibility = Visibility.Visible; any = true; }
            if (Meta.Lightning != null) { LightningText.Text = Meta.Lightning; LightningRow.Visibility = Visibility.Visible; any = true; }
            if (Meta.Btc != null) { BtcText.Text = Meta.Btc; BtcRow.Visibility = Visibility.Visible; any = true; }
            SupportPanel.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        }

        // Values here come from the remote meta.json. The blob is a dumb pipe that
        // must never be able to launch anything but a browser, so only https URLs
        // are ever handed to the shell.
        private static void OpenUrl(string url)
        {
            if (url == null || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                LensLog.Warn($"refusing to open non-https url: {url ?? "(null)"}");
                return;
            }
            try { Process.Start(url); }
            catch (Exception ex) { LensLog.Debug($"failed to open url: {ex.Message}"); }
        }

        private void UpdateLink_Click(object sender, RoutedEventArgs e) => OpenUrl(Meta.ReleaseUrl);

        private void KofiButton_Click(object sender, RoutedEventArgs e) => OpenUrl(Meta.Kofi);

        private void CopyLightning_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(Meta.Lightning); } catch { }
        }

        private void CopyBtc_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(Meta.Btc); } catch { }
        }
    }
}
