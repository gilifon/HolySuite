using System;
using System.Windows;

namespace HolyLogger
{
    // Shows the daily-backup restore instructions in-app (the same text written to
    // HOW TO RESTORE.txt) so the user sees them immediately, with one-click access to the
    // Backups folder and an optional "extra backup copy folder" that is remembered in Settings.
    public partial class BackupRestoreWindow : Window
    {
        private const string NoneText = "(none — daily backups are saved only in the default Backups folder)";
        private readonly string _backupsFolder;

        public BackupRestoreWindow(string instructions, string backupsFolder)
        {
            InitializeComponent();
            _backupsFolder = backupsFolder;
            TB_Instructions.Text = instructions;
            RefreshExtraFolder();
        }

        private void RefreshExtraFolder()
        {
            string path = Properties.Settings.Default.ExtraBackupFolder;
            TB_ExtraFolder.Text = string.IsNullOrWhiteSpace(path) ? NoneText : path;
        }

        private void Btn_Browse_Click(object sender, RoutedEventArgs e)
        {
            if (TryPickWritableFolder(this, Properties.Settings.Default.ExtraBackupFolder, out string chosen))
            {
                Properties.Settings.Default.ExtraBackupFolder = chosen;
                Properties.Settings.Default.Save();
                RefreshExtraFolder();
            }
        }

        // Shows a folder picker and verifies the chosen folder is writable. Returns true + the path if
        // the user picked a usable folder; false if they cancelled or it wasn't writable (an error is
        // shown in that case). Shared by this window's Browse button and the first-run offer so the
        // pick-and-validate behaviour stays identical in both places.
        public static bool TryPickWritableFolder(Window owner, string initial, out string chosen)
        {
            chosen = null;
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.Description = "Choose a folder for extra copies of your daily backups";
                dlg.ShowNewFolderButton = true;
                if (!string.IsNullOrWhiteSpace(initial) && System.IO.Directory.Exists(initial))
                    dlg.SelectedPath = initial;

                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                    return false;

                string path = dlg.SelectedPath;

                // Verify we can actually write there before committing, so a bad choice (read-only
                // location, etc.) is caught now rather than silently failing at backup time.
                try
                {
                    System.IO.Directory.CreateDirectory(path);
                    string probe = System.IO.Path.Combine(path, ".holylogger_write_test.tmp");
                    System.IO.File.WriteAllText(probe, "ok");
                    System.IO.File.Delete(probe);
                }
                catch (Exception ex)
                {
                    Log.Swallow(ex);
                    HolyMessageBox.ShowError("That folder can't be written to, so it wasn't set:\n" + path, "Extra backup folder", owner);
                    return false;
                }

                chosen = path;
                return true;
            }
        }

        private void Btn_Clear_Click(object sender, RoutedEventArgs e)
        {
            Properties.Settings.Default.ExtraBackupFolder = string.Empty;
            Properties.Settings.Default.Save();
            RefreshExtraFolder();
        }

        private void Btn_OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.IO.Directory.CreateDirectory(_backupsFolder);   // first run: may not exist yet
                System.Diagnostics.Process.Start("explorer.exe", _backupsFolder);
            }
            catch (Exception ex)
            {
                Log.Swallow(ex);
                HolyMessageBox.ShowError("Could not open the backups folder:\n" + _backupsFolder, "Backups & Restore", this);
            }
        }

        private void Btn_Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
