using System.Windows;

namespace HolyLogger
{
    // First-run dialog: the user must name their Main Log. If there are existing (pre-logs) QSOs,
    // they also choose whether to bring them into this log or start it empty (existing QSOs are then
    // preserved in a separate "Previous Log"). This dialog only collects the choices; MainWindow does
    // the actual database work.
    public partial class LogSetupWindow : Window
    {
        public string LogName { get; private set; }
        public bool ImportExisting { get; private set; } = true;
        public bool Completed { get; private set; }

        public LogSetupWindow(int existingQsoCount)
        {
            InitializeComponent();

            if (existingQsoCount > 0)
            {
                ExistingPanel.Visibility = Visibility.Visible;
                RB_ImportText.Text = "Bring my " + existingQsoCount.ToString("N0") + " existing QSOs into this log";
            }

            Loaded += (s, e) => { TB_Name.Focus(); TB_Name.SelectAll(); };
        }

        private void Btn_Ok_Click(object sender, RoutedEventArgs e)
        {
            string name = (TB_Name.Text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(name))
            {
                HolyMessageBox.ShowWarning("Please enter a name for your log.", "Log name", this);
                return;
            }

            LogName = name;
            ImportExisting = RB_Import.IsChecked == true;
            Completed = true;
            Close();
        }
    }
}
