using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using HolyLogger.Contests;

namespace HolyLogger
{
    // Collects the Cabrillo header fields for a contest. Built dynamically from CabrilloHeader.Catalog:
    // required fields (per the contest) are marked with a red *, values are pre-filled from what the
    // caller passes in. Two modes:
    //   setup  -> primary = "Save", secondary = "Skip" (always allowed; partial input is kept)
    //   export -> primary = "Save & Export" (enabled only when every required field is filled),
    //             secondary = "Cancel" (aborts the export)
    public partial class ContestInfoWindow : Window
    {
        private readonly bool _exportMode;
        private readonly HashSet<string> _required;
        private readonly Dictionary<string, List<string>> _extraChoices;   // the contest's own additions
        private readonly Dictionary<string, FrameworkElement> _inputs = new Dictionary<string, FrameworkElement>();

        // Collected values (tag -> value); set on Save and Skip so partial input is never lost.
        public Dictionary<string, string> Values { get; private set; }
        // True when the user pressed the primary (Save) button.
        public bool Completed { get; private set; }
        // True when the user pressed Skip (setup mode only).
        public bool Skipped { get; private set; }

        public ContestInfoWindow(Contest contest, IDictionary<string, string> current, bool exportMode)
        {
            InitializeComponent();
            _exportMode = exportMode;
            _required = CabrilloHeader.RequiredFor(contest);

            string contestName = contest?.Name ?? contest?.CabrilloName ?? "this contest";
            TB_Title.Text = "Contest Information — " + contestName;
            TB_Sub.Text = exportMode
                ? "These lines go into the Cabrillo log header. Fields marked * are required and must be filled before the file can be exported."
                : "These lines go into the Cabrillo log header. Fill them now or later — fields marked * are required before you can export. You may Skip for now.";

            _extraChoices = contest?.ExtraChoices;
            BuildFields(current ?? new Dictionary<string, string>());

            Btn_Primary.Content   = exportMode ? "Save & Export" : "Save";
            Btn_Secondary.Content = exportMode ? "Cancel" : "Skip";
            Validate();
        }

        // Label and box side by side: the label column is as wide as the longest label in that
        // column (SharedSizeGroup across the column's rows), the box a fixed width - wide enough for
        // the longest choice ("LOW (Not more than 100W)") without the old window-wide boxes.
        private const double InputWidth = 220;

        private void BuildFields(IDictionary<string, string> current)
        {
            Grid.SetIsSharedSizeScope(SP_Contest, true);
            // The moved fields share the LEFT column's label width, so both left blocks line up.
            Grid.SetIsSharedSizeScope(SP_Personal.Parent as Grid, true);

            var contestRows = new List<FrameworkElement>();
            CabrilloFieldScope? lastScope = null;
            foreach (var field in CabrilloHeader.Catalog)
            {
                StackPanel column = field.Scope == CabrilloFieldScope.Personal ? SP_Personal : SP_Contest;
                if (lastScope != field.Scope)
                {
                    lastScope = field.Scope;
                    column.Children.Add(Heading(field.Scope == CabrilloFieldScope.Personal ? "Station & operator" : "This contest"));
                }

                FrameworkElement row = BuildFieldRow(field, current);
                column.Children.Add(row);
                if (field.Scope == CabrilloFieldScope.Contest) contestRows.Add(row);
            }

            BalanceColumns(contestRows);

            // Never taller than the screen it opens on: past that the list scrolls, rather than the
            // buttons going off the bottom.
            Loaded += (s, e) =>
            {
                try
                {
                    var screen = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle);
                    var source = PresentationSource.FromVisual(this);
                    double scale = source?.CompositionTarget != null ? source.CompositionTarget.TransformToDevice.M22 : 1.0;
                    MaxHeight = screen.WorkingArea.Height / scale;
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            };
        }

        private static TextBlock Heading(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = ThemeManager.Brush("AccentBrush"),
                Margin = new Thickness(0, 8, 0, 4)
            };
        }

        // NO EMPTY CORNER (his request, from a screenshot): the personal fields are fewer than the
        // contest ones, which left a hole under the left column while the right one ran on. So the
        // LAST contest fields move under the left column, beneath a "This contest" heading of their
        // own - as many as make the two columns closest in height, measured, not guessed. The order
        // of the contest fields is kept: right column top to bottom, then the left column's tail.
        private void BalanceColumns(List<FrameworkElement> contestRows)
        {
            try
            {
                var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);
                SP_Personal.Measure(infinite);
                SP_Contest.Measure(infinite);
                double left = SP_Personal.DesiredSize.Height;
                double right = SP_Contest.DesiredSize.Height;

                TextBlock heading = Heading("This contest");
                heading.Measure(infinite);
                double headingH = heading.DesiredSize.Height + heading.Margin.Top + heading.Margin.Bottom;

                // How many trailing contest fields to move: the k with the shortest taller column.
                int bestK = 0;
                double best = Math.Max(left, right);
                double moved = 0;
                for (int k = 1; k < contestRows.Count; k++)
                {
                    FrameworkElement r = contestRows[contestRows.Count - k];
                    moved += r.DesiredSize.Height + r.Margin.Top + r.Margin.Bottom;
                    double tallest = Math.Max(left + headingH + moved, right - moved);
                    if (tallest < best - 0.5) { best = tallest; bestK = k; }
                }
                if (bestK == 0) return;

                SP_More.Children.Add(heading);
                for (int i = contestRows.Count - bestK; i < contestRows.Count; i++)
                {
                    SP_Contest.Children.Remove(contestRows[i]);
                    SP_More.Children.Add(contestRows[i]);
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // One field: its label and its box side by side, and its hint (if any) under the box.
        private FrameworkElement BuildFieldRow(CabrilloHeaderField field, IDictionary<string, string> current)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "Label" });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(InputWidth) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            bool required = _required.Contains(field.Tag);
            current.TryGetValue(field.Tag, out string val);
            val = val ?? string.Empty;

            var label = new TextBlock
            {
                FontSize = 16,
                Margin = new Thickness(0, 4, 10, 4),
                VerticalAlignment = field.Input == CabrilloFieldInput.MultiLineText
                    ? VerticalAlignment.Top : VerticalAlignment.Center,
                Foreground = ThemeManager.Brush("TextBrush")
            };
            label.Inlines.Add(new Run(field.Label));
            if (required)
                label.Inlines.Add(new Run("  *") { Foreground = Brushes.Red, FontWeight = FontWeights.Bold });
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);

            FrameworkElement input;
            if (field.Input == CabrilloFieldInput.Choice)
            {
                var combo = new ComboBox { FontSize = 16, Height = 26, IsEnabled = !field.ReadOnly };
                // Each item SHOWS a label and HOLDS the Cabrillo value (Tag), so "LOW (Not more than
                // 100W)" can be read while "LOW" is what goes into the header.
                var values = new List<string> { string.Empty };   // blank = not specified
                values.AddRange(field.Choices);
                if (_extraChoices != null && _extraChoices.TryGetValue(field.Tag, out var extra) && extra != null)
                    foreach (var c in extra)
                        if (!string.IsNullOrWhiteSpace(c) && !values.Contains(c)) values.Add(c);
                foreach (var v in values)
                {
                    var item = new ComboBoxItem { Content = CabrilloHeader.ChoiceLabel(field.Tag, v), Tag = v };
                    combo.Items.Add(item);
                    if (v == val) combo.SelectedItem = item;
                }
                if (combo.SelectedItem == null) combo.SelectedIndex = 0;
                combo.SelectionChanged += (s, e) => Validate();
                input = combo;
            }
            else
            {
                var tb = new TextBox
                {
                    FontSize = 16,
                    Text = val,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    IsReadOnly = field.ReadOnly,
                    IsTabStop = !field.ReadOnly
                };
                if (field.ReadOnly)
                    tb.Background = ThemeManager.Brush("PanelBg");   // signal it's not editable here
                if (field.Input == CabrilloFieldInput.MultiLineText)
                {
                    tb.AcceptsReturn = true;
                    tb.TextWrapping = TextWrapping.Wrap;
                    tb.Height = 48;
                    tb.VerticalContentAlignment = VerticalAlignment.Top;   // multiline: text starts at the top
                    tb.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                }
                else
                {
                    tb.Height = 26;
                }
                tb.TextChanged += (s, e) => Validate();
                input = tb;
            }
            input.Margin = new Thickness(0, 3, 0, 3);
            Grid.SetColumn(input, 1);
            grid.Children.Add(input);
            _inputs[field.Tag] = input;

            // A hint goes under its box, in the box's own fixed-width column, so it wraps there
            // instead of widening the form.
            if (!string.IsNullOrWhiteSpace(field.Hint))
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var hint = new TextBlock
                {
                    Text = field.Hint,
                    FontSize = 16,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = ThemeManager.Brush("MutedTextBrush"),
                    Margin = new Thickness(0, 0, 0, 4)
                };
                Grid.SetRow(hint, 1);
                Grid.SetColumn(hint, 1);
                grid.Children.Add(hint);
            }
            return grid;
        }

        private static string ReadValue(FrameworkElement input)
        {
            if (input is ComboBox cb) return (((cb.SelectedItem as ComboBoxItem)?.Tag as string) ?? string.Empty).Trim();
            if (input is TextBox tb) return (tb.Text ?? string.Empty).Trim();
            return string.Empty;
        }

        private List<string> MissingRequired()
        {
            var missing = new List<string>();
            foreach (var tag in _required)
            {
                // Read-only fields (e.g. CALLSIGN) are owned elsewhere and can't be filled here, so
                // they don't block this window; they're validated at the export site instead.
                var f = CabrilloHeader.Find(tag);
                if (f != null && f.ReadOnly) continue;
                if (_inputs.TryGetValue(tag, out var input) && string.IsNullOrWhiteSpace(ReadValue(input)))
                    missing.Add(f?.Label ?? tag);
            }
            return missing;
        }

        private void Validate()
        {
            var missing = MissingRequired();
            if (_exportMode)
            {
                Btn_Primary.IsEnabled = missing.Count == 0;
                TB_Validation.Text = missing.Count == 0 ? string.Empty : "Required: " + string.Join(", ", missing);
            }
            else
            {
                Btn_Primary.IsEnabled = true;   // setup: saving partial info is fine
                TB_Validation.Text = missing.Count == 0
                    ? string.Empty
                    : missing.Count + " required field" + (missing.Count == 1 ? "" : "s") + " still empty";
            }
        }

        private Dictionary<string, string> Collect()
        {
            var d = new Dictionary<string, string>();
            foreach (var kv in _inputs) d[kv.Key] = ReadValue(kv.Value);
            return d;
        }

        private void Btn_Primary_Click(object sender, RoutedEventArgs e)
        {
            if (_exportMode && MissingRequired().Count > 0) return;   // guard; button is normally disabled
            Values = Collect();
            Completed = true;
            Close();
        }

        private void Btn_Secondary_Click(object sender, RoutedEventArgs e)
        {
            Values = Collect();          // keep whatever was entered, even on Skip/Cancel
            Completed = false;
            Skipped = !_exportMode;
            Close();
        }
    }
}
