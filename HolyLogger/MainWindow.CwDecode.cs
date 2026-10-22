using System;
using System.Windows;

namespace HolyLogger
{
    // Everything the main window needs for CW decoding lives in this file and nowhere else.
    //
    // KEPT APART ON PURPOSE. The decoding work belongs to the 64-bit branch, while bug fixes and
    // improvements go on being made on the 32-bit one and are merged across. Every line of this work
    // that sits inside MainWindow.xaml.cs is a line those merges have to argue about; in a file of
    // its own there is nothing to argue about.
    public partial class MainWindow
    {
        // IT FOLLOWS THE RADIO INTO AND OUT OF CW, UNLESS HE CLOSED IT (2026-10-21, his rules, in this order):
        // first it was tied to the keyer, which brought it back when he had closed it; then it was left to him
        // alone, and stayed on in SSB ("this is not good"). Now: the radio leaving CW closes it, coming into CW
        // opens it - the same moments the keyer uses (UpdateVoiceMessageAvailabilityState). Closed BY HIM, it
        // stays closed, in CW too, until he opens it from the View menu - as the keyer does. Closed by the
        // program (leaving CW, or the program closing) does not count as his.
        CwDecodeWindow cwDecodeWindow;
        bool _cwDecodeClosingByProgram;

        void CloseCwDecodeByProgram()
        {
            if (cwDecodeWindow == null) return;
            _cwDecodeClosingByProgram = true;
            try { cwDecodeWindow.Close(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            finally { _cwDecodeClosingByProgram = false; }
        }

        // The radio came into CW: open, unless he closed it himself.
        void OpenCwDecodeOnCw()
        {
            try
            {
                if (cwDecodeWindow == null && Properties.Settings.Default.CwDecodeWindowWasOpen) OpenCwDecodeWindow();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        /// <summary>
        /// A callsign double-clicked in the decoded text goes into the DX Callsign box - the same
        /// box he would have typed it into, so everything downstream follows on its own: {CALL} in
        /// a keyer macro reads from there, and so does Add.
        ///
        /// TYPED IN, NOT LOGGED. It is put in the box and no further: the decoder gets characters
        /// wrong, and a callsign that arrived by machine deserves the same look-over as one typed by
        /// hand before it goes in the log.
        /// </summary>
        void TakeCallsignFromDecode(string callsign)
        {
            if (string.IsNullOrWhiteSpace(callsign)) return;

            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (TB_DXCallsign == null) return;

                        TB_DXCallsign.Text = callsign.Trim().ToUpperInvariant();
                        TB_DXCallsign.CaretIndex = TB_DXCallsign.Text.Length;

                        // The focus goes with it: the next thing he does is nearly always to fill in
                        // the rest of the contact, and a callsign that lands in a box he then has to
                        // click into has saved him nothing.
                        TB_DXCallsign.Focus();
                    }
                    catch (Exception swallowed) { Log.Swallow(swallowed); }
                }));
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        private void CwDecodeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            // Modeless, and only ever one of it: the operator watches the decode while he works the
            // station, so it must not hold the log hostage the way a dialog would.
            if (cwDecodeWindow != null)
            {
                try { cwDecodeWindow.Activate(); } catch (Exception swallowed) { Log.Swallow(swallowed); }
                return;
            }

            OpenCwDecodeWindow();
        }

        void OpenCwDecodeWindow()
        {
            try
            {
                cwDecodeWindow = new CwDecodeWindow();
                try { cwDecodeWindow.Owner = this; } catch (Exception swallowed) { Log.Swallow(swallowed); }
                cwDecodeWindow.Closed += (s, args) =>
                {
                    cwDecodeWindow = null;
                    // Closed by him, it stays closed - next time and in CW; closed by the program (leaving CW,
                    // or the program going), it comes back.
                    if (!_isShutdownCleanupDone && !_cwDecodeClosingByProgram) RememberCwDecodeOpen(false);
                };
                cwDecodeWindow.CallsignChosen += TakeCallsignFromDecode;
                cwDecodeWindow.ShiftRadio = ShiftRigForPitch;
                cwDecodeWindow.WentWrongWay = TuneWentWrongWay;
                cwDecodeWindow.OpenSettings = OpenOptionsOnCwDecoder;
                cwDecodeWindow.Show();
                RememberCwDecodeOpen(true);
            }
            catch (Exception ex)
            {
                Log.Swallow(ex);
                cwDecodeWindow = null;
            }
        }

        static void RememberCwDecodeOpen(bool open)
        {
            if (Properties.Settings.Default.CwDecodeWindowWasOpen == open) return;
            Properties.Settings.Default.CwDecodeWindowWasOpen = open;
            SettingsFlush.RequestSave();
        }
    }
}
