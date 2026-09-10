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

        private void CwDecodeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            // Modeless, and only ever one of it: the operator watches the decode while he works the
            // station, so it must not hold the log hostage the way a dialog would.
            if (cwDecodeWindow != null)
            {
                try { cwDecodeWindow.Activate(); } catch (Exception swallowed) { Log.Swallow(swallowed); }
                return;
            }

            try
            {
                cwDecodeWindow = new CwDecodeWindow();
                try { cwDecodeWindow.Owner = this; } catch (Exception swallowed) { Log.Swallow(swallowed); }
                cwDecodeWindow.Closed += (s, args) => cwDecodeWindow = null;
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
