using System;
using System.Diagnostics;
using System.Windows;

namespace LobbyLens
{
    public partial class SettingsWindow : Window
    {
        private readonly Action onResetLayout;
        private bool ready = false;

        public SettingsWindow(Action onResetLayout)
        {
            InitializeComponent();
            this.onResetLayout = onResetLayout;
            ApplyMeta();

            FontSlider.Value = Settings.Instance.fontSize;
            OpacitySlider.Value = Settings.Instance.opacity;
            ScaleSlider.Value = Math.Max(0.5, Math.Min(Settings.Instance.scaleRatio, 3.0));
            SortByPlaceBox.IsChecked = Settings.Instance.sortByPlace;
            BestFirstBox.IsChecked = Settings.Instance.bestFirst;
            DebugBox.IsChecked = Settings.Instance.debugLog;
            RankNumbersBox.IsChecked = Settings.Instance.showRankNumbers;
            HeroInfoBox.IsChecked = Settings.Instance.showHeroInfo;
            CompsBox.IsChecked = Settings.Instance.showComps;
            EliminationsBox.IsChecked = Settings.Instance.showEliminations;
            ReportMatchesBox.IsChecked = Settings.Instance.reportMatches;
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
            Settings.Instance.debugLog = DebugBox.IsChecked == true;
            LensLog.SetDebug(Settings.Instance.debugLog);
            Settings.NotifyChanged();
        }

        private void FeatureBox_Changed(object sender, RoutedEventArgs e)
        {
            if (!ready) { return; }
            Settings.Instance.showRankNumbers = RankNumbersBox.IsChecked == true;
            Settings.Instance.showHeroInfo = HeroInfoBox.IsChecked == true;
            Settings.Instance.showComps = CompsBox.IsChecked == true;
            Settings.Instance.showEliminations = EliminationsBox.IsChecked == true;
            Settings.NotifyChanged();
        }

        private void ReportBox_Changed(object sender, RoutedEventArgs e)
        {
            if (!ready) { return; }
            Settings.Instance.reportMatches = ReportMatchesBox.IsChecked == true;
            Settings.NotifyChanged();
        }

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            Settings.Instance.scaleRatio = 1.0;
            Settings.Instance.positionLeft = 0.0;
            Settings.Instance.positionTop = 0.0;
            Settings.Instance.ifLoad = false;
            Settings.Save();
            onResetLayout?.Invoke();
            Settings.NotifyChanged();
        }

        // Sections light up only for values present in the remote meta.json, so
        // shipped binaries gain/lose links without an update (HDT can't auto-update).
        private void ApplyMeta()
        {
            UpdateText.Visibility = Meta.UpdateAvailable ? Visibility.Visible : Visibility.Collapsed;

            bool any = false;
            if (Meta.Kofi != null) { KofiButton.Visibility = Visibility.Visible; any = true; }
            if (Meta.Lightning != null) { LightningText.Text = Meta.Lightning; LightningRow.Visibility = Visibility.Visible; any = true; }
            if (Meta.Btc != null) { BtcText.Text = Meta.Btc; BtcRow.Visibility = Visibility.Visible; any = true; }
            SupportPanel.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        }

        private static void OpenUrl(string url)
        {
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
