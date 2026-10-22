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

        // -- SENDING RTTY BY FSK ------------------------------------------------------------------------
        //
        // Options > General > "RTTY keying": the port and the line the radio's FSK input is on (on the
        // IC-7610 its second USB port, as set in the radio's USB Keying (RTTY) menu). HolyLogger keys the
        // bits on that line (RttyFskKeyer) and switches the radio to transmit and back over CAT.
        //
        // OPENED WHEN FIRST NEEDED and held open, like the CW keying port; a change in Options lets it go
        // (OnRttyViaChanged) and the next send opens the new one.
        RttyFskKeyer rttyKeyer;
        string rttyKeyerTrouble;
        bool _rttyTransmitOn;

        RttyFskKeyer RttyKeyer()
        {
            if (_isShutdownCleanupDone) return null;
            string port = (Properties.Settings.Default.RttyKeyPort ?? string.Empty).Trim();
            bool rts = !string.Equals(Properties.Settings.Default.RttyKeyLine, "DTR", StringComparison.OrdinalIgnoreCase);
            bool markIsOn = Properties.Settings.Default.RttySwapFsk;
            if (port.Length == 0)
            {
                CloseRttyKeyer();
                rttyKeyerTrouble = "Choose the RTTY keying port in Options > General.";
                return null;
            }
            if (rttyKeyer != null && string.Equals(rttyKeyer.PortName, port, StringComparison.OrdinalIgnoreCase)
                && rttyKeyer.Line == (rts ? "RTS" : "DTR") && _rttyKeyerMarkIsOn == markIsOn)
                return rttyKeyer;

            CloseRttyKeyer();
            string catPort = ReadOmniRigCatPortFromFile(Properties.Settings.Default.SelectedOmniRig2);
            if (Properties.Settings.Default.EnableOmniRigCAT && catPort != null
                && string.Equals(catPort, port, StringComparison.OrdinalIgnoreCase))
            {
                rttyKeyerTrouble = port + " is the radio's CAT port. Choose the RTTY keying port in Options > General.";
                return null;
            }

            string whyNot;
            rttyKeyer = RttyFskKeyer.Open(port, rts, markIsOn, out whyNot);
            _rttyKeyerMarkIsOn = markIsOn;
            rttyKeyerTrouble = whyNot == null ? null : whyNot + " Choose the port in Options > General.";
            if (rttyKeyer != null)
            {
                rttyKeyer.BusyChanged += busy => Dispatcher.BeginInvoke(new Action(() => OnRttyBusyChanged(busy)));
                rttyKeyer.CharacterSent += c =>
                {
                    var w = rttyDecodeWindow;
                    if (w != null) w.ShowSent(c);
                };
                Log.Warn("RTTY keying port " + port + " (" + rttyKeyer.Line + (markIsOn ? ", mark = on" : ", mark = off") + ") opened.");
            }
            return rttyKeyer;
        }

        bool _rttyKeyerMarkIsOn;

        /// <summary>
        /// Sends text from the RTTY window. Null when it is on its way, else a short reason to show.
        /// A new line first, so it starts on a line of its own at the other end, and a space after it.
        /// </summary>
        internal string SendRtty(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (!IsCatLive()) return "No radio on CAT - HolyLogger cannot switch it to transmit.";
            var keyer = RttyKeyer();
            if (keyer == null) return rttyKeyerTrouble ?? "The RTTY keying port could not be opened.";
            return keyer.Send("\r\n" + text.Trim() + " ") ? null : "The RTTY keying port is closing.";
        }

        /// <summary>Esc in the RTTY window: stop at once and back to receive.</summary>
        internal void StopRtty()
        {
            try { if (rttyKeyer != null) rttyKeyer.Stop(); } catch (Exception swallowed) { Log.Swallow(swallowed); }
            SetRttyTransmit(false);
        }

        // The keyer started (its lead of mark is running) or finished (its tail is over).
        void OnRttyBusyChanged(bool busy)
        {
            SetRttyTransmit(busy);
            var w = rttyDecodeWindow;
            if (w != null) w.ShowTransmitting(busy);
        }

        // Transmit on or off over CAT. Off is always sent when asked, whatever was thought to be the state:
        // a transmitter left on is the one thing that must never happen.
        void SetRttyTransmit(bool on)
        {
            if (on == _rttyTransmitOn && on) return;
            _rttyTransmitOn = on;
            try
            {
                if (Rig != null && IsCatLive()) Rig.Tx = (OmniRig.RigParamX)(on ? PM_TX : PM_RX);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        void CloseRttyKeyer()
        {
            var keyer = rttyKeyer;
            rttyKeyer = null;
            if (keyer != null)
            {
                try { keyer.Dispose(); } catch (Exception swallowed) { Log.Swallow(swallowed); }
            }
            if (_rttyTransmitOn) SetRttyTransmit(false);
        }

        /// <summary>Options changed the RTTY keying port, line or swap: the open port is let go.</summary>
        internal void OnRttyViaChanged()
        {
            CloseRttyKeyer();
            rttyKeyerTrouble = null;
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
                rttyDecodeWindow.Closed += (s, args) =>
                {
                    rttyDecodeWindow = null;
                    // Closing the window stops a transmission in progress - nothing left to stop it with.
                    StopRtty();
                };
                rttyDecodeWindow.SendText = SendRtty;
                rttyDecodeWindow.StopSending = StopRtty;
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
