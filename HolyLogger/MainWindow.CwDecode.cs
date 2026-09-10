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
        /// Opens the decode window alongside the CW keyer, listening from the moment it appears.
        /// Called when the keyer window is shown.
        ///
        /// TIED TO THE KEYER, NOT TO THE RADIO'S MODE. It followed CW mode first, which looked right
        /// and was not: a rig left sitting in CW all evening is not an operator working CW, and the
        /// window kept opening - and holding the sound card - when nobody was using it. Opening the
        /// keyer is the moment he actually starts, and closing it is the moment he stops.
        /// </summary>
        internal void OpenCwDecodeWithKeyer()
        {
            try
            {
                if (cwDecodeWindow != null) return;

                OpenCwDecodeWindow();
                cwDecodeOpenedItself = true;

                // Opened by itself, so it listens by itself too. Being handed a window that then
                // waits to be told to listen would waste the point of opening it automatically.
                if (cwDecodeWindow != null) cwDecodeWindow.StartListeningNow();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        /// <summary>
        /// Closes the decode window when the keyer closes - but only if it opened itself. The sound
        /// card is given back as it goes, which matters where another program wants the same codec.
        /// </summary>
        internal void CloseCwDecodeIfItOpenedItself()
        {
            try
            {
                if (cwDecodeWindow == null || !cwDecodeOpenedItself) return;
                cwDecodeOpenedItself = false;
                cwDecodeWindow.Close();
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
