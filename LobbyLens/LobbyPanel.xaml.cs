using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Collections.Generic;
using Hearthstone_Deck_Tracker.API;
using Hearthstone_Deck_Tracker.Utility.Extensions;

namespace LobbyLens
{
    public partial class LobbyPanel : UserControl
    {
        public const double MinScale = 0.5;
        public const double MaxScale = 3.0;
        public const double MinPanelWidth = 190;  // matches PanelBorder MinWidth
        public const double MaxPanelWidth = 640;

        private static readonly Brush NormalBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xE3, 0xE3));
        private static readonly Brush RatingBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
        private static readonly Brush DimBrush = new SolidColorBrush(Color.FromRgb(0x8C, 0x91, 0x95));
        private static readonly Brush DeadBrush = new SolidColorBrush(Color.FromRgb(0x8C, 0x88, 0x88));
        private static readonly Brush DividerBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));

        // Mouse interaction: body = move, E/W edges = width, SE corner = zoom.
        // Height is content-driven (one row per player), so N/S edges do nothing —
        // "taller" can only honestly mean "zoom", which is what the corner does.
        private enum GripZone { None, Move, EdgeE, EdgeW, CornerSE }

        private bool isFirst = true;
        private bool isChange = false;
        private bool pillClickCandidate = false;
        private GripZone mouseMode = GripZone.None;
        private Point originalGridPosition;
        private Point originalMousePosition;
        private double originalWidth;
        private double originalScale;

        public LobbyPanel()
        {
            InitializeComponent();
            Visibility = Visibility.Hidden;
            if (Settings.Instance.ifLoad)
            {
                RootGrid.Margin = new Thickness(Settings.Instance.positionLeft, Settings.Instance.positionTop, 0.0, 0.0);
                isFirst = false;
            }
            ApplyAppearance();
            Settings.Changed += ApplyAppearance;
            Core.OverlayCanvas.Children.Add(this);
            OverlayExtensions.SetIsOverlayHitTestVisible(PanelBorder, true);
            OverlayExtensions.SetIsOverlayHitTestVisible(CloseButton, true);
            OverlayExtensions.SetIsOverlayHitTestVisible(MinimizeButton, true);
            OverlayExtensions.SetIsOverlayHitTestVisible(GearButton, true);
        }

        private void ApplyAppearance()
        {
            double scale = Math.Max(MinScale, Math.Min(Settings.Instance.scaleRatio, MaxScale));
            RootScale.ScaleX = scale;
            RootScale.ScaleY = scale;
            RootGrid.Opacity = Settings.Instance.opacity;
            double pw = Settings.Instance.panelWidth;
            PanelBorder.Width = pw > 0 ? Math.Max(MinPanelWidth, Math.Min(pw, MaxPanelWidth)) : double.NaN;
        }

        // Reset position/scale to first-run defaults (invoked from the settings window).
        public void ResetLayout()
        {
            Settings.Instance.scaleRatio = Math.Max(MinScale, Math.Min(Core.OverlayWindow.Width / 1920.0, 2.0));
            Settings.Instance.panelWidth = 0;
            RootGrid.Margin = new Thickness(0.0, 0.0, 0.0, 0.0);
            ApplyAppearance();
            isChange = true;
            LensLog.Debug("panel layout reset to defaults");
        }

        public void HidePanel()
        {
            Visibility = Visibility.Hidden;
        }

        // Live preview: render a representative lobby so the panel can be positioned and
        // scaled from Settings without starting a match. Uses invented names only.
        public void ShowPreview()
        {
            if (collapsed) { SetCollapsed(false); }
            var lines = new List<RankLine>
            {
                new RankLine("Lobby avg 9240 · you +180", dim: true),
                new RankLine("Quillstone (next) ×2") { Right = "11240", RightDim = "#389 · avg 3.2",
                    Sub = "Zephrys, the Great · T5 · 28♥", Sub2 = "3 Dragon · 2 Naga — t9" },
                new RankLine("Rime") { Right = "10663", RightDim = "#228 · avg 4.1",
                    Sub = "Malygos · T6 · 31♥+4" },
                new RankLine("(6th) Copperjaw", dead: true) { Right = "8000↓" },
            };
            DisplayLines(lines);
        }

        public void DisplayLines(List<RankLine> lines)
        {
            if (isFirst)
            {
                Settings.Instance.scaleRatio = Math.Max(MinScale, Math.Min(Core.OverlayWindow.Width / 1920.0, 2.0));
                isFirst = false;
            }

            ApplyAppearance();
            RowsHost.Children.Clear();
            foreach (RankLine line in lines)
            {
                RowsHost.Children.Add(BuildRow(line));
            }

            if (RowsHost.Visibility != Visibility.Visible && !collapsed)
            {
                // Legacy safety: if rows were hidden without the pill state, restore them
                // so a new match's display can never be silently swallowed.
                RowsHost.Visibility = Visibility.Visible;
            }
            Visibility = Visibility.Visible;
            SetPosition(RootGrid.Margin.Left, RootGrid.Margin.Top);
            LensLog.Debug($"render: {lines.Count} rows, pos=({RootGrid.Margin.Left:F0},{RootGrid.Margin.Top:F0}), scale={RootScale.ScaleX:F2}");
        }

        private FrameworkElement BuildRow(RankLine line)
        {
            if (line.Divider)
            {
                return new Border
                {
                    Height = 1,
                    Background = DividerBrush,
                    Margin = new Thickness(0, 4, 0, 4),
                    SnapsToDevicePixels = true
                };
            }

            Grid row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock left = new TextBlock
            {
                Text = line.Text,
                Foreground = line.Dead ? DeadBrush : (line.Dim ? DimBrush : NormalBrush),
                FontSize = Settings.Instance.fontSize,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            if (line.Dim) { left.FontStyle = FontStyles.Italic; }
            if (line.Dead) { left.TextDecorations = TextDecorations.Strikethrough; }
            row.Children.Add(left);

            if (!string.IsNullOrEmpty(line.Right))
            {
                TextBlock right = new TextBlock
                {
                    FontSize = Settings.Instance.fontSize,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(14, 0, 0, 0)
                };
                Run rating = new Run(line.Right)
                {
                    Foreground = line.Dead ? DeadBrush : RatingBrush,
                    FontWeight = FontWeights.SemiBold
                };
                if (line.Dead) { rating.TextDecorations = TextDecorations.Strikethrough; }
                right.Inlines.Add(rating);
                if (!string.IsNullOrEmpty(line.RightDim))
                {
                    Run rank = new Run("  " + line.RightDim)
                    {
                        Foreground = DimBrush,
                        FontSize = Math.Max(10.0, Settings.Instance.fontSize - 4.0)
                    };
                    if (line.Dead) { rank.TextDecorations = TextDecorations.Strikethrough; }
                    right.Inlines.Add(rank);
                }
                Grid.SetColumn(right, 1);
                row.Children.Add(right);
            }

            if (!string.IsNullOrEmpty(line.Sub) || !string.IsNullOrEmpty(line.Sub2))
            {
                StackPanel cell = new StackPanel();
                cell.Children.Add(row);
                if (!string.IsNullOrEmpty(line.Sub))
                {
                    cell.Children.Add(new TextBlock
                    {
                        Text = line.Sub,
                        Foreground = DimBrush,
                        FontSize = Math.Max(10.0, Settings.Instance.fontSize - 5.0),
                        Margin = new Thickness(10, -1, 0, 0)
                    });
                }
                if (!string.IsNullOrEmpty(line.Sub2))
                {
                    cell.Children.Add(new TextBlock
                    {
                        Text = line.Sub2,
                        Foreground = DimBrush,
                        FontSize = Math.Max(10.0, Settings.Instance.fontSize - 6.0),
                        Margin = new Thickness(10, 0, 0, 2)
                    });
                }
                return cell;
            }

            return row;
        }

        public void Clean()
        {
            Settings.Changed -= ApplyAppearance;
            if (isChange)
            {
                Settings.Instance.scaleRatio = RootScale.ScaleX;
                Settings.Instance.positionLeft = RootGrid.Margin.Left;
                Settings.Instance.positionTop = RootGrid.Margin.Top;
                Settings.Instance.ifLoad = true;
                Settings.Save();
            }
            Core.OverlayCanvas.Children.Remove(this);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Visibility = Visibility.Hidden;
        }

        private void GearButton_Click(object sender, RoutedEventArgs e)
        {
            SettingsWindow.Open(ResetLayout, ShowPreview);
        }

        // Collapse to a compact low-opacity pill (title + close only), restoring on a
        // click anywhere on it. Position and collapsed state persist across the match.
        private bool collapsed = false;

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            SetCollapsed(true);
        }

        private void SetCollapsed(bool value)
        {
            collapsed = value;
            RowsHost.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            TitleText.Text = value ? "LOBBYLENS" : "OPPONENT MMR";
            GearButton.Visibility = MinimizeButton.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            RestoreHint.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            RootGrid.Opacity = value ? Math.Min(0.45, Settings.Instance.opacity) : Settings.Instance.opacity;
            PanelBorder.Width = value ? double.NaN : (Settings.Instance.panelWidth > 0
                ? Math.Max(MinPanelWidth, Math.Min(Settings.Instance.panelWidth, MaxPanelWidth)) : double.NaN);
            isChange = true;
            LensLog.Debug($"panel {(value ? "collapsed to pill" : "restored")}");
        }

        // Grip zones in PanelBorder-local (unscaled) units; the threshold is divided
        // by the current scale so it stays ~10 SCREEN pixels grabbable at any zoom.
        private GripZone ZoneAt(MouseEventArgs e)
        {
            double scale = Math.Max(0.1, RootScale.ScaleX);
            double grip = 10.0 / scale;
            Point p = e.GetPosition(PanelBorder);
            double w = PanelBorder.ActualWidth;
            double h = PanelBorder.ActualHeight;
            if (w <= 0 || h <= 0) { return GripZone.Move; }

            bool nearE = p.X >= w - grip;
            bool nearW = p.X <= grip;
            bool nearS = p.Y >= h - grip;
            if (nearE && nearS) { return GripZone.CornerSE; }
            if (nearE && p.Y > 30) { return GripZone.EdgeE; } // keep the –/✕ buttons out of the east grip
            if (nearW) { return GripZone.EdgeW; }
            return GripZone.Move;
        }

        private void PanelBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (collapsed)
            {
                // a click that isn't a drag restores; a drag just moves the pill
                mouseMode = GripZone.Move;
                originalMousePosition = e.GetPosition(this);
                originalGridPosition = new Point(RootGrid.Margin.Left, RootGrid.Margin.Top);
                pillClickCandidate = true;
                PanelBorder.CaptureMouse();
                return;
            }

            GripZone zone = ZoneAt(e);

            if (e.ClickCount == 2 && (zone == GripZone.EdgeE || zone == GripZone.EdgeW))
            {
                Settings.Instance.panelWidth = 0;
                ApplyAppearance();
                isChange = true;
                LensLog.Debug("panel width reset to auto (edge double-click)");
                return;
            }

            mouseMode = zone;
            originalMousePosition = e.GetPosition(this);
            originalGridPosition = new Point(RootGrid.Margin.Left, RootGrid.Margin.Top);
            originalWidth = PanelBorder.ActualWidth;
            originalScale = RootScale.ScaleX;
            PanelBorder.CaptureMouse(); // fast edge-drags routinely leave the border
        }

        private void PanelBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            mouseMode = GripZone.None;
            if (PanelBorder.IsMouseCaptured) { PanelBorder.ReleaseMouseCapture(); }
            if (collapsed && pillClickCandidate) { SetCollapsed(false); }
            pillClickCandidate = false;
        }

        private void PanelBorder_MouseLeave(object sender, MouseEventArgs e)
        {
            if (!PanelBorder.IsMouseCaptured)
            {
                mouseMode = GripZone.None;
                PanelBorder.Cursor = null;
            }
        }

        private void PanelBorder_MouseMove(object sender, MouseEventArgs e)
        {
            if (mouseMode == GripZone.None)
            {
                GripZone hover = ZoneAt(e);
                PanelBorder.Cursor = hover == GripZone.CornerSE ? Cursors.SizeNWSE
                    : hover == GripZone.EdgeE || hover == GripZone.EdgeW ? Cursors.SizeWE
                    : null;
                return;
            }

            if (e.LeftButton != MouseButtonState.Pressed) // missed the up event somehow
            {
                mouseMode = GripZone.None;
                if (PanelBorder.IsMouseCaptured) { PanelBorder.ReleaseMouseCapture(); }
                return;
            }

            Point cur = e.GetPosition(this);
            double dx = cur.X - originalMousePosition.X;
            double dy = cur.Y - originalMousePosition.Y;

            // any real movement while collapsed means "drag the pill", not "restore"
            if (pillClickCandidate && (Math.Abs(dx) > 3 || Math.Abs(dy) > 3)) { pillClickCandidate = false; }

            switch (mouseMode)
            {
                case GripZone.Move:
                    SetPosition(originalGridPosition.X + dx, originalGridPosition.Y + dy);
                    isChange = true;
                    break;

                case GripZone.EdgeE:
                {
                    // dx is in canvas (screen) pixels; width is stored unscaled
                    double newW = Clamp(originalWidth + dx / originalScale, MinPanelWidth, MaxPanelWidth);
                    Settings.Instance.panelWidth = newW;
                    PanelBorder.Width = newW;
                    isChange = true;
                    break;
                }

                case GripZone.EdgeW:
                {
                    // west drag: width changes AND the panel shifts so the right edge stays put
                    double newW = Clamp(originalWidth - dx / originalScale, MinPanelWidth, MaxPanelWidth);
                    Settings.Instance.panelWidth = newW;
                    PanelBorder.Width = newW;
                    double newLeft = originalGridPosition.X + (originalWidth - newW) * originalScale;
                    RootGrid.Margin = new Thickness(Math.Max(0.0, newLeft), originalGridPosition.Y, 0.0, 0.0);
                    isChange = true;
                    break;
                }

                case GripZone.CornerSE:
                {
                    // scale so the corner tracks the horizontal drag; height follows
                    double newScale = Clamp((originalWidth * originalScale + dx) / Math.Max(1.0, originalWidth), MinScale, MaxScale);
                    Settings.Instance.scaleRatio = newScale;
                    ApplyAppearance();
                    isChange = true;
                    break;
                }
            }
        }

        private static double Clamp(double v, double lo, double hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        private void PanelBorder_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            double newRatio = RootScale.ScaleX;

            if (e.Delta > 0) { newRatio += 0.1; } // wheel up = zoom in
            else if (e.Delta < 0) { newRatio -= 0.1; }

            newRatio = Math.Max(MinScale, Math.Min(newRatio, MaxScale));

            Settings.Instance.scaleRatio = newRatio;
            ApplyAppearance();
            isChange = true;
        }

        private void SetPosition(double nowLeft, double nowTop)
        {
            double w = PanelBorder.ActualWidth > 0 ? PanelBorder.ActualWidth : 190;
            double h = PanelBorder.ActualHeight > 0 ? PanelBorder.ActualHeight : 100;
            double newLeft = Math.Max(0.0, Math.Min(nowLeft, Core.OverlayWindow.Width - (w * RootScale.ScaleX)));
            double newTop = Math.Max(0.0, Math.Min(nowTop, Core.OverlayWindow.Height - (h * RootScale.ScaleX)));
            RootGrid.Margin = new Thickness(newLeft, newTop, 0.0, 0.0);
        }
    }
}
