using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace HolyLogger
{
    // Reusable dialog to name a log (Create New Log, contest log, Rename). Rejects a name that is
    // already used by another log. For rename, pass excludeId so the log can keep its own name.
    // When showCopyOptions is true it also collects the log's station callsign and an optional
    // copy-target log; those extras are hidden for the plain name / rename uses.
    //
    // The callsign box is TYPEABLE. It is filled from the main window when there is something there,
    // but a log must be able to get its callsign here: with the main window's box empty there was
    // nowhere else to enter one, and the log was created with no callsign at all - which is a log you
    // cannot log into. The operator is NOT part of a log's identity (a club station has many
    // operators through one callsign), so it is not asked for here.
    public partial class NewLogWindow : Window
    {
        private readonly DataAccess _dal;
        private readonly long _excludeId;
        public string LogName { get; private set; }


        // Set only when showCopyOptions is true. CopyTargetLogId is null when "(don't copy)" is chosen.
        public string LogCallsign { get; private set; }
        public long? CopyTargetLogId { get; private set; }

        public NewLogWindow(DataAccess dal, string prompt = "Enter a name for the new log:", string initial = "",
                            long excludeId = 0, bool showCopyOptions = false,
                            string defaultCallsign = "")
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
                RefreshCopyTargets();
            }

            Loaded += (s, e) => { TB_Name.Focus(); TB_Name.SelectAll(); };
        }

        // First item = "(don't copy)" sentinel (Id 0); then EVERY regular log. A copy may go to any log
        // the operator chooses - the callsign of this log does not limit where its QSOs are mirrored,
        // and a log that receives copies made under another callsign simply holds that callsign too
        // from then on. Contest logs are the one exception: they must never receive copies, because a
        // contest log's QSOs may only come from contest operation.
        private void RefreshCopyTargets()
        {
            if (_dal == null || CB_CopyTarget == null) return;
            if (CopyOptionsPanel == null || CopyOptionsPanel.Visibility != Visibility.Visible) return;

            var items = new List<LogInfo> { new LogInfo { Id = 0, Name = "(don't copy)" } };
            try
            {
                items.AddRange(_dal.GetLogs().Where(l => string.IsNullOrEmpty(l.EventType)));
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            CB_CopyTarget.ItemsSource = items;
            CB_CopyTarget.SelectedIndex = 0;
            CB_CopyTarget.IsEnabled = true;
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
                // A log without a callsign cannot be logged into, and now that the box is typeable
                // there is somewhere to put one - so ask for it here instead of creating such a log.
                string call = (TB_Callsign.Text ?? string.Empty).Trim();
                if (call.Length == 0)
                {
                    HolyMessageBox.ShowWarning("Enter the station callsign this log is for.", "Station callsign", this);
                    TB_Callsign.Focus();
                    return;
                }

                LogCallsign = call;
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
