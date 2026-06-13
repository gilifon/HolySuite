using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace HolyLogger.OptionsUserControls
{
    /// <summary>
    /// Interaction logic for EqslServiceControl.xaml — the "Automatically upload each QSO" toggle and
    /// a per-station-callsign eQSL accounts table. Each station callsign you log under gets a row;
    /// you fill in the eQSL user name + password for the ones you want to upload. A QSO is only sent
    /// to the account of the callsign it was logged under.
    /// </summary>
    public partial class EqslServiceControl : UserControl
    {
        private bool _loading;
        public bool HasChanged { get; set; }

        // The callsign currently in the main screen. Its row is the "active" account and may not be
        // deleted. Set by the Options window when it opens.
        public string CurrentCallsign { get; set; }

        private ObservableCollection<EqslAccount> _accounts;

        public EqslServiceControl()
        {
            InitializeComponent();

            _loading = true;
            CB_AutoUpload.IsChecked = Properties.Settings.Default.EqslAutoUpload;
            LoadAccounts();
            _loading = false;

            HasChanged = false;
        }

        // Re-reads the eQSL accounts from the database into the grid. Public so the Options window can
        // refresh it each time the page is shown (a freshly used callsign may have a new row).
        public void LoadAccounts()
        {
            try
            {
                DataAccess dal = DataAccess.GetInstance();
                _accounts = new ObservableCollection<EqslAccount>(dal.GetEqslAccounts());
                DG_Accounts.ItemsSource = _accounts;
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show("Failed to load eQSL accounts: " + ex.Message);
            }
        }

        // Force-commits any edit still in progress (e.g. the user typed a password but didn't press
        // Enter before closing the window) and writes every row to the database. Called when the
        // Options window closes so no edit is ever lost.
        public void SaveAll()
        {
            try
            {
                DG_Accounts.CommitEdit(DataGridEditingUnit.Cell, true);
                DG_Accounts.CommitEdit(DataGridEditingUnit.Row, true);

                DataAccess dal = DataAccess.GetInstance();
                if (_accounts != null)
                {
                    foreach (EqslAccount a in _accounts)
                        dal.SaveEqslAccountCredentials(a.Callsign, a.Username, a.Password);
                }
            }
            catch { /* best effort on close */ }
        }

        private void CB_AutoUpload_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            Properties.Settings.Default.EqslAutoUpload = CB_AutoUpload.IsChecked == true;
            Properties.Settings.Default.Save();
            HasChanged = true;
        }

        // Persist the edited user name / password for a row. CellEditEnding fires BEFORE the binding
        // writes the new value back to the object, so we read the new text straight from the editing
        // TextBox and update the object ourselves, then save immediately. (The previous approach read
        // the object's value, which could still be the old one — so edits appeared not to "stick".)
        private void DG_Accounts_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (_loading || e.EditAction != DataGridEditAction.Commit) return;
            EqslAccount acct = e.Row.Item as EqslAccount;
            if (acct == null) return;

            // Push the just-typed text into the object for the column being edited.
            TextBox tb = e.EditingElement as TextBox;
            string path = (e.Column as DataGridTextColumn)?.Binding is Binding b ? b.Path?.Path : null;
            if (tb != null && path != null)
            {
                if (path == "Username") acct.Username = tb.Text;
                else if (path == "Password") acct.Password = tb.Text;
            }

            try
            {
                DataAccess.GetInstance().SaveEqslAccountCredentials(acct.Callsign, acct.Username, acct.Password);
                HasChanged = true;
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show("Failed to save eQSL account: " + ex.Message);
            }
        }

        private void RemoveBtn_Click(object sender, RoutedEventArgs e)
        {
            EqslAccount acct = DG_Accounts.SelectedItem as EqslAccount;
            if (acct == null)
            {
                System.Windows.Forms.MessageBox.Show("Select a callsign row first, then press Remove.");
                return;
            }

            // The active account (matching the main-screen callsign) can't be removed.
            if (!string.IsNullOrWhiteSpace(CurrentCallsign) &&
                string.Equals(acct.Callsign, CurrentCallsign.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                System.Windows.Forms.MessageBox.Show(
                    acct.Callsign + " is your current station callsign, so it can't be removed.\n\n" +
                    "Change the callsign on the main screen first if you want to remove it.");
                return;
            }

            if (System.Windows.MessageBox.Show(
                    "Remove the eQSL account for " + acct.Callsign + "?",
                    "Remove eQSL account", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            try
            {
                DataAccess.GetInstance().DeleteEqslAccount(acct.Callsign);
                _accounts.Remove(acct);
                HasChanged = true;
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show("Failed to remove eQSL account: " + ex.Message);
            }
        }

        private async void TestConnectionBtn_Click(object sender, RoutedEventArgs e)
        {
            EqslAccount acct = DG_Accounts.SelectedItem as EqslAccount;
            if (acct == null)
            {
                System.Windows.Forms.MessageBox.Show("Select a callsign row first, then press Test Connection.");
                return;
            }

            string user = (acct.Username ?? string.Empty).Trim();
            string pwd = acct.Password ?? string.Empty;
            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pwd))
            {
                System.Windows.Forms.MessageBox.Show("Enter the eQSL user name and password for " + acct.Callsign + " first.");
                return;
            }

            TestConnectionBtn.IsEnabled = false;
            try
            {
                // Verify the credentials against eQSL's READ-ONLY Inbox endpoint. This authenticates
                // without ever adding anything to the user's log. Bad user/password => the page
                // contains "Error: No such Username/Password found".
                string url = "https://www.eQSL.cc/qslcard/DownloadInBox.cfm"
                    + "?UserName=" + Uri.EscapeDataString(user)
                    + "&Password=" + Uri.EscapeDataString(pwd);
                string resp;
                using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(25) })
                {
                    resp = await http.GetStringAsync(url);
                }

                if (string.IsNullOrWhiteSpace(resp))
                {
                    System.Windows.Forms.MessageBox.Show("Could not verify the connection to eQSL. Please try again.");
                }
                else if (resp.IndexOf("No such", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    System.Windows.Forms.MessageBox.Show("eQSL rejected the user name / password for " + acct.Callsign + ".");
                }
                else
                {
                    System.Windows.Forms.MessageBox.Show("Connected to eQSL successfully as " + acct.Callsign + "!");
                }
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show("Connection failed: " + ex.Message);
            }
            finally
            {
                TestConnectionBtn.IsEnabled = true;
            }
        }
    }
}
