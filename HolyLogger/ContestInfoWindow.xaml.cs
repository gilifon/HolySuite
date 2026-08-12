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

            BuildFields(current ?? new Dictionary<string, string>());

            Btn_Primary.Content   = exportMode ? "Save & Export" : "Save";
            Btn_Secondary.Content = exportMode ? "Cancel" : "Skip";
            Validate();
        }

        private void BuildFields(IDictionary<string, string> current)
        {
            CabrilloFieldScope? lastScope = null;
            foreach (var field in CabrilloHeader.Catalog)
            {
                if (lastScope != field.Scope)
                {
                    lastScope = field.Scope;
                    SP_Fields.Children.Add(new TextBlock
                    {
                        Text = field.Scope == CabrilloFieldScope.Personal ? "Station & operator" : "This contest",
                        FontSize = 16,
                        FontWeight = FontWeights.Bold,
                        Foreground = ThemeManager.Brush("AccentBrush"),
                        Margin = new Thickness(0, 12, 0, 4)
                    });
                }

                bool required = _required.Contains(field.Tag);
                current.TryGetValue(field.Tag, out string val);
                val = val ?? string.Empty;

                var label = new TextBlock
                {
                    FontSize = 16,
                    Margin = new Thickness(0, 6, 0, 2),
                    Foreground = ThemeManager.Brush("TextBrush")
                };
                label.Inlines.Add(new Run(field.Label));
                if (required)
                    label.Inlines.Add(new Run("  *") { Foreground = Brushes.Red, FontWeight = FontWeights.Bold });
                SP_Fields.Children.Add(label);

                FrameworkElement input;
                if (field.Input == CabrilloFieldInput.Choice)
                {
                    var combo = new ComboBox { FontSize = 16, Height = 26, IsEnabled = !field.ReadOnly };
                    combo.Items.Add(string.Empty);   // blank = not specified
                    foreach (var c in field.Choices) combo.Items.Add(c);
                    combo.SelectedItem = combo.Items.Contains(val) ? val : string.Empty;
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
                _inputs[field.Tag] = input;
                SP_Fields.Children.Add(input);

                if (!string.IsNullOrWhiteSpace(field.Hint))
                {
                    SP_Fields.Children.Add(new TextBlock
                    {
                        Text = field.Hint,
                        FontSize = 16,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = ThemeManager.Brush("MutedTextBrush"),
                        Margin = new Thickness(0, 1, 0, 0)
                    });
                }
            }
        }

        private static string ReadValue(FrameworkElement input)
        {
            if (input is ComboBox cb) return ((cb.SelectedItem as string) ?? cb.Text ?? string.Empty).Trim();
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
