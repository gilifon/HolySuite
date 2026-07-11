using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace HolyLogger
{
    // Per-log Copy settings dialog (Log Manager -> "Copy settings…"): set / change / stop the log's
    // copy-target and edit its identity (station callsign + operator). Never targets itself.
    public partial class CopySettingsWindow : Window
    {
        // Identity is display-only here (permanent); this dialog only changes the copy-target.
        public long? CopyTargetLogId { get; private set; }

        public CopySettingsWindow(DataAccess dal, long logId, string callsign, string opr, long? currentTarget)
        {
            InitializeComponent();

            callsign = (callsign ?? string.Empty).Trim();
            opr = (opr ?? string.Empty).Trim();
            bool hasIdentity = callsign.Length > 0 && opr.Length > 0;
            TB_Callsign.Text = hasIdentity ? callsign : "(not set yet)";
            TB_Operator.Text = hasIdentity ? opr : "(not set yet)";

            // First item = "(don't copy)" sentinel (Id 0); then every OTHER REGULAR log. A log can't copy
            // to itself, and contest logs are excluded — they must never receive copies.
            var items = new List<LogInfo> { new LogInfo { Id = 0, Name = "(don't copy)" } };
            try { items.AddRange(dal.GetLogs().Where(l => l.Id != logId && string.IsNullOrEmpty(l.EventType))); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            CB_CopyTarget.ItemsSource = items;

            CB_CopyTarget.SelectedValue = currentTarget.HasValue ? (object)currentTarget.Value : (object)0L;
            if (CB_CopyTarget.SelectedItem == null) CB_CopyTarget.SelectedIndex = 0;   // target was deleted -> off

            // A copy-target can't be set on a log with no identity — it has no way to decide which QSOs
            // are yours. Block the choice and explain how to fix it.
            if (!hasIdentity)
            {
                CB_CopyTarget.IsEnabled = false;
                Header.Text = "This log has no identity yet, so it can't copy its QSOs anywhere. Open the log — logging a QSO or importing one sets its identity — then set the copy-target here.";
            }
        }

        private void Btn_Save_Click(object sender, RoutedEventArgs e)
        {
            long tid = 0;
            try { if (CB_CopyTarget.SelectedValue != null) tid = Convert.ToInt64(CB_CopyTarget.SelectedValue); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            CopyTargetLogId = tid > 0 ? (long?)tid : null;
            DialogResult = true;
            Close();
        }
    }
}
