using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace HolyLogger
{
    // Reusable dialog to name a log (Create New Log, contest log, Rename). Rejects a name that is
    // already used by another log. For rename, pass excludeId so the log can keep its own name.
    // When showCopyOptions is true it also collects the log's identity (station callsign + operator)
    // and an optional copy-target log; those extras are hidden for the plain name / rename uses.
    public partial class NewLogWindow : Window
    {
        private readonly DataAccess _dal;
        private readonly long _excludeId;
        public string LogName { get; private set; }

        // Set only when showCopyOptions is true. CopyTargetLogId is null when "(don't copy)" is chosen.
        public string LogCallsign { get; private set; }
        public string LogOperator { get; private set; }
        public long? CopyTargetLogId { get; private set; }

        public NewLogWindow(DataAccess dal, string prompt = "Enter a name for the new log:", string initial = "",
                            long excludeId = 0, bool showCopyOptions = false,
                            string defaultCallsign = "", string defaultOperator = "")
        {
            InitializeComponent();
            _dal = dal;
            _excludeId = excludeId;
            Prompt.Text = prompt;
            TB_Name.Text = initial ?? string.Empty;

            if (showCopyOptions)
            {
                CopyOptionsPanel.Visibility = Visibility.Visible;
                TB_Callsign.Text = (defaultCallsign ?? string.Empty).Trim();
                TB_Operator.Text = (defaultOperator ?? string.Empty).Trim();

                // First item = "(don't copy)" sentinel (Id 0); then only the REGULAR logs that SHARE THIS
                // log's identity (same station callsign + operator). Copying mirrors a QSO into the target
                // only when the QSO matches the target's identity, so a log with a different identity would
                // never actually receive a copy — offering it would just mislead. Contest logs are excluded
                // too (a contest log must never receive copies).
                string myCall = TB_Callsign.Text;   // already trimmed above
                string myOp = TB_Operator.Text;
                var items = new List<LogInfo> { new LogInfo { Id = 0, Name = "(don't copy)" } };
                try
                {
                    items.AddRange(_dal.GetLogs().Where(l =>
                        string.IsNullOrEmpty(l.EventType) &&
                        string.Equals((l.Callsign ?? string.Empty).Trim(), myCall, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals((l.Operator ?? string.Empty).Trim(), myOp, StringComparison.OrdinalIgnoreCase)));
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
                CB_CopyTarget.ItemsSource = items;
                CB_CopyTarget.SelectedIndex = 0;

                // The identity is captured from the main window. If it has no callsign/operator, this log
                // would have no identity — so it can't be given a copy-target. Disable the choice.
                if (TB_Callsign.Text.Length == 0 || TB_Operator.Text.Length == 0)
                    CB_CopyTarget.IsEnabled = false;
            }

            Loaded += (s, e) => { TB_Name.Focus(); TB_Name.SelectAll(); };
        }

        private void Btn_Ok_Click(object sender, RoutedEventArgs e)
        {
            string name = (TB_Name.Text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(name))
            {
                HolyMessageBox.ShowWarning("Please enter a name.", "Log name", this);
                return;
            }
            if (!_dal.LogNameAvailable(name, _excludeId))
            {
                HolyMessageBox.ShowWarning("A log named \"" + name + "\" already exists. Please choose a different name.", "Name already used", this);
                return;
            }

            if (CopyOptionsPanel.Visibility == Visibility.Visible)
            {
                LogCallsign = (TB_Callsign.Text ?? string.Empty).Trim();
                LogOperator = (TB_Operator.Text ?? string.Empty).Trim();
                long tid = 0;
                try { if (CB_CopyTarget.SelectedValue != null) tid = Convert.ToInt64(CB_CopyTarget.SelectedValue); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
                CopyTargetLogId = tid > 0 ? (long?)tid : null;
            }

            LogName = name;
            DialogResult = true;
            Close();
        }
    }
}
