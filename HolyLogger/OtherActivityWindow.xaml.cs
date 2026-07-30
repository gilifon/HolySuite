using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace HolyLogger
{
    // Collects a programme name and a reference for any activity programme that ADIF does NOT give a
    // field of its own. The pair is stored in the standard SIG / SIG_INFO fields.
    //
    // Two boxes rather than one because a single box would let everyone write the programme their own
    // way - "WCA", "Castles", "castle" - and nothing could ever group them again. That is exactly the
    // reason the standard splits them, and it is why this window exists instead of a fifth text box on
    // the main form (where, measured, there is no room for one anyway).
    public partial class OtherActivityWindow : Window
    {
        // The well-known names, with what each one collects. Not a closed list: the box is editable,
        // because a programme founded next year must be usable without a new version of HolyLogger.
        private static readonly KeyValuePair<string, string>[] KnownProgrammes =
        {
            new KeyValuePair<string, string>("WCA",    "World Castles Award - castles and fortresses"),
            new KeyValuePair<string, string>("COTA",   "Castles on the Air"),
            new KeyValuePair<string, string>("MOTA",   "Mills on the Air"),
            new KeyValuePair<string, string>("GMA",    "Global Mountain Activity"),
            new KeyValuePair<string, string>("ARLHS",  "Amateur Radio Lighthouse Society"),
            new KeyValuePair<string, string>("WLOTA",  "World Lighthouses on the Air"),
            new KeyValuePair<string, string>("ILLW",   "International Lighthouse and Lightship Weekend"),
            new KeyValuePair<string, string>("BOTA",   "Beaches on the Air"),
        };

        public string Programme { get; private set; }
        public string Reference { get; private set; }

        public OtherActivityWindow(string programme, string reference)
        {
            InitializeComponent();

            foreach (var p in KnownProgrammes) CB_Programme.Items.Add(p.Key);
            CB_Programme.Text = (programme ?? "").Trim();
            TB_Reference.Text = (reference ?? "").Trim();

            CB_Programme.KeyUp += (s, e) => ShowProgrammeHint();
            CB_Programme.SelectionChanged += (s, e) => Dispatcher.BeginInvoke(new Action(ShowProgrammeHint));
            ShowProgrammeHint();

            Loaded += (s, e) => CB_Programme.Focus();
        }

        // Spells out what the typed short name means, so nobody has to remember that WCA is castles.
        private void ShowProgrammeHint()
        {
            string typed = (CB_Programme.Text ?? "").Trim();
            TB_ProgrammeHint.Text = "";
            foreach (var p in KnownProgrammes)
            {
                if (string.Equals(p.Key, typed, StringComparison.OrdinalIgnoreCase)) { TB_ProgrammeHint.Text = p.Value; break; }
            }
        }

        private void Btn_Ok_Click(object sender, RoutedEventArgs e)
        {
            string programme = (CB_Programme.Text ?? "").Trim().ToUpperInvariant();
            string reference = (TB_Reference.Text ?? "").Trim();

            // One without the other is half a record: a reference with no programme cannot be looked up,
            // and a programme with no reference says nothing about where the station was.
            if (programme.Length == 0 && reference.Length == 0)
            {
                Programme = null; Reference = null;
                DialogResult = true;
                return;
            }
            if (programme.Length == 0 || reference.Length == 0)
            {
                TB_Warning.Text = programme.Length == 0
                    ? "Say which programme this reference belongs to, or press Clear this QSO."
                    : "Type the reference within " + programme + ", or press Clear this QSO.";
                TB_Warning.Visibility = Visibility.Visible;
                (programme.Length == 0 ? (Control)CB_Programme : TB_Reference).Focus();
                return;
            }

            Programme = programme;
            Reference = reference;
            DialogResult = true;
        }

        private void Btn_Clear_Click(object sender, RoutedEventArgs e)
        {
            Programme = null;
            Reference = null;
            DialogResult = true;
        }
    }
}
