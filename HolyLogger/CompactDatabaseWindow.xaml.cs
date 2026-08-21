using System;
using System.Threading.Tasks;
using System.Windows;

namespace HolyLogger
{
    // GIVES BACK THE EMPTY SPACE IN THE LOG FILE.
    //
    // A deleted QSO does not make the file smaller - its pages are marked free and kept for later. On
    // the log this was written for, 147 MB of a 381 MB file was empty in exactly that way, and the
    // whole file is walked every time the log is read at startup.
    //
    // Asked for, never automatic: it rewrites the file from end to end and holds the database for as
    // long as that takes - about half a minute for 380 MB. So the operator is told what it will cost
    // and what it will save BEFORE anything happens, and nothing starts until the button is pressed.
    public partial class CompactDatabaseWindow : Window
    {
        private readonly DataAccess _dal;
        private bool _busy;

        public CompactDatabaseWindow(DataAccess dal)
        {
            InitializeComponent();
            _dal = dal;
            ShowSizes();
        }

        private void ShowSizes()
        {
            long file = _dal.DatabaseFileBytes;
            long free = _dal.DatabaseFreeBytes;

            SizeText.Text = "Your log file is " + Mb(file) + "."
                          + (free > 0
                             ? "  About " + Mb(free) + " of it is empty space left behind by QSOs that were deleted."
                             : "  It holds almost no empty space.");

            if (free < 20L * 1024 * 1024)
            {
                ExplainText.Text = "There is little to gain here - the file is already tight. "
                                 + "Compacting it would take minutes and give back very little.";
                CompactBtn.IsEnabled = false;
            }
            else
            {
                ExplainText.Text =
                    "Compacting rewrites the file without that empty space, so the log opens from a "
                    + "smaller file. Nothing is deleted and no QSO is changed.\n\n"
                    + "It takes about half a minute for every 400 MB, and HolyLogger cannot use the log "
                    + "while it runs. Your daily backups are untouched.";
            }
        }

        private static string Mb(long bytes)
        {
            return (bytes / 1048576.0).ToString("0") + " MB";
        }

        private async void CompactBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            _busy = true;

            long before = _dal.DatabaseFileBytes;

            CompactBtn.IsEnabled = false;
            CloseBtn.IsEnabled = false;
            Spinner.Visibility = Visibility.Visible;
            ExplainText.Text = "Compacting… this window will say when it is done. Please do not close HolyLogger.";

            string error = null;
            bool ok = false;

            // ON A WORKER, so the window can go on drawing its progress bar. The database is held for
            // the whole run either way - that is what VACUUM does - but a frozen window on top of it
            // would leave the operator with no sign that anything was happening.
            await Task.Run(() =>
            {
                try { ok = _dal.CompactDatabase(out error); }
                catch (Exception ex) { Log.Swallow(ex); error = ex.Message; ok = false; }
            });

            Spinner.Visibility = Visibility.Collapsed;
            CloseBtn.IsEnabled = true;
            _busy = false;

            if (ok)
            {
                long after = _dal.DatabaseFileBytes;
                long saved = before - after;
                TitleText.Text = "Done";
                ExplainText.Text = saved > 0
                    ? "The log file went from " + Mb(before) + " to " + Mb(after)
                      + " — " + Mb(saved) + " given back."
                    : "The file was already as small as it can be.";
                ShowSizesQuietly();
            }
            else
            {
                TitleText.Text = "It did not work";
                ExplainText.Text = "The database was not changed.\n\n" + (error ?? "Unknown reason.")
                                 + "\n\nThe most common reason is not enough free space on the disk: "
                                 + "compacting needs as much room as the log file itself while it works.";
            }
        }

        private void ShowSizesQuietly()
        {
            try
            {
                SizeText.Text = "Your log file is now " + Mb(_dal.DatabaseFileBytes) + ".";
            }
            catch (Exception ex) { Log.Swallow(ex); }
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            Close();
        }

        // While it is working the window must not be closed - the run would go on without anything on
        // screen to say so, and the log would look frozen for no visible reason.
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_busy) e.Cancel = true;
            base.OnClosing(e);
        }
    }
}
