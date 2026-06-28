using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HolyLogger.Contests;

namespace HolyLogger
{
    // Lets the operator pick a contest to enter Contest Mode (or exit it). Supported contests are
    // selectable; not-yet-supported ones are listed but greyed and can't be activated.
    public partial class ContestPickerWindow : Window
    {
        public class Row
        {
            public Contest Contest { get; set; }
            public bool Supported { get; set; }
            public string Name => Contest.Name;
            public string Sub => Contest.Sponsor
                + (string.IsNullOrEmpty(Contest.Period) ? "" : "   ·   " + Contest.Period);
            public string StatusTag => Supported ? "" : "coming soon";
            public double RowOpacity => Supported ? 1.0 : 0.45;
        }

        // The chosen contest when Activate is pressed; null otherwise.
        public Contest SelectedContest { get; private set; }
        // True when the operator asked to leave contest mode.
        public bool ExitRequested { get; private set; }

        public ContestPickerWindow(Contest current)
        {
            InitializeComponent();

            var rows = ContestService.All
                .OrderByDescending(c => ContestService.IsSupported(c))
                .ThenBy(c => c.Name)
                .Select(c => new Row { Contest = c, Supported = ContestService.IsSupported(c) })
                .ToList();
            LB_Contests.ItemsSource = rows;

            Btn_Exit.IsEnabled = current != null;
            if (current != null)
            {
                var match = rows.FirstOrDefault(r => r.Contest.Id == current.Id);
                if (match != null) LB_Contests.SelectedItem = match;
            }
        }

        private Row Selected => LB_Contests.SelectedItem as Row;

        private void LB_Contests_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Btn_Activate.IsEnabled = Selected != null && Selected.Supported;
        }

        private void LB_Contests_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (Selected != null && Selected.Supported) Activate();
        }

        private void Btn_Activate_Click(object sender, RoutedEventArgs e) => Activate();

        private void Activate()
        {
            if (Selected == null || !Selected.Supported) return;
            SelectedContest = Selected.Contest;
            DialogResult = true;
        }

        private void Btn_Exit_Click(object sender, RoutedEventArgs e)
        {
            ExitRequested = true;
            DialogResult = true;
        }

        private void Btn_Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
