using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HolyLogger
{
    // The log manager: lists every Log with its stats, and lets the user open (activate), rename,
    // delete, or export a selected log to ADIF / Cabrillo.
    public partial class ViewLogsWindow : Window
    {
        private readonly MainWindow _main;
        private readonly DataAccess _dal;

        // One grid row.
        public class Row
        {
            public int Num { get; set; }
            public string Name { get; set; }
            public string EventType { get; set; }
            public string StartDate { get; set; }
            public string EndDate { get; set; }
            public int QsoCount { get; set; }
            public long Id { get; set; }
            public bool IsContest { get; set; }
        }

        public ViewLogsWindow(MainWindow main, DataAccess dal)
        {
            InitializeComponent();
            _main = main;
            _dal = dal;
            LoadLogs();
        }

        private void LoadLogs()
        {
            var rows = new List<Row>();
            int n = 1;
            foreach (var li in _dal.GetLogs())
            {
                bool isContest = !string.IsNullOrEmpty(li.EventType);
                string eventDisplay = isContest
                    ? (Contests.ContestService.FindById(li.EventType)?.Name ?? li.EventType)
                    : "General";
                rows.Add(new Row
                {
                    Num = n++,
                    Name = li.Name + (li.Id == _dal.ActiveLogId ? "  (active)" : ""),
                    EventType = eventDisplay,
                    StartDate = FormatQsoDate(li.StartDate),
                    EndDate = FormatQsoDate(li.EndDate),
                    QsoCount = li.QsoCount,
                    Id = li.Id,
                    IsContest = isContest,
                });
            }
            LogsGrid.ItemsSource = rows;

            // Pre-select the active log so Export ADIF/Cabrillo, Rename and Delete default to it
            // (and a single log always appears selected). SelectionChanged then updates Open Log.
            var preselect = rows.FirstOrDefault(r => r.Id == _dal.ActiveLogId) ?? rows.FirstOrDefault();
            if (preselect != null) LogsGrid.SelectedItem = preselect;
        }

        // QSO dates are stored as YYYYMMDD; show them as e.g. "30 Sep 2006". Empty stays empty.
        private static string FormatQsoDate(string yyyymmdd)
        {
            if (string.IsNullOrWhiteSpace(yyyymmdd)) return string.Empty;
            if (DateTime.TryParseExact(yyyymmdd.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                return d.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
            return yyyymmdd;   // unexpected format -> show as-is
        }

        private Row Selected => LogsGrid.SelectedItem as Row;

        // "Open Log" is only meaningful for a log that is NOT already the active one.
        private void LogsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Btn_Open.IsEnabled = Selected != null && Selected.Id != _dal.ActiveLogId;
        }

        private bool RequireSelection()
        {
            if (Selected == null)
            {
                HolyMessageBox.ShowWarning("Please select a log first.", "Logs", this);
                return false;
            }
            return true;
        }

        private void OpenSelected()
        {
            if (!RequireSelection()) return;
            if (Selected.Id == _dal.ActiveLogId) return;   // already the active log
            _main.SwitchActiveLog(Selected.Id);
            Close();
        }

        private void Btn_Open_Click(object sender, RoutedEventArgs e) => OpenSelected();

        private void LogsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (Selected != null) OpenSelected();
        }

        private void Btn_Rename_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireSelection()) return;
            long id = Selected.Id;
            string current = _dal.GetLogName(id);
            var dlg = new NewLogWindow(_dal, "Enter a new name for this log:", current, id) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            _dal.RenameLog(id, dlg.LogName);
            if (id == _dal.ActiveLogId) _main.UpdateActiveLogTitle();
            LoadLogs();
        }

        private void Btn_Delete_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireSelection()) return;
            long id = Selected.Id;

            if (id == _dal.ActiveLogId)
            {
                HolyMessageBox.ShowWarning("This log is currently open. Open a different log first, then delete this one.", "Delete Log", this);
                return;
            }
            if (_dal.GetLogCount() <= 1)
            {
                HolyMessageBox.ShowWarning("You cannot delete your only log.", "Delete Log", this);
                return;
            }

            if (!HolyMessageBox.ShowConfirm(
                    "Delete the log \"" + _dal.GetLogName(id) + "\" and ALL " + Selected.QsoCount.ToString("N0") +
                    " QSO(s) in it?\n\nThis permanently removes those QSOs from the database and cannot be undone.",
                    "Delete Log", HolyMsgType.Warning, this))
                return;

            _dal.DeleteLog(id);
            LoadLogs();
        }

        private void Btn_Adif_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireSelection()) return;
            _main.ExportQsosToAdif(_dal.GetQSOsForLog(Selected.Id), this);
        }

        private void Btn_Cabrillo_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireSelection()) return;
            if (!Selected.IsContest)
            {
                HolyMessageBox.ShowWarning("Cabrillo export is for contest logs. This log is a normal (day-by-day) log — use Export ADIF instead.", "Export Cabrillo", this);
                return;
            }
            _main.ExportQsosToCabrillo(_dal.GetQSOsForLog(Selected.Id), this);
        }
    }
}
