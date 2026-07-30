using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace HolyLogger
{
    // Collects a program name and a reference for any activity program that ADIF does NOT give a
    // field of its own. The pair is stored in the standard SIG / SIG_INFO fields.
    //
    // Two boxes rather than one because a single box would let everyone write the program their own
    // way - "WCA", "Castles", "castle" - and nothing could ever group them again. That is exactly the
    // reason the standard splits them, and it is why this window exists instead of a fifth text box on
    // the main form (where, measured, there is no room for one anyway).
    public partial class OtherActivityWindow : Window
    {
        // The well-known names, with what each one collects. Not a closed list: the box is editable,
        // because a program founded next year must be usable without a new version of HolyLogger.
        private static readonly KeyValuePair<string, string>[] KnownPrograms =
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

        // A program's short name: what everyone writes on the air and in a log. Deliberately permissive
        // about WHICH letters - a program founded next year gets to pick its own name - and strict only
        // about it being one short token.
        private static readonly Regex ShortName = new Regex(@"^[A-Z0-9][A-Z0-9\-]{1,14}$", RegexOptions.Compiled);

        // The same list, for the QSO editor's Program box. One list in one place: two copies would
        // drift the moment a program is added to whichever window someone happened to be editing.
        public static IList<KeyValuePair<string, string>> Known
        {
            get { return Array.AsReadOnly(KnownPrograms); }
        }

        // What a short name stands for, or an empty string when it is not one HolyLogger knows.
        public static string DescriptionOf(string name)
        {
            string typed = (name ?? string.Empty).Trim();
            foreach (var p in KnownPrograms)
                if (string.Equals(p.Key, typed, StringComparison.OrdinalIgnoreCase)) return p.Value;
            return string.Empty;
        }

        public string Program { get; private set; }
        public string Reference { get; private set; }

        public OtherActivityWindow(string program, string reference)
        {
            InitializeComponent();

            foreach (var p in KnownPrograms) CB_Program.Items.Add(p.Key);
            CB_Program.Text = (program ?? "").Trim();
            TB_Reference.Text = (reference ?? "").Trim();

            CB_Program.KeyUp += (s, e) => ShowProgramHint();
            CB_Program.SelectionChanged += (s, e) => Dispatcher.BeginInvoke(new Action(ShowProgramHint));
            ShowProgramHint();

            Loaded += (s, e) => CB_Program.Focus();
        }

        // Spells out what the typed short name means, so nobody has to remember that WCA is castles.
        private void ShowProgramHint()
        {
            string typed = (CB_Program.Text ?? "").Trim();
            TB_ProgramHint.Text = "";
            foreach (var p in KnownPrograms)
            {
                if (string.Equals(p.Key, typed, StringComparison.OrdinalIgnoreCase)) { TB_ProgramHint.Text = p.Value; break; }
            }
        }

        private void Btn_Ok_Click(object sender, RoutedEventArgs e)
        {
            string program = (CB_Program.Text ?? "").Trim().ToUpperInvariant();
            string reference = (TB_Reference.Text ?? "").Trim();

            // One without the other is half a record: a reference with no program cannot be looked up,
            // and a program with no reference says nothing about where the station was.
            if (program.Length == 0 && reference.Length == 0)
            {
                Program = null; Reference = null;
                DialogResult = true;
                return;
            }
            if (program.Length == 0 || reference.Length == 0)
            {
                TB_Warning.Text = program.Length == 0
                    ? "Say which program this reference belongs to, or press Clear this QSO."
                    : "Type the reference within " + program + ", or press Clear this QSO.";
                TB_Warning.Visibility = Visibility.Visible;
                (program.Length == 0 ? (Control)CB_Program : TB_Reference).Focus();
                return;
            }

            // The program NAME is checked; the reference inside it cannot be. There is no standard list
            // of programs and each one numbers its references its own way, so there is no format to
            // check a reference against - only ADIF's four named programs have one, and those have
            // their own boxes on the main form.
            //
            // What is checked is that the name is a short token, because its whole job is to be the
            // same word every time: "WCA" written once as "WCA" and once as "World Castles Award" can
            // never be grouped again, which is the reason the standard keeps the name and the reference
            // in two separate fields in the first place.
            if (!ShortName.IsMatch(program))
            {
                TB_Warning.Text = "Use the program's SHORT name - WCA, MOTA, ARLHS - not a sentence. "
                    + "Letters, digits and hyphens, up to 15 characters. The full name of the ones "
                    + "HolyLogger knows is shown beside the box.";
                TB_Warning.Visibility = Visibility.Visible;
                CB_Program.Focus();
                return;
            }

            TB_Warning.Visibility = Visibility.Collapsed;
            Program = program;
            Reference = reference;
            DialogResult = true;
        }

        private void Btn_Clear_Click(object sender, RoutedEventArgs e)
        {
            Program = null;
            Reference = null;
            DialogResult = true;
        }
    }
}
