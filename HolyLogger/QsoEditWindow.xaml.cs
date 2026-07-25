using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DXCCManager;
using HolyParser;

namespace HolyLogger
{
    // A small, rounded, light-blue dialog for editing every field of ONE QSO. Nothing is written to the
    // QSO or the database until Save is pressed; Cancel (or closing) leaves the QSO exactly as it was.
    // Each box is a normal WPF TextBox, so Ctrl+Z reverts typing per-field for free. The window positions
    // itself ABOVE or BELOW the row it was opened from, so the row being edited is never hidden.
    public partial class QsoEditWindow : Window
    {
        private readonly QSO _qso;
        private readonly Rect _avoidRect;          // the source row's screen rectangle (to not cover it)
        private bool _loading;                     // suppress Freq->Band recompute while filling the form

        private static readonly EntityResolver _resolver = new EntityResolver();
        private static readonly QsoDateDisplayConverter _dateConv = new QsoDateDisplayConverter();  // yyyyMMdd <-> dd-MM-yyyy
        private static readonly QsoTimeEditConverter _timeConv = new QsoTimeEditConverter();        // HHmmss  <-> HH:mm:ss
        private static readonly CultureInfo _ci = CultureInfo.InvariantCulture;
        private static readonly string[] _bands =
            { "160M", "80M", "60M", "40M", "30M", "20M", "17M", "15M", "12M", "10M", "6M", "4M", "2M", "70CM", "23CM", "13CM" };

        public QsoEditWindow(QSO qso, Rect avoidScreenRect = default(Rect))
        {
            InitializeComponent();
            _qso = qso;
            _avoidRect = avoidScreenRect;
            foreach (var b in _bands) CB_Band.Items.Add(b);
            LoadFromQso();
            Loaded += (s, e) => PositionWindow();
        }

        // Open on the SAME monitor HolyLogger (the owner) is on - never the primary just because that is
        // where SystemParameters.WorkArea points. Reopen where the operator last left it, but only if that
        // spot is on the owner's monitor; otherwise place it beside the row on the owner's monitor.
        private void PositionWindow()
        {
            Rect wa = OwnerWorkArea();
            if (TryParseSavedPos(out double sl, out double st)
                && sl >= wa.Left - 4 && sl <= wa.Right - 80 && st >= wa.Top - 4 && st <= wa.Bottom - 30)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = sl;
                Top = st;
                return;
            }
            PositionAwayFromRow(wa);
        }

        // The work area (in WPF units) of the monitor the OWNER window is on - so the dialog lands on
        // HolyLogger's screen, and clamps to that monitor, on a multi-monitor setup.
        private Rect OwnerWorkArea()
        {
            try
            {
                if (Owner != null)
                {
                    var handle = new WindowInteropHelper(Owner).Handle;
                    var wa = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;   // device pixels
                    var src = PresentationSource.FromVisual(this) ?? PresentationSource.FromVisual(Owner);
                    Matrix m = src != null ? src.CompositionTarget.TransformFromDevice : Matrix.Identity;
                    Point tl = m.Transform(new Point(wa.Left, wa.Top));
                    Point br = m.Transform(new Point(wa.Right, wa.Bottom));
                    return new Rect(tl, br);
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            return SystemParameters.WorkArea;
        }

        private static bool TryParseSavedPos(out double left, out double top)
        {
            left = top = 0;
            string saved = Properties.Settings.Default.QsoEditWindowPos;
            if (string.IsNullOrWhiteSpace(saved)) return false;
            var parts = saved.Split(',');
            return parts.Length == 2
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out left)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out top);
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                Properties.Settings.Default.QsoEditWindowPos =
                    Left.ToString(CultureInfo.InvariantCulture) + "," + Top.ToString(CultureInfo.InvariantCulture);
                Properties.Settings.Default.Save();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            base.OnClosed(e);
        }

        private static string S(string v) => v ?? string.Empty;

        private void LoadFromQso()
        {
            if (_qso == null) return;
            _loading = true;

            TB_TitleCall.Text = S(_qso.DXCall);
            TB_Call.Text     = S(_qso.DXCall);
            TB_Country.Text  = S(_qso.Country);
            // Date / Time shown in the SAME friendly format as the log table (dd-MM-yyyy, HH:mm:ss).
            TB_Date.Text     = (_dateConv.Convert(S(_qso.Date), typeof(string), null, _ci) as string) ?? S(_qso.Date);
            TB_Time.Text     = (_timeConv.Convert(S(_qso.Time), typeof(string), null, _ci) as string) ?? S(_qso.Time);
            TB_Mode.Text     = S(_qso.Mode);
            TB_Freq.Text     = S(_qso.Freq);
            TB_RstSent.Text  = S(_qso.RST_SENT);
            TB_RstRcvd.Text  = S(_qso.RST_RCVD);
            TB_Name.Text     = S(_qso.Name);
            TB_Exchange.Text = S(_qso.SRX);
            TB_Comment.Text  = S(_qso.Comment);

            SetBandValue(S(_qso.Band));

            CB_ConfLotw.IsChecked    = _qso.LotwQslRcvd == 1;
            CB_ConfQrz.IsChecked     = _qso.QrzQslRcvd == 1;
            CB_ConfEqsl.IsChecked    = _qso.EqslQslRcvd == 1;
            CB_ConfClublog.IsChecked = _qso.ClublogQslRcvd == 1;
            CB_ConfPaper.IsChecked   = _qso.PaperQslRcvd == 1;

            _loading = false;
            UpdateBandFromFreq();   // lock/derive the band if a frequency is set
        }

        // Country is read-only and always follows the callsign's DXCC prefix (letting it disagree with the
        // callsign is exactly the kind of error awards/statistics are counted from), so re-derive it live.
        private void Call_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TB_Country == null) return;
            string call = (TB_Call.Text ?? string.Empty).Trim();
            if (call.Length == 0) { TB_Country.Text = string.Empty; return; }
            try
            {
                string name = _resolver.GetDXCC(call)?.Name;
                if (!string.IsNullOrEmpty(name) && !string.Equals(name, "Unknown", StringComparison.OrdinalIgnoreCase))
                    TB_Country.Text = name;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // The band is a pickable dropdown ONLY while there is no frequency. The moment a valid frequency is
        // present the band is derived from it (the authoritative source) and the dropdown is locked, so the
        // two can never disagree.
        private void Freq_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            UpdateBandFromFreq();
        }

        private void UpdateBandFromFreq()
        {
            if (CB_Band == null) return;
            string mhz = HolyLogParser.NormalizeFreqToMhz((TB_Freq.Text ?? string.Empty).Trim());
            if (!string.IsNullOrWhiteSpace(mhz))
            {
                string band = HolyLogParser.convertFreqToBand(mhz);
                if (!string.IsNullOrWhiteSpace(band)) SetBandValue(band);
                CB_Band.IsEnabled = false;   // derived from frequency -> locked
            }
            else
            {
                CB_Band.IsEnabled = true;    // no frequency -> the operator picks the band
            }
        }

        private void SetBandValue(string band)
        {
            band = (band ?? string.Empty).Trim();
            if (band.Length == 0) { CB_Band.SelectedItem = null; CB_Band.Text = string.Empty; return; }
            if (!CB_Band.Items.Contains(band)) CB_Band.Items.Insert(0, band);
            CB_Band.SelectedItem = band;
            CB_Band.Text = band;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_qso == null) { DialogResult = false; return; }

            // Date / Time are entered in the friendly format; reject anything unparseable rather than
            // storing junk (ConvertBack returns Binding.DoNothing, i.e. not a string, for a bad value).
            object dv = _dateConv.ConvertBack(TB_Date.Text, typeof(string), null, _ci);
            if (!(dv is string ds)) { HolyMessageBox.ShowError("Date must look like 03-05-2026.", "Edit QSO", this); return; }
            object tv = _timeConv.ConvertBack(TB_Time.Text, typeof(string), null, _ci);
            if (!(tv is string ts)) { HolyMessageBox.ShowError("Time must look like 20:34:59.", "Edit QSO", this); return; }

            try
            {
                _qso.DXCall    = TB_Call.Text.Trim();
                _qso.Country   = TB_Country.Text.Trim();
                _qso.Date      = ds;
                _qso.Time      = ts;
                _qso.Band      = (CB_Band.Text ?? string.Empty).Trim();
                _qso.Mode      = TB_Mode.Text.Trim();
                _qso.Freq      = TB_Freq.Text.Trim();
                _qso.RST_SENT  = TB_RstSent.Text.Trim();
                _qso.RST_RCVD  = TB_RstRcvd.Text.Trim();
                _qso.Name      = TB_Name.Text.Trim();
                _qso.SRX       = TB_Exchange.Text.Trim();
                _qso.Comment   = TB_Comment.Text.Trim();

                _qso.LotwQslRcvd    = CB_ConfLotw.IsChecked    == true ? 1 : 0;
                _qso.QrzQslRcvd     = CB_ConfQrz.IsChecked     == true ? 1 : 0;
                _qso.EqslQslRcvd    = CB_ConfEqsl.IsChecked    == true ? 1 : 0;
                _qso.ClublogQslRcvd = CB_ConfClublog.IsChecked == true ? 1 : 0;
                _qso.PaperQslRcvd   = CB_ConfPaper.IsChecked   == true ? 1 : 0;

                var dal = DataAccess.GetInstance();
                dal?.Update(_qso);                 // data fields
                dal?.UpdateConfirmations(_qso);    // the five confirmation flags
                DialogResult = true;
            }
            catch (Exception ex)
            {
                HolyMessageBox.ShowError("Could not save the QSO: " + ex.Message, "Edit QSO", this);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                try { DragMove(); } catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // Place the window so it never covers the row it was opened from: below the row if there is room,
        // otherwise above it. Horizontally centred on the owner, clamped to the owner monitor's work area.
        private void PositionAwayFromRow(Rect wa)
        {
            double w = ActualWidth > 0 ? ActualWidth : Width;
            double h = ActualHeight;
            const double gap = 8;

            double left = (Owner != null)
                ? Owner.Left + (Owner.Width - w) / 2
                : wa.Left + (wa.Width - w) / 2;
            left = Math.Max(wa.Left, Math.Min(left, wa.Right - w));

            double top;
            bool haveRow = _avoidRect.Width > 0 && _avoidRect.Height > 0;
            if (haveRow && _avoidRect.Bottom + gap + h <= wa.Bottom)
                top = _avoidRect.Bottom + gap;                       // below the row
            else if (haveRow && _avoidRect.Top - gap - h >= wa.Top)
                top = _avoidRect.Top - gap - h;                      // above the row
            else
                top = Math.Max(wa.Top, Math.Min(wa.Top + (wa.Height - h) / 2, wa.Bottom - h));

            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
    }
}
