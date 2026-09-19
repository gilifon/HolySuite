using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using HolyLogger.Contests;

namespace HolyLogger
{
    // Options > General > Contest Radio Setup: one row per radio, holding the commands that put it in VFO mode,
    // simplex, no tone and no TSQL, and the one contest they are for (picked at the top). Opening a
    // log of that contest sends the row of the radio on CAT - see MainWindow.ApplyContestRadioSetup.
    // Saved when the window closes.
    public partial class ContestRadioSetupWindow : Window
    {
        private readonly ObservableCollection<RadioCommandSet> _sets = new ObservableCollection<RadioCommandSet>();
        private readonly Func<RadioCommandSet, string> _sendNow;

        // Offered in the Radio box: radios that work on 2m and/or 70cm (ContestRadioCommands.VhfUhfRadios)
        // AND have a file in OmniRig right now, read fresh each time the window opens. The ones set up as
        // RIG1/RIG2 come first. The box is editable, so a file renamed in OmniRig can still be typed.
        public List<string> RigNames { get; }

        public ContestRadioSetupWindow(Window owner, IEnumerable<string> omniRigRigs, Func<RadioCommandSet, string> sendNow)
        {
            var omniRigFiles = ContestRadioCommands.OmniRigRigFiles();
            NotInOmniRigBrushConverter.OmniRigFiles = omniRigFiles;
            RigNames = BuildRigNames(omniRigRigs, omniRigFiles);
            InitializeComponent();
            Owner = owner;
            _sendNow = sendNow;

            var setup = ContestRadioCommands.Load();

            // A contest picked before that no longer has 2m/70cm still shows, so it is not lost silently.
            var contests = ContestRadioCommands.VhfUhfContests();
            var picked = ContestService.FindById(setup.ContestId);
            if (picked != null && !contests.Contains(picked)) contests.Insert(0, picked);
            ContestBox.ItemsSource = contests;
            ContestBox.SelectedItem = picked ?? contests.FirstOrDefault();

            foreach (var s in setup.Radios) _sets.Add(s);
            RadiosGrid.ItemsSource = _sets;
            RadiosGrid.ColumnHeaderStyle = MainWindow.BuildLogTableHeaderStyle();

            Closing += Window_Closing;
        }

        private static List<string> BuildRigNames(IEnumerable<string> omniRigRigs, HashSet<string> omniRigFiles)
        {
            // No OmniRig folder found: nothing to check against, so the whole list is offered.
            var vhfUhf = ContestRadioCommands.VhfUhfRadios
                             .Where(n => omniRigFiles == null || omniRigFiles.Contains(n)).ToList();
            var names = new List<string>();
            foreach (string r in omniRigRigs ?? Enumerable.Empty<string>())
                if (!string.IsNullOrWhiteSpace(r) && vhfUhf.Contains(r.Trim(), StringComparer.OrdinalIgnoreCase))
                    names.Add(r.Trim());
            names.AddRange(vhfUhf);
            return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private void BtnAdd_Click(object sender, RoutedEventArgs e)
        {
            var s = new RadioCommandSet();
            _sets.Add(s);
            RadiosGrid.SelectedItem = s;
            RadiosGrid.ScrollIntoView(s);
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (!(RadiosGrid.SelectedItem is RadioCommandSet s)) return;
            string name = string.IsNullOrWhiteSpace(s.Radio) ? "this radio" : "the " + s.Radio.Trim();
            if (!HolyMessageBox.ShowConfirm("Delete the commands for " + name + "?", "Contest Radio Setup",
                                            HolyMsgType.Warning, this))
                return;
            _sets.Remove(s);
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void BtnSendNow_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is RadioCommandSet s)) return;
            if (!CheckCommands(new[] { s })) return;
            string result = _sendNow?.Invoke(s);
            if (!string.IsNullOrEmpty(result))
                HolyMessageBox.Show(result, "Contest Radio Setup", HolyMsgType.Info, this);
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (!CheckCommands(_sets)) { e.Cancel = true; return; }
            ContestRadioCommands.Save((ContestBox.SelectedItem as Contest)?.Id, _sets);
        }

        // Stops a byte typed with one digit ("07 0 FD") from being sent as text.
        private bool CheckCommands(IEnumerable<RadioCommandSet> sets)
        {
            foreach (var s in sets)
            {
                var cells = new[] { ("VFO", s.Vfo), ("Simplex", s.Simplex), ("No Tone", s.NoTone), ("No TSQL", s.NoTsql) };
                foreach (var (label, command) in cells)
                {
                    if (ContestRadioCommands.LooksValid(command)) continue;
                    string radio = string.IsNullOrWhiteSpace(s.Radio) ? "a radio" : "the " + s.Radio.Trim();
                    RadiosGrid.SelectedItem = s;
                    HolyMessageBox.ShowError("The " + label + " command for " + radio + " is not right.\n\n"
                                             + "Each byte needs two digits, with a space between: FE FE 8C E0 07 00 FD",
                                             "Contest Radio Setup", this);
                    return false;
                }
            }
            return true;
        }
    }

    // A radio name with no file in OmniRig - renamed or deleted there since the row was saved - is
    // shown in red, so the operator knows to fix or delete the row. Bound to the box's own text, so it
    // follows each keystroke.
    public class NotInOmniRigBrushConverter : IValueConverter
    {
        internal static HashSet<string> OmniRigFiles;
        private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string name = (value as string ?? "").Trim();
            if (OmniRigFiles == null || name.Length == 0 || OmniRigFiles.Contains(name))
                return DependencyProperty.UnsetValue;
            return Red;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
