using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace HolyLogger.OptionsUserControls
{
    /// <summary>
    /// Interaction logic for EqslServiceControl.xaml — the "Automatically upload each QSO" toggle and
    /// a fully manual per-callsign eQSL accounts table. The user adds/edits/removes rows by hand;
    /// nothing is added automatically. A callsign that isn't in the table is never uploaded to eQSL.
    /// </summary>
    public partial class EqslServiceControl : UserControl
    {
        private bool _loading;
        public bool HasChanged { get; set; }

        // One long-lived client for the credential test, instead of a new HttpClient per click
        // (creating one per call can exhaust sockets).
        private static readonly System.Net.Http.HttpClient _testHttp =
            new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(25) };

        private ObservableCollection<EqslAccount> _accounts;

        public EqslServiceControl()
        {
            InitializeComponent();

            _loading = true;
            CB_AutoUpload.IsChecked = Properties.Settings.Default.EqslAutoUpload;
            PasswordMaskConverter.Reveal = false;   // passwords masked by default each time the page loads
            CB_ShowPasswords.IsChecked = false;
            LoadAccounts();
            _loading = false;

            HasChanged = false;
        }

        // Re-reads the eQSL accounts from the database into the grid.
        public void LoadAccounts()
        {
            try
            {
                _accounts = new ObservableCollection<EqslAccount>(DataAccess.GetInstance().GetEqslAccounts());
                DG_Accounts.ItemsSource = _accounts;
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show("Failed to load eQSL accounts: " + ex.Message);
            }
        }

        private void CB_AutoUpload_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            Properties.Settings.Default.EqslAutoUpload = CB_AutoUpload.IsChecked == true;
            Properties.Settings.Default.Save();
            HasChanged = true;
        }

        private void CB_ShowPasswords_Changed(object sender, RoutedEventArgs e)
        {
            PasswordMaskConverter.Reveal = CB_ShowPasswords.IsChecked == true;
            DG_Accounts.Items.Refresh();   // re-run the mask converter so the display updates
        }

        private void AddBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_accounts == null) return;
            var row = new EqslAccount { Callsign = string.Empty, Username = string.Empty, Password = string.Empty };
            _accounts.Add(row);
            DG_Accounts.SelectedItem = row;
            DG_Accounts.ScrollIntoView(row);
            // Start editing the new row's callsign cell.
            DG_Accounts.CurrentCell = new DataGridCellInfo(row, DG_Accounts.Columns[0]);
            DG_Accounts.BeginEdit();
        }

        // Persist a row after an edit. With UpdateSourceTrigger=PropertyChanged the bound object
        // already holds the new text by the time this fires, so we can save it directly.
        private void DG_Accounts_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (_loading || e.EditAction != DataGridEditAction.Commit) return;
            EqslAccount acct = e.Row.Item as EqslAccount;
            SaveRow(acct);
        }

        private void SaveRow(EqslAccount acct)
        {
            if (acct == null) return;
            // A new row with no callsign yet isn't saved (the user is still filling it in).
            if (string.IsNullOrWhiteSpace(acct.Callsign)) return;

            string error;
            if (DataAccess.GetInstance().SaveEqslAccount(acct, out error))
            {
                HasChanged = true;
            }
            else if (!string.IsNullOrEmpty(error))
            {
                System.Windows.Forms.MessageBox.Show(error);
                // Discard the invalid edit (e.g. a duplicate callsign) by reloading from the database.
                Dispatcher.BeginInvoke(new Action(LoadAccounts), System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        // ---- Password cell editing: dots (PasswordBox) when hidden, plain (TextBox) when revealed ----

        private void EditPassword_Loaded(object sender, RoutedEventArgs e)
        {
            PasswordBox pb = sender as PasswordBox;
            if (pb == null) return;
            EqslAccount acct = pb.DataContext as EqslAccount;
            pb.Password = acct?.Password ?? string.Empty;

            if (PasswordMaskConverter.Reveal)
            {
                pb.Visibility = Visibility.Collapsed;   // revealed -> the TextBox is used instead
            }
            else
            {
                pb.Visibility = Visibility.Visible;
                pb.Dispatcher.BeginInvoke(new Action(() => { pb.Focus(); pb.SelectAll(); }),
                    System.Windows.Threading.DispatcherPriority.Input);
            }
        }

        private void EditPassword_Changed(object sender, RoutedEventArgs e)
        {
            PasswordBox pb = sender as PasswordBox;
            EqslAccount acct = pb?.DataContext as EqslAccount;
            if (acct != null) acct.Password = pb.Password;   // keep the bound object in sync
        }

        private void EditText_Loaded(object sender, RoutedEventArgs e)
        {
            TextBox tb = sender as TextBox;
            if (tb == null) return;

            if (PasswordMaskConverter.Reveal)
            {
                tb.Visibility = Visibility.Visible;
                tb.Dispatcher.BeginInvoke(new Action(() => { tb.Focus(); tb.SelectAll(); }),
                    System.Windows.Threading.DispatcherPriority.Input);
            }
            else
            {
                tb.Visibility = Visibility.Collapsed;   // hidden -> the PasswordBox is used instead
            }
        }

        private void RemoveBtn_Click(object sender, RoutedEventArgs e)
        {
            EqslAccount acct = DG_Accounts.SelectedItem as EqslAccount;
            if (acct == null)
            {
                System.Windows.Forms.MessageBox.Show("Select a row first, then press Remove.");
                return;
            }

            string label = string.IsNullOrWhiteSpace(acct.Callsign) ? "this row" : ("the eQSL account for " + acct.Callsign);
            if (System.Windows.MessageBox.Show("Remove " + label + "?", "Remove eQSL account",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            try
            {
                if (acct.Id > 0) DataAccess.GetInstance().DeleteEqslAccount(acct.Id);
                _accounts.Remove(acct);
                HasChanged = true;
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show("Failed to remove eQSL account: " + ex.Message);
            }
        }

        // Force-commits any edit still in progress and saves every row. Called when Options closes.
        public void SaveAll()
        {
            try
            {
                DG_Accounts.CommitEdit(DataGridEditingUnit.Cell, true);
                DG_Accounts.CommitEdit(DataGridEditingUnit.Row, true);

                if (_accounts != null)
                {
                    foreach (EqslAccount a in _accounts)
                    {
                        if (string.IsNullOrWhiteSpace(a.Callsign)) continue;
                        string err;
                        DataAccess.GetInstance().SaveEqslAccount(a, out err); // ignore dup errors on bulk close
                    }
                }
            }
            catch { /* best effort on close */ }
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
                System.Windows.Forms.MessageBox.Show("Enter the eQSL user name and password for " +
                    (string.IsNullOrWhiteSpace(acct.Callsign) ? "this row" : acct.Callsign) + " first.");
                return;
            }

            TestConnectionBtn.IsEnabled = false;
            try
            {
                // Verify the credentials against eQSL's READ-ONLY Inbox endpoint (no upload happens).
                // Bad user/password => the page contains "Error: No such Username/Password found".
                string url = "https://www.eQSL.cc/qslcard/DownloadInBox.cfm"
                    + "?UserName=" + Uri.EscapeDataString(user)
                    + "&Password=" + Uri.EscapeDataString(pwd);
                string resp = await _testHttp.GetStringAsync(url);

                if (string.IsNullOrWhiteSpace(resp))
                    System.Windows.Forms.MessageBox.Show("Could not verify the connection to eQSL. Please try again.");
                else if (resp.IndexOf("No such", StringComparison.OrdinalIgnoreCase) >= 0)
                    System.Windows.Forms.MessageBox.Show("eQSL rejected the user name / password.");
                else
                    System.Windows.Forms.MessageBox.Show("Connected to eQSL successfully!");
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

    // Masks a password for display: shows bullet characters unless Reveal is on (toggled by the
    // "Show passwords" checkbox). Reveal is shared across the single options page instance.
    public class PasswordMaskConverter : IValueConverter
    {
        public static bool Reveal;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string s = value as string ?? string.Empty;
            return Reveal ? s : new string('●', s.Length);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }
}
