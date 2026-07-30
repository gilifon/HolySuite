using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using DXCCManager;
using HolyParser;

namespace HolyLogger
{
    // Checks a whole log and offers corrections, one tick at a time.
    //
    // A logged QSO keeps the country that was worked out when it was saved, and that answer can be years
    // out of date: K9W was logged as United States in 2013 because its prefix says so, while Club Log
    // records that for those two weeks K9W was Wake Island, and the QSO's own Name field says "2013 WAKE
    // ISLAND COMMEMORATIVE". Nothing in the program ever re-asked the question. This window asks it for
    // every QSO, on the QSO's own date, and shows what it finds.
    //
    // Two rules govern everything here:
    //   * Nothing is written until the operator ticks a row and presses Apply, and the log is copied to a
    //     .bak first.
    //   * A finding that cannot be corrected automatically (a callsign Club Log lists as never valid, an
    //     impossible date) is reported as FYI with no tick box, rather than guessed at.
    public partial class LogVerifierWindow : Window
    {
        // One thing found wrong with one QSO. Apply is the only mutable part, so INotifyPropertyChanged
        // exists purely to keep the button's count honest.
        public class Finding : INotifyPropertyChanged
        {
            public QSO Qso;
            public string Field;          // which QSO field the fix would write
            public string NewValue;       // the value to write (Field-specific)
            public int NewCq, NewItu;     // zones that travel with a country correction (0 = leave alone)
            public string NewContinent;

            public string Call { get; set; }
            public string Time { get; set; }
            public string DateText { get; set; }
            public string Problem { get; set; }
            public string Current { get; set; }
            public string Suggested { get; set; }
            public string Evidence { get; set; }
            public bool Fixable { get; set; }

            private bool apply;
            public bool Apply
            {
                get { return apply; }
                set
                {
                    if (apply == value) return;
                    apply = value && Fixable;
                    if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("Apply"));
                    if (ApplyChanged != null) ApplyChanged();
                }
            }

            public Action ApplyChanged;
            public event PropertyChangedEventHandler PropertyChanged;
        }

        private readonly List<QSO> _qsos;
        private readonly string _logName;
        private readonly ObservableCollection<Finding> _findings = new ObservableCollection<Finding>();

        // A callsign may legitimately hold letters, digits and strokes - anything else is damage, and the
        // log has at least one row that arrived from an import with rubbish bytes in front of the call.
        private static readonly Regex LegalCallChars = new Regex("^[A-Z0-9/]+$", RegexOptions.Compiled);

        // Maidenhead: field, square, and optionally subsquare and extended square.
        private static readonly Regex LegalLocator =
            new Regex("^[A-R]{2}[0-9]{2}([A-X]{2}([0-9]{2})?)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // A Holyland square (one letter, two digits, two letters - K07YZ) is not a Maidenhead locator, but
        // Holyland contest QSOs in this log do carry one in the DX locator field. It is deliberate data,
        // not damage, so it is left in peace.
        private static readonly Regex HolylandSquare =
            new Regex("^[A-Z][0-9]{2}[A-Z]{2}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static bool IsCallChar(char c)
        {
            return (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '/';
        }

        public LogVerifierWindow(IEnumerable<QSO> qsos, string logName = null)
        {
            InitializeComponent();
            _qsos = (qsos ?? Enumerable.Empty<QSO>()).Where(q => q != null).ToList();
            _logName = string.IsNullOrWhiteSpace(logName) ? "" : logName.Trim();
            Title = string.IsNullOrEmpty(_logName) ? "Verify Log" : "Verify Log — " + _logName;
            FindingsGrid.ItemsSource = _findings;
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            TB_Header.Text = "Checking " + _qsos.Count.ToString("N0") + " QSOs…";
            TB_Summary.Text = "working…";
            await RunCheck();
        }

        private async Task RunCheck()
        {
            _findings.Clear();
            Btn_Apply.IsEnabled = false;

            List<QSO> snapshot = _qsos;
            // The country lookup parses two databases and every QSO is resolved twice, so the whole scan
            // runs off the UI thread - a 40,000-QSO log would otherwise freeze the window.
            List<Finding> found = await Task.Run(() => Scan(snapshot));

            foreach (Finding f in found)
            {
                f.ApplyChanged = UpdateApplyButton;
                _findings.Add(f);
            }

            int fixable = found.Count(f => f.Fixable);
            TB_Header.Text = found.Count == 0
                ? "No problems found in " + _qsos.Count.ToString("N0") + " QSOs."
                : found.Count.ToString("N0") + " problem" + (found.Count == 1 ? "" : "s")
                  + " found in " + _qsos.Count.ToString("N0") + " QSOs";
            TB_Summary.Text = found.Count == 0
                ? "Nothing to correct."
                : fixable.ToString("N0") + " can be corrected automatically, "
                  + (found.Count - fixable).ToString("N0") + " are for information only.";
            UpdateApplyButton();
        }

        private static List<Finding> Scan(List<QSO> qsos)
        {
            var findings = new List<Finding>();
            CountryLookup lookup = CountryLookup.Shared;
            DateTime today = DateTime.UtcNow.Date.AddDays(1);   // tomorrow: a QSO dated later cannot exist

            foreach (QSO q in qsos)
            {
                string call = (q.DXCall ?? string.Empty).Trim();
                string dateRaw = (q.Date ?? string.Empty).Trim();

                // --- the callsign itself -------------------------------------------------------------
                if (call.Length == 0)
                {
                    findings.Add(Fyi(q, "No callsign", "(empty)", "cannot be guessed", "the log"));
                    continue;   // nothing else can be judged without a callsign
                }

                if (!LegalCallChars.IsMatch(call.ToUpperInvariant()))
                {
                    // Junk at the front or the back is padding that arrived with an import and can be
                    // trimmed off with confidence. Junk in the MIDDLE is a note the operator squeezed
                    // into the callsign - "N1WON/P(KP2)" means he was in KP2 - and deleting the
                    // brackets would fuse it into nonsense, so that is only reported.
                    string trimmed = call.ToUpperInvariant().Trim();
                    while (trimmed.Length > 0 && !IsCallChar(trimmed[0])) trimmed = trimmed.Substring(1);
                    while (trimmed.Length > 0 && !IsCallChar(trimmed[trimmed.Length - 1]))
                        trimmed = trimmed.Substring(0, trimmed.Length - 1);

                    if (trimmed.Length >= 3 && LegalCallChars.IsMatch(trimmed))
                    {
                        Finding f = New(q, "Damaged callsign", call, trimmed, "illegal characters");
                        f.Field = "DXCall";
                        f.NewValue = trimmed;
                        f.Fixable = true;
                        findings.Add(f);
                    }
                    else
                    {
                        findings.Add(Fyi(q, "Callsign holds odd characters", call,
                                         "check what was really worked", "the log"));
                    }
                }

                // --- date and time ------------------------------------------------------------------
                DateTime when;
                bool dateOk = DateTime.TryParseExact(dateRaw, "yyyyMMdd", CultureInfo.InvariantCulture,
                                  DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out when);
                if (!dateOk)
                    findings.Add(Fyi(q, "Unreadable date", dateRaw.Length == 0 ? "(empty)" : dateRaw,
                                     "a real date (YYYYMMDD)", "the log"));
                else if (when >= today)
                    findings.Add(Fyi(q, "Date in the future", when.ToString("dd-MM-yyyy"),
                                     "a date that has happened", "the log"));
                else if (when.Year < 1920)
                    findings.Add(Fyi(q, "Impossible date", when.ToString("dd-MM-yyyy"),
                                     "amateur radio is not that old", "the log"));

                string timeRaw = (q.Time ?? string.Empty).Trim();
                if (timeRaw.Length >= 4)
                {
                    int hh, mm;
                    bool timeOk = int.TryParse(timeRaw.Substring(0, 2), out hh)
                               && int.TryParse(timeRaw.Substring(2, 2), out mm)
                               && hh <= 23 && mm <= 59;
                    if (!timeOk)
                        findings.Add(Fyi(q, "Impossible time", timeRaw, "00:00 to 23:59", "the log"));
                }

                // --- band against frequency ---------------------------------------------------------
                string freq = (q.Freq ?? string.Empty).Trim();
                string band = (q.Band ?? string.Empty).Trim();
                if (freq.Length > 0)
                {
                    string mhz = HolyLogParser.NormalizeFreqToMhz(freq);
                    string fromFreq = string.IsNullOrWhiteSpace(mhz) ? "" : HolyLogParser.convertFreqToBand(mhz);
                    if (!string.IsNullOrWhiteSpace(fromFreq) && band.Length > 0
                        && !string.Equals(fromFreq, band, StringComparison.OrdinalIgnoreCase))
                    {
                        Finding f = New(q, "Band does not match the frequency",
                                        band + "  (" + freq + ")", fromFreq, "the frequency logged");
                        f.Field = "Band";
                        f.NewValue = fromFreq;
                        f.Fixable = true;
                        findings.Add(f);
                    }
                }
                if (band.Length == 0)
                {
                    // An empty band is repairable whenever the frequency is there to derive it from.
                    string mhz = HolyLogParser.NormalizeFreqToMhz(freq);
                    string fromFreq = string.IsNullOrWhiteSpace(mhz) ? "" : HolyLogParser.convertFreqToBand(mhz);
                    if (!string.IsNullOrWhiteSpace(fromFreq))
                    {
                        Finding f = New(q, "No band", "(empty)   (" + freq + ")", fromFreq,
                                        "the frequency logged");
                        f.Field = "Band";
                        f.NewValue = fromFreq;
                        f.Fixable = true;
                        findings.Add(f);
                    }
                    else findings.Add(Fyi(q, "No band", "(empty)", "a band", "the log"));
                }
                if (string.IsNullOrWhiteSpace(q.Mode))
                    findings.Add(Fyi(q, "No mode", "(empty)", "a mode", "the log"));

                // --- the worked station's grid ------------------------------------------------------
                string grid = (q.DXLocator ?? string.Empty).Trim();
                if (grid.Length > 0 && !LegalLocator.IsMatch(grid) && !HolylandSquare.IsMatch(grid))
                    findings.Add(Fyi(q, "Grid is not a locator", grid, "e.g. KM72OR", "the log"));

                // --- the country, on the QSO's own date ---------------------------------------------
                if (!dateOk) continue;

                DXCC dated;
                try { dated = lookup.Resolve(call, when); }
                catch { continue; }
                if (dated == null) continue;

                if (dated.InvalidOperation)
                    findings.Add(Fyi(q, "Club Log lists this as never valid", call,
                                     "does not count for awards", "Club Log"));

                if (string.IsNullOrEmpty(dated.Name) || dated.Name == "Unknown") continue;

                string storedCountry = (q.Country ?? string.Empty).Trim();
                string storedCont = (q.Continent ?? string.Empty).Trim();

                if (storedCountry.Length > 0 &&
                    !string.Equals(storedCountry, dated.Name, StringComparison.OrdinalIgnoreCase))
                {
                    Finding f = New(q, "Wrong country",
                                    storedCountry + ZoneSuffix(q.CQZone, q.ITUZone),
                                    dated.Name + ZoneSuffix(
                                        dated.CqZone > 0 ? dated.CqZone.ToString() : q.CQZone,
                                        dated.ItuZone > 0 ? dated.ItuZone.ToString() : q.ITUZone),
                                    dated.ResolvedBy);
                    f.Field = "Country";
                    f.NewValue = dated.Name;
                    f.NewContinent = dated.Continent != null && dated.Continent != "XX" ? dated.Continent : null;
                    f.NewCq = dated.CqZone;
                    f.NewItu = dated.ItuZone;
                    f.Fixable = true;
                    findings.Add(f);
                }
                else if (storedCountry.Length == 0)
                {
                    Finding f = New(q, "No country", "(empty)", dated.Name, dated.ResolvedBy);
                    f.Field = "Country";
                    f.NewValue = dated.Name;
                    f.NewContinent = dated.Continent != null && dated.Continent != "XX" ? dated.Continent : null;
                    f.NewCq = dated.CqZone;
                    f.NewItu = dated.ItuZone;
                    f.Fixable = true;
                    findings.Add(f);
                }
                else if (!string.IsNullOrEmpty(dated.Continent) && dated.Continent != "XX"
                         && !string.Equals(storedCont, dated.Continent, StringComparison.OrdinalIgnoreCase))
                {
                    // Country agrees, so only the continent is adrift - usually an old row that was
                    // saved before the field was filled in reliably.
                    Finding f = New(q, "Wrong continent",
                                    storedCont.Length == 0 ? "(empty)" : storedCont,
                                    dated.Continent, dated.ResolvedBy);
                    f.Field = "Continent";
                    f.NewValue = dated.Continent;
                    f.Fixable = true;
                    findings.Add(f);
                }
            }

            // Worst first: a wrong country matters more than a missing continent, and within a kind the
            // oldest QSO first so a run through the list reads chronologically.
            return findings
                .OrderBy(f => Rank(f.Problem))
                .ThenBy(f => f.Qso != null ? (f.Qso.Date ?? "") : "")
                .ThenBy(f => f.Qso != null ? (f.Qso.Time ?? "") : "")
                .ToList();
        }

        private static int Rank(string problem)
        {
            if (problem.StartsWith("Club Log lists")) return 0;
            if (problem == "Wrong country") return 1;
            if (problem == "No country") return 2;
            if (problem == "Damaged callsign") return 3;
            if (problem.StartsWith("Band")) return 4;
            if (problem == "Wrong continent") return 5;
            return 6;
        }

        private static string ZoneSuffix(string cq, string itu)
        {
            cq = (cq ?? string.Empty).Trim();
            itu = (itu ?? string.Empty).Trim();
            if (cq.Length == 0 && itu.Length == 0) return string.Empty;
            return "   (CQ " + (cq.Length == 0 ? "-" : cq) + ", ITU " + (itu.Length == 0 ? "-" : itu) + ")";
        }

        private static Finding New(QSO q, string problem, string current, string suggested, string evidence)
        {
            return new Finding
            {
                Qso = q,
                Call = (q.DXCall ?? string.Empty).Trim(),
                Time = FormatTime(q.Time),
                DateText = FormatDate(q.Date),
                Problem = problem,
                Current = current,
                Suggested = suggested,
                Evidence = evidence
            };
        }

        private static Finding Fyi(QSO q, string problem, string current, string note, string evidence)
        {
            Finding f = New(q, problem, current, note, evidence);
            f.Fixable = false;
            f.Suggested = "FYI — " + note;
            return f;
        }

        private static string FormatDate(string yyyymmdd)
        {
            string s = (yyyymmdd ?? string.Empty).Trim();
            if (s.Length != 8) return s;
            return s.Substring(6, 2) + "-" + s.Substring(4, 2) + "-" + s.Substring(0, 4);
        }

        private static string FormatTime(string hhmmss)
        {
            string s = (hhmmss ?? string.Empty).Trim();
            if (s.Length < 4) return s;
            return s.Substring(0, 2) + ":" + s.Substring(2, 2);
        }

        private void UpdateApplyButton()
        {
            int n = _findings.Count(f => f.Apply);
            Btn_Apply.IsEnabled = n > 0;
            Btn_Apply.Content = n == 0
                ? "Apply the ticked corrections"
                : "Apply " + n.ToString("N0") + " correction" + (n == 1 ? "" : "s");
        }

        private void Btn_All_Click(object sender, RoutedEventArgs e)
        {
            foreach (Finding f in _findings) f.Apply = f.Fixable;
            FindingsGrid.Items.Refresh();
            UpdateApplyButton();
        }

        private void Btn_None_Click(object sender, RoutedEventArgs e)
        {
            foreach (Finding f in _findings) f.Apply = false;
            FindingsGrid.Items.Refresh();
            UpdateApplyButton();
        }

        private async void Btn_Apply_Click(object sender, RoutedEventArgs e)
        {
            // Committing an edit in progress first, or the tick the operator has just clicked is not yet
            // in the bound object.
            FindingsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);

            List<Finding> chosen = _findings.Where(f => f.Apply && f.Fixable && f.Qso != null).ToList();
            if (chosen.Count == 0) return;

            var dal = DataAccess.GetInstance();
            if (dal == null)
            {
                MessageBox.Show(this, "The log database is not open.", "Verify Log",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (MessageBox.Show(this,
                    chosen.Count.ToString("N0") + " QSO correction" + (chosen.Count == 1 ? "" : "s")
                    + " will be written to the log.\n\nA copy of the log is saved first, so this can be undone "
                    + "by restoring that copy (Tools > Backups & Restore).\n\nApply now?",
                    "Verify Log", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;

            string backup = SaveBackup(dal);
            if (backup == null &&
                MessageBox.Show(this, "The safety copy of the log could not be written.\n\nApply the "
                    + "corrections anyway?", "Verify Log",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                return;

            Btn_Apply.IsEnabled = false;
            TB_Summary.Text = "applying…";

            int written = 0;
            try
            {
                // Several findings can belong to one QSO (a wrong country and a wrong band), so the
                // changes are gathered per QSO and the row is written once.
                foreach (var group in chosen.GroupBy(f => f.Qso))
                {
                    QSO qso = group.Key;
                    foreach (Finding f in group) ApplyTo(qso, f);
                    await Task.Run(() => dal.Update(qso));
                    written++;
                }
            }
            catch (Exception ex)
            {
                Log.Swallow(ex);
                MessageBox.Show(this, "Something went wrong while writing the corrections:\n\n" + ex.Message
                    + (backup != null ? "\n\nThe log as it was before is in:\n" + backup : ""),
                    "Verify Log", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            MessageBox.Show(this, written.ToString("N0") + " QSO" + (written == 1 ? "" : "s") + " corrected."
                + (backup != null ? "\n\nThe log as it was before is in:\n" + Path.GetFileName(backup) : "")
                + "\n\nClose and reopen the log window to see the new values.",
                "Verify Log", MessageBoxButton.OK, MessageBoxImage.Information);

            // Re-check, so what is left on screen is what is still wrong.
            await RunCheck();
        }

        private static void ApplyTo(QSO qso, Finding f)
        {
            switch (f.Field)
            {
                case "DXCall":
                    qso.DXCall = f.NewValue;
                    break;
                case "Band":
                    qso.Band = f.NewValue;
                    break;
                case "Continent":
                    qso.Continent = f.NewValue;
                    break;
                case "Country":
                    qso.Country = f.NewValue;
                    if (!string.IsNullOrEmpty(f.NewContinent)) qso.Continent = f.NewContinent;
                    // The zones belong to the entity, so a country correction carries them along - a
                    // Wake Island QSO cannot keep the CQ zone of the United States.
                    if (f.NewCq > 0) qso.CQZone = f.NewCq.ToString();
                    if (f.NewItu > 0) qso.ITUZone = f.NewItu.ToString();
                    break;
            }
        }

        // A plain file copy of the log, named like the program's other safety copies so it shows up in
        // Backups & Restore. Returns the path, or null when it could not be made.
        private static string SaveBackup(DataAccess dal)
        {
            try
            {
                string path = dal.DbPath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                string backup = path + ".pre-verify-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak";
                File.Copy(path, backup, false);
                return backup;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return null; }
        }

        private void Btn_Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
