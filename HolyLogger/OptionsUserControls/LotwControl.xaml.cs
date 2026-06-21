using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HolyLogger.OptionsUserControls
{
    public partial class LotwControl : UserControl
    {
        private bool _loading;
        private bool _loadingResolution;
        private string _currentCallsign;

        public DataAccess Dal { get; set; }

        // Set by the host (MainWindow) so the panel can show which station location will sign the
        // callsign currently in use.
        public string CurrentCallsign
        {
            get { return _currentCallsign; }
            set { _currentCallsign = value; RefreshResolution(); }
        }

        public event Action LotwQueueChanged;

        // One row in the read-only "station locations found in TQSL" table.
        public class LotwLocationRow
        {
            public string Callsign { get; set; }
            public string Location { get; set; }
            public string Grid { get; set; }
            public string Dxcc { get; set; }
            public bool IsCurrent { get; set; }   // highlighted: matches the current callsign + resolved location
        }

        public LotwControl()
        {
            InitializeComponent();

            _loading = true;
            TB_TqslPath.Text = Properties.Settings.Default.LotwTqslPath ?? string.Empty;
            PB_Password.Password = Properties.Settings.Default.LotwTqslPassword ?? string.Empty;
            TB_WebUser.Text = Properties.Settings.Default.LotwWebUser ?? string.Empty;
            PB_WebPassword.Password = Properties.Settings.Default.LotwWebPassword ?? string.Empty;
            DP_FromDate.SelectedDate = DateTime.Today;
            int mode = Properties.Settings.Default.LotwUploadOnExitMode;
            CB_UploadOnExit.SelectedIndex = (mode >= 0 && mode <= 2) ? mode : 0;
            _loading = false;

            LoadStationLocations();
        }

        // Reads the TQSL station_data file and fills the read-only table, then refreshes the
        // current-callsign resolution line.
        private void LoadStationLocations()
        {
            int count = 0;
            try
            {
                var rows = TqslStationData.Read()
                    .Select(l => new LotwLocationRow
                    {
                        Callsign = l.Call,
                        Location = l.Name,
                        Grid = l.Grid,
                        Dxcc = l.Dxcc
                    })
                    .OrderBy(r => r.Callsign, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(r => r.Location, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                DG_Locations.ItemsSource = rows;
                count = rows.Count;
            }
            catch { DG_Locations.ItemsSource = null; count = 0; }

            UpdateLocationsHelp(count);
            RefreshResolution();
        }

        // Marks the table row(s) that match the current callsign + resolved location so they can be
        // highlighted green. For an ambiguous callsign with no pick yet, all of its rows are marked.
        private void ApplyCurrentHighlight()
        {
            var rows = DG_Locations.ItemsSource as IEnumerable<LotwLocationRow>;
            if (rows == null) return;

            string call = (_currentCallsign ?? string.Empty).Trim();
            var choice = LotwStationResolver.Resolve(call, Properties.Settings.Default.LotwCallsignLocations);
            string resolved = choice.LocationName;

            foreach (var r in rows)
            {
                bool callMatch = string.Equals((r.Callsign ?? string.Empty).Trim(), call, StringComparison.OrdinalIgnoreCase);
                r.IsCurrent = callMatch && (string.IsNullOrEmpty(resolved)
                    || string.Equals((r.Location ?? string.Empty).Trim(), resolved, StringComparison.OrdinalIgnoreCase));
            }
            DG_Locations.Items.Refresh();
        }

        // Distinguishes "TQSL file present but unreadable / format changed" (the suspicious case,
        // shown as a red warning) from the normal states.
        private void UpdateLocationsHelp(int count)
        {
            if (TB_LocationsHelp == null) return;

            bool fileExists = false;
            try { fileExists = File.Exists(TqslStationData.DefaultPath); } catch { }

            if (fileExists && count == 0)
            {
                TB_LocationsHelp.Text = "⚠ Couldn't read any station locations from TQSL, although its file exists. " +
                                        "TQSL may have changed its file format — LoTW uploads will not work until this is resolved.";
                TB_LocationsHelp.Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0x20, 0x20));
            }
            else if (!fileExists)
            {
                TB_LocationsHelp.Text = "TQSL station-locations file not found yet. Create your station location(s) in TQSL, " +
                                        "then click \"Refresh from TQSL\".";
                TB_LocationsHelp.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            }
            else
            {
                TB_LocationsHelp.Text = "HolyLogger reads your TQSL station locations (read-only) and signs each callsign " +
                                        "with its own location. Nothing to type — just keep your locations set up in TQSL.";
                TB_LocationsHelp.Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
            }
        }

        // Shows which station location will sign the current callsign. If the callsign has more than
        // one location, a dropdown lets the user pick (the choice is saved per callsign).
        private void RefreshResolution()
        {
            if (TB_CurrentCallsign == null) return;   // template not built yet

            string call = (_currentCallsign ?? string.Empty).Trim();
            TB_CurrentCallsign.Text = call.Length == 0 ? "(none)" : call;

            string savedPicks = Properties.Settings.Default.LotwCallsignLocations;
            var choice = LotwStationResolver.Resolve(call, savedPicks);

            _loadingResolution = true;
            bool multi = choice.Options != null && choice.Options.Count > 1;
            if (multi)
            {
                CB_AmbiguousPick.ItemsSource = choice.Options;
                CB_AmbiguousPick.SelectedItem = choice.LocationName;   // null when no pick yet
                CB_AmbiguousPick.Visibility = Visibility.Visible;
            }
            else
            {
                CB_AmbiguousPick.Visibility = Visibility.Collapsed;
                CB_AmbiguousPick.ItemsSource = null;
            }

            if (multi && string.IsNullOrWhiteSpace(choice.LocationName))
            {
                TB_ResolvedLocation.Text = "→ choose the site:";
                TB_ResolvedLocation.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x6A, 0x00));
            }
            else if (!string.IsNullOrWhiteSpace(choice.LocationName))
            {
                TB_ResolvedLocation.Text = multi ? "→ signs with:" : $"→ signs with \"{choice.LocationName}\"";
                TB_ResolvedLocation.Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x7E, 0x34));
            }
            else
            {
                TB_ResolvedLocation.Text = "→ no TQSL certificate / station location — will not upload";
                TB_ResolvedLocation.Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0x20, 0x20));
            }
            _loadingResolution = false;

            ApplyCurrentHighlight();
        }

        private void CB_AmbiguousPick_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingResolution) return;
            string call = (_currentCallsign ?? string.Empty).Trim();
            string sel = CB_AmbiguousPick.SelectedItem as string;
            if (call.Length == 0 || string.IsNullOrEmpty(sel)) return;

            var picks = LotwStationResolver.ParsePicks(Properties.Settings.Default.LotwCallsignLocations);
            picks[call] = sel;
            Properties.Settings.Default.LotwCallsignLocations = LotwStationResolver.FormatPicks(picks);
            Properties.Settings.Default.Save();
            RefreshResolution();
        }

        private void RefreshLocationsBtn_Click(object sender, RoutedEventArgs e)
        {
            LoadStationLocations();
        }

        private void CB_UploadOnExit_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            Properties.Settings.Default.LotwUploadOnExitMode = CB_UploadOnExit.SelectedIndex;
            Properties.Settings.Default.Save();
        }

        private void TB_TqslPath_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            Properties.Settings.Default.LotwTqslPath = TB_TqslPath.Text.Trim();
            Properties.Settings.Default.Save();
            StatusBadge.Visibility = Visibility.Collapsed;
        }

        private void PB_Password_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            Properties.Settings.Default.LotwTqslPassword = PB_Password.Password;
            Properties.Settings.Default.Save();
        }

        private void TB_WebUser_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            Properties.Settings.Default.LotwWebUser = TB_WebUser.Text.Trim();
            Properties.Settings.Default.Save();
        }

        private void PB_WebPassword_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            Properties.Settings.Default.LotwWebPassword = PB_WebPassword.Password;
            Properties.Settings.Default.Save();
        }

        private void BrowseBtn_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new System.Windows.Forms.OpenFileDialog
            {
                Title = "Locate TQSL executable",
                Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*",
                FileName = "tqsl.exe"
            };
            if (!string.IsNullOrWhiteSpace(TB_TqslPath.Text) && File.Exists(TB_TqslPath.Text))
                dlg.InitialDirectory = Path.GetDirectoryName(TB_TqslPath.Text);
            else
                dlg.InitialDirectory = @"C:\Program Files (x86)\Trusted QSL";

            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                TB_TqslPath.Text = dlg.FileName;
        }

        private void ClearQueueBtn_Click(object sender, RoutedEventArgs e)
        {
            if (Dal == null)
            {
                ShowError("Database not available.");
                return;
            }

            int pending = Dal.GetPendingLotwCount();
            if (pending == 0)
            {
                ShowOk("The LoTW queue is already empty.");
                return;
            }

            bool confirmed = HolyMessageBox.ShowConfirm(
                $"Remove all {pending} QSO(s) from the LoTW upload queue?\n\n" +
                "They will no longer be included in the next upload.",
                "Clear LoTW Queue", HolyMsgType.Warning, System.Windows.Window.GetWindow(this));

            if (!confirmed) return;

            int count = Dal.ClearLotwQueue();
            ShowOk($"{count} QSO(s) removed from the LoTW queue.");
            LotwQueueChanged?.Invoke();
        }

        private void TestBtn_Click(object sender, RoutedEventArgs e)
        {
            string path = TB_TqslPath.Text.Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                ShowError("Enter the path to your TQSL executable first.");
                return;
            }
            if (!File.Exists(path))
            {
                ShowError($"File not found:\n{path}");
                return;
            }
            ShowOk($"TQSL found: {path}");
        }

        private void ResetFromDateBtn_Click(object sender, RoutedEventArgs e)
        {
            if (Dal == null)
            {
                ShowError("Database not available.");
                return;
            }
            if (DP_FromDate.SelectedDate == null)
            {
                ShowError("Please select a date first.");
                return;
            }

            DateTime selectedDate = DP_FromDate.SelectedDate.Value;
            string fromDate = selectedDate.ToString("yyyyMMdd");
            string displayDate = selectedDate.ToString("dd-MM-yyyy");

            int qsoCount = Dal.GetQsoCountFromDate(fromDate);
            var dlg = new LotwConfirmQueueDialog(displayDate, qsoCount)
            {
                Owner = System.Windows.Window.GetWindow(this)
            };
            dlg.ShowDialog();
            if (!dlg.Confirmed) return;

            int count = Dal.ResetLotwStatusFromDate(fromDate);
            ShowOk($"{count} QSO(s) marked as pending from {displayDate} onwards.");
            LotwQueueChanged?.Invoke();
        }

        private void ShowOk(string text)
        {
            StatusBadge.Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E6F4EA"));
            StatusBadge.BorderBrush = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#34A853"));
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1E7E34"));
            StatusText.Text = text;
            StatusBadge.Visibility = Visibility.Visible;
        }

        private void ShowError(string text)
        {
            StatusBadge.Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FDE8E8"));
            StatusBadge.BorderBrush = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#D32F2F"));
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#B71C1C"));
            StatusText.Text = text;
            StatusBadge.Visibility = Visibility.Visible;
        }
    }
}
