using System.Windows;
using System.Windows.Controls;

namespace HolyLogger.OptionsUserControls
{
    // Options panel for the Club Log real-time upload service. Club Log uses ONE account (e-mail +
    // password + an API key); the station callsign is taken from each QSO, so there is no per-callsign
    // account table like eQSL. The checkbox bindings (master switch, auto-upload) are TwoWay into
    // Settings; the e-mail / password / API key are persisted from code so they save immediately.
    public partial class ClublogServiceControl : UserControl
    {
        private bool _loading;

        // Raised after the Club Log queue is cleared so the main window can refresh the
        // "Upload Queue to Club Log (N)" menu count.
        public event System.Action ClublogQueueChanged;

        public ClublogServiceControl()
        {
            InitializeComponent();

            _loading = true;
            TB_Email.Text = Properties.Settings.Default.ClublogEmail ?? string.Empty;
            PB_Password.Password = Properties.Settings.Default.ClublogPassword ?? string.Empty;
            _loading = false;
        }

        // Master switch and auto-upload checkbox: their IsChecked bindings already wrote the value into
        // Settings; this just flushes it to disk.
        private void Setting_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            Save();
        }

        // Removes all pending QSOs from the Club Log upload queue (marks them dismissed). Mirrors the
        // Clear QRZ Queue button.
        private void ClearQueueBtn_Click(object sender, RoutedEventArgs e)
        {
            var dal = DataAccess.GetInstance();
            int pending = dal.GetPendingClublogCount();
            if (pending == 0)
            {
                HolyMessageBox.Show("The Club Log queue is already empty.", "Clear Club Log Queue",
                    HolyMsgType.Info, Window.GetWindow(this));
                return;
            }

            bool confirmed = HolyMessageBox.ShowConfirm(
                $"Remove all {pending} QSO(s) from the Club Log upload queue?\n\n" +
                "They will no longer be included in the next upload.",
                "Clear Club Log Queue", HolyMsgType.Warning, Window.GetWindow(this));
            if (!confirmed) return;

            int count = dal.ClearClublogQueue();
            HolyMessageBox.ShowSuccess($"{count} QSO(s) removed from the Club Log queue.",
                "Clear Club Log Queue", Window.GetWindow(this));
            ClublogQueueChanged?.Invoke();
        }

        private void TB_Email_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            Properties.Settings.Default.ClublogEmail = (TB_Email.Text ?? string.Empty).Trim();
            Save();
        }

        private void PB_Password_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            Properties.Settings.Default.ClublogPassword = PB_Password.Password;
            Save();
        }

        private void TB_PasswordPlain_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            Properties.Settings.Default.ClublogPassword = TB_PasswordPlain.Text;
            Save();
        }

        // The eye toggle swaps the masked PasswordBox for a plain TextBox (and back), copying the
        // current value across so nothing is lost. The _loading guard stops the copy from re-firing
        // the change handlers.
        private void TG_ShowPassword_Changed(object sender, RoutedEventArgs e)
        {
            bool show = TG_ShowPassword.IsChecked == true;
            _loading = true;
            try
            {
                if (show)
                {
                    TB_PasswordPlain.Text = PB_Password.Password;
                    PB_Password.Visibility = Visibility.Collapsed;
                    TB_PasswordPlain.Visibility = Visibility.Visible;
                }
                else
                {
                    PB_Password.Password = TB_PasswordPlain.Text;
                    TB_PasswordPlain.Visibility = Visibility.Collapsed;
                    PB_Password.Visibility = Visibility.Visible;
                }
            }
            finally { _loading = false; }
        }

        // The password currently entered, read from whichever box is visible.
        private string CurrentPassword =>
            TG_ShowPassword.IsChecked == true ? TB_PasswordPlain.Text : PB_Password.Password;

        // Verifies the entered Club Log e-mail + password by logging in via getadif.php (no QSO is
        // created). Club Log also checks the callsign belongs to the account, so we test with the
        // operator's station callsign.
        private async void TestBtn_Click(object sender, RoutedEventArgs e)
        {
            string email = (TB_Email.Text ?? string.Empty).Trim();
            string password = CurrentPassword;
            string call = (Properties.Settings.Default.my_callsign ?? string.Empty).Trim();
            var owner = Window.GetWindow(this);

            // Every Club Log API (including the login check) needs HolyLogger's application API key.
            // Without it Club Log just returns its "requires a valid API key" page, so there is nothing
            // to test yet — say so plainly instead of making a doomed request.
            if (!ClublogService.HasApiKey)
            {
                HolyMessageBox.ShowWarning(
                    "Club Log cannot be tested or used yet: this build of HolyLogger does not have a Club Log application API key.\n\n"
                    + "The program maintainer must request one from Club Log (support@clublog.org) and add it to HolyLogger. "
                    + "Your e-mail and password can only be verified once that key is in place.",
                    "Club Log", owner);
                return;
            }
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                HolyMessageBox.ShowWarning("Enter your Club Log e-mail and password first.", "Club Log", owner);
                return;
            }
            if (string.IsNullOrWhiteSpace(call))
            {
                HolyMessageBox.ShowWarning("Set your station callsign (My Callsign on the main screen) first — Club Log verifies that the callsign belongs to your account.", "Club Log", owner);
                return;
            }

            // Persist exactly what we're testing, so a pass reflects what will actually be used.
            Properties.Settings.Default.ClublogEmail = email;
            Properties.Settings.Default.ClublogPassword = password;
            Save();

            TestBtn.IsEnabled = false;
            string label = TestBtn.Content as string;
            TestBtn.Content = "Testing…";
            try
            {
                ClublogResult r = await ClublogService.TestCredentialsAsync(email, password, call);

                // Club Log serves its "Access to this form requires a valid API key" HTML page when the
                // application key is missing/invalid. Detect that so we show a clear message instead of
                // the raw HTML, and so it is not mistaken for a wrong-password (403) case.
                string body = r.Message ?? string.Empty;
                bool apiKeyRejected =
                    body.IndexOf("requires a valid API key", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || body.TrimStart().StartsWith("<!DOCTYPE", System.StringComparison.OrdinalIgnoreCase)
                    || body.TrimStart().StartsWith("<html", System.StringComparison.OrdinalIgnoreCase);

                if (r.NetworkError)
                {
                    HolyMessageBox.ShowError("Could not reach Club Log. Check your internet connection and try again.", "Club Log", owner);
                }
                else if (apiKeyRejected)
                {
                    HolyMessageBox.ShowError(
                        "Club Log rejected the request because HolyLogger's Club Log application API key is missing or invalid.\n\n"
                        + "This has to be fixed by the program maintainer (request a key from support@clublog.org). "
                        + "Your e-mail and password could not be checked until the key is valid.",
                        "Club Log", owner);
                }
                else if (r.Ok)
                {
                    string extra = ClublogService.HasApiKey
                        ? "HolyLogger can upload your QSOs to Club Log."
                        : "Note: Club Log upload is not active in this build yet (the Club Log application key has not been configured). Contact the program maintainer.";
                    HolyMessageBox.ShowSuccess("Club Log accepted your login for " + call + ".\nYour e-mail and password are valid.\n\n" + extra, "Club Log", owner);
                }
                else if (r.StatusCode == 403)
                {
                    string reason = string.IsNullOrWhiteSpace(r.Message) ? string.Empty : "\n\nClub Log says: " + r.Message.Trim();
                    HolyMessageBox.ShowError(
                        "Club Log rejected your login (403).\n\nMost likely you entered your main Club Log website password. "
                        + "The Club Log API needs an 'Application Password' — create one at clublog.org (Settings → App Passwords) and paste it here.\n\n"
                        + "Also check the e-mail is exact (e.g. name@gmail.com) and that the callsign " + call + " is registered to this Club Log account."
                        + reason,
                        "Club Log", owner);
                }
                else
                {
                    HolyMessageBox.ShowError("Club Log did not accept the request (HTTP " + r.StatusCode + ").\n" + (r.Message ?? string.Empty), "Club Log", owner);
                }
            }
            finally
            {
                TestBtn.Content = label ?? "Test Connection";
                TestBtn.IsEnabled = true;
            }
        }

        private static void Save()
        {
            try { Properties.Settings.Default.Save(); }
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }
        }
    }
}
