using System.Collections.Generic;
using System.Windows;

namespace HolyLogger
{
    // Shown before an ADIF import runs so the operator confirms (and can cancel) the identity the log
    // will get. The station callsign is read from the ADIF and NOT editable — the user only picks when
    // the file holds more than one. The operator is editable, because ADIF files often omit OPERATOR and
    // an identity needs both.
    public partial class ImportIdentityWindow : Window
    {
        public string Callsign { get; private set; }
        public string Operator { get; private set; }

        public ImportIdentityWindow(List<string> stationCallsigns, List<string> operators, string fileName)
        {
            InitializeComponent();

            Header.Text = "Confirm the identity of \"" + (fileName ?? "the imported log") + "\"";

            stationCallsigns = stationCallsigns ?? new List<string>();
            operators = operators ?? new List<string>();

            if (stationCallsigns.Count > 0)
            {
                // From the ADIF -> pick only, never edit.
                CB_Station.IsEditable = false;
                CB_Station.ItemsSource = stationCallsigns;
                CB_Station.SelectedIndex = 0;
            }
            else
            {
                // The file has no station callsign -> the user must supply one.
                CB_Station.IsEditable = true;
            }

            CB_Operator.ItemsSource = operators;
            if (operators.Count > 0) CB_Operator.SelectedIndex = 0;   // else stays blank for the user to type
        }

        private static string ValueOf(System.Windows.Controls.ComboBox cb)
            => ((cb.SelectedItem as string) ?? cb.Text ?? string.Empty).Trim();

        private void Btn_Ok_Click(object sender, RoutedEventArgs e)
        {
            string call = ValueOf(CB_Station);
            string opr = ValueOf(CB_Operator);
            if (call.Length == 0)
            {
                HolyMessageBox.ShowWarning("The station callsign is required.", "Imported log identity", this);
                return;
            }
            if (opr.Length == 0)
            {
                HolyMessageBox.ShowWarning("The ADIF file has no operator — please enter the operator callsign.", "Imported log identity", this);
                return;
            }
            Callsign = call.ToUpperInvariant();
            Operator = opr.ToUpperInvariant();
            DialogResult = true;
            Close();
        }
    }
}
