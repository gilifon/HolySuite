using System.Windows;

namespace HolyLogger
{
    // Shown on Add (F1) when "Validate for HAM frequency" is on and the typed frequency is not an
    // amateur band. It never blocks outright — it asks, and offers a shortcut to turn the check off.
    public partial class HamFreqWarningWindow : Window
    {
        // True when the operator chose to log the QSO despite the out-of-band frequency.
        public bool SaveAnyway { get; private set; }

        // True when the operator clicked the "here" link to go turn the check off in Options > General.
        // The caller opens that page (and does NOT save the QSO).
        public bool OpenSettingsRequested { get; private set; }

        // freqKhzText is the entered frequency already formatted in kHz (empty when the box is blank).
        public HamFreqWarningWindow(string freqKhzText)
        {
            InitializeComponent();
            TB_Message.Text = string.IsNullOrWhiteSpace(freqKhzText)
                ? "Frequency is not HAM frequency"
                : $"Frequency {freqKhzText} kHz is not HAM frequency";
        }

        private void Btn_Yes_Click(object sender, RoutedEventArgs e)
        {
            SaveAnyway = true;
            DialogResult = true;   // closes the dialog
        }

        private void Btn_No_Click(object sender, RoutedEventArgs e)
        {
            SaveAnyway = false;
            DialogResult = false;
        }

        private void Link_Settings_Click(object sender, RoutedEventArgs e)
        {
            OpenSettingsRequested = true;
            SaveAnyway = false;
            DialogResult = false;
        }
    }
}
