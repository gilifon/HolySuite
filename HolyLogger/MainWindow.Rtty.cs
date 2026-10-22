using System;
using System.Windows;

namespace HolyLogger
{
    // Everything the main window needs for RTTY decoding lives in this file, like the CW decoder's
    // in MainWindow.CwDecode.cs: one file the branch merges never have to argue about.
    public partial class MainWindow
    {
        RttyDecodeWindow rttyDecodeWindow;

        private void RttyDecodeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            OpenRttyDecodeWindow();
        }

        /// <summary>
        /// The Radio Control Panel's mode buttons (his request, 2026-10-22): pressing RTTY opens the
        /// RTTY decoder, pressing any other mode closes it. The radio's own mode does the same - see
        /// FollowRadioModeForRtty.
        /// </summary>
        internal void FollowPanelMode(string mode)
        {
            try
            {
                if (string.Equals(mode, "RTTY", StringComparison.OrdinalIgnoreCase)) OpenRttyForMode();
                else if (rttyDecodeWindow != null) rttyDecodeWindow.Close();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // Whether the radio was on RTTY at the last reading; null before the first one.
        bool? _radioWasOnRtty;

        /// <summary>
        /// THE RADIO'S OWN MODE OPENS AND CLOSES IT TOO (his request, 2026-10-22): the mode knob turned
        /// to RTTY opens the decoder, turned to anything else closes it. Only a CHANGE does it - OmniRig
        /// reports many times a second, so acting on the state itself would reopen a window he had
        /// just closed while the radio sat on RTTY.
        ///
        /// AND THE FIRST READING COUNTS AS A CHANGE TO RTTY (his report, same day: HolyLogger started
        /// with the radio already on RTTY and no decoder came up). Never before the main window is on
        /// screen, though: a window owned by one not yet shown throws, and at start that can stop
        /// HolyLogger starting at all - so until then it waits for ContentRendered.
        ///
        /// RTTY is the slot the panel's RTTY button lights for and presses (PM_DIG_L - see
        /// UpdateRadioPanel). Called from ShowRigParams, on the window's thread.
        /// </summary>
        void FollowRadioModeForRtty()
        {
            try
            {
                bool online = RadioFollowedForRtty();
                ShowDialOnRtty(online);
                if (!online) return;

                bool rtty = (int)Rig.Mode == PM_DIG_L;
                bool? was = _radioWasOnRtty;
                _radioWasOnRtty = rtty;
                if (was != null && was.Value == rtty) return;

                if (!rtty)
                {
                    if (rttyDecodeWindow != null) rttyDecodeWindow.Close();
                    return;
                }
                if (IsLoaded && IsVisible) { OpenRttyForMode(); return; }

                EventHandler whenShown = null;
                whenShown = (s, e) =>
                {
                    ContentRendered -= whenShown;
                    // Still on RTTY by the time the main window is up.
                    if (_radioWasOnRtty == true) OpenRttyForMode();
                };
                ContentRendered += whenShown;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // CAT on, the radio answering, and not in Manual - the panel's own test.
        bool RadioFollowedForRtty()
        {
            return Properties.Settings.Default.EnableOmniRigCAT
                   && OmniRigEngine != null && Rig != null
                   && Rig.Status == OmniRig.RigStatusX.ST_ONLINE
                   && !Properties.Settings.Default.isManualMode;
        }

        // The radio's dial on the RTTY window's bar (his request, 2026-10-22) - the real reading, with no
        // satellite shift, as the Radio Control Panel shows it. Nothing when there is no radio.
        void ShowDialOnRtty(bool online)
        {
            if (rttyDecodeWindow == null) return;
            double khz = 0;
            if (online)
            {
                try { khz = Rig.GetRxFrequency() / 1000.0; }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }
            rttyDecodeWindow.ShowRadioKhz(khz);
        }

        /// <summary>
        /// Opened because the mode became RTTY: the CW windows go FIRST (his report, 2026-10-22 - CW to RTTY
        /// put the RTTY decoder up while the CW decoder was still there, which then closed a moment later).
        /// They would go anyway, when the mode change reaches the CW keyer's own check (see
        /// UpdateVoiceMessageAvailabilityState); this only does it before, not after. The same rule as
        /// there: only CW windows that came up with CW - one he opened by hand outside CW is his.
        /// Closed by the program, so they come back the next time the radio goes to CW.
        /// </summary>
        void OpenRttyForMode()
        {
            if (_cwKeyboardWasWanted)
            {
                try { CloseCwKeyboardByProgram(); } catch (Exception swallowed) { Log.Swallow(swallowed); }
                try { CloseCwDecodeByProgram(); } catch (Exception swallowed) { Log.Swallow(swallowed); }
            }
            OpenRttyDecodeWindow();
        }

        void OpenRttyDecodeWindow()
        {
            // Modeless and only ever one, like the CW decoder: it is watched while the station is worked.
            if (rttyDecodeWindow != null)
            {
                try { rttyDecodeWindow.Activate(); } catch (Exception swallowed) { Log.Swallow(swallowed); }
                return;
            }

            try
            {
                rttyDecodeWindow = new RttyDecodeWindow();
                try { rttyDecodeWindow.Owner = this; } catch (Exception swallowed) { Log.Swallow(swallowed); }
                rttyDecodeWindow.Closed += (s, args) => rttyDecodeWindow = null;
                // A callsign double-clicked in the text goes in the DX Callsign box, as from the CW decoder.
                rttyDecodeWindow.CallsignChosen += TakeCallsignFromDecode;
                rttyDecodeWindow.Show();
                // The dial at once, not at the radio's next report.
                try { ShowDialOnRtty(RadioFollowedForRtty()); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }
            catch (Exception ex)
            {
                Log.Swallow(ex);
                rttyDecodeWindow = null;
            }
        }
    }
}
