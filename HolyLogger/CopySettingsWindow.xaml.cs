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
        public string LogCallsign { get; private set; }
        public string LogOperator { get; private set; }
        public long? CopyTargetLogId { get; private set; }

        public CopySettingsWindow(DataAccess dal, long logId, string callsign, string opr, long? currentTarget)
        {
            InitializeComponent();

            TB_Callsign.Text = (callsign ?? string.Empty).Trim();
            TB_Operator.Text = (opr ?? string.Empty).Trim();

            // First item = "(don't copy)" sentinel (Id 0); then every OTHER REGULAR log. A log can't copy
            // to itself, and contest logs are excluded — they must never receive copies.
            var items = new List<LogInfo> { new LogInfo { Id = 0, Name = "(don't copy)" } };
            try { items.AddRange(dal.GetLogs().Where(l => l.Id != logId && string.IsNullOrEmpty(l.EventType))); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            CB_CopyTarget.ItemsSource = items;

            CB_CopyTarget.SelectedValue = currentTarget.HasValue ? (object)currentTarget.Value : (object)0L;
            if (CB_CopyTarget.SelectedItem == null) CB_CopyTarget.SelectedIndex = 0;   // target was deleted -> off
        }

        private void Btn_Save_Click(object sender, RoutedEventArgs e)
        {
            LogCallsign = (TB_Callsign.Text ?? string.Empty).Trim();
            LogOperator = (TB_Operator.Text ?? string.Empty).Trim();
            long tid = 0;
            try { if (CB_CopyTarget.SelectedValue != null) tid = Convert.ToInt64(CB_CopyTarget.SelectedValue); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            CopyTargetLogId = tid > 0 ? (long?)tid : null;
            DialogResult = true;
            Close();
        }
    }
}
