using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace HolyLogger
{
    // THE ADIF MONITOR MANAGER.
    //
    // The list of ADIF files HolyLogger watches (MainWindow.AdifMonitor.cs does the watching). Add file
    // location picks a file and adds it ticked; untick a line to stop watching that file without losing
    // it; Delete removes it. Save writes the list, and the watching is re-read when the Options window
    // closes. Cancel leaves everything as it was.
    public partial class AdifMonitorWindow : Window
    {
        private readonly ObservableCollection<AdifMonitorEntry> _rows = new ObservableCollection<AdifMonitorEntry>();

        public AdifMonitorWindow(Window owner)
        {
            InitializeComponent();
            Owner = owner;

            foreach (var row in AdifMonitorStore.Load()) _rows.Add(row);
            FilesGrid.ItemsSource = _rows;
            FilesGrid.ColumnHeaderStyle = MainWindow.BuildLogTableHeaderStyle();
        }

        private void BtnAdd_Click(object sender, RoutedEventArgs e)
        {
            var open = new OpenFileDialog
            {
                Title = "Choose the ADIF file to monitor",
                Filter = "ADIF files (*.adi;*.adif)|*.adi;*.adif|All files (*.*)|*.*",
                CheckFileExists = true
            };
            // WSJT-X keeps its log here, so that is the likeliest place to start.
            try
            {
                string wsjtx = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WSJT-X");
                if (Directory.Exists(wsjtx)) open.InitialDirectory = wsjtx;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            if (open.ShowDialog(this) != true) return;

            var existing = _rows.FirstOrDefault(r => AdifMonitorStore.SamePath(r.Path, open.FileName));
            if (existing != null)
            {
                // Already listed: just make sure it is on, rather than a second line for the same file.
                existing.IsOn = true;
                FilesGrid.SelectedItem = existing;
                return;
            }

            var row = new AdifMonitorEntry { IsOn = true, Path = open.FileName };
            _rows.Add(row);
            FilesGrid.SelectedItem = row;
        }

        private void FilesGrid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Delete) return;
            var row = FilesGrid.SelectedItem as AdifMonitorEntry;
            if (row == null) return;
            e.Handled = true;
            _rows.Remove(row);
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => Close();

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            FilesGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
            FilesGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
            AdifMonitorStore.Save(_rows);
            DialogResult = true;
            Close();
        }
    }
}
