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
        CwDecodeWindow cwDecodeWindow;

        // Whether THIS program opened the window because the radio went to CW, or the operator asked
        // for it from the View menu. Only a window that opened by itself is allowed to close by
        // itself: one the operator opened on purpose stays where he put it, whatever the radio does.
        bool cwDecodeOpenedItself;

        /// <summary>
        /// Opens the decode window when the radio goes to CW and closes it again when it leaves.
        /// Called from UpdateVoiceMessageAvailabilityState, which is where every mode change already
        /// arrives - the same place that decides whether the keyer may open.
        /// </summary>
        internal void FollowCwModeForDecode()
        {
            try
            {
                if (IsCwModeActive())
                {
                    if (cwDecodeWindow != null) return;

                    OpenCwDecodeWindow();
                    cwDecodeOpenedItself = true;

                    // Opened by itself, so it starts listening by itself too. Being handed a window
                    // that then waits to be told to listen would make the whole point of opening it
                    // automatically pointless.
                    if (cwDecodeWindow != null) cwDecodeWindow.StartListeningNow();
                    return;
                }

                // Not CW any more. The audio device is given back as the window closes, which matters
                // on a station where another program wants the same codec.
                if (cwDecodeWindow != null && cwDecodeOpenedItself)
                {
                    cwDecodeOpenedItself = false;
                    cwDecodeWindow.Close();
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        private void CwDecodeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            // Modeless, and only ever one of it: the operator watches the decode while he works the
            // station, so it must not hold the log hostage the way a dialog would.
            // Asked for by hand, so it is his window now: it will not close itself when the radio
            // leaves CW.
            cwDecodeOpenedItself = false;

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
                cwDecodeWindow.Closed += (s, args) => { cwDecodeWindow = null; cwDecodeOpenedItself = false; };
                cwDecodeWindow.Show();
            }
            catch (Exception ex)
            {
                Log.Swallow(ex);
                cwDecodeWindow = null;
            }
        }
    }
}
