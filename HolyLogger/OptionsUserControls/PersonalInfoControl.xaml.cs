using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HolyLogger.Contests;

namespace HolyLogger.OptionsUserControls
{
    // Options page for the shared personal/station Cabrillo header fields. Built dynamically from the
    // CabrilloHeader catalog (personal scope only); values persist via ContestHeaderStore and are
    // reused for every contest. CALLSIGN is read-only here — it is owned by the main-window box.
    public partial class PersonalInfoControl : UserControl
    {
        private readonly Dictionary<string, TextBox> _inputs = new Dictionary<string, TextBox>();
        private bool _loading;

        public PersonalInfoControl()
        {
            InitializeComponent();
            BuildFields();
        }

        // Re-read values from the store each time the panel is shown (e.g. the station callsign may
        // have changed in the main window since Options was opened).
        public void Reload()
        {
            _loading = true;
            var current = ContestHeaderStore.Load(null);
            foreach (var kv in _inputs)
                kv.Value.Text = current.TryGetValue(kv.Key, out var v) ? (v ?? string.Empty) : string.Empty;
            _loading = false;
        }

        private void BuildFields()
        {
            _loading = true;
            var current = ContestHeaderStore.Load(null);

            foreach (var field in CabrilloHeader.Catalog)
            {
                if (field.Scope != CabrilloFieldScope.Personal) continue;
                current.TryGetValue(field.Tag, out string val);

                SP_Fields.Children.Add(new TextBlock
                {
                    Text = field.Label,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 8, 0, 2)
                });

                var tb = new TextBox
                {
                    FontSize = 13,
                    Text = val ?? string.Empty,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Width = 330,
                    IsReadOnly = field.ReadOnly,
                    IsTabStop = !field.ReadOnly
                };
                if (field.ReadOnly)
                    tb.Background = Brushes.WhiteSmoke;   // reference only; edited in the main window
                if (field.Input == CabrilloFieldInput.MultiLineText)
                {
                    tb.AcceptsReturn = true;
                    tb.TextWrapping = TextWrapping.Wrap;
                    tb.Height = 48;
                    tb.Width = 400;
                    tb.VerticalContentAlignment = VerticalAlignment.Top;   // multiline: text starts at the top
                    tb.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                }
                else
                {
                    tb.Height = 24;
                }
                tb.LostFocus += (s, e) => SaveAll();
                _inputs[field.Tag] = tb;
                SP_Fields.Children.Add(tb);

                if (!string.IsNullOrWhiteSpace(field.Hint))
                {
                    SP_Fields.Children.Add(new TextBlock
                    {
                        Text = field.Hint,
                        FontSize = 11,
                        Foreground = Brushes.Gray,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 1, 0, 0)
                    });
                }
            }
            _loading = false;
        }

        private void SaveAll()
        {
            if (_loading) return;
            var values = new Dictionary<string, string>();
            foreach (var kv in _inputs) values[kv.Key] = (kv.Value.Text ?? string.Empty).Trim();
            ContestHeaderStore.Save(null, values);
        }
    }
}
