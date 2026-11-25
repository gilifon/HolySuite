using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

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
                    UpdateMessageButtonLabels();
                    UpdateVoiceMessageAvailabilityState();
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }));
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

        void TriggerPcMessage(int n)
        {
            // A second press of the one going out stops it; a press of another stops it and sends that.
            if (_pcMsgNumber != 0)
            {
                int was = _pcMsgNumber;
                StopPcMessage();
                if (was == n) return;
            }

            if (!AudioMessages.HasRecording(n))
            {
                if (HolyMessageBox.ShowConfirm("Msg " + n + " has no recording yet.\n\nOpen Audio Messages to record it?",
                                               "Voice Message", HolyMsgType.Info, this))
                    OpenAudioMessagesWindow(n);
                return;
            }

            string card = AudioMessages.Settings.RadioDevice;
            if (string.IsNullOrEmpty(card))
            {
                if (HolyMessageBox.ShowConfirm("Choose the sound card that goes to the radio first.\n\nOpen Audio Messages?",
                                               "Voice Message", HolyMsgType.Info, this))
                    OpenAudioMessagesWindow(n);
                return;
            }
            if (!WaveOutPlayer.GetOutputDeviceNames().Contains(card))
            {
                HolyMessageBox.ShowWarning("The sound card to the radio (" + card + ") is not connected.\n\n"
                                         + "Is the radio on, and its USB cable in?", "Voice Message", this);
                return;
            }

            short[] samples; int rate;
            if (!AudioMessages.Load(n, out samples, out rate))
            {
                HolyMessageBox.ShowWarning("The recording of Msg " + n + " cannot be read.\n\nRecord it again in Audio Messages.",
                                           "Voice Message", this);
                return;
            }

            string whyNotKeyed;
            if (!KeyRadio(true, out whyNotKeyed))
            {
                HolyMessageBox.ShowWarning((whyNotKeyed ?? "The radio could not be switched to transmit.") + "\n\nNothing was sent.",
                                           "Voice Message", this);
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
                UnkeyRadio();
                HolyMessageBox.ShowWarning(error + "\n\nNothing was sent.", "Voice Message", this);
                return;
            }

            _pcMsgPlayer = player;
            _pcMsgNumber = n;
            UpdateVoiceMessageButtonHighlight();
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
                    _pttPort = sp;
                    _pttUseRts = rts;
                    _pcMsgKeyed = true;
                    _pcMsgKeyedVia = via;
                    return true;
                }

                if (!IsCatLive() || Rig == null) { whyNot = "CAT is not working, so the radio cannot be switched to transmit."; return false; }
                Rig.Tx = (OmniRig.RigParamX)PM_TX;
                _pcMsgKeyed = true;
                _pcMsgKeyedVia = AudioMessages.PttCat;
                return true;
            }
            catch (Exception ex)
            {
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
            _pcMsgPlayer = null;
            if (p != null) p.Stop();
            EndPcMessage();
            return was;
        }

        void EndPcMessage()
        {
            _pcMsgPlayer = null;
            _pcMsgNumber = 0;
            // ALWAYS BACK TO RECEIVE, whatever happened: a transmitter left on is the one thing that
            // must never come out of this.
            if (_pcMsgKeyed || _pttPort != null) UnkeyRadio();
            try { UpdateVoiceMessageButtonHighlight(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }
    }
}
