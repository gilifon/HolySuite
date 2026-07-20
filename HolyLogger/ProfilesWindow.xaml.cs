using System;
using System.Windows;

namespace HolyLogger
{
    // The configuration manager: create, switch, rename, delete, import and export profiles.
    // See ProfileManager for what a profile contains and what is deliberately left out of one.
    public partial class ProfilesWindow : Window
    {
        public ProfilesWindow(Window owner)
        {
            InitializeComponent();
            Owner = owner;
            // Key deliberately changed from "Profiles": this window got shorter, and WindowBounds would
            // otherwise restore the taller size saved under the old key and bring the gap back.
            WindowBounds.Attach(this, "ProfileManager");
            Refresh();
        }

        private string Selected => LB_Profiles.SelectedItem as string;

        private void Refresh()
        {
            string previouslySelected = Selected;

            LB_Profiles.ItemsSource = ProfileManager.List();
            string active = ProfileManager.ActiveProfile;
            TB_Active.Text = string.IsNullOrWhiteSpace(active) ? "(none - unsaved setup)" : active;

            // Preselect: keep what was selected, otherwise land on the active profile. Opening with
            // nothing selected left every action button greyed out, which read as "the window is dead".
            LB_Profiles.SelectedItem = previouslySelected ?? (string.IsNullOrWhiteSpace(active) ? null : active);
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            bool any = Selected != null;
            BTN_Switch.IsEnabled = any;
            BTN_Update.IsEnabled = any;
            BTN_Rename.IsEnabled = any;
            BTN_Delete.IsEnabled = any;
            BTN_Export.IsEnabled = any;
        }

        private void LB_Profiles_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
            => UpdateButtons();

        private void LB_Profiles_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (Selected != null) SwitchTo(Selected);
        }

        private void BTN_Switch_Click(object sender, RoutedEventArgs e)
        {
            if (Selected != null) SwitchTo(Selected);
        }

        // Applying a profile touches everything, so confirm first: the restart must never be a surprise.
        private void SwitchTo(string name)
        {
            bool ok = HolyMessageBox.ShowConfirm(
                $"Switch to the profile \"{name}\"?\n\n" +
                "HolyLogger will close and reopen so the whole setup is rebuilt.\n\n" +
                "Anything you changed since your last save is NOT kept unless you saved it to a profile first.",
                "Switch profile", HolyMsgType.Warning, this);
            if (!ok) return;

            if (!ProfileManager.Apply(name))
            {
                HolyMessageBox.ShowError($"Could not apply the profile \"{name}\".", "Profiles", this);
                return;
            }
            ProfileManager.RestartApplication();
        }

        private void BTN_SaveAs_Click(object sender, RoutedEventArgs e)
        {
            string name = PromptForName("Save the current setup as a new profile:", string.Empty);
            if (name == null) return;

            if (ProfileManager.Exists(name) &&
                !HolyMessageBox.ShowConfirm($"A profile named \"{name}\" already exists.\n\nReplace it?",
                                            "Profiles", HolyMsgType.Warning, this))
                return;

            if (ProfileManager.Save(name))
            {
                Properties.Settings.Default.ActiveProfile = name;
                try { Properties.Settings.Default.Save(); } catch (Exception swallowed) { Log.Swallow(swallowed); }
                Refresh();
                ShowSavedMessage(name);
            }
            else
            {
                HolyMessageBox.ShowError("Could not save the profile.", "Profiles", this);
            }
        }

        private void BTN_Update_Click(object sender, RoutedEventArgs e)
        {
            string name = Selected;
            if (name == null) return;
            if (!HolyMessageBox.ShowConfirm(
                    $"Overwrite the profile \"{name}\" with the current setup?",
                    "Profiles", HolyMsgType.Warning, this))
                return;

            if (ProfileManager.Save(name)) { Refresh(); ShowSavedMessage(name); }
            else HolyMessageBox.ShowError("Could not update the profile.", "Profiles", this);
        }

        // Confirm the write and say exactly WHERE it went, so the file can be found, backed up or shared.
        private void ShowSavedMessage(string name)
        {
            HolyMessageBox.ShowSuccess(
                $"The profile \"{name}\" was saved successfully.\n\n{ProfileManager.PathFor(name)}",
                "Profile Manager", this);
        }

        private void BTN_Rename_Click(object sender, RoutedEventArgs e)
        {
            string oldName = Selected;
            if (oldName == null) return;

            string newName = PromptForName("New name for this profile:", oldName);
            if (newName == null || newName == oldName) return;

            if (ProfileManager.Exists(newName))
            {
                HolyMessageBox.ShowWarning($"A profile named \"{newName}\" already exists.", "Profiles", this);
                return;
            }
            if (ProfileManager.Rename(oldName, newName)) Refresh();
            else HolyMessageBox.ShowError("Could not rename the profile.", "Profiles", this);
        }

        private void BTN_Delete_Click(object sender, RoutedEventArgs e)
        {
            string name = Selected;
            if (name == null) return;
            if (!HolyMessageBox.ShowConfirm($"Delete the profile \"{name}\"?\n\nThis cannot be undone.",
                                            "Profiles", HolyMsgType.Warning, this))
                return;

            if (ProfileManager.Delete(name)) Refresh();
            else HolyMessageBox.ShowError("Could not delete the profile.", "Profiles", this);
        }

        private void BTN_Import_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import profile",
                Filter = "Profile files (*.json)|*.json|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog(this) != true) return;

            string suggested = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
            string name = PromptForName("Name for the imported profile:", suggested);
            if (name == null) return;

            if (ProfileManager.Exists(name) &&
                !HolyMessageBox.ShowConfirm($"A profile named \"{name}\" already exists.\n\nReplace it?",
                                            "Profiles", HolyMsgType.Warning, this))
                return;

            if (ProfileManager.ImportFrom(dlg.FileName, name)) Refresh();
            else HolyMessageBox.ShowError("Could not import that file.", "Profiles", this);
        }

        private void BTN_Export_Click(object sender, RoutedEventArgs e)
        {
            string name = Selected;
            if (name == null) return;

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export profile",
                FileName = name + ".json",
                Filter = "Profile files (*.json)|*.json"
            };
            if (dlg.ShowDialog(this) != true) return;

            if (ProfileManager.ExportTo(name, dlg.FileName))
                HolyMessageBox.ShowSuccess(
                    $"The profile \"{name}\" was exported successfully.\n\n{dlg.FileName}",
                    "Profile Manager", this);
            else
                HolyMessageBox.ShowError("Could not export the profile.", "Profiles", this);
        }

        // Small inline name prompt. Returns null when cancelled or the name isn't usable as a file name.
        private string PromptForName(string message, string initial)
        {
            var dlg = new ProfileNameDialog(message, initial) { Owner = this };
            if (dlg.ShowDialog() != true) return null;

            string name = (dlg.EnteredName ?? string.Empty).Trim();
            if (!ProfileManager.IsValidName(name))
            {
                HolyMessageBox.ShowWarning(
                    "Please enter a name without these characters:  \\ / : * ? \" < > |",
                    "Profiles", this);
                return null;
            }
            return name;
        }

        // Destructive and wide-reaching, so spell out exactly what goes and what is kept before doing it.
        private void BTN_Factory_Click(object sender, RoutedEventArgs e)
        {
            bool ok = HolyMessageBox.ShowConfirm(
                "Put EVERY setting back to how HolyLogger ships?\n\n" +
                "This also clears your station callsign, locator and the logins for LoTW, QRZ, eQSL and " +
                "Club Log, so you would need to enter them again.\n\n" +
                "Your logs and QSOs are NOT touched, and the log you are working in does not change. " +
                "Saved profiles are not deleted.\n\n" +
                "HolyLogger will restart.",
                "Restore factory defaults", HolyMsgType.Warning, this);
            if (!ok) return;

            if (!ProfileManager.RestoreFactoryDefaults())
            {
                HolyMessageBox.ShowError("Could not restore the factory defaults.", "Profile Manager", this);
                return;
            }
            ProfileManager.RestartApplication();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
