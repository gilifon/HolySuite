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
        // The one list of bands the program works (HolyParser), shared with the main window's Band
        // picker so a band offered in one place is offered in the other.
        private static readonly string[] _bands = HolyLogParser.KnownBands;

        // READ-ONLY IS THE SAME WINDOW WITH NOTHING TO PRESS. The Log Fixer opens a contact to be
        // looked at, not changed, so the whole form is frozen rather than a second, thinner window
        // being written and then drifting out of step with this one.
        private readonly bool _viewOnly;

        public QsoEditWindow(QSO qso, Rect avoidScreenRect = default(Rect), bool viewOnly = false)
        {
            InitializeComponent();
            _qso = qso;
            _avoidRect = avoidScreenRect;
            _viewOnly = viewOnly;
            foreach (var b in _bands) CB_Band.Items.Add(b);
            LoadFromQso();
            Loaded += (s, e) =>
            {
                // Freezing waits for Loaded because it walks the visual tree, and there is no visual
                // tree to walk until the window has been laid out.
                if (_viewOnly) MakeViewOnly();
                PositionWindow();
            };
        }

        // Every box refuses typing, the pickers and the ticks are frozen, Save is gone and Cancel says
        // Close. The text is left black and selectable on purpose: this window is for reading a QSO,
        // and greyed-out boxes are exactly what a reader does not want.
        private void MakeViewOnly()
        {
            Title = "QSO";
            TB_TitleMode.Text = "Full View";
            BTN_Save.Visibility = Visibility.Collapsed;
            BTN_Save.IsDefault = false;   // otherwise Enter still finds it, hidden or not
            BTN_Cancel.Content = "Close";
            BTN_Cancel.IsDefault = true;
            Freeze(this);
        }

        private static void Freeze(DependencyObject root)
        {
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);

                TextBox tb = child as TextBox;
                if (tb != null) { tb.IsReadOnly = true; tb.IsReadOnlyCaretVisible = false; }

                ComboBox cb = child as ComboBox;
                if (cb != null) { cb.IsReadOnly = true; cb.IsHitTestVisible = false; cb.Focusable = false; }

                CheckBox chk = child as CheckBox;
                if (chk != null) { chk.IsHitTestVisible = false; chk.Focusable = false; }

                Freeze(child);
            }
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
                // The window is tall (every field of the QSO is on it), so a remembered top that used to
                // fit can now push Save/Cancel off the bottom of the screen. Slide it up until it fits.
                Left = sl;
                Top = ActualHeight > 0 && st + ActualHeight > wa.Bottom
                    ? Math.Max(wa.Top, wa.Bottom - ActualHeight)
                    : st;
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
            TB_TitleId.Text   = _qso.id > 0 ? "#" + _qso.id : string.Empty;
            TB_Call.Text     = S(_qso.DXCall);
            TB_Country.Text  = S(_qso.Country);
            // Date / Time shown in the SAME friendly format as the log table (dd-MM-yyyy, HH:mm:ss).
            TB_Date.Text     = (_dateConv.Convert(S(_qso.Date), typeof(string), null, _ci) as string) ?? S(_qso.Date);
            TB_Time.Text     = (_timeConv.Convert(S(_qso.Time), typeof(string), null, _ci) as string) ?? S(_qso.Time);
            TB_Mode.Text     = S(_qso.Mode);
            TB_Submode.Text  = S(_qso.SUBMode);
            TB_Freq.Text     = S(_qso.Freq);
            TB_RstSent.Text  = S(_qso.RST_SENT);
            TB_RstRcvd.Text  = S(_qso.RST_RCVD);
            TB_Name.Text     = S(_qso.Name);
            TB_Exchange.Text = S(_qso.SRX);
            TB_Comment.Text  = S(_qso.Comment);

            TB_DxLocator.Text = S(_qso.DXLocator);
            TB_Iota.Text      = S(_qso.Iota);
            TB_SotaRef.Text   = S(_qso.SotaRef);
            TB_PotaRef.Text   = S(_qso.PotaRef);
            TB_WwffRef.Text   = S(_qso.WwffRef);
            // The program drop-down carries the same eight names the main form's Other window offers,
            // and its tooltip spells out whichever one is in the box - "ARLHS" means nothing on its own.
            TB_Sig.ItemsSource = OtherActivityWindow.Known;
            TB_Sig.Text       = S(_qso.Sig);
            ShowProgramMeaning();
            TB_Sig.LostFocus += (s, e) => ShowProgramMeaning();
            TB_Sig.SelectionChanged += (s, e) => Dispatcher.BeginInvoke(new Action(ShowProgramMeaning));
            TB_SigInfo.Text   = S(_qso.SigInfo);
            TB_Continent.Text = S(_qso.Continent);
            TB_CqZone.Text    = S(_qso.CQZone);
            TB_ItuZone.Text   = S(_qso.ITUZone);
            TB_State.Text     = S(_qso.State);
            TB_Qth.Text       = S(_qso.Qth);
            TB_PropMode.Text  = S(_qso.PROP_MODE);
            TB_SatName.Text   = S(_qso.SAT_NAME);

            TB_MyCall.Text    = S(_qso.MyCall);
            TB_Operator.Text  = S(_qso.Operator);
            TB_MySquare.Text  = S(_qso.STX);
            TB_MyLocator.Text = S(_qso.MyLocator);
            // Blank for the machine IDs older QSOs carry here (see QSO.IsGeneratedSoapboxId): the box is
            // for the operator's own words, or a note an imported ADIF brought with it.
            TB_Soapbox.Text   = S(QSO.SoapboxText(_qso.SOAPBOX));

            SetBandValue(S(_qso.Band));

            CB_ConfLotw.IsChecked    = _qso.LotwQslRcvd == 1;
            CB_ConfQrz.IsChecked     = _qso.QrzQslRcvd == 1;
            CB_ConfEqsl.IsChecked    = _qso.EqslQslRcvd == 1;
            CB_ConfClublog.IsChecked = _qso.ClublogQslRcvd == 1;
            CB_ConfPaper.IsChecked   = _qso.PaperQslRcvd == 1;

            TB_LotwNote.Text    = ConfNote(_qso.LotwQslRcvd, _qso.LotwQslRDate, _qso.LotwDeletedEntity);
            TB_QrzNote.Text     = ConfNote(_qso.QrzQslRcvd, _qso.QrzQslRDate, _qso.QrzDeletedEntity);
            TB_EqslNote.Text    = ConfNote(_qso.EqslQslRcvd, _qso.EqslQslRDate, _qso.EqslDeletedEntity);
            TB_ClublogNote.Text = ConfNote(_qso.ClublogQslRcvd, _qso.ClublogQslRDate, _qso.ClublogDeletedEntity);
            // A paper card is recorded by hand and carries no date or DXCC code with it.
            TB_PaperNote.Text   = _qso.PaperQslRcvd == 1 ? "by post" : string.Empty;

            TB_UpLotw.Text    = UploadNote(_qso.LotwStatus);
            TB_UpQrz.Text     = UploadNote(_qso.QrzStatus);
            TB_UpEqsl.Text    = UploadNote(_qso.EqslStatus);
            TB_UpClublog.Text = UploadNote(_qso.ClublogStatus);

            DeriveFromCall(onlyIfBlank: true);   // fill Country/Continent only where the QSO has none

            _loading = false;
            UpdateBandFromFreq();   // lock/derive the band if a frequency is set
        }

        // The line under a confirmation tick: when the service recorded it, and whether the entity it was
        // credited to is a DELETED DXCC entity. Nothing at all when the QSO is not confirmed there.
        private static string ConfNote(int rcvd, string rdate, int deletedEntity)
        {
            if (rcvd != 1) return string.Empty;
            string when = (_dateConv.Convert(S(rdate), typeof(string), null, _ci) as string) ?? S(rdate);
            if (string.IsNullOrWhiteSpace(when)) when = "date not given";
            return deletedEntity == 1 ? when + "\ndeleted DXCC entity" : when;
        }

        // The upload-queue states a QSO can be in, in the words the upload menus use.
        private static string UploadNote(int status)
        {
            switch (status)
            {
                case 1:  return "Uploaded";
                case 2:  return "Rejected";
                default: return "Pending";
            }
        }

        // Country and Continent are read-only and always follow the callsign's DXCC prefix (letting either
        // disagree with the callsign is exactly the kind of error awards/statistics are counted from), so
        // re-derive them the moment the callsign is retyped.
        private void Call_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            DeriveFromCall(onlyIfBlank: false);
        }

        // Country and Continent from ONE prefix lookup, the same one the main window logs with.
        // onlyIfBlank is used at load time: what the QSO actually carries is shown as-is, and only a field
        // the QSO never had (an old row logged before the field existed) is filled in from the prefix.
        private void DeriveFromCall(bool onlyIfBlank)
        {
            if (TB_Country == null || TB_Continent == null) return;
            string call = (TB_Call.Text ?? string.Empty).Trim();
            if (call.Length == 0)
            {
                if (!onlyIfBlank) { TB_Country.Text = string.Empty; TB_Continent.Text = string.Empty; }
                return;
            }
            try
            {
                // Resolved on the QSO's own date, so an old contact is named by the entity that existed
                // then rather than by whoever holds the prefix today.
                var dxcc = CountryLookup.Shared.Resolve(call, CountryLookup.QsoDate(_qso != null ? _qso.Date : null, _qso != null ? _qso.Time : null));
                string name = dxcc?.Name;
                if (!string.IsNullOrEmpty(name) && !string.Equals(name, "Unknown", StringComparison.OrdinalIgnoreCase)
                    && (!onlyIfBlank || string.IsNullOrWhiteSpace(TB_Country.Text)))
                    TB_Country.Text = name;
                // "XX" is the resolver's way of saying it did not recognise the prefix - keep what the QSO
                // already carried rather than overwriting it with a non-continent.
                string cont = dxcc?.Continent;
                if (!string.IsNullOrEmpty(cont) && !string.Equals(cont, "XX", StringComparison.OrdinalIgnoreCase)
                    && (!onlyIfBlank || string.IsNullOrWhiteSpace(TB_Continent.Text)))
                    TB_Continent.Text = cont;
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

        // Puts the full name of the typed program in the box's tooltip, so "ARLHS" can be checked
        // without opening anything. Falls back to the general hint for a name we do not know.
        private void ShowProgramMeaning()
        {
            string meaning = OtherActivityWindow.DescriptionOf(TB_Sig.Text);
            TB_Sig.ToolTip = meaning.Length > 0
                ? meaning
                : "Any other program, by its short name — WCA for castles, MOTA for mills, ARLHS for lighthouses";
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
                _qso.SUBMode   = TB_Submode.Text.Trim();
                _qso.DXLocator = TB_DxLocator.Text.Trim();
                _qso.Iota      = TB_Iota.Text.Trim();
                _qso.SotaRef   = TB_SotaRef.Text.Trim();
                _qso.PotaRef   = TB_PotaRef.Text.Trim();
                _qso.WwffRef   = TB_WwffRef.Text.Trim();
                // Upper-cased here rather than by CharacterCasing, which a ComboBox does not have.
                _qso.Sig       = (TB_Sig.Text ?? string.Empty).Trim().ToUpperInvariant();
                _qso.SigInfo   = TB_SigInfo.Text.Trim();
                _qso.Continent = TB_Continent.Text.Trim();
                _qso.CQZone    = TB_CqZone.Text.Trim();
                _qso.ITUZone   = TB_ItuZone.Text.Trim();
                _qso.State     = TB_State.Text.Trim();
                _qso.Qth       = TB_Qth.Text.Trim();
                _qso.PROP_MODE = TB_PropMode.Text.Trim();
                _qso.SAT_NAME  = TB_SatName.Text.Trim();
                // MyCall is deliberately NOT written back: the station callsign is the log's identity and
                // this window only displays it (see the read-only box in the XAML). Leaving the assignment
                // out means no path through this editor can change it, whatever the box ends up holding.
                _qso.Operator  = TB_Operator.Text.Trim();
                _qso.STX       = TB_MySquare.Text.Trim();
                _qso.MyLocator = TB_MyLocator.Text.Trim();
                _qso.SOAPBOX   = TB_Soapbox.Text.Trim();

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
