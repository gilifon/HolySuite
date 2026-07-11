using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace HolyLogger
{
    // One-time dialog to assign a log's PERMANENT identity (station callsign + operator). Pre-filled from
    // the callsigns actually used in the log's QSOs (most common first); if the log is empty, from the
    // passed-in defaults (the main-window callsign/operator). Editable, because a legacy/imported log may
    // belong to a different call than the current operating one. Shown when a log without an identity
    // becomes active, so you never log into an identity-less log.
    public partial class SetIdentityWindow : Window
    {
        public string Callsign { get; private set; }
        public string Operator { get; private set; }

        public SetIdentityWindow(IEnumerable<LogIdentityCandidate> candidates, string logName,
                                 string defaultCallsign, string defaultOperator)
        {
            InitializeComponent();

            Header.Text = "Set the identity for \"" + (logName ?? "this log") + "\"";

            var list = new List<LogIdentityCandidate>();
            if (candidates != null) list.AddRange(candidates);

            if (list.Count > 0)
            {
                CandidatePanel.Visibility = Visibility.Visible;
                CB_Candidates.ItemsSource = list;
                CB_Candidates.SelectedIndex = 0;   // most-frequent -> fills the boxes via SelectionChanged
            }
            else
            {
                TB_Callsign.Text = (defaultCallsign ?? string.Empty).Trim();
                TB_Operator.Text = (defaultOperator ?? string.Empty).Trim();
            }

            Loaded += (s, e) => { TB_Callsign.Focus(); TB_Callsign.SelectAll(); };
        }

        private void CB_Candidates_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CB_Candidates.SelectedItem is LogIdentityCandidate c)
            {
                TB_Callsign.Text = c.Callsign;
                TB_Operator.Text = c.Operator;
            }
        }

        private void Btn_Ok_Click(object sender, RoutedEventArgs e)
        {
            string call = (TB_Callsign.Text ?? string.Empty).Trim();
            string opr = (TB_Operator.Text ?? string.Empty).Trim();
            if (call.Length == 0 || opr.Length == 0)
            {
                HolyMessageBox.ShowWarning("Enter both a station callsign and an operator.", "Set log identity", this);
                return;
            }
            Callsign = call;
            Operator = opr;
            DialogResult = true;
            Close();
        }
    }
}
