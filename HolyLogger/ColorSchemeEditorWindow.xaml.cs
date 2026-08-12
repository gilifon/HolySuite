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
    // Model: the selected scheme is edited IN PLACE. Every change applies to the whole application
    // IMMEDIATELY (ThemeManager re-resolves all DynamicResource brushes) and is saved to that
    // scheme, which keeps its name. Per-row Reset returns one color to the scheme's factory value;
    // Reset All returns every color to factory. No separate "Custom" scheme is ever created.
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

        // The working copy of the active scheme's user edits (token -> hex) and the scheme itself.
        private Dictionary<string, string> _colors;
        private ColorScheme _scheme;

        public ColorSchemeEditorWindow()
        {
            InitializeComponent();
            WindowBounds.Attach(this, "ColorSchemeEditor");   // remember position + size

            // Dead simple model: you edit whatever scheme is selected right now, in place. Your
            // changes are saved to that scheme and remembered; the factory colors are always
            // recoverable via Reset. Open, click, done.
            _scheme = ThemeManager.CurrentScheme;
            _colors = SchemeOverrides.For(_scheme.Id);

            Title = "Customize Current Color Scheme";

            // The banner names the scheme being edited -- the one currently on screen.
            TB_BannerPrefix.Text = "You are editing color scheme:";
            TB_BannerScheme.Text = _scheme.DisplayName.ToUpperInvariant();

            TB_SubHeader.Text =
                "Click a color square to change it — changes apply immediately to the whole application and are "
                + "saved to the \"" + _scheme.DisplayName + "\" scheme. Use Reset (or Reset All) at any time to return "
                + "a color to this scheme's original value.";

            BuildRows();
            UpdateFooter();
        }

        // The hex a token should currently show: the user's edit, or the scheme's factory value.
        private string EffectiveHex(string token)
            => _colors.TryGetValue(token, out string hex) ? hex : BaseHex(token);

        // The scheme's factory palette value -- what Reset returns the token to.
        private string BaseHex(string token)
            => ThemePalette.Tokens.TryGetValue(token, out string[] row) ? row[_scheme.Column] : "#FF6600";

        private void BuildRows()
        {
            GroupsPanel.Children.Clear();
            _rows.Clear();

            foreach (var group in ThemePalette.TokenCatalog.GroupBy(t => t.Group))
            {
                var header = new TextBlock
                {
                    Text = group.Key,
                    FontSize = 16,
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
            var name = new TextBlock { Text = info.DisplayName, FontSize = 16, FontWeight = FontWeights.SemiBold };
            name.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            var desc = new TextBlock { Text = info.Description, FontSize = 16, TextWrapping = TextWrapping.Wrap };
            desc.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            texts.Children.Add(name);
            texts.Children.Add(desc);
            Grid.SetColumn(texts, 1);
            grid.Children.Add(texts);

            var hexLabel = new TextBlock
            {
                FontSize = 16,
                FontFamily = new FontFamily("Consolas"),
                VerticalAlignment = VerticalAlignment.Center
            };
            hexLabel.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            Grid.SetColumn(hexLabel, 2);
            grid.Children.Add(hexLabel);

            var reset = new Button
            {
                Content = "Reset",
                FontSize = 16,
                Padding = new Thickness(6, 2, 6, 2),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Return this color to the " + _scheme.DisplayName + " scheme's original value"
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

        // Persist the working edits to the active scheme and apply them live everywhere. When the
        // last edit is removed, the scheme's stored overrides are cleared, so it returns to its
        // pristine factory colors with no leftover state.
        private void SaveAndApply()
        {
            SchemeOverrides.Save(_scheme.Id, _colors);
            ThemeManager.Apply(_scheme.Id);
            UpdateFooter();
        }

        private void UpdateFooter()
        {
            TB_BaseInfo.Text = _colors.Count == 0
                ? "No changes yet — showing the original " + _scheme.DisplayName + " colors."
                : _colors.Count + " color(s) changed from the original " + _scheme.DisplayName + " colors.";
            Btn_ResetAll.IsEnabled = _colors.Count > 0;
        }

        private void Btn_ResetAll_Click(object sender, RoutedEventArgs e)
        {
            if (!HolyMessageBox.ShowConfirm(
                    "Discard ALL your color changes to the " + _scheme.DisplayName + " scheme and return it to its original colors?",
                    "Reset All Colors", HolyMsgType.Warning, this))
                return;

            _colors.Clear();
            SaveAndApply();
            foreach (var row in _rows) RefreshRow(row);
        }

        private void Btn_Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
