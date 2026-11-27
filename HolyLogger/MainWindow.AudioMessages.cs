using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace HolyLogger
{
    // ── SSB MESSAGES PLAYED FROM THIS COMPUTER (his design, 2026-11-25) ─────────────────────────────
    //
    // See AudioMessages for the why. This file is the main window's side of it: the Tools menu item,
    // the window, the picture on the keycaps, and sending a recording on the air - transmitter on by
    // CAT, the recording played to the radio's sound card, transmitter off when it ends or is stopped.
    public partial class MainWindow
    {
        AudioMessageManagerWindow _audioMessagesWindow;
        AudioClipPlayer _pcMsgPlayer;
        int _pcMsgNumber;              // 0 = nothing going out
        bool _pcMsgKeyed;
        bool _audioMessagesHooked;

        // The radio needs a moment to change over to transmit before the first word.
        const int PcMsgLeadMs = 250;

        private void AudioMessagesMenuItem_Click(object sender, RoutedEventArgs e)
        {
            OpenAudioMessagesWindow(0);
        }

        void OpenAudioMessagesWindow(int pointAt)
        {
            try
            {
                if (_audioMessagesWindow != null)
                {
                    _audioMessagesWindow.Activate();
                    _audioMessagesWindow.PointAt(pointAt);
                    return;
                }
                _audioMessagesWindow = new AudioMessageManagerWindow(
                    n => MsgButtonLabel(n, isCw: false),
                    n => ShowMsgButtonRenameDialog(n),
                    RadioMemoryNote);
                try { _audioMessagesWindow.Owner = this; } catch (Exception swallowed) { Log.Swallow(swallowed); }
                _audioMessagesWindow.Closed += (s, args) => _audioMessagesWindow = null;
                _audioMessagesWindow.Show();
                _audioMessagesWindow.PointAt(pointAt);
            }
            catch (Exception ex)
            {
                Log.Swallow(ex);
                _audioMessagesWindow = null;
            }
        }

        // What the window says under "The radio's own memory": nothing when the radio has the command (or
        // no radio is connected to ask), and the plain fact when it has not.
        string RadioMemoryNote()
        {
            try
            {
                if (Rig == null || Rig.Status != OmniRig.RigStatusX.ST_ONLINE) return null;
                string rigType = NormalizeRigType(Rig.RigType);
                if (rigType.Length == 0 || VoiceProfileFor(rigType) != null) return null;
                return "This radio (" + rigType + ") cannot be told to play its memory by CAT.";
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return null; }
        }

        // ── THE WINDOW GOES WHEN THE RADIO LEAVES SSB (his request, 2026-11-26) ──────────────────────
        //
        // These are SSB messages, so the window has nothing to do in CW, RTTY or digital. ON THE CHANGE
        // ONLY: opened on purpose while the radio is in CW (to record ahead), it stays until the next time
        // the radio goes from SSB to something else. The mode is the radio's when it answers, else the
        // Mode box.
        string _audioWindowLastMode;

        void FollowModeWithAudioMessagesWindow()
        {
            try
            {
                string mode = null;
                bool rigOnline = Properties.Settings.Default.EnableOmniRigCAT && OmniRigEngine != null && Rig != null
                                 && Rig.Status == OmniRig.RigStatusX.ST_ONLINE;
                if (rigOnline) mode = GetNormalizedRigMode();
                if (string.IsNullOrEmpty(mode))
                {
                    if (CB_Mode != null && CB_Mode.SelectedItem is ComboBoxItem item) mode = item.Content as string;
                    else if (CB_Mode != null) mode = CB_Mode.Text;
                }
                mode = (mode ?? string.Empty).Trim().ToUpperInvariant();
                if (mode.Length == 0) return;

                bool leftSsb = _audioWindowLastMode == "SSB" && mode != "SSB";
                _audioWindowLastMode = mode;
                if (leftSsb && _audioMessagesWindow != null) _audioMessagesWindow.Close();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // Called once the main window is built: the keycap pictures follow the switch.
        void HookAudioMessages()
        {
            if (_audioMessagesHooked) return;
            _audioMessagesHooked = true;
            AudioMessages.SourceChanged += () => Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (!AudioMessages.UsePc) StopPcMessage();
                    _txAudioOkFor = null;   // switched: the next Msg press asks the radio again
                    UpdateMessageButtonLabels();
                    UpdateVoiceMessageAvailabilityState();
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }));

            // REC QSO follows the radio's sound card: chosen in Options, plugged in or pulled out.
            CwDecodeWindow.InputDeviceChanged += () => Dispatcher.BeginInvoke(new Action(RefreshRadioSoundCard));
            AudioMessages.SettingsSaved += () => Dispatcher.BeginInvoke(new Action(RefreshRadioSoundCard));
            SourceInitialized += (s, e) =>
            {
                try
                {
                    var src = PresentationSource.FromVisual(this) as System.Windows.Interop.HwndSource;
                    if (src != null) src.AddHook(SoundCardChangeHook);
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            };
            RefreshRadioSoundCard();
        }

        // ── THE PICTURE ON THE KEYCAP ───────────────────────────────────────────────────────────────
        //
        // HIS LAYOUT: the key name moves to the bottom-left of the button, and the picture sits at the
        // bottom-right. Only in SSB - in CW and RTTY the buttons send text and there is no "where from".
        static readonly Brush KeycapIconInk = MakeKeycapInk();
        static Brush MakeKeycapInk() { var b = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)); b.Freeze(); return b; }

        static void ShowMessageSourceIcon(Button button, bool ssb)
        {
            var panel = button != null ? button.Content as StackPanel : null;
            if (panel == null || panel.Children.Count < 2) return;
            var row = panel.Children[1] as Grid;
            if (row == null || row.Children.Count < 2) return;

            var keyLine = row.Children[0] as TextBlock;
            var holder = row.Children[1] as Border;
            if (keyLine == null || holder == null) return;

            if (!ssb)
            {
                holder.Child = null;
                holder.Visibility = Visibility.Collapsed;
                keyLine.HorizontalAlignment = HorizontalAlignment.Center;
                row.Visibility = keyLine.Visibility;
                return;
            }

            Path icon = AudioMessages.UsePc ? AudioSourceIcons.Pc(1.0, KeycapIconInk) : AudioSourceIcons.Sd(1.0, KeycapIconInk);
            holder.Child = icon;
            holder.Visibility = Visibility.Visible;
            keyLine.HorizontalAlignment = HorizontalAlignment.Left;
            row.Visibility = Visibility.Visible;
        }

        // ── ON THE AIR ──────────────────────────────────────────────────────────────────────────────

        bool PcMessageGoingOut(int n) { return _pcMsgNumber != 0 && _pcMsgNumber == n; }

        // ── WHAT A MSG PRESS DID, STEP BY STEP, IN THE LOG (his request, 2026-11-27) ─────────────────
        //
        // A friend's radio did not go to transmit when he sent a recording from his PC, and his log had
        // not one line about it: the press, the checks it passed or failed, the transmit switch and the
        // sound card were all silent. Every step now leaves a "VOICE MSG" line, so the next log says
        // where it stopped. Log.Warn is called directly (not through a helper) so each line keeps the
        // file and line it came from.
        const string VoiceMsgTag = "VOICE MSG  ";

        // Everything a press depends on, in one line - written once per press.
        string VoiceMsgState()
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                sb.Append("from=").Append(AudioMessages.UsePc ? "PC recordings" : "radio memory");
                sb.Append(" | transmit by=").Append(AudioMessages.PttVia);
                if (AudioMessages.PttVia == AudioMessages.PttPort)
                    sb.Append(" (").Append(AudioMessages.Settings.PttPort).Append(' ').Append(AudioMessages.Settings.PttLine).Append(')');
                sb.Append(" | card to radio=").Append(string.IsNullOrEmpty(AudioMessages.Settings.RadioDevice) ? "(none)" : AudioMessages.Settings.RadioDevice);
                sb.Append(" | level=").Append((int)Math.Round(AudioMessages.Settings.RadioLevel * 100)).Append('%');
                sb.Append(" | CAT enabled=").Append(Properties.Settings.Default.EnableOmniRigCAT);
                sb.Append(" | OmniRig=").Append(Rig == null ? "(none)" : Rig.Status.ToString());
                sb.Append(" | radio=").Append(NormalizeRigType(Rig != null ? Rig.RigType : null));
                sb.Append(" | radio mode=").Append(GetNormalizedRigMode() ?? "(none)");
                sb.Append(" | Mode box=").Append(CB_Mode != null ? CB_Mode.Text : string.Empty);
                sb.Append(" | buttons available=").Append(_messageSendAvailable);
            }
            catch (Exception ex) { sb.Append(" | (reading the state failed: ").Append(ex.GetType().Name).Append(": ").Append(ex.Message).Append(')'); }
            return sb.ToString();
        }

        async void TriggerPcMessage(int n)
        {
            if (_txAudioChecking) return;   // still asking the radio for the press before this one

            // A second press of the one going out stops it; a press of another stops it and sends that.
            if (_pcMsgNumber != 0)
            {
                int was = _pcMsgNumber;
                Log.Warn(VoiceMsgTag + "Msg " + n + " pressed while Msg " + was + " is going out - stopping Msg " + was);
                StopPcMessage();
                if (was == n) return;
            }

            if (!AudioMessages.HasRecording(n))
            {
                // No file chosen, or the chosen one is gone (moved, deleted, a USB stick taken out).
                string chosen = AudioMessages.FilePath(n);
                Log.Warn(VoiceMsgTag + "Msg " + n + " NOT SENT: no recording - "
                         + (chosen.Length == 0 ? "no file chosen" : "file missing: " + chosen));
                string why = chosen.Length == 0
                    ? "Msg " + n + " has no sound file yet."
                    : "The sound file of Msg " + n + " is not there any more:\n" + chosen;
                if (HolyMessageBox.ShowConfirm(why + "\n\nOpen Audio Messages to record or choose one?",
                                               "Voice Message", HolyMsgType.Info, this))
                    OpenAudioMessagesWindow(n);
                return;
            }

            if (!RadioCardReady(n, "Voice Message")) return;
            if (!await RadioTxAudioAllowsSend()) return;

            short[] samples; int rate;
            if (!AudioMessages.Load(n, out samples, out rate))
            {
                Log.Warn(VoiceMsgTag + "Msg " + n + " NOT SENT: the recording cannot be read: " + AudioMessages.FilePath(n));
                HolyMessageBox.ShowWarning("The recording of Msg " + n + " cannot be read.\n\nRecord it again in Audio Messages.",
                                           "Voice Message", this);
                return;
            }

            Log.Warn(VoiceMsgTag + "Msg " + n + " recording read: " + AudioMessages.FilePath(n) + " | "
                     + (samples != null ? samples.Length : 0) + " samples at " + rate + " Hz = "
                     + (rate > 0 && samples != null ? (samples.Length / (double)rate).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "?") + " s");
            SendClipOnAir(samples, rate, n, "Voice Message");
        }

        // Is the sound card that goes to the radio chosen and plugged in? Shared by the Msg buttons and
        // the Play button over them, so both say the same thing when it is not.
        bool RadioCardReady(int pointAt, string title)
        {
            string card = AudioMessages.Settings.RadioDevice;
            if (string.IsNullOrEmpty(card))
            {
                Log.Warn(VoiceMsgTag + "NOT SENT: no sound card to the radio is chosen");
                if (HolyMessageBox.ShowConfirm("Choose the sound card that goes to the radio first.\n\nOpen Audio Messages?",
                                               title, HolyMsgType.Info, this))
                    OpenAudioMessagesWindow(pointAt);
                return false;
            }
            var outputs = WaveOutPlayer.GetOutputDeviceNames();
            if (WaveInRecorder.FindDevice(outputs, card) == null)
            {
                Log.Warn(VoiceMsgTag + "NOT SENT: the sound card to the radio (" + card + ") is not connected | sound cards there now: "
                         + string.Join(", ", outputs));
                HolyMessageBox.ShowWarning("The sound card to the radio (" + card + ") is not connected.\n\n"
                                         + "Is the radio on, and its USB cable in?", title, this);
                return false;
            }
            return true;
        }

        // Transmitter on, the clip to the radio's sound card, transmitter off when it ends. number is
        // the Msg key (1-4) or QsoPlayNumber for the Play button; Esc stops either.
        void SendClipOnAir(short[] samples, int rate, int number, string title)
        {
            string card = AudioMessages.Settings.RadioDevice;

            string whyNotKeyed;
            if (!KeyRadio(true, out whyNotKeyed))
            {
                Log.Warn(VoiceMsgTag + (number == QsoPlayNumber ? "PLAY" : "Msg " + number) + " NOT SENT: transmit could not be switched on: "
                         + (whyNotKeyed ?? "(no reason given)"));
                HolyMessageBox.ShowWarning((whyNotKeyed ?? "The radio could not be switched to transmit.") + "\n\nNothing was sent.",
                                           title, this);
                return;
            }

            string error;
            AudioClipPlayer player = null;
            player = AudioClipPlayer.Start(samples, rate, WaveOutPlayer.ResolveDeviceId(card), AudioMessages.Settings.RadioLevel,
                PcMsgLeadMs,
                () => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_pcMsgPlayer != player) return;   // already stopped by hand
                    EndPcMessage();
                })),
                out error);

            if (player == null)
            {
                Log.Warn(VoiceMsgTag + (number == QsoPlayNumber ? "PLAY" : "Msg " + number) + " NOT SENT: the sound card to the radio ("
                         + card + ") did not start: " + error + " - back to receive");
                UnkeyRadio();
                HolyMessageBox.ShowWarning(error + "\n\nNothing was sent.", title, this);
                return;
            }

            Log.Warn(VoiceMsgTag + (number == QsoPlayNumber ? "PLAY" : "Msg " + number) + " playing to " + card + " at "
                     + (int)Math.Round(AudioMessages.Settings.RadioLevel * 100) + "% after a " + PcMsgLeadMs + " ms lead");
            _pcMsgPlayer = player;
            _pcMsgNumber = number;
            UpdateVoiceMessageButtonHighlight();
            PaintQsoAudioBar();
        }

        // -- SWITCHING THE TRANSMITTER (his request, 2026-11-25: radios with no CAT too) --------------
        //
        // Three ways, chosen in the Audio Messages window: CAT, a COM port line, or the radio's own VOX.
        // Whichever way it went on is remembered, so it goes off the same way even if the choice is
        // changed while a message is playing.
        string _pcMsgKeyedVia;
        System.IO.Ports.SerialPort _pttPort;
        bool _pttUseRts;

        /// <summary>True when the messages key the radio by CAT - and so need CAT to be working.</summary>
        static bool PcMessagesKeyByCat { get { return AudioMessages.PttVia == AudioMessages.PttCat; } }

        bool KeyRadio(bool on, out string whyNot)
        {
            whyNot = null;
            if (!on) { UnkeyRadio(); return true; }

            string via = AudioMessages.PttVia;
            try
            {
                if (via == AudioMessages.PttVox)
                {
                    // Nothing to switch: the radio's VOX hears the message and transmits by itself.
                    Log.Warn(VoiceMsgTag + "transmit by VOX: HolyLogger switches nothing - the radio's VOX must hear the sound");
                    _pcMsgKeyed = true;
                    _pcMsgKeyedVia = via;
                    return true;
                }

                if (via == AudioMessages.PttPort)
                {
                    string port = (AudioMessages.Settings.PttPort ?? string.Empty).Trim();
                    bool rts = !string.Equals(AudioMessages.Settings.PttLine, "DTR", StringComparison.OrdinalIgnoreCase);
                    if (port.Length == 0) { whyNot = "Choose the COM port for transmit in Audio Messages."; return false; }

                    // NEVER THE RADIO'S OWN CAT PORT - OmniRig holds it, and its lines are not a PTT.
                    string catPort = ReadOmniRigCatPortFromFile(Properties.Settings.Default.SelectedOmniRig2);
                    if (Properties.Settings.Default.EnableOmniRigCAT && catPort != null
                        && string.Equals(catPort, port, StringComparison.OrdinalIgnoreCase))
                    {
                        whyNot = port + " is the radio's CAT port. Choose the interface's own port in Audio Messages.";
                        return false;
                    }

                    // ONE CABLE, TWO JOBS. An interface box often keys CW on one line and PTT on the
                    // other of the same port, so the CW keyer may be holding it: let it go; it opens
                    // again by itself on the next CW message.
                    if (portCwKeyer != null && string.Equals(portCwKeyer.PortName, port, StringComparison.OrdinalIgnoreCase))
                        ClosePortKeyer();

                    var sp = new System.IO.Ports.SerialPort(port)
                    {
                        // Both lines off BEFORE it opens, so the radio never sees one rise by itself.
                        DtrEnable = false, RtsEnable = false, Handshake = System.IO.Ports.Handshake.None
                    };
                    try { sp.Open(); }
                    catch (UnauthorizedAccessException) { sp.Dispose(); whyNot = port + " is being used by another program."; return false; }
                    catch (System.IO.IOException) { sp.Dispose(); whyNot = port + " is not there - is the interface cable plugged in?"; return false; }

                    if (rts) sp.RtsEnable = true; else sp.DtrEnable = true;
                    Log.Warn(VoiceMsgTag + "transmit ON by " + port + " " + (rts ? "RTS" : "DTR") + " (line raised)");
                    _pttPort = sp;
                    _pttUseRts = rts;
                    _pcMsgKeyed = true;
                    _pcMsgKeyedVia = via;
                    return true;
                }

                if (!IsCatLive() || Rig == null) { whyNot = "CAT is not working, so the radio cannot be switched to transmit."; return false; }

                // OmniRig drops a command its rig file has no line for WITHOUT an error, so whether the
                // file has a transmit line ([pmTx]) is written down first.
                bool txInFile = false;
                try { txInFile = ((int)Rig.WriteableParams & PM_TX) != 0; }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
                Log.Warn(VoiceMsgTag + "transmit ON by CAT: sending OmniRig Tx=TX to " + NormalizeRigType(Rig.RigType)
                         + " | its rig file " + (txInFile ? "has" : "HAS NO") + " transmit command");
                Rig.Tx = (OmniRig.RigParamX)PM_TX;
                _pcMsgKeyed = true;
                _pcMsgKeyedVia = AudioMessages.PttCat;
                LogTxReadbackSoon();
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn(VoiceMsgTag + "transmit by " + via + " FAILED: " + ex.GetType().Name + ": " + ex.Message);
                Log.Swallow(ex);
                UnkeyRadio();
                whyNot = "The radio could not be switched to transmit.";
                return false;
            }
        }

        // Back to receive, the same way it went to transmit. Safe to call when nothing is keyed.
        void UnkeyRadio()
        {
            string via = _pcMsgKeyedVia;
            _pcMsgKeyed = false;
            _pcMsgKeyedVia = null;

            var sp = _pttPort;
            _pttPort = null;
            if (sp != null)
            {
                try { if (_pttUseRts) sp.RtsEnable = false; else sp.DtrEnable = false; }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
                try { sp.Close(); sp.Dispose(); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }

            if (via == AudioMessages.PttCat)
            {
                try { if (Rig != null) Rig.Tx = (OmniRig.RigParamX)PM_RX; }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }
            if (via != null || sp != null)
                Log.Warn(VoiceMsgTag + "transmit OFF (back to receive) by " + (via ?? "port"));
        }

        // DID THE RADIO REALLY GO TO TRANSMIT? OmniRig takes Tx=TX and says nothing either way, so
        // the radio's own state is read a moment later, once OmniRig has polled it, and written down.
        async void LogTxReadbackSoon()
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(800);
                if (Rig == null) { Log.Warn(VoiceMsgTag + "800 ms after transmit ON: no radio to ask"); return; }
                int tx = (int)Rig.Tx;
                string said = tx == PM_TX ? "TRANSMIT" : tx == PM_RX ? "RECEIVE" : "unknown (0x" + tx.ToString("X8") + ")";
                Log.Warn(VoiceMsgTag + "800 ms after transmit ON the radio reports: " + said
                         + " | OmniRig=" + Rig.Status + " | still sending=" + (_pcMsgNumber != 0));
            }
            catch (Exception ex) { Log.Warn(VoiceMsgTag + "reading the radio's transmit state failed: " + ex.GetType().Name + ": " + ex.Message); }
        }

        // ── WILL THE RADIO PUT THE COMPUTER'S SOUND ON THE AIR? (his request, 2026-11-27) ─────────────
        //
        // A friend's IC-7300 went to transmit and sent nothing. In plain USB/LSB the radio takes its
        // transmit sound from the input chosen in its own menu, and the IC-7300's factory setting is
        // MIC,ACC - not USB. So while the Msg buttons play from this computer, the radio is ASKED which
        // input it uses, and when the computer's sound is not among them he is told what to change and
        // where. Only told: HolyLogger never changes it itself - it is the radio owner's setting, and on
        // some radios it also decides whether his microphone works in SSB.
        //
        // WHEN (his rule, 2026-11-27, changed the same day): ONLY WHEN A Msg BUTTON IS PRESSED to send a
        // recording from this computer - a user who never uses the buttons is never bothered. Wrong:
        // the warning, and nothing is sent (it would go out silent). Read again on the next press, so
        // once he fixes the radio the press just sends. Right (or not askable): not asked again while
        // the radio stays connected, so later presses do not wait for it.
        //
        // ONLY WHEN THE SOUND GOES IN BY THE RADIO'S OWN USB - the card to the radio is the radio's
        // "USB Audio CODEC". Any other card means an interface box into MIC, ACC or the rear DATA jack,
        // which is right for those radios, and HolyLogger cannot see which, so nothing is said.
        //
        // EACH FROM THE MAKER'S OWN DOCUMENT (copies in his Downloads\Radio Manuals):
        //   IC-7300    Full Manual A7292-4EX-5: 1A 05 0066 "MOD input connector during DATA OFF"
        //              (00=MIC, 01=ACC, 02=MIC/ACC, 03=USB); MENU > SET > Connectors > DATA OFF MOD, p. 12-8,
        //              default MIC,ACC. No MIC+USB choice: on USB the microphone does not work in SSB.
        //   IC-7300MK2 CI-V Reference Guide p. 9: 1A 05 00 84 DATA OFF MOD
        //              (00=MIC, 01=USB, 02=ACC, 03=MIC,USB, 04=MIC,ACC, 05=LAN) - its own order, not the IC-7300's.
        //   IC-7610    CI-V Reference Guide p. 6: 1A 05 00 91 Connectors > MOD Input > DATA OFF MOD, in Set mode
        //              (00=MIC, 01=ACC, 02=MIC,ACC, 03=USB, 04=MIC,USB, 05=LAN).
        //   IC-9700    CI-V Reference Guide p. 8: 1A 05 0115 SET > Connectors > MOD Input > DATA OFF MOD, the
        //              IC-7610's values.
        //   FTDX101    CAT Reference, Table 2, RADIO SETTING > MODE SSB: EX 01 01 11 SSB MOD SOURCE
        //              (0: MIC 1: REAR), EX 01 01 12 REAR SELECT (0: DATA 1: USB).
        //   FTDX10     CAT Reference, Table 2, the same two at EX 01 01 13 and EX 01 01 14.
        //   FT-991A    CAT Reference: menu 106 SSB MIC SELECT (0: MIC 1: REAR), 109 SSB PORT SELECT (0: DATA 1: USB).
        // Not here yet: Kenwood, the IC-705, IC-7100, IC-7760 and others - their documents still to be read.
        sealed class TxAudioItem
        {
            public string Path;       // where it is in the radio's menu, in the radio's words
            public string Ask;        // Icom: the item after 1A 05; Yaesu: the whole read command
            public string[] Values;   // what each value means, in the radio's words
            public int[] Good;        // the values that let the USB sound through
            public string SetTo;      // what to choose
        }

        sealed class TxAudioRadio
        {
            public string Key;        // the start of VoiceModelKey's name for it
            public string Name;       // as people write it
            public bool Icom;
            public string Note;       // said under the advice, or null
            public bool Tested;       // tried on a real radio; untested ones offer Send anyway
            public TxAudioItem[] Items;
        }

        static readonly string[] IcomModInputs = { "MIC", "ACC", "MIC,ACC", "USB", "MIC,USB", "LAN" };

        // Order matters where one name starts another: IC7300MK2 before IC7300, FTDX101 before FTDX10.
        static readonly TxAudioRadio[] TxAudioRadios =
        {
            new TxAudioRadio { Key = "IC7300MK2", Name = "IC-7300MK2", Icom = true, Items = new[] { new TxAudioItem {
                Path = "MENU > SET > Connectors > MOD Input > DATA OFF MOD", Ask = "00 84",
                Values = new[] { "MIC", "USB", "ACC", "MIC,USB", "MIC,ACC", "LAN" }, Good = new[] { 1, 3 },
                SetTo = "MIC,USB (or USB)" } } },
            new TxAudioRadio { Key = "IC7300", Name = "IC-7300", Icom = true,
                Note = "On the IC-7300 the microphone does not work in SSB while this is USB.",
                Items = new[] { new TxAudioItem {
                Path = "MENU > SET > Connectors > DATA OFF MOD", Ask = "00 66",
                Values = new[] { "MIC", "ACC", "MIC,ACC", "USB" }, Good = new[] { 3 }, SetTo = "USB" } } },
            new TxAudioRadio { Key = "IC7610", Name = "IC-7610", Icom = true, Tested = true /* his, 2026-11-27 */, Items = new[] { new TxAudioItem {
                Path = "MENU > SET > Connectors > MOD Input > DATA OFF MOD", Ask = "00 91",
                Values = IcomModInputs, Good = new[] { 3, 4 }, SetTo = "MIC,USB (or USB)" } } },
            new TxAudioRadio { Key = "IC9700", Name = "IC-9700", Icom = true, Items = new[] { new TxAudioItem {
                Path = "MENU > SET > Connectors > MOD Input > DATA OFF MOD", Ask = "01 15",
                Values = IcomModInputs, Good = new[] { 3, 4 }, SetTo = "MIC,USB (or USB)" } } },
            new TxAudioRadio { Key = "FTDX101", Name = "FTDX101", Icom = false,
                Note = "While SSB MOD SOURCE is REAR, the microphone does not work in SSB.",
                Items = new[] {
                new TxAudioItem { Path = "MENU > RADIO SETTING > MODE SSB > SSB MOD SOURCE", Ask = "EX010111;",
                                  Values = new[] { "MIC", "REAR" }, Good = new[] { 1 }, SetTo = "REAR" },
                new TxAudioItem { Path = "MENU > RADIO SETTING > MODE SSB > REAR SELECT", Ask = "EX010112;",
                                  Values = new[] { "DATA", "USB" }, Good = new[] { 1 }, SetTo = "USB" } } },
            new TxAudioRadio { Key = "FTDX10", Name = "FTDX10", Icom = false,
                Note = "While SSB MOD SOURCE is REAR, the microphone does not work in SSB.",
                Items = new[] {
                new TxAudioItem { Path = "MENU > RADIO SETTING > MODE SSB > SSB MOD SOURCE", Ask = "EX010113;",
                                  Values = new[] { "MIC", "REAR" }, Good = new[] { 1 }, SetTo = "REAR" },
                new TxAudioItem { Path = "MENU > RADIO SETTING > MODE SSB > REAR SELECT", Ask = "EX010114;",
                                  Values = new[] { "DATA", "USB" }, Good = new[] { 1 }, SetTo = "USB" } } },
            new TxAudioRadio { Key = "FT991A", Name = "FT-991A", Icom = false,
                Note = "While SSB MIC SELECT is REAR, the microphone does not work in SSB.",
                Items = new[] {
                new TxAudioItem { Path = "MENU 106 SSB MIC SELECT", Ask = "EX106;",
                                  Values = new[] { "MIC", "REAR" }, Good = new[] { 1 }, SetTo = "REAR" },
                new TxAudioItem { Path = "MENU 109 SSB PORT SELECT", Ask = "EX109;",
                                  Values = new[] { "DATA", "USB" }, Good = new[] { 1 }, SetTo = "USB" } } },
        };

        string _txAudioOkFor;          // read as right (or not askable) on this connection; null = ask on the next press
        bool _txAudioChecking;

        // From the 5-second timer: the radio went away, so the next press asks again.
        void ForgetRadioTxAudioWhenOffline()
        {
            try { if (!IsCatLive()) _txAudioOkFor = null; }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // Asked by a Msg press before it sends. False only when the radio ANSWERED with a setting that
        // will not take the computer's sound - then he has been told what to change. Anything else
        // (no CAT, a radio not in the list, another sound card, no answer) lets the press send.
        async System.Threading.Tasks.Task<bool> RadioTxAudioAllowsSend()
        {
            try
            {
                if (!IsCatLive()) return true;
                string rigType = NormalizeRigType(Rig.RigType);
                if (string.Equals(_txAudioOkFor, rigType, StringComparison.OrdinalIgnoreCase)) return true;
                bool ok = await CheckRadioTxAudio(rigType);
                if (ok) _txAudioOkFor = rigType;
                return ok;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return true; }
        }

        async System.Threading.Tasks.Task<bool> CheckRadioTxAudio(string rigType)
        {
            _txAudioChecking = true;
            try
            {
                string key = VoiceModelKey(rigType);
                TxAudioRadio radio = null;
                foreach (var r in TxAudioRadios)
                    if (key.StartsWith(r.Key, StringComparison.Ordinal)) { radio = r; break; }
                if (radio == null)
                {
                    Log.Warn(VoiceMsgTag + "radio sound check: " + rigType + " is not in the list of radios HolyLogger knows how to ask");
                    return true;
                }

                string card = AudioMessages.Settings.RadioDevice ?? string.Empty;
                if (card.IndexOf("USB Audio CODEC", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    Log.Warn(VoiceMsgTag + "radio sound check: not asked - the card to the radio (" + (card.Length == 0 ? "none" : card)
                             + ") is not the radio's own USB sound");
                    return true;
                }

                string address = radio.Icom ? GetIcomCivAddress(rigType) : null;
                if (radio.Icom && address == null)
                {
                    Log.Warn(VoiceMsgTag + "radio sound check: no CI-V address for " + rigType);
                    return true;
                }
                bool? echo = radio.Icom ? IcomEchoesCommands(rigType) : null;

                var wrong = new System.Collections.Generic.List<string>();
                foreach (var item in radio.Items)
                {
                    int v = radio.Icom
                        ? await AskRig(Rig, "FE FE " + address + " E0 1A 05 " + item.Ask + " FD", (echo == false ? 0 : 9) + 10, string.Empty, IcomValueOf(item.Ask))
                        : await AskRig(Rig, item.Ask, 0, ";", AsciiNumberAfter(item.Ask.TrimEnd(';')));
                    string now = v >= 0 && v < item.Values.Length ? item.Values[v] : v < 0 ? "(no answer)" : "(" + v + ")";
                    bool good = v >= 0 && Array.IndexOf(item.Good, v) >= 0;
                    Log.Warn(VoiceMsgTag + "radio sound check: " + radio.Name + " " + item.Path + " = " + now + (good ? " - right" : v < 0 ? "" : " - WRONG"));
                    if (v < 0) return true;     // no answer: say nothing rather than something wrong
                    if (!good) wrong.Add(item.Path + "\n    now: " + now + " - set it to: " + item.SetTo);
                }
                if (wrong.Count == 0) return true;

                string said = "Your " + radio.Name + " will not transmit the sound from this computer in SSB.\n\n"
                    + "Change this on the radio:\n" + string.Join("\n", wrong)
                    + (radio.Note != null ? "\n\n" + radio.Note : string.Empty);

                if (radio.Tested)
                {
                    Log.Warn(VoiceMsgTag + "NOT SENT: the radio will not take the sound from this computer - told him what to change");
                    HolyMessageBox.ShowWarning(said + "\n\nNothing was sent.",
                        "Voice Message", this, fitLongestLine: true);   // a menu path is read whole, not split
                    return false;
                }

                // NOT YET TRIED ON THIS RADIO (his rule, 2026-11-27): read from the maker's document only, so
                // a misread item could say "wrong" when it is right. Then he can send anyway - the cost of
                // a mistake here is one click, not a button that never works. Enter does NOT send.
                bool anyway = HolyMessageBox.ShowConfirm(
                    said + "\n\nIf this setting is already right on your radio, press Send anyway.",
                    "Voice Message", HolyMsgType.Warning, this,
                    yesText: "Send anyway", noText: "Don't send", fitLongestLine: true, enterSaysNo: true);
                Log.Warn(VoiceMsgTag + (anyway ? "the radio reads wrong, he chose Send anyway (" + radio.Name + " not yet tested)"
                                               : "NOT SENT: the radio will not take the sound from this computer - told him what to change"));
                return anyway;
            }
            catch (Exception ex) { Log.Swallow(ex); return true; }
            finally { _txAudioChecking = false; }
        }

        /// <summary>
        /// May the SSB Msg buttons send a PC recording with no CAT at all? Only when they key by a COM
        /// port or VOX. The mode comes from the radio when it answers, or else from the Mode box he sets
        /// by hand.
        /// </summary>
        bool PcMessagesSendableWithoutCat()
        {
            if (!AudioMessages.UsePc || PcMessagesKeyByCat) return false;
            bool rigOnline = Properties.Settings.Default.EnableOmniRigCAT && OmniRigEngine != null && Rig != null
                             && Rig.Status == OmniRig.RigStatusX.ST_ONLINE;
            if (rigOnline) return IsVoiceMessageModeActive();
            string mode = null;
            if (CB_Mode != null && CB_Mode.SelectedItem is ComboBoxItem item) mode = item.Content as string;
            else if (CB_Mode != null) mode = CB_Mode.Text;
            return string.Equals((mode ?? string.Empty).Trim(), "SSB", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Stops a PC message going out, if one is. True when something was stopped.</summary>
        bool StopPcMessage()
        {
            var p = _pcMsgPlayer;
            bool was = p != null || _pcMsgKeyed;
            if (was) Log.Warn(VoiceMsgTag + (_pcMsgNumber == QsoPlayNumber ? "PLAY" : "Msg " + _pcMsgNumber) + " stopped by hand");
            _pcMsgPlayer = null;
            if (p != null) p.Stop();
            EndPcMessage();
            return was;
        }

        void EndPcMessage()
        {
            if (_pcMsgNumber != 0)
                Log.Warn(VoiceMsgTag + (_pcMsgNumber == QsoPlayNumber ? "PLAY" : "Msg " + _pcMsgNumber) + " ended");
            _pcMsgPlayer = null;
            _pcMsgNumber = 0;
            // ALWAYS BACK TO RECEIVE, whatever happened: a transmitter left on is the one thing that
            // must never come out of this.
            if (_pcMsgKeyed || _pttPort != null) UnkeyRadio();
            try { UpdateVoiceMessageButtonHighlight(); PaintQsoAudioBar(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // ── RECORD AND PLAY, OVER THE MSG BUTTONS (his design, 2026-11-26) ──────────────────────────
        //
        // Record: the radio's sound as it comes into the computer on the USB line - the same recording
        // device the CW decoder listens to (Options > Listen to:). The button shows a black square while
        // it records; a second press stops, and a small window asks Save As: LAST / Save As... / Cancel.
        //
        // Play: a small window asks Play LAST / Play file... / Cancel, and the file goes on the air the
        // same way a Msg button sends a PC recording - same sound card, level and transmit switch (Tools >
        // Audio Messages). It plays to the end; Esc stops it, and so does a second press of Play.
        //
        // WRITTEN TO DISK AS IT COMES IN, not gathered in memory: a QSO can run for many minutes, and the
        // program is 32-bit. The file grows in the QSO Recordings folder and is moved to where he saves it.
        //
        // A FOLDER OF THEIR OWN (his request, 2026-11-26), not the Msg recordings' folder: beside the
        // database, next to Exported Logs and Deleted Logs. LAST lives there. Save As... and Play file...
        // may go anywhere he chooses - the Msg folder too, if he wants - and open there next time.

        const int QsoPlayNumber = 5;           // in _pcMsgNumber: the Play button, not a Msg key
        const int QsoRecordMaxMinutes = 60;    // a Record left running must not fill the disk

        WaveInRecorder _qsoRec;
        QsoWavWriter _qsoWav;
        DispatcherTimer _qsoRecWatch;

        static string QsoTempPath { get { return System.IO.Path.Combine(QsoDefaultFolder, "recording.tmp.wav"); } }
        static string QsoLastPath { get { return System.IO.Path.Combine(QsoDefaultFolder, "LAST.wav"); } }

        const string QsoFolderName = "QSO Recordings";

        static string QsoDefaultFolder
        {
            get
            {
                try
                {
                    string data = DataAccess.GetInstance()?.DataFolder;
                    if (!string.IsNullOrWhiteSpace(data)) return System.IO.Path.Combine(data, QsoFolderName);
                }
                catch (Exception ex) { Log.Swallow(ex); }

                // Only if the database folder is somehow unknown. Documents always exists and is writable.
                return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                                              "HolyLogger " + QsoFolderName);
            }
        }

        // ── IS THE RADIO'S SOUND CARD THERE? (his rule, 2026-11-26) ─────────────────────────────────
        //
        // REC QSO is offered only when we are sure there is something to record from: the device named
        // in Options > Listen to: (the radio's USB codec, or whatever interface brings its sound in), and
        // that device is plugged in right now. "System default" is not enough - it is most often the
        // PC's own microphone, and a recording of the shack is not a recording of the QSO.
        //
        // Read once and kept, not on every repaint: the Msg labels repaint often. Read again when Options
        // changes the device, and when Windows says a device came or went (a USB plug), once the burst
        // of messages a plug-in makes has been quiet for a moment.
        //
        // PLAY THE SAME WAY, for the other direction: only when the sound card that goes TO the radio
        // (Tools > Audio Messages) is chosen and plugged in. Read again whenever Audio Messages saves
        // its settings, so picking the card there shows PLAY at once.
        bool _radioSoundCardPresent;
        bool _radioSoundOutPresent;
        DispatcherTimer _soundCardSettle;
        const int WM_DEVICECHANGE_MSG = 0x0219;

        void RefreshRadioSoundCard()
        {
            bool present = false, outPresent = false;
            try
            {
                string device = Properties.Settings.Default.CwDecodeInputDevice ?? string.Empty;
                if (device.Trim().Length > 0)
                    present = WaveInRecorder.FindDevice(WaveInRecorder.GetInputDeviceNames(), device) != null;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            try
            {
                string card = AudioMessages.Settings.RadioDevice ?? string.Empty;
                if (card.Trim().Length > 0)
                    outPresent = WaveInRecorder.FindDevice(WaveOutPlayer.GetOutputDeviceNames(), card) != null;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            _radioSoundCardPresent = present;
            _radioSoundOutPresent = outPresent;
            PaintQsoAudioBar();
        }

        IntPtr SoundCardChangeHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_DEVICECHANGE_MSG) return IntPtr.Zero;
            if (_soundCardSettle == null)
            {
                _soundCardSettle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                _soundCardSettle.Tick += (s, e) => { _soundCardSettle.Stop(); RefreshRadioSoundCard(); };
            }
            _soundCardSettle.Stop();
            _soundCardSettle.Start();
            return IntPtr.Zero;
        }

        // Shown in SSB only - and also while it is recording or playing, so it can always be stopped.
        // REC QSO itself only when the radio's sound card is there (above), or while it records; PLAY
        // only when the card to the radio is there, or while it plays.
        void PaintQsoAudioBar()
        {
            if (QsoAudioBar == null) return;
            try
            {
                bool ssb = !IsCwModeActive() && !IsRttyModeActive();
                bool recording = _qsoRec != null;
                bool playing = _pcMsgNumber == QsoPlayNumber || _qsoListenPlayer != null;
                bool canRecord = _radioSoundCardPresent || recording;
                bool canPlay = _radioSoundOutPresent || playing;

                // No button to show, no frame either - an empty frame would sit there meaning nothing.
                QsoAudioBar.Visibility = (ssb || recording || playing) && (canRecord || canPlay)
                                         ? Visibility.Visible : Visibility.Collapsed;
                Btn_QsoRecord.Visibility = canRecord ? Visibility.Visible : Visibility.Collapsed;
                Btn_QsoPlay.Visibility = canPlay ? Visibility.Visible : Visibility.Collapsed;
                Btn_QsoPlay.Margin = canRecord ? new Thickness(8, 0, 0, 0) : new Thickness(0);

                if (recording)
                {
                    // "REC" stays; "QSO" gives way to the black square - the stop sign (his design).
                    Btn_QsoRecord.Content = WordAndSquare("REC");
                    Btn_QsoRecord.ToolTip = "Recording. Press to stop.";
                }
                else
                {
                    Btn_QsoRecord.Content = "REC QSO";
                    Btn_QsoRecord.ToolTip = "Record the radio's sound. Press again to stop.";
                }

                if (playing)
                {
                    Btn_QsoPlay.Content = WordAndSquare("PLAY");
                    Btn_QsoPlay.ToolTip = "On the air. Press, or Esc, to stop.";
                }
                else
                {
                    Btn_QsoPlay.Content = "PLAY";
                    Btn_QsoPlay.ToolTip = "Send a recording on the air. Esc stops it.";
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // "REC ■" / "PLAY ■": the word that stays, then the black square - the stop sign.
        static StackPanel WordAndSquare(string word)
        {
            var face = new StackPanel { Orientation = Orientation.Horizontal };
            face.Children.Add(new TextBlock { Text = word, VerticalAlignment = VerticalAlignment.Center });
            face.Children.Add(new Rectangle { Width = 14, Height = 14, Fill = Brushes.Black,
                                              Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            return face;
        }

        private void QsoAudioBar_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(AlignQsoAudioBar), DispatcherPriority.Loaded);
        }

        private void QsoAudioBar_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(AlignQsoAudioBar), DispatcherPriority.Loaded);
        }

        // Centred over the frame round the four Msg buttons, measured from where both really are.
        void AlignQsoAudioBar()
        {
            try
            {
                if (QsoAudioBar == null || PlayCommandsBorder == null) return;
                if (!QsoAudioBar.IsVisible || !PlayCommandsBorder.IsVisible) return;
                var parent = QsoAudioBar.Parent as UIElement;
                if (parent == null) return;

                double frameLeft = PlayCommandsBorder.TranslatePoint(new Point(0, 0), parent).X;
                double left = frameLeft + (PlayCommandsBorder.ActualWidth - QsoAudioBar.ActualWidth) / 2;
                if (left < 0) left = 0;
                if (Math.Abs(left - QsoAudioBar.Margin.Left) < 0.5) return;
                var m = QsoAudioBar.Margin;   // top and bottom stay as the XAML set them (the -1s)
                QsoAudioBar.Margin = new Thickness(left, m.Top, m.Right, m.Bottom);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        private void QsoRecord_Click(object sender, RoutedEventArgs e)
        {
            if (_qsoRec != null) StopQsoRecording(null);
            else StartQsoRecording();
        }

        void StartQsoRecording()
        {
            string device = Properties.Settings.Default.CwDecodeInputDevice ?? string.Empty;

            // A named device that is not there would quietly record the default one (often the PC's own
            // microphone) - say so instead.
            if (device.Length > 0 && WaveInRecorder.FindDevice(WaveInRecorder.GetInputDeviceNames(), device) == null)
            {
                HolyMessageBox.ShowWarning("The radio's sound card (" + device + ") is not connected.\n\n"
                                         + "Is the radio on, and its USB cable in?", "Record", this);
                return;
            }

            WaveInRecorder rec = null;
            QsoWavWriter wav = null;
            try
            {
                rec = new WaveInRecorder();
                rec.Samples += OnQsoSamples;
                string error;
                if (!rec.Start(device, AudioMessages.VoiceRates, out error))
                {
                    rec.Samples -= OnQsoSamples;
                    rec.Dispose();
                    HolyMessageBox.ShowWarning("The radio's sound cannot be recorded.\n\n"
                                             + (string.IsNullOrEmpty(error) ? "The device cannot be opened." : error), "Record", this);
                    return;
                }

                System.IO.Directory.CreateDirectory(QsoDefaultFolder);
                wav = new QsoWavWriter(QsoTempPath, rec.ActualSampleRate);
            }
            catch (Exception ex)
            {
                Log.Swallow(ex);
                if (rec != null) { try { rec.Samples -= OnQsoSamples; rec.Stop(); rec.Dispose(); } catch (Exception swallowed) { Log.Swallow(swallowed); } }
                if (wav != null) wav.Finish();
                HolyMessageBox.ShowWarning("The recording file cannot be made in\n" + QsoDefaultFolder, "Record", this);
                return;
            }

            rec.Failed += why => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_qsoRec == rec) StopQsoRecording(why);
            }));

            _qsoWav = wav;
            _qsoRec = rec;

            _qsoRecWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _qsoRecWatch.Tick += (s, args) =>
            {
                var w = _qsoWav;
                if (w != null && w.Rate > 0 && w.Samples >= (long)w.Rate * 60 * QsoRecordMaxMinutes)
                    StopQsoRecording("The recording stopped by itself after " + QsoRecordMaxMinutes + " minutes.");
            };
            _qsoRecWatch.Start();

            PaintQsoAudioBar();
        }

        // On the capture thread. The writer has its own lock.
        void OnQsoSamples(short[] block, int count)
        {
            var w = _qsoWav;
            if (w != null) w.Write(block, count);
        }

        // whyStopped: null when he pressed the square; otherwise what stopped it, said before saving.
        void StopQsoRecording(string whyStopped)
        {
            var rec = _qsoRec;
            var wav = _qsoWav;
            _qsoRec = null;
            _qsoWav = null;
            if (_qsoRecWatch != null) { _qsoRecWatch.Stop(); _qsoRecWatch = null; }

            if (rec != null)
            {
                try { rec.Samples -= OnQsoSamples; rec.Stop(); rec.Dispose(); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }
            long samples = wav != null ? wav.Samples : 0;
            bool written = wav != null && wav.Finish();
            PaintQsoAudioBar();

            if (!string.IsNullOrEmpty(whyStopped))
                HolyMessageBox.ShowWarning(whyStopped, "Record", this);

            if (!written || samples == 0)
            {
                TryDelete(QsoTempPath);
                HolyMessageBox.ShowWarning("Nothing was recorded.", "Record", this);
                return;
            }

            while (true)
            {
                int choice = QsoAudioChoiceWindow.Ask(this, "Save Recording", "QsoSaveChoice", 1,
                                                      "Save As: LAST", "Save As...", "Cancel");
                if (choice == 0) { TryDelete(QsoTempPath); return; }

                string target;
                if (choice == 1) target = QsoLastPath;
                else
                {
                    target = BrowseQsoSave();
                    if (target == null) continue;   // closed the browse window: back to the three choices
                }

                if (MoveRecording(QsoTempPath, target))
                {
                    if (choice == 2) RememberQsoFolder(target);
                    return;
                }
                HolyMessageBox.ShowWarning("The recording cannot be saved to\n" + target, "Record", this);
            }
        }

        string BrowseQsoSave()
        {
            try
            {
                string call = TB_DXCallsign != null ? (TB_DXCallsign.Text ?? string.Empty).Trim() : string.Empty;
                foreach (char c in System.IO.Path.GetInvalidFileNameChars()) call = call.Replace(c, '-');
                string name = (call.Length > 0 ? call + " " : string.Empty) + DateTime.UtcNow.ToString("yyyy-MM-dd HHmm") + ".wav";

                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Save Recording",
                    Filter = "Sound files (*.wav)|*.wav",
                    DefaultExt = ".wav",
                    FileName = name,
                    InitialDirectory = QsoFolder()
                };
                return dlg.ShowDialog(this) == true ? dlg.FileName : null;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return null; }
        }

        // ── PLAY: ON THE AIR, OR ONLY IN HIS OWN SPEAKERS (his design, 2026-11-26) ──────────────────
        //
        // Transmit LAST / Transmit file: on the air, as a Msg key sends a PC recording.
        // Listen to LAST / Listen to file: on this computer's speakers only - the speakers chosen in
        // Audio Messages - and the radio stays on receive. Either one shows PLAY ■; a second press of
        // PLAY or Esc stops it. Starting one stops the other.
        AudioClipPlayer _qsoListenPlayer;

        private void QsoPlay_Click(object sender, RoutedEventArgs e)
        {
            // A second press while it plays stops it, like a Msg button.
            if (_pcMsgNumber == QsoPlayNumber) { StopPcMessage(); return; }
            if (_qsoListenPlayer != null) { StopQsoListen(); return; }

            // No default button: a stray Enter must not put anything on the air.
            int choice = QsoAudioChoiceWindow.Ask(this, "Play Recording", "QsoPlayChoice", 0,
                "Transmit LAST", "Transmit file...", "Listen to LAST", "Listen to file...", "Cancel");
            if (choice == 0) return;

            bool transmit = choice == 1 || choice == 2;
            bool useLast = choice == 1 || choice == 3;
            string title = transmit ? "Transmit" : "Listen";

            string path;
            if (useLast)
            {
                path = QsoLastPath;
                if (!System.IO.File.Exists(path))
                {
                    HolyMessageBox.ShowWarning("There is no LAST recording yet.", title, this);
                    return;
                }
            }
            else
            {
                try
                {
                    var dlg = new Microsoft.Win32.OpenFileDialog
                    {
                        Title = transmit ? "Transmit file" : "Listen to file",
                        Filter = "Sound files (*.wav)|*.wav",
                        InitialDirectory = QsoFolder()
                    };
                    if (dlg.ShowDialog(this) != true) return;
                    path = dlg.FileName;
                    RememberQsoFolder(path);
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); return; }
            }

            if (transmit && !RadioCardReady(0, title)) return;

            short[] samples; int rate;
            if (!AudioMessages.Load(path, out samples, out rate) || samples == null || samples.Length == 0)
            {
                HolyMessageBox.ShowWarning("This file cannot be played:\n" + path + "\n\nOnly plain WAV files can be played.", title, this);
                return;
            }

            // One at a time: a Msg going out, or a recording being listened to, stops first.
            if (_pcMsgNumber != 0) StopPcMessage();
            StopQsoListen();

            if (transmit) SendClipOnAir(samples, rate, QsoPlayNumber, title);
            else ListenToClip(samples, rate);
        }

        void ListenToClip(short[] samples, int rate)
        {
            string error;
            uint device = WaveOutPlayer.ResolveDeviceId(AudioMessages.Settings.SpeakerDevice);
            AudioClipPlayer player = null;
            player = AudioClipPlayer.Start(samples, rate, device, 1.0, 0, () => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_qsoListenPlayer != player) return;   // already stopped by hand
                _qsoListenPlayer = null;
                PaintQsoAudioBar();
            })), out error);

            if (player == null)
            {
                HolyMessageBox.ShowWarning(string.IsNullOrEmpty(error) ? "The speakers cannot be opened." : error, "Listen", this);
                return;
            }
            _qsoListenPlayer = player;
            PaintQsoAudioBar();
        }

        /// <summary>Stops listening to a recording, if one is playing. True when something was stopped.</summary>
        bool StopQsoListen()
        {
            var p = _qsoListenPlayer;
            _qsoListenPlayer = null;
            if (p == null) return false;
            try { p.Stop(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            PaintQsoAudioBar();
            return true;
        }

        static string QsoFolder()
        {
            string f = AudioMessages.Settings.QsoFolder;
            if (!string.IsNullOrEmpty(f) && System.IO.Directory.Exists(f)) return f;
            string d = QsoDefaultFolder;
            try { System.IO.Directory.CreateDirectory(d); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            return System.IO.Directory.Exists(d) ? d : string.Empty;
        }

        static void RememberQsoFolder(string file)
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(file);
                if (string.IsNullOrEmpty(dir)) return;
                AudioMessages.Settings.QsoFolder = dir;
                AudioMessages.SaveSettings();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        static bool MoveRecording(string from, string to)
        {
            try
            {
                if (string.Equals(System.IO.Path.GetFullPath(from), System.IO.Path.GetFullPath(to), StringComparison.OrdinalIgnoreCase))
                    return true;
                string dir = System.IO.Path.GetDirectoryName(to);
                if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                if (System.IO.File.Exists(to)) System.IO.File.Delete(to);
                System.IO.File.Move(from, to);
                return true;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return false; }
        }

        static void TryDelete(string path)
        {
            try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }
    }

    // A WAV file written as the sound arrives: header first with the sizes left at 0, the samples
    // appended block by block, and the two sizes filled in at Finish. 16-bit mono, like the Msg files,
    // so AudioMessages.Load plays it back.
    internal sealed class QsoWavWriter
    {
        readonly object _gate = new object();
        System.IO.FileStream _fs;
        System.IO.BinaryWriter _w;
        long _samples;

        public int Rate { get; private set; }
        public long Samples { get { lock (_gate) return _samples; } }

        public QsoWavWriter(string path, int rate)
        {
            Rate = rate;
            _fs = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.Read);
            _w = new System.IO.BinaryWriter(_fs);
            _w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
            _w.Write(36);
            _w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });
            _w.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
            _w.Write(16);
            _w.Write((short)1);          // PCM
            _w.Write((short)1);          // mono
            _w.Write(rate);
            _w.Write(rate * 2);
            _w.Write((short)2);
            _w.Write((short)16);
            _w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
            _w.Write(0);
        }

        public void Write(short[] block, int count)
        {
            lock (_gate)
            {
                if (_w == null) return;
                try
                {
                    for (int i = 0; i < count; i++) _w.Write(block[i]);
                    _samples += count;
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }
        }

        /// <summary>Fills in the sizes and closes the file. False if it could not be finished.</summary>
        public bool Finish()
        {
            lock (_gate)
            {
                if (_w == null) return false;
                bool ok = true;
                try
                {
                    long dataBytes = _samples * 2;
                    _w.Flush();
                    _fs.Seek(4, System.IO.SeekOrigin.Begin);
                    _w.Write((int)(36 + dataBytes));
                    _fs.Seek(40, System.IO.SeekOrigin.Begin);
                    _w.Write((int)dataBytes);
                    _w.Flush();
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); ok = false; }
                try { _w.Dispose(); } catch (Exception swallowed) { Log.Swallow(swallowed); }
                _w = null;
                _fs = null;
                return ok;
            }
        }
    }

    // THE SMALL CHOICE WINDOW behind REC QSO and PLAY: buttons one under the other, and nothing else.
    // It remembers where he put it. The LAST button is always Cancel. Returns 1, 2, ... for the others,
    // 0 for Cancel, for Esc and for the X. defaultChoice is the one Enter presses (0 = none - so a
    // stray Enter never puts anything on the air).
    internal sealed class QsoAudioChoiceWindow : Window
    {
        int _choice;

        // ── HOW EACH BUTTON LOOKS (his design, 2026-11-26) ──────────────────────────────────────────
        //
        // Transmit: light red, with a radiating mast. Listen: light green, with a speaker. Cancel: white
        // on blue. Anything else (the Save window's two) a plain light grey. Icon and words centred in the
        // button, like Cancel (his request). Drawn here, not image files: they take the text colour and stay
        // sharp at any screen scaling. Their own template, so the mouse only lightens a button's own colour
        // instead of Windows painting its pale blue over it.
        internal enum Look { Plain, Transmit, Listen, Cancel }

        static Look LookOf(string text, bool isCancel)
        {
            if (isCancel) return Look.Cancel;
            if (text.StartsWith("Transmit", StringComparison.OrdinalIgnoreCase)) return Look.Transmit;
            if (text.StartsWith("Listen", StringComparison.OrdinalIgnoreCase)) return Look.Listen;
            return Look.Plain;
        }

        static Brush Frozen(string hex)
        {
            var b = (Brush)new BrushConverter().ConvertFromString(hex);
            b.Freeze();
            return b;
        }

        static readonly Brush DarkInk = Frozen("#222222");

        static ControlTemplate _template;
        static ControlTemplate ChoiceTemplate
        {
            get
            {
                if (_template == null)
                    _template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
                        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'"
                      + " xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>"
                      + "<Grid SnapsToDevicePixels='True'>"
                      + "<Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' CornerRadius='5'/>"
                      + "<Border x:Name='Shade' Background='White' Opacity='0' CornerRadius='5' Margin='1'/>"
                      + "<ContentPresenter Margin='{TemplateBinding Padding}' HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalAlignment='Center'/>"
                      + "</Grid>"
                      + "<ControlTemplate.Triggers>"
                      + "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Shade' Property='Opacity' Value='0.3'/></Trigger>"
                      + "<Trigger Property='IsPressed' Value='True'><Setter TargetName='Shade' Property='Background' Value='Black'/><Setter TargetName='Shade' Property='Opacity' Value='0.12'/></Trigger>"
                      + "</ControlTemplate.Triggers>"
                      + "</ControlTemplate>");
                return _template;
            }
        }

        // A mast with waves going out both sides - "on the air".
        static UIElement TransmitIcon(Brush ink)
        {
            var c = new Canvas { Width = 24, Height = 24 };
            c.Children.Add(Stroke("M12,11 L8,22 M12,11 L16,22 M9.1,19 L14.9,19", ink));
            c.Children.Add(Stroke("M8.2,5.6 A5,5 0 0 0 8.2,12.4 M15.8,5.6 A5,5 0 0 1 15.8,12.4", ink));
            c.Children.Add(Stroke("M5.2,2.8 A9,9 0 0 0 5.2,15.2 M18.8,2.8 A9,9 0 0 1 18.8,15.2", ink));
            c.Children.Add(new Ellipse { Width = 4, Height = 4, Fill = ink, Margin = new Thickness(10, 7, 0, 0) });
            return new Viewbox { Width = 22, Height = 22, Child = c };
        }

        // A loudspeaker with two sound waves - "in my speakers".
        static UIElement SpeakerIcon(Brush ink)
        {
            var c = new Canvas { Width = 24, Height = 24 };
            c.Children.Add(new Path { Data = Geometry.Parse("M3,9 L7,9 L12,4.5 L12,19.5 L7,15 L3,15 Z"), Fill = ink });
            c.Children.Add(Stroke("M15,9 A4.2,4.2 0 0 1 15,15", ink));
            c.Children.Add(Stroke("M17.6,6 A8,8 0 0 1 17.6,18", ink));
            return new Viewbox { Width = 22, Height = 22, Child = c };
        }

        // The button's words, with LAST in bold wherever it appears (his request) - it is the name of a
        // file, and the eye should find it.
        static TextBlock Words(string text, Brush ink)
        {
            var tb = new TextBlock { Foreground = ink, VerticalAlignment = VerticalAlignment.Center };
            const string bold = "LAST";
            int at = 0;
            while (true)
            {
                int i = text.IndexOf(bold, at, StringComparison.Ordinal);
                if (i < 0) { if (at < text.Length) tb.Inlines.Add(new System.Windows.Documents.Run(text.Substring(at))); break; }
                if (i > at) tb.Inlines.Add(new System.Windows.Documents.Run(text.Substring(at, i - at)));
                tb.Inlines.Add(new System.Windows.Documents.Run(bold) { FontWeight = FontWeights.Bold });
                at = i + bold.Length;
            }
            return tb;
        }

        static Path Stroke(string data, Brush ink)
        {
            return new Path
            {
                Data = Geometry.Parse(data),
                Stroke = ink,
                StrokeThickness = 1.9,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
        }

        QsoAudioChoiceWindow(string title, string key, int defaultChoice, string[] choices)
        {
            Title = title;
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Background = ThemeManager.Brush("WindowBg");
            Foreground = ThemeManager.Brush("TextBrush");
            FontSize = 16;
            if (!WindowBounds.HasSaved(key)) WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var panel = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
            int last = choices.Length - 1;
            for (int i = 0; i < choices.Length; i++)
            {
                int value = i == last ? 0 : i + 1;
                var b = Choice(choices[i], value, isDefault: value != 0 && value == defaultChoice, isCancel: i == last);
                if (i == last) b.Margin = new Thickness(0);
                panel.Children.Add(b);
            }
            Content = panel;

            WindowBounds.Attach(this, key);
        }

        Button Choice(string text, int value, bool isDefault, bool isCancel)
        {
            Look look = LookOf(text, isCancel);
            // EVERY BUTTON IN A FINE BLACK FRAME with rounded corners (his request): one pixel, the same
            // round corners as the face.
            Brush back, ink;
            Brush border = Brushes.Black;
            switch (look)
            {
                case Look.Transmit: back = Frozen("#FFCDD2"); ink = DarkInk; break;
                case Look.Listen:   back = Frozen("#C8E6C9"); ink = DarkInk; break;
                case Look.Cancel:   back = Frozen("#1565C0"); ink = Brushes.White; break;
                default:            back = Frozen("#E8E8E8"); ink = DarkInk; break;
            }

            TextBlock words = Words(text, ink);
            object content = words;
            var align = HorizontalAlignment.Center;
            if (look == Look.Transmit || look == Look.Listen)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(look == Look.Transmit ? TransmitIcon(ink) : SpeakerIcon(ink));
                words.Margin = new Thickness(12, 0, 0, 0);
                row.Children.Add(words);
                content = row;
            }

            var b = new Button
            {
                Content = content,
                Template = ChoiceTemplate,
                Background = back,
                BorderBrush = border,
                Foreground = ink,
                MinWidth = 240,
                Height = 38,
                Padding = new Thickness(16, 0, 16, 0),
                HorizontalContentAlignment = align,
                Margin = new Thickness(0, 0, 0, 8),
                Cursor = System.Windows.Input.Cursors.Hand,
                IsDefault = isDefault,
                IsCancel = isCancel
            };
            b.Click += (s, e) => { _choice = value; Close(); };
            return b;
        }

        public static int Ask(Window owner, string title, string key, int defaultChoice, params string[] choices)
        {
            try
            {
                if (choices == null || choices.Length < 2) return 0;
                var w = new QsoAudioChoiceWindow(title, key, defaultChoice, choices);
                try { w.Owner = owner; } catch (Exception swallowed) { Log.Swallow(swallowed); }
                w.ShowDialog();
                return w._choice;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return 0; }
        }
    }
}
