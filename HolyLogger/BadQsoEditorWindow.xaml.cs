using HolyParser;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace HolyLogger
{
    internal class BadQsoViewModel : INotifyPropertyChanged
    {
        private readonly QSO _qso;
        private string _band;
        private string _mode;
        private string _comment;
        private string _freq;

        public bool IsHintRow { get; }

        // Real QSO row
        public BadQsoViewModel(QSO qso)
        {
            _qso = qso;
            _band = qso.Band;
            _mode = qso.Mode;
            _comment = qso.Comment;
            _freq = qso.Freq;
        }

        // Hint/example row
        private BadQsoViewModel(bool hint)
        {
            IsHintRow = true;
            _band = "20M";
            _mode = "SSB";
            _comment = "optional note";
            _freq = "14200";
        }

        public static BadQsoViewModel CreateHint() => new BadQsoViewModel(true);

        // id exposed as string so the hint row can show a label instead of a number
        public string id      => IsHintRow ? "ID"           : _qso.id.ToString();
        public string Date    => IsHintRow ? "20241231"      : _qso.Date;
        public string Time    => IsHintRow ? "143000"        : _qso.Time;
        public string DXCall  => IsHintRow ? "W1AW"         : _qso.DXCall;
        public string Country => IsHintRow ? "United States" : _qso.Country;
        public string Freq    => _freq;

        public string Band
        {
            get => _band;
            set
            {
                if (_band == value) return;
                _band = value;
                IsDirty = true;
                // Derive a valid standard frequency from the new band so the
                // junk Freq value is replaced immediately in the grid.
                string stdFreq = HolyLogParser.convertBandToFreq(value ?? "");
                if (!string.IsNullOrEmpty(stdFreq)) { _freq = stdFreq; OnPropertyChanged(nameof(Freq)); }
                OnPropertyChanged(nameof(Band));
                OnPropertyChanged(nameof(HasBandProblem));
                OnPropertyChanged(nameof(HasProblem));
            }
        }

        public string Mode
        {
            get => _mode;
            set
            {
                if (_mode == value) return;
                _mode = value;
                IsDirty = true;
                OnPropertyChanged(nameof(Mode));
                OnPropertyChanged(nameof(HasModeProblem));
                OnPropertyChanged(nameof(HasProblem));
            }
        }

        public string Comment
        {
            get => _comment;
            set
            {
                if (_comment == value) return;
                _comment = value;
                IsDirty = true;
                OnPropertyChanged(nameof(Comment));
            }
        }

        // Hint row never shows red cells — it already has valid example values
        public bool HasBandProblem => !IsHintRow && string.IsNullOrEmpty(_band);
        public bool HasModeProblem => !IsHintRow && string.IsNullOrEmpty(_mode);
        public bool HasProblem => HasBandProblem || HasModeProblem;
        public bool IsDirty { get; private set; }

        public QSO ApplyToQso(bool markClean = true)
        {
            if (IsHintRow) return null;
            _qso.Band = _band;
            _qso.Mode = _mode;
            _qso.Comment = _comment;
            _qso.Freq = _freq;
            if (markClean) IsDirty = false;
            return _qso;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public partial class BadQsoEditorWindow : Window
    {
        private readonly DataAccess _dal;
        private readonly ObservableCollection<BadQsoViewModel> _rows;
        public bool AnySaved { get; private set; }

        public BadQsoEditorWindow(List<QSO> badQsos, DataAccess dal)
        {
            InitializeComponent();
            _dal = dal;
            var items = new[] { BadQsoViewModel.CreateHint() }
                        .Concat(badQsos.Select(q => new BadQsoViewModel(q)));
            _rows = new ObservableCollection<BadQsoViewModel>(items);
            DG_BadQsos.ItemsSource = _rows;
        }

        // Prevent the hint row from entering edit mode
        private void DG_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
        {
            if ((e.Row.Item as BadQsoViewModel)?.IsHintRow == true)
                e.Cancel = true;
        }

        private void DG_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            // Binding updates the ViewModel via INotifyPropertyChanged; no extra logic needed.
        }

        private void BTN_SaveAll_Click(object sender, RoutedEventArgs e)
        {
            var dirty = _rows.Where(r => !r.IsHintRow && r.IsDirty).ToList();
            if (dirty.Count == 0)
            {
                TB_SaveStatus.Text = "No changes to save.";
                TB_SaveStatus.Visibility = Visibility.Visible;
                return;
            }

            int saved = 0;
            foreach (var row in dirty)
            {
                try
                {
                    var qso = row.ApplyToQso();
                    _dal.Update(qso);
                    saved++;
                }
                catch (Exception ex)
                {
                    HolyMessageBox.ShowError($"Error saving QSO {row.id}: {ex.Message}", "Save Error", this);
                }
            }

            AnySaved = saved > 0;
            TB_SaveStatus.Text = $"{saved} QSO{(saved == 1 ? "" : "s")} saved.";
            TB_SaveStatus.Visibility = Visibility.Visible;
        }

        // ── CSV export ────────────────────────────────────────────────────────

        private void BTN_ExportCsv_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Title = "Save Problem QSOs",
                Filter = "ADIF files (*.adif)|*.adif|CSV files (*.csv)|*.csv|Text files (*.txt)|*.txt",
                FilterIndex = 1,
                FileName = "problem_qsos"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var qsos = _rows.Where(r => !r.IsHintRow).Select(r => r.ApplyToQso(markClean: false)).Where(q => q != null).ToList();
                string ext = System.IO.Path.GetExtension(dlg.FileName).ToLowerInvariant();

                using (var sw = new StreamWriter(dlg.FileName, false, Encoding.UTF8))
                {
                    if (ext == ".adif")
                    {
                        sw.Write(HolyParser.Services.GenerateAdif(qsos));
                    }
                    else if (ext == ".txt")
                    {
                        sw.WriteLine("ID\tDate\tTime\tDXCall\tCountry\tFreq\tBand\tMode\tComment");
                        foreach (var row in _rows.Where(r => !r.IsHintRow))
                            sw.WriteLine($"{row.id}\t{row.Date}\t{row.Time}\t{row.DXCall}\t{row.Country}\t{row.Freq}\t{row.Band}\t{row.Mode}\t{row.Comment}");
                    }
                    else
                    {
                        sw.WriteLine("ID,Date,Time,DXCall,Country,Freq,Band,Mode,Comment");
                        foreach (var row in _rows.Where(r => !r.IsHintRow))
                            sw.WriteLine($"{row.id},{row.Date},{row.Time}," +
                                         $"{CsvQuote(row.DXCall)},{CsvQuote(row.Country)}," +
                                         $"{CsvQuote(row.Freq)},{CsvQuote(row.Band)}," +
                                         $"{CsvQuote(row.Mode)},{CsvQuote(row.Comment)}");
                    }
                }

                int count = _rows.Count(r => !r.IsHintRow);
                TB_SaveStatus.Text = $"{count} QSO{(count == 1 ? "" : "s")} exported.";
                TB_SaveStatus.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                HolyMessageBox.ShowError($"Export failed: {ex.Message}", "Export Error", this);
            }
        }

        // ── CSV import ────────────────────────────────────────────────────────

        private void BTN_ImportCsv_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Load Problem QSOs from CSV",
                Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var lines = File.ReadAllLines(dlg.FileName, Encoding.UTF8);
                if (lines.Length < 2) return;

                var headers = ParseCsvLine(lines[0]);
                int iId      = Array.IndexOf(headers, "ID");
                int iBand    = Array.IndexOf(headers, "Band");
                int iMode    = Array.IndexOf(headers, "Mode");
                int iComment = Array.IndexOf(headers, "Comment");

                if (iId < 0 || iBand < 0 || iMode < 0)
                {
                    HolyMessageBox.ShowError("The CSV must contain at least ID, Band and Mode columns.", "Import Error", this);
                    return;
                }

                var lookup = _rows.Where(r => !r.IsHintRow).ToDictionary(r => r.id);

                int updated = 0;
                for (int i = 1; i < lines.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(lines[i])) continue;
                    var cols = ParseCsvLine(lines[i]);
                    if (iId >= cols.Length) continue;

                    string idStr = cols[iId].Trim();
                    if (!lookup.TryGetValue(idStr, out var row)) continue;

                    bool changed = false;
                    if (iBand < cols.Length && !string.IsNullOrEmpty(cols[iBand]))
                    { row.Band = cols[iBand]; changed = true; }
                    if (iMode < cols.Length && !string.IsNullOrEmpty(cols[iMode]))
                    { row.Mode = cols[iMode]; changed = true; }
                    if (iComment >= 0 && iComment < cols.Length)
                        row.Comment = cols[iComment];

                    if (changed) updated++;
                }

                TB_SaveStatus.Text = $"{updated} QSO{(updated == 1 ? "" : "s")} updated from file.";
                TB_SaveStatus.Foreground = System.Windows.Media.Brushes.DarkBlue;
                TB_SaveStatus.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                HolyMessageBox.ShowError($"Import failed: {ex.Message}", "Import Error", this);
            }
        }

        // ── CSV helpers ───────────────────────────────────────────────────────

        private static string CsvQuote(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }

        // ── eQSL fill ─────────────────────────────────────────────────────────

        private void BTN_FillFromEqsl_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select eQSL inbox ADIF file",
                Filter = "ADIF / text files (*.adi;*.adif;*.txt)|*.adi;*.adif;*.txt|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var eqslRecords = ParseAdif(dlg.FileName);

                // Build lookup: "CALL_UPPER|YYYYMMDD" → list of (eqslTime HHMM, band, mode)
                var lookup = new Dictionary<string, List<(string Time, string Band, string Mode)>>(StringComparer.OrdinalIgnoreCase);
                foreach (var rec in eqslRecords)
                {
                    if (!rec.TryGetValue("CALL", out string call) || string.IsNullOrEmpty(call)) continue;
                    if (!rec.TryGetValue("QSO_DATE", out string date) || string.IsNullOrEmpty(date)) continue;
                    rec.TryGetValue("TIME_ON", out string time);
                    rec.TryGetValue("BAND", out string band);
                    rec.TryGetValue("MODE", out string mode);

                    string key = call.ToUpper() + "|" + date;
                    if (!lookup.ContainsKey(key))
                        lookup[key] = new List<(string, string, string)>();
                    lookup[key].Add((time ?? "", band ?? "", mode ?? ""));
                }

                int filled = 0;
                foreach (var row in _rows.Where(r => !r.IsHintRow && r.HasProblem))
                {
                    string key = row.DXCall.ToUpper() + "|" + row.Date;
                    if (!lookup.TryGetValue(key, out var candidates)) continue;

                    // Find best time match (±10 min)
                    (string Time, string Band, string Mode)? match = null;
                    foreach (var c in candidates)
                    {
                        if (EqslTimesMatch(row.Time, c.Time)) { match = c; break; }
                    }
                    if (match == null) continue;

                    if (row.HasBandProblem && !string.IsNullOrEmpty(match.Value.Band))
                        row.Band = match.Value.Band;
                    if (row.HasModeProblem && !string.IsNullOrEmpty(match.Value.Mode))
                        row.Mode = match.Value.Mode;

                    filled++;
                }

                TB_SaveStatus.Text = filled > 0
                    ? $"{filled} QSO{(filled == 1 ? "" : "s")} filled from eQSL — review then click Save All."
                    : "No matching eQSL records found for the problem QSOs.";
                TB_SaveStatus.Foreground = filled > 0
                    ? System.Windows.Media.Brushes.DarkGreen
                    : System.Windows.Media.Brushes.DarkOrange;
                TB_SaveStatus.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                HolyMessageBox.ShowError($"eQSL fill failed: {ex.Message}", "eQSL Fill", this);
            }
        }

        // ── LoTW fill ──────────────────────────────────────────────────────────

        private async void BTN_CheckLotw_Click(object sender, RoutedEventArgs e)
        {
            string user = Properties.Settings.Default.LotwWebUser?.Trim();
            string pass = Properties.Settings.Default.LotwWebPassword;

            if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
            {
                bool result = HolyMessageBox.ShowConfirm(
                    "LoTW web login credentials are not set.\n\nClick Yes to open Options → LoTW and enter them now.",
                    "LoTW Login Missing", HolyMsgType.Warning, this);
                if (result)
                {
                    var opts = new OptionsWindow();
                    opts.LotwControlInstance.Dal = _dal;
                    opts.Owner = this;
                    opts.LotwItem.IsSelected = true;
                    opts.ShowDialog();
                }
                return;
            }

            BTN_CheckLotw.IsEnabled = false;
            TB_SaveStatus.Text = "Downloading from LoTW…";
            TB_SaveStatus.Foreground = System.Windows.Media.Brushes.DarkBlue;
            TB_SaveStatus.Visibility = Visibility.Visible;

            try
            {
                string url = $"https://lotw.arrl.org/lotwuser/lotwreport.adi" +
                             $"?login={Uri.EscapeDataString(user)}" +
                             $"&password={Uri.EscapeDataString(pass)}" +
                             $"&qso_query=1&qso_qsl=yes";

                string adif;
                using (var http = new System.Net.Http.HttpClient())
                {
                    http.Timeout = TimeSpan.FromSeconds(60);
                    adif = await http.GetStringAsync(url);
                }

                if (adif.Contains("Invalid password") || adif.Contains("login incorrect") ||
                    adif.Contains("<Error>"))
                {
                    TB_SaveStatus.Text = "LoTW rejected the login — check your username and password.";
                    TB_SaveStatus.Foreground = System.Windows.Media.Brushes.DarkRed;
                    return;
                }

                var lotwRecords = ParseAdifString(adif);

                // Build lookup: "CALL_UPPER|YYYYMMDD" → list of (time HHMM, band, mode)
                var lookup = new Dictionary<string, List<(string Time, string Band, string Mode)>>(StringComparer.OrdinalIgnoreCase);
                foreach (var rec in lotwRecords)
                {
                    if (!rec.TryGetValue("CALL", out string call) || string.IsNullOrEmpty(call)) continue;
                    if (!rec.TryGetValue("QSO_DATE", out string date) || string.IsNullOrEmpty(date)) continue;
                    rec.TryGetValue("TIME_ON", out string time);
                    rec.TryGetValue("BAND", out string band);
                    rec.TryGetValue("MODE", out string mode);

                    string key = call.ToUpper() + "|" + date;
                    if (!lookup.ContainsKey(key))
                        lookup[key] = new List<(string, string, string)>();
                    lookup[key].Add((time ?? "", band ?? "", mode ?? ""));
                }

                int filled = 0;
                foreach (var row in _rows.Where(r => !r.IsHintRow && r.HasProblem))
                {
                    string key = row.DXCall.ToUpper() + "|" + row.Date;
                    if (!lookup.TryGetValue(key, out var candidates)) continue;

                    (string Time, string Band, string Mode)? match = null;
                    foreach (var c in candidates)
                    {
                        if (EqslTimesMatch(row.Time, c.Time)) { match = c; break; }
                    }
                    if (match == null) continue;

                    if (row.HasBandProblem && !string.IsNullOrEmpty(match.Value.Band))
                        row.Band = match.Value.Band.ToUpper();
                    if (row.HasModeProblem && !string.IsNullOrEmpty(match.Value.Mode))
                        row.Mode = match.Value.Mode.ToUpper();

                    filled++;
                }

                TB_SaveStatus.Text = filled > 0
                    ? $"{filled} QSO{(filled == 1 ? "" : "s")} filled from LoTW — review then click Update My Log."
                    : "No matching LoTW records found for the problem QSOs.";
                TB_SaveStatus.Foreground = filled > 0
                    ? System.Windows.Media.Brushes.DarkGreen
                    : System.Windows.Media.Brushes.DarkOrange;
            }
            catch (Exception ex)
            {
                TB_SaveStatus.Text = $"LoTW download failed: {ex.Message}";
                TB_SaveStatus.Foreground = System.Windows.Media.Brushes.DarkRed;
            }
            finally
            {
                BTN_CheckLotw.IsEnabled = true;
            }
        }

        // Parses an ADIF string (already loaded into memory) instead of a file.
        private static List<Dictionary<string, string>> ParseAdifString(string text)
        {
            var records = new List<Dictionary<string, string>>();

            int eoh = text.IndexOf("<EOH>", StringComparison.OrdinalIgnoreCase);
            if (eoh >= 0) text = text.Substring(eoh + 5);

            int pos = 0;
            while (pos < text.Length)
            {
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                while (pos < text.Length)
                {
                    int lt = text.IndexOf('<', pos);
                    if (lt < 0) { pos = text.Length; break; }
                    int gt = text.IndexOf('>', lt + 1);
                    if (gt < 0) { pos = text.Length; break; }

                    string tag = text.Substring(lt + 1, gt - lt - 1);
                    string[] parts = tag.Split(':');
                    string fieldName = parts[0].Trim().ToUpper();

                    if (fieldName == "EOR") { pos = gt + 1; break; }
                    if (fieldName == "EOH") { pos = gt + 1; continue; }

                    if (parts.Length >= 2 && int.TryParse(parts[1], out int len) && len >= 0)
                    {
                        int start = gt + 1;
                        int end = Math.Min(start + len, text.Length);
                        dict[fieldName] = text.Substring(start, end - start).Trim();
                        pos = end;
                    }
                    else { pos = gt + 1; }
                }
                if (dict.Count > 0) records.Add(dict);
            }
            return records;
        }

        // Parses an ADIF file and returns each record as a case-insensitive field dictionary.
        private static List<Dictionary<string, string>> ParseAdif(string filePath)
        {
            var records = new List<Dictionary<string, string>>();
            string text = File.ReadAllText(filePath, Encoding.UTF8);

            // Skip the header section before <EOH>
            int eoh = text.IndexOf("<EOH>", StringComparison.OrdinalIgnoreCase);
            if (eoh >= 0) text = text.Substring(eoh + 5);

            int pos = 0;
            while (pos < text.Length)
            {
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                while (pos < text.Length)
                {
                    int lt = text.IndexOf('<', pos);
                    if (lt < 0) { pos = text.Length; break; }
                    int gt = text.IndexOf('>', lt + 1);
                    if (gt < 0) { pos = text.Length; break; }

                    string tag = text.Substring(lt + 1, gt - lt - 1);
                    string[] parts = tag.Split(':');
                    string fieldName = parts[0].Trim().ToUpper();

                    if (fieldName == "EOR") { pos = gt + 1; break; }
                    if (fieldName == "EOH") { pos = gt + 1; continue; }

                    if (parts.Length >= 2 && int.TryParse(parts[1], out int len) && len >= 0)
                    {
                        int start = gt + 1;
                        int end = Math.Min(start + len, text.Length);
                        dict[fieldName] = text.Substring(start, end - start).Trim();
                        pos = end;
                    }
                    else
                    {
                        pos = gt + 1;
                    }
                }
                if (dict.Count > 0) records.Add(dict);
            }
            return records;
        }

        // Compares local time (HHMMSS) with eQSL time (HHMM) within ±10 minutes.
        private static bool EqslTimesMatch(string localTime, string eqslTime, int toleranceMinutes = 10)
        {
            if (string.IsNullOrEmpty(localTime) || string.IsNullOrEmpty(eqslTime)) return true;
            if (localTime.Length < 4 || eqslTime.Length < 4) return true;
            if (!int.TryParse(localTime.Substring(0, 2), out int lh) ||
                !int.TryParse(localTime.Substring(2, 2), out int lm)) return false;
            if (!int.TryParse(eqslTime.Substring(0, 2), out int eh) ||
                !int.TryParse(eqslTime.Substring(2, 2), out int em)) return false;
            return Math.Abs(lh * 60 + lm - (eh * 60 + em)) <= toleranceMinutes;
        }

        private static string[] ParseCsvLine(string line)
        {
            var result = new List<string>();
            int i = 0;
            while (i <= line.Length)
            {
                if (i == line.Length) { result.Add(""); break; }
                if (line[i] == '"')
                {
                    i++;
                    var sb = new StringBuilder();
                    while (i < line.Length)
                    {
                        if (line[i] == '"' && i + 1 < line.Length && line[i + 1] == '"')
                        { sb.Append('"'); i += 2; }
                        else if (line[i] == '"')
                        { i++; break; }
                        else
                        { sb.Append(line[i++]); }
                    }
                    result.Add(sb.ToString());
                    if (i < line.Length && line[i] == ',') i++;
                }
                else
                {
                    int start = i;
                    while (i < line.Length && line[i] != ',') i++;
                    result.Add(line.Substring(start, i - start));
                    if (i < line.Length) i++;
                }
            }
            return result.ToArray();
        }
    }
}
