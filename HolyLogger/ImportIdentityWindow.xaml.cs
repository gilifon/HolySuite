using System.Collections.Generic;
using System.Windows;

namespace HolyLogger
{
    // Shown before an ADIF import runs so the operator confirms (and can cancel) the identity the log
    // will get. The station callsign is read from the ADIF and NOT editable — the user only picks when
    // the file holds more than one. The operator is editable and optional: it is not part of a log's
    // identity, and ADIF files often carry no OPERATOR at all.
    public partial class ImportIdentityWindow : Window
    {
        public string Callsign { get; private set; }
        public string Operator { get; private set; }

        // What to do with the records in the file that name no callsign at all - neither
        // STATION_CALLSIGN nor OPERATOR. True: they get the callsign chosen above. False: they are not
        // imported, and are written out as a file to correct. Only asked when the file holds some.
        public bool FillMissingCallsign { get; private set; } = true;

        public ImportIdentityWindow(List<string> stationCallsigns, List<string> operators, string fileName,
                                    int recordsWithNoCallsign = 0)
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

            // The Station Callsign field as it looks on the main window, showing what is chosen here.
            PicturePlace.Content = Helper.StationCallsignPicture(ValueOf(CB_Station), out _pictureValue);
            CB_Station.SelectionChanged += (s, e) => UpdatePicture();
            CB_Station.LostFocus += (s, e) => UpdatePicture();
            UpdatePicture();

            // THE QUESTION SITS NEXT TO ITS ANSWER. Records that name no callsign have to be given one
            // or left out, and the callsign they would be given is the one being chosen in this window -
            // so the choice is offered here, naming that callsign, and follows it if it is changed.
            if (recordsWithNoCallsign > 0)
            {
                NoCallPanel.Visibility = Visibility.Visible;
                NoCallText.Text = recordsWithNoCallsign.ToString("N0")
                                + (recordsWithNoCallsign == 1 ? " QSO in this file does" : " QSOs in this file do")
                                + " not say which callsign made them.";
                UpdateFillOptionText();
                CB_Station.SelectionChanged += (s, e) => UpdateFillOptionText();
                CB_Station.LostFocus += (s, e) => UpdateFillOptionText();   // typed, when the file named none
            }
        }

        // The callsign shown inside the picture of the main window's field.
        private System.Windows.Controls.TextBlock _pictureValue;

        private void UpdatePicture()
        {
            if (_pictureValue == null) return;
            _pictureValue.Text = ValueOf(CB_Station).ToUpperInvariant();
            UpdateFillOptionText();
        }

        private void UpdateFillOptionText()
        {
            if (RB_FillMissingText == null) return;
            string call = ValueOf(CB_Station).ToUpperInvariant();
            RB_FillMissingText.Text = call.Length > 0 ? "Give them " + call
                                                      : "Give them the callsign above";
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
            // NO CHECK ON THE OPERATOR. A log's identity is its station callsign; the operator is not
            // part of it (one callsign, many operators - a club station), and nothing in the program
            // needs it. Demanding it here stopped imports of files that simply never carried the field.
            Callsign = call.ToUpperInvariant();
            Operator = opr.ToUpperInvariant();
            FillMissingCallsign = RB_LeaveMissing.IsChecked != true;
            DialogResult = true;
            Close();
        }
    }
}
