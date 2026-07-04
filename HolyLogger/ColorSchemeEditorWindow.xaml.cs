using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HolyLogger
{
    // "Customize Colors" (View > Color Scheme > Customize Colors): shows every color role from
    // ThemePalette.TokenCatalog with a friendly name, an explanation, and a clickable swatch.
    //
    // Model: edits are layered on top of the built-in scheme that was active when editing began
    // (the "base"). The first change creates the user's Custom scheme and activates it; every
    // change applies to the whole application IMMEDIATELY (ThemeManager re-resolves all
    // DynamicResource brushes) and is saved. Per-row Reset returns one color to the base scheme's
    // value; Reset All discards the Custom scheme entirely and reactivates the base.
    public partial class ColorSchemeEditorWindow : Window
    {
        // Everything the UI needs to keep one color row up to date after an edit or reset.
        private class Row
        {
            public TokenInfo Info;
            public Border Swatch;
            public TextBlock HexLabel;
            public Button ResetButton;
        }

        private readonly List<Row> _rows = new List<Row>();

        // The working copy of the custom colors (token -> hex) and the built-in base scheme.
        private Dictionary<string, string> _colors;
        private ColorScheme _base;

        public ColorSchemeEditorWindow()
        {
            InitializeComponent();

            // Decide WHAT is being edited, and say so explicitly in the header -- the one question
            // every user asks here is "which scheme am I changing?". The answer is always: your own
            // Custom scheme; the built-in schemes are never modified.
            bool editExisting = ThemeManager.CurrentSchemeId == CustomSchemeStore.Id;

            if (!editExisting && CustomSchemeStore.Exists)
            {
                // A Custom scheme exists but a built-in one is active. Editing must not silently
                // overwrite the user's earlier work -- make the choice theirs.
                string existingBase = ThemePalette.FindScheme(CustomSchemeStore.BaseId).DisplayName;
                editExisting = HolyMessageBox.ShowConfirm(
                    "You already have a Custom scheme (based on " + existingBase + ").\n\n" +
                    "YES — continue editing your existing Custom scheme.\n" +
                    "NO — start over from the current " + ThemeManager.CurrentScheme.DisplayName +
                    " scheme (your first change will replace the existing Custom scheme).",
                    "Customize Colors", HolyMsgType.Info, Application.Current.MainWindow);
            }

            if (editExisting)
            {
                _base = ThemePalette.FindScheme(CustomSchemeStore.BaseId);
                _colors = CustomSchemeStore.Load() ?? new Dictionary<string, string>();
                // Make the scheme being edited the one on screen, so every click is seen live.
                if (ThemeManager.CurrentSchemeId != CustomSchemeStore.Id)
                    ThemeManager.Apply(CustomSchemeStore.Id);
            }
            else
            {
                _base = ThemeManager.CurrentScheme;
                _colors = new Dictionary<string, string>();
            }

            Title = "Customize Colors — based on " + _base.DisplayName;

            // The banner is the headline: WHAT is being edited (always the Custom scheme) and
            // WHICH built-in scheme its colors start from, in large type nobody can miss.
            TB_BannerPrefix.Text = editExisting
                ? "You are editing your CUSTOM scheme, based on:"
                : "Creating a new CUSTOM scheme, starting from:";
            TB_BannerScheme.Text = _base.DisplayName.ToUpperInvariant();

            TB_SubHeader.Text =
                "Click a color square to change it — changes apply immediately to the whole application and are "
                + "saved automatically as \"Custom\" in the Color Scheme menu. The built-in schemes (Light, Dark, …) "
                + "are never modified; you can always switch back to them.";

            BuildRows();
            UpdateFooter();
        }

        // The hex a token should currently show: the user's override, or the base scheme's value.
        private string EffectiveHex(string token)
            => _colors.TryGetValue(token, out string hex) ? hex : BaseHex(token);

        private string BaseHex(string token)
            => ThemePalette.Tokens.TryGetValue(token, out string[] row) ? row[_base.Column] : "#FF6600";

        private void BuildRows()
        {
            GroupsPanel.Children.Clear();
            _rows.Clear();

            foreach (var group in ThemePalette.TokenCatalog.GroupBy(t => t.Group))
            {
                var header = new TextBlock
                {
                    Text = group.Key,
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 12, 0, 4)
                };
                header.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
                GroupsPanel.Children.Add(header);

                foreach (var info in group)
                    GroupsPanel.Children.Add(BuildRow(info));
            }
        }

        private UIElement BuildRow(TokenInfo info)
        {
            var grid = new Grid { Margin = new Thickness(4, 3, 4, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });                       // swatch
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });     // texts
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });                       // hex
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });                       // reset

            var swatch = new Border
            {
                Width = 44,
                Height = 26,
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Click to choose a color"
            };
            swatch.SetResourceReference(Border.BorderBrushProperty, "ThemeBorderBrush");
            swatch.MouseLeftButtonUp += (s, e) => PickColor(info);
            Grid.SetColumn(swatch, 0);
            grid.Children.Add(swatch);

            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0) };
            var name = new TextBlock { Text = info.DisplayName, FontSize = 13, FontWeight = FontWeights.SemiBold };
            name.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            var desc = new TextBlock { Text = info.Description, FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
            desc.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            texts.Children.Add(name);
            texts.Children.Add(desc);
            Grid.SetColumn(texts, 1);
            grid.Children.Add(texts);

            var hexLabel = new TextBlock
            {
                FontSize = 11.5,
                FontFamily = new FontFamily("Consolas"),
                VerticalAlignment = VerticalAlignment.Center
            };
            hexLabel.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            Grid.SetColumn(hexLabel, 2);
            grid.Children.Add(hexLabel);

            var reset = new Button
            {
                Content = "Reset",
                FontSize = 11,
                Padding = new Thickness(6, 2, 6, 2),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Return this color to the " + _base.DisplayName + " scheme's value"
            };
            reset.Click += (s, e) => ResetOne(info);
            Grid.SetColumn(reset, 3);
            grid.Children.Add(reset);

            var row = new Row { Info = info, Swatch = swatch, HexLabel = hexLabel, ResetButton = reset };
            _rows.Add(row);
            RefreshRow(row);
            return grid;
        }

        private void RefreshRow(Row row)
        {
            string hex = EffectiveHex(row.Info.Key);
            try { row.Swatch.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
            catch (Exception ex) { Log.Swallow(ex); }
            row.HexLabel.Text = hex.ToUpperInvariant();
            // Reset is only meaningful (and only shown enabled) when the color differs from the base.
            row.ResetButton.IsEnabled = !string.Equals(hex, BaseHex(row.Info.Key), StringComparison.OrdinalIgnoreCase);
        }

        private void PickColor(TokenInfo info)
        {
            Color current;
            try { current = (Color)ColorConverter.ConvertFromString(EffectiveHex(info.Key)); }
            catch { current = Colors.Gray; }

            using (var dlg = new System.Windows.Forms.ColorDialog())
            {
                dlg.FullOpen = true;
                dlg.Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B);
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

                string hex = string.Format("#{0:X2}{1:X2}{2:X2}", dlg.Color.R, dlg.Color.G, dlg.Color.B);
                _colors[info.Key] = hex;
                SaveAndApply();
                RefreshRow(_rows.First(r => r.Info.Key == info.Key));
            }
        }

        private void ResetOne(TokenInfo info)
        {
            _colors.Remove(info.Key);
            SaveAndApply();
            RefreshRow(_rows.First(r => r.Info.Key == info.Key));
        }

        // Persist the working colors as the Custom scheme and apply them live everywhere. When the
        // last override is removed, the Custom scheme is deleted and the base scheme reactivated,
        // so an all-default "Custom" entry never lingers in the menu.
        private void SaveAndApply()
        {
            if (_colors.Count == 0)
            {
                CustomSchemeStore.Delete();
                ThemeManager.Apply(_base.Id);
            }
            else
            {
                CustomSchemeStore.Save(_colors, _base.Id);
                ThemeManager.Apply(CustomSchemeStore.Id);
            }
            UpdateFooter();
        }

        private void UpdateFooter()
        {
            TB_BaseInfo.Text = _colors.Count == 0
                ? "No changes yet — showing the " + _base.DisplayName + " scheme."
                : _colors.Count + " color(s) changed, based on the " + _base.DisplayName + " scheme.";
            Btn_ResetAll.IsEnabled = _colors.Count > 0;
        }

        private void Btn_ResetAll_Click(object sender, RoutedEventArgs e)
        {
            if (!HolyMessageBox.ShowConfirm(
                    "Discard ALL your color changes and return to the " + _base.DisplayName + " scheme?",
                    "Reset All Colors", HolyMsgType.Warning, this))
                return;

            _colors.Clear();
            SaveAndApply();
            foreach (var row in _rows) RefreshRow(row);
        }

        private void Btn_Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
