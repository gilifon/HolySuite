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
                else CloseRttyByProgram();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // -- LEAVING RTTY (his rule, 2026-10-23) --------------------------------------------------------------
        //
        // Closing the decoder himself means leaving RTTY: the radio goes back to the mode it was on before
        // RTTY - the mode only, never the frequency. Not when the program closed it because the mode had
        // already changed (another mode button, or the radio's own knob): that change is the answer.
        bool _rttyClosingByProgram;
        int _modeBeforeRtty;   // OmniRig's mode value, read while the decoder was shut; 0 = not known

        void CloseRttyByProgram()
        {
            if (rttyDecodeWindow == null) return;
            _rttyClosingByProgram = true;
            try { rttyDecodeWindow.Close(); }
            finally { _rttyClosingByProgram = false; }
        }

        void RestoreModeAfterRtty()
        {
            try
            {
                if (_isShutdownCleanupDone || _modeBeforeRtty == 0 || !RadioFollowedForRtty()) return;
                int now = (int)Rig.Mode;
                if (now == _modeBeforeRtty || !IsRttyRigMode(now, true)) return;
                var mode = (OmniRig.RigParamX)_modeBeforeRtty;
                SetRigMode(mode);
                SendSpectrumWidthForMode(mode);
                UpdateRadioPanel();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // The radio's mode is RTTY: its RTTY mode - or, with AFSK and the decoder open, SSB (AFSK is
        // sent and heard on a sideband, so the open decoder is what makes that SSB a RTTY one).
        bool IsRttyRigMode(int mode, bool decoderOpen)
        {
            if (mode == PM_DIG_L) return true;
            return decoderOpen && RttyByAfsk() && (mode == PM_SSB_L || mode == PM_SSB_U);
        }

        // -- THE MSG BUTTONS IN RTTY (his request, 2026-10-22) ----------------------------------------------
        //
        // A third state beside voice and CW: in RTTY the four Msg buttons (F5-F8) send RTTY text and wear
        // the yellow of the panel's RTTY button. The text is written like a CW message - the same macros,
        // * ! # $ {CALL} {LOG} and the rest - and kept apart from it, four texts and four names of its own.
        // Right-click edits it.
        //
        // WHEN IS IT RTTY? The radio on its RTTY mode. And, for AFSK, the radio on SSB with the RTTY
        // window open: AFSK runs on LSB, which the radio reports as plain SSB (the IC-7610's LSB-D comes
        // through OmniRig as SSB - seen in his log), so the open RTTY window is what says the operator
        // is working RTTY rather than talking.
        internal bool IsRttyModeActive()
        {
            try
            {
                string mode = null;
                if (Properties.Settings.Default.EnableOmniRigCAT && OmniRigEngine != null && Rig != null
                    && Rig.Status == OmniRig.RigStatusX.ST_ONLINE)
                    mode = GetNormalizedRigMode();
                if (string.IsNullOrEmpty(mode) && CB_Mode != null) mode = CB_Mode.Text;
                mode = (mode ?? string.Empty).Trim();
                if (string.Equals(mode, "RTTY", StringComparison.OrdinalIgnoreCase)) return true;
                // His rule (2026-10-23): while the RTTY decoder is open, it is RTTY - whatever the radio says.
                return rttyDecodeWindow != null;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return false; }
        }

        const string RttyMsgLabelsSetting = "RttyMsgLabelsJson";

        static string RttyMsgButtonLabel(int messageNumber)
        {
            string[] labels = CwKeyboardWindow.ReadLabels(RttyMsgLabelsSetting, 4);
            return messageNumber >= 1 && messageNumber <= labels.Length ? (labels[messageNumber - 1] ?? string.Empty).Trim() : string.Empty;
        }

        static string GetRttyMessageText(int messageNumber)
        {
            var s = Properties.Settings.Default;
            switch (messageNumber)
            {
                case 1: return s.RttyMsgText1 ?? string.Empty;
                case 2: return s.RttyMsgText2 ?? string.Empty;
                case 3: return s.RttyMsgText3 ?? string.Empty;
                case 4: return s.RttyMsgText4 ?? string.Empty;
                default: return string.Empty;
            }
        }

        static void SetRttyMessageText(int messageNumber, string text)
        {
            var s = Properties.Settings.Default;
            switch (messageNumber)
            {
                case 1: s.RttyMsgText1 = text; break;
                case 2: s.RttyMsgText2 = text; break;
                case 3: s.RttyMsgText3 = text; break;
                case 4: s.RttyMsgText4 = text; break;
                default: return;
            }
            try { s.Save(); } catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // The button whose text is going out, so pressing it again stops it - as on a CW keyer.
        int _rttyMessageSending;

        // Whether this button's text is on the air now - it is painted orange while it is.
        bool RttyMessageGoingOut(int messageNumber)
        {
            return rttyKeyer != null && rttyKeyer.Busy && _rttyMessageSending == messageNumber;
        }

        void TriggerRttyTextMessage(int messageNumber)
        {
            if (messageNumber < 1 || messageNumber > 4) return;

            // Pressed while something is going out: that stops first; the same button again only stops.
            if (rttyKeyer != null && rttyKeyer.Busy)
            {
                StopRtty();
                if (_rttyMessageSending == messageNumber) { _rttyMessageSending = 0; return; }
            }

            string stored = GetRttyMessageText(messageNumber);
            if (string.IsNullOrWhiteSpace(stored))
            {
                HolyMessageBox.ShowWarning("RTTY text " + messageNumber + " is empty. Right-click the button to edit it.", "RTTY Text", this);
                return;
            }
            string macroProblem = DescribeCwMacroProblem(stored);
            if (macroProblem != null)
            {
                HolyMessageBox.ShowWarning("RTTY text " + messageNumber + " cannot be sent: " + macroProblem + ".", "RTTY Text", this);
                return;
            }

            string text = (ExpandCwMacros(stored) ?? string.Empty).Trim();
            bool logsQso = CwMacroLogsQso(stored);
            bool wipesForm = CwMacroWipesForm(stored);
            if (text.Length > 0)
            {
                string why = SendRtty(text);
                if (why != null)
                {
                    // Empty: he answered Cancel to the question above - nothing more to say.
                    if (why.Length > 0) HolyMessageBox.ShowWarning(why, "RTTY Text", this);
                    return;
                }
                _rttyMessageSending = messageNumber;
            }
            RunCwMacroActions(logsQso, wipesForm);
        }

        // Right-click in RTTY: the same editor the CW texts use - the text and the button's name together.
        void ShowRttyMessageEditDialog(int messageNumber)
        {
            var button = GetMessageButton(messageNumber);
            var name = new CwKeyboardWindow.ButtonName
            {
                Label = RttyMsgButtonLabel(messageNumber),
                BoxWidth = button != null ? button.ActualWidth : 0,
                FaceFontSize = 11,
                FaceFontFamily = null
            };
            string updated = ShowCwTextEditDialog("Edit RTTY Text " + messageNumber + " (F" + (messageNumber + 4) + ")",
                                                  GetRttyMessageText(messageNumber), name);
            if (updated == null) return;

            string[] labels = CwKeyboardWindow.ReadLabels(RttyMsgLabelsSetting, 4);
            labels[messageNumber - 1] = (name.Label ?? string.Empty).Trim();
            CwKeyboardWindow.SaveLabels(RttyMsgLabelsSetting, labels, 4);
            SetRttyMessageText(messageNumber, updated);
            UpdateMessageButtonLabels();
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

                int mode = (int)Rig.Mode;
                // What to go back to when he closes the decoder: the last mode seen before RTTY.
                if (rttyDecodeWindow == null && mode != 0 && mode != PM_DIG_L) _modeBeforeRtty = mode;

                bool rtty = IsRttyRigMode(mode, rttyDecodeWindow != null);
                bool? was = _radioWasOnRtty;
                _radioWasOnRtty = rtty;
                if (was != null && was.Value == rtty) return;

                if (!rtty)
                {
                    // The radio still on the old way's mode, a moment after FSK/AFSK changed and before it
                    // has arrived on the new one: not a mode change of his - keep the decoder.
                    if (rttyDecodeWindow != null && (DateTime.Now - _rttyViaMovedAt).TotalSeconds < 3)
                    {
                        _radioWasOnRtty = true;
                        return;
                    }
                    CloseRttyByProgram();
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
            var sense = RttyDecodeWindow.DialSense.Unknown;
            if (online)
            {
                try
                {
                    khz = Rig.GetRxFrequency() / 1000.0;
                    int mode = (int)Rig.Mode;
                    if (mode == PM_DIG_L) sense = _rttyTuneOtherWay ? RttyDecodeWindow.DialSense.RttyModeOtherWay : RttyDecodeWindow.DialSense.RttyMode;
                    else if (mode == PM_SSB_L) sense = RttyDecodeWindow.DialSense.Lsb;
                    else if (mode == PM_SSB_U || mode == PM_DIG_U) sense = RttyDecodeWindow.DialSense.Usb;
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }
            rttyDecodeWindow.ShowRadioKhz(khz, sense);
        }

        /// <summary>
        /// THE QSO GOES IN THE LOG ON THE STATION'S FREQUENCY while RTTY is being worked from the RTTY window
        /// (his request, 2026-10-23): where he really was, worked out from the lines - not the radio's dial,
        /// which with AFSK can be two kilohertz away. Null when there is nothing better than the dial.
        /// </summary>
        string RttyStationFreqForLog()
        {
            try
            {
                var w = rttyDecodeWindow;
                if (w == null || !IsRttyModeActive()) return null;
                double khz = w.StationKhz;
                if (khz <= 0) return null;
                return (khz / 1000.0).ToString("###0.000000", System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return null; }
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

        // -- SENDING RTTY: FSK OR AFSK ------------------------------------------------------------------
        //
        // Options > General > "RTTY sending" chooses how:
        //   FSK - the radio makes the tones itself; HolyLogger keys its FSK input on one line of a COM port
        //         (on the IC-7610 its second USB port, as set in its USB Keying (RTTY) menu). RttyFskKeyer.
        //   AFSK - HolyLogger makes the tones and plays them through a sound card into the radio, on LSB.
        //          RttyAfskSender.
        // Either way transmit is switched on and off over CAT.
        //
        // OPENED WHEN FIRST NEEDED and held open, like the CW keying port; a change in Options lets it go
        // (OnRttyViaChanged) and the next send opens the new one.
        IRttySender rttyKeyer;
        string rttyKeyerTrouble;
        string _rttyKeyerSetup;      // what the open sender was opened with, to know when to open another
        bool _rttyTransmitOn;

        internal static bool RttyByAfsk()
        {
            try { return string.Equals(Properties.Settings.Default.RttyVia, "AFSK", StringComparison.OrdinalIgnoreCase); }
            catch (Exception swallowed) { Log.Swallow(swallowed); return false; }
        }

        IRttySender RttyKeyer()
        {
            if (_isShutdownCleanupDone) return null;
            bool swap = Properties.Settings.Default.RttySwapFsk;
            bool afsk = RttyByAfsk();
            string port = (Properties.Settings.Default.RttyKeyPort ?? string.Empty).Trim();
            bool rts = !string.Equals(Properties.Settings.Default.RttyKeyLine, "DTR", StringComparison.OrdinalIgnoreCase);
            string device = (Properties.Settings.Default.RttyAfskDevice ?? string.Empty).Trim();
            string setup = afsk ? "AFSK|" + device + "|" + swap : "FSK|" + port + "|" + rts + "|" + swap;

            if (rttyKeyer != null && setup == _rttyKeyerSetup) return rttyKeyer;
            CloseRttyKeyer();

            string whyNot;
            if (afsk)
            {
                // NEVER THE SYSTEM DEFAULT BY ACCIDENT: that is usually the speakers, and RTTY tones out of
                // the speakers with the transmitter keyed would put a dead carrier on the air.
                if (device.Length == 0)
                {
                    rttyKeyerTrouble = "Choose the sound card that goes to the radio in Options > General.";
                    return null;
                }
                rttyKeyer = RttyAfskSender.Open(device, swap, 0.5, out whyNot);
            }
            else
            {
                if (port.Length == 0)
                {
                    rttyKeyerTrouble = "Choose the RTTY keying port in Options > General.";
                    return null;
                }
                string catPort = ReadOmniRigCatPortFromFile(Properties.Settings.Default.SelectedOmniRig2);
                if (Properties.Settings.Default.EnableOmniRigCAT && catPort != null
                    && string.Equals(catPort, port, StringComparison.OrdinalIgnoreCase))
                {
                    rttyKeyerTrouble = port + " is the radio's CAT port. Choose the RTTY keying port in Options > General.";
                    return null;
                }
                // Mark with the line OFF is right for the IC-7610 (checked on the air); the swap turns it round.
                rttyKeyer = RttyFskKeyer.Open(port, rts, swap, out whyNot);
            }
            _rttyKeyerSetup = setup;
            rttyKeyerTrouble = whyNot == null ? null : whyNot + " Choose it in Options > General.";
            if (rttyKeyer != null)
            {
                rttyKeyer.BusyChanged += busy => Dispatcher.BeginInvoke(new Action(() => OnRttyBusyChanged(busy)));
                rttyKeyer.CharacterSent += c =>
                {
                    var w = rttyDecodeWindow;
                    if (w != null) w.ShowSent(c);
                };
                Log.Warn("RTTY sending opened: " + (afsk ? "AFSK on " : "FSK on ") + rttyKeyer.Describe + (swap ? ", mark and space swapped" : "") + ".");
            }
            return rttyKeyer;
        }

        /// <summary>
        /// Sends text from the RTTY window. Null when it is on its way, else a short reason to show.
        /// A new line first, so it starts on a line of its own at the other end, and a space after it.
        /// </summary>
        internal string SendRtty(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (!IsCatLive()) return "No radio on CAT - HolyLogger cannot switch it to transmit.";
            var keyer = RttyKeyer();
            if (keyer == null) return rttyKeyerTrouble ?? "RTTY sending could not be started.";
            // AFSK answers on the station being read: the tones the RTTY window is listening to.
            var afsk = keyer as RttyAfskSender;
            var w = rttyDecodeWindow;

            // THE SAFETY QUESTION (his point, 2026-10-23): in FSK the radio sends on its own dial, and a
            // station read far from it will not hear the answer. A click moves the radio onto the station
            // already; this catches the rest - a station reached by the arrow keys, or the fine tuning
            // pulled away. Only when well off: a caller a little off a running frequency is answered where
            // we are, as he expects, and must not be asked about.
            if (afsk == null && w != null && Math.Abs(w.OffTransmitHz) > 50)
            {
                int answer = HolyMessageBox.ShowChoice(
                    "You are reading a station " + Math.Round(Math.Abs(w.OffTransmitHz)).ToString("N0")
                    + " Hz away from where you transmit. He will not hear you there.",
                    "RTTY", HolyMsgType.Warning, this, "Move onto him", "Send anyway", "Cancel");
                if (answer == 0) return string.Empty;
                if (answer == 1) w.TuneNow();
            }
            if (afsk != null && w != null)
            {
                double mark, space;
                w.ListeningTones(out mark, out space);
                afsk.SetTones(mark, space);
            }
            return keyer.Send("\r\n" + text.Trim() + " ") ? null : "RTTY sending is closing.";
        }

        // Which way the dial goes for a tone heard too low. In the radio's RTTY mode a higher audio tone is
        // a lower radio frequency (mark on 2125, space on 2295 below it on the air); a radio that turns out
        // the other way round is caught by the window's check after the move, and remembered for the session.
        bool _rttyTuneOtherWay;

        internal void RttyTuneWentWrongWay() { _rttyTuneOtherWay = !_rttyTuneOtherWay; }

        /// <summary>
        /// TUNE (2026-10-22): moves the radio so the station whose lower tone is heard at lowToneHz comes
        /// onto 2125 / 2295 - where the radio itself sends, so the answer lands on his frequency. Null when
        /// done, else a short reason.
        /// </summary>
        internal string TuneRadioOntoRttyStation(double lowToneHz)
        {
            if (!IsCatLive()) return "No radio";
            try
            {
                double moveHz = RttyAfskSender.MarkHz - lowToneHz;      // dial up for a tone heard low
                if (_rttyTuneOtherWay) moveHz = -moveHz;
                if (_writeableParamsCache < 0) _writeableParamsCache = (int)Rig.WriteableParams;
                bool viaFreq = (_writeableParamsCache & PM_FREQ) != 0;
                if (!viaFreq && (_writeableParamsCache & PM_FREQA) == 0) return "Cannot tune";
                int now = viaFreq ? Rig.Freq : Rig.FreqA;
                if (now <= 0) now = viaFreq ? Rig.FreqA : Rig.Freq;
                if (now <= 0) return "No frequency";
                int to = now + (int)Math.Round(moveHz);
                if (viaFreq) Rig.Freq = to; else Rig.FreqA = to;
                try
                {
                    TB_Frequency.Text = (to / 1000000.0).ToString("###0.000000", System.Globalization.CultureInfo.InvariantCulture);
                    UpdateFreqLed();
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
                return null;
            }
            catch (Exception swallowed)
            {
                Log.Swallow(swallowed);
                _writeableParamsCache = -1;
                return "No answer";
            }
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
            try { UpdateVoiceMessageButtonHighlight(); } catch (Exception swallowed) { Log.Swallow(swallowed); }
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

        /// <summary>Options changed how RTTY is sent: the open port or sound card is let go.</summary>
        internal void OnRttyViaChanged()
        {
            CloseRttyKeyer();
            rttyKeyerTrouble = null;
            MoveRadioForRttyVia();
        }

        // FSK OR AFSK CHANGED WHILE IN RTTY (his report, 2026-10-23: it only took effect at the next switch
        // to RTTY). With the decoder open the radio moves at once to what the new way needs: AFSK - LSB;
        // FSK - the radio's RTTY mode. The dial moves 2.125 kHz with it, so the station being read stays on
        // the same lines (the RTTY mode's dial is the mark; on LSB the mark is 2125 Hz below the dial).
        DateTime _rttyViaMovedAt = DateTime.MinValue;

        void MoveRadioForRttyVia()
        {
            try
            {
                if (rttyDecodeWindow == null || !RadioFollowedForRtty()) { UpdateRadioPanel(); return; }
                int mode = (int)Rig.Mode;
                double khz = Rig.GetRxFrequency() / 1000.0;
                if (khz <= 0) return;
                bool afsk = RttyByAfsk();
                if (afsk && mode == PM_DIG_L) { _rttyViaMovedAt = DateTime.Now; TuneRadioToKhz(khz + 2.125, "LSB"); }
                else if (!afsk && mode == PM_SSB_L) { _rttyViaMovedAt = DateTime.Now; TuneRadioToKhz(khz - 2.125, "RTTY"); }
                else if (!afsk && mode == PM_SSB_U) { _rttyViaMovedAt = DateTime.Now; TuneRadioToKhz(khz + 2.125, "RTTY"); }
                else UpdateRadioPanel();
                try { UpdateMessageButtonLabels(); } catch (Exception swallowed) { Log.Swallow(swallowed); }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
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
                    // He closed it: back to the mode before RTTY. The program closing it: the mode has moved already.
                    if (!_rttyClosingByProgram) RestoreModeAfterRtty();
                    // Closing the window stops a transmission in progress - nothing left to stop it with.
                    StopRtty();
                    // With AFSK on SSB the open window is what made the Msg buttons RTTY ones, and the mode box RTTY.
                    try { UpdateMessageButtonLabels(); } catch (Exception swallowed) { Log.Swallow(swallowed); }
                    try { QueueShowRigParams(); } catch (Exception swallowed) { Log.Swallow(swallowed); }
                };
                rttyDecodeWindow.SendText = SendRtty;
                rttyDecodeWindow.TuneRadio = TuneRadioOntoRttyStation;
                rttyDecodeWindow.OpenSettings = OpenOptionsOnRtty;
                rttyDecodeWindow.ClickTunesRadio = () => !RttyByAfsk() && IsCatLive();
                rttyDecodeWindow.TuneWentWrongWay = RttyTuneWentWrongWay;
                rttyDecodeWindow.StopSending = StopRtty;
                // A callsign double-clicked in the text goes in the DX Callsign box, as from the CW decoder.
                rttyDecodeWindow.CallsignChosen += TakeCallsignFromDecode;
                rttyDecodeWindow.Show();
                try { UpdateMessageButtonLabels(); } catch (Exception swallowed) { Log.Swallow(swallowed); }
                try { QueueShowRigParams(); } catch (Exception swallowed) { Log.Swallow(swallowed); }   // the mode box says RTTY
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
