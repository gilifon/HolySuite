using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HolyLogger.Contests;

namespace HolyLogger.OptionsUserControls
{
    // Options page for the shared personal/station Cabrillo header fields. Built dynamically from the
    // CabrilloHeader catalog (personal scope only); values persist via ContestHeaderStore and are
    // reused for every contest. CALLSIGN here is the owner's own callsign, not the main-window box.
    public partial class PersonalInfoControl : UserControl
    {
        private readonly Dictionary<string, TextBox> _inputs = new Dictionary<string, TextBox>();
        private bool _loading;

        // Raised as the operator types, so the Options list can mark "Personal Info" red while the
        // name or e-mail is still empty (Send Log to IARC needs both).
        public event System.EventHandler FilledChanged;

        public bool NameOrEmailMissing
        {
            get
            {
                TextBox tb;
                return !_inputs.TryGetValue("NAME", out tb) || string.IsNullOrWhiteSpace(tb.Text)
                    || !_inputs.TryGetValue("EMAIL", out tb) || string.IsNullOrWhiteSpace(tb.Text);
            }
        }

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
            var current = ContestHeaderStore.Load(null, personalInfoPage: true);
            foreach (var kv in _inputs)
                kv.Value.Text = current.TryGetValue(kv.Key, out var v) ? (v ?? string.Empty) : string.Empty;
            _loading = false;
        }

        private void BuildFields()
        {
            _loading = true;
            var current = ContestHeaderStore.Load(null, personalInfoPage: true);

            // The catalog's personal fields, with the owner's Holyland square after the grid locator. The
            // square is not a Cabrillo field, so it is added here and not to the catalog (which would put
            // it in every contest's export window). Grid and square are the owner's saved values; the
            // main window's boxes only start from them when empty (see ContestHeaderStore).
            const string fillsEmptyBox = "Fills an empty box in the main window.";   // short: must fit the 330 column
            var fields = new List<CabrilloHeaderField>();
            foreach (var f in CabrilloHeader.Catalog)
            {
                if (f.Scope != CabrilloFieldScope.Personal) continue;
                if (f.Tag == ContestHeaderStore.CallsignTag)
                    // The owner's own callsign, editable here. The catalog's read-only "Station callsign"
                    // is the main window's box, which a loaded club log changes - not whose PC this is.
                    fields.Add(new CabrilloHeaderField(f.Tag, "Your callsign", f.Scope, f.Input, hint: fillsEmptyBox));
                else if (f.Tag == ContestHeaderStore.GridTag)
                {
                    fields.Add(new CabrilloHeaderField(f.Tag, f.Label, f.Scope, f.Input, hint: fillsEmptyBox));
                    fields.Add(new CabrilloHeaderField(ContestHeaderStore.HolylandSquareTag, "Holyland square",
                        CabrilloFieldScope.Personal, CabrilloFieldInput.Text, hint: fillsEmptyBox));
                }
                else fields.Add(f);
            }

            // Two columns, so the page is not taller than the window: who and where the station is on
            // the left, the postal address (Address and every field after it) on the right.
            StackPanel panel = SP_Fields;
            foreach (var field in fields)
            {
                if (field.Tag == "ADDRESS") panel = SP_FieldsRight;
                current.TryGetValue(field.Tag, out string val);

                panel.Children.Add(new TextBlock
                {
                    Text = field.Label,
                    FontSize = 16,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 8, 0, 2)
                });

                var tb = new TextBox
                {
                    FontSize = 16,
                    Text = val ?? string.Empty,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Width = 330,
                    IsReadOnly = field.ReadOnly,
                    IsTabStop = !field.ReadOnly
                };
                if (field.ReadOnly)
                    tb.Background = Brushes.WhiteSmoke;   // reference only; edited in the main window
                if (field.Tag == ContestHeaderStore.HolylandSquareTag || field.Tag == ContestHeaderStore.CallsignTag)
                    tb.CharacterCasing = CharacterCasing.Upper;   // as the main window's callsign and Holyland boxes
                if (field.Input == CabrilloFieldInput.MultiLineText)
                {
                    tb.AcceptsReturn = true;
                    tb.TextWrapping = TextWrapping.Wrap;
                    tb.Height = 48;
                    tb.Width = 330;   // the right-hand column is as wide as the left
                    tb.VerticalContentAlignment = VerticalAlignment.Top;   // multiline: text starts at the top
                    tb.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                }
                else
                {
                    tb.Height = 24;
                }
                tb.LostFocus += (s, e) => SaveAll();
                tb.TextChanged += (s, e) => FilledChanged?.Invoke(this, System.EventArgs.Empty);
                _inputs[field.Tag] = tb;
                panel.Children.Add(tb);

                if (!string.IsNullOrWhiteSpace(field.Hint))
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = field.Hint,
                        FontSize = 16,
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
            ContestHeaderStore.Save(null, values, personalInfoPage: true);
        }
    }
}
