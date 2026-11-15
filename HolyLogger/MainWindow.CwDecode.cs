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
                TurnOnSideToneToPc();
            }
            catch (Exception ex)
            {
                Log.Swallow(ex);
                cwDecodeWindow = null;
            }
        }

        // HIS SIDE TONE TO THE PC (his request, 2026-11-13). The CW window reads his own straight-key sending from
        // the radio's side tone (see CwDecodeWindow.OneClearNote), which the radio sends down the USB sound only with
        // its "AF Beep/Speech... Output" menu item ON - OFF from the factory. So opening the window turns it on, for
        // the radios whose maker's own CI-V table names the item (read 2026-11-13):
        //   IC-7610 - CI-V Reference Guide (IC-7610_ENG_CI-V_4): 1A 05 00 84, "Connectors > USB AF/IF Output >
        //             AF Beep/Speech... Output (00=OFF, 01=ON)". Tried on his radio: the side tone comes through.
        //   IC-7300 - Full Manual (IC-7300_ENG_FM_12b) p. 19-4: 1A 05 00 62, "beep and speech output setting to
        //             ACC/USB (00=OFF, 01=ON)". Not tried on a radio: the manual says beep and speech, not side tone.
        //   IC-7300MK2 - CI-V Reference Guide (IC-7300MK2_ENG_CI-V_0): 1A 05 00 72, "SET > Connectors > USB AF/IF
        //             Output", AF Beep/Speech... Output (00=OFF, 01=ON); 00 77 is the same item for ACC. Not tried on
        //             a radio. A different number from the IC-7300's, so it comes first: "IC7300" matches it too.
        // Yaesu's manuals (FTDX10, FTDX101, FT-991A, FT-710) name no such item. Left ON when the window closes: it
        // only adds the radio's beeps and voice announcements to the PC sound.
        //
        // THE OTHER TWO ITEMS FOR THE SOUND TO THE PC (his request, 2026-11-15), set only from Options' "Set the radio
        // for CW decoding" button: Output Select = AF (on IF the radio sends a 12 kHz IF signal, nothing a decoder
        // can read) and AF SQL = OFF (Open) (ON cuts the sound whenever the radio's squelch is closed). From the same
        // documents, the same menu (IC-7300: Full Manual p. 19-5, 1A 05 00 59 "AF/IF signal output to ACC/USB
        // (00=AF, 01=IF)" and 00 61 "squelch function for the AF signal output to ACC/USB (00=OFF (Open), 01=ON)").
        // The AF Output Level is left alone on purpose: no one value is right, and other programs (WSJT-X) share it.
        // Columns: model; Output Select, AF SQL, AF Beep/Speech... Output - each the item after 1A 05, or null when the
        // radio has no such item; the three items' names as the radio's own menu writes them, so the window shows the
        // names he will find there; and the radio's name as people write it, for the list an unsupported radio is shown.
        //
        // MORE ICOMS (his request, 2026-11-15: "the most common CW radios"), each from Icom's own document:
        //   IC-705 (CI-V guide IC-705_ENG_CI-V_6), IC-9700 (IC-9700_ENG_CI-V_4), IC-7760 (IC-7760_ENG_CI-V_2):
        //     SET > Connectors > USB AF/IF Output > Output Select / AF SQL / AF Beep/Speech... Output.
        //   IC-7100 (Full Manual IC-7100_ENG_FM_5): 0084 "ACC and USB output setting (00=AF, 01=IF)", 0083 "USB audio
        //     squelch setting (00=OFF (OPEN), 01=ON)"; no beep item.
        //   IC-7410 (IC-7410_ENG_01) 0036, IC-9100 (IC-9100_ENG_2a) 0053, IC-7600 (IC-7600_ENG_4) 0028: "USB Audio SQL"
        //     only - their manuals say "the beep tones and the voice synthesizer announcements are not sent".
        //   IC-7850/7851 have no USB sound (ACC and S/PDIF only), so they are not here.
        // Order matters where one name starts another: IC7300MK2 before IC7300.
        static readonly string[,] PcSoundItems =
        {
            { "IC7610", "00 80", "00 83", "00 84", "USB AF/IF Output > Output Select", "USB AF/IF Output > AF SQL", "USB AF/IF Output > AF Beep/Speech... Output", "IC-7610" },
            { "IC7300MK2", "00 69", "00 71", "00 72", "USB AF/IF Output > Output Select", "USB AF/IF Output > AF SQL", "USB AF/IF Output > AF Beep/Speech... Output", "IC-7300MK2" },
            { "IC7300", "00 59", "00 61", "00 62", "ACC/USB Output Select", "ACC/USB AF SQL", "ACC/USB AF Beep/Speech... Output", "IC-7300" },
            { "IC705", "01 09", "01 11", "01 12", "USB AF/IF Output > Output Select", "USB AF/IF Output > AF SQL", "USB AF/IF Output > AF Beep/Speech... Output", "IC-705" },
            { "IC9700", "01 05", "01 07", "01 08", "USB AF/IF Output > Output Select", "USB AF/IF Output > AF SQL", "USB AF/IF Output > AF Beep/Speech... Output", "IC-9700" },
            { "IC7760", "01 03", "01 06", "01 07", "USB AF/IF Output > Output Select", "USB AF/IF Output > AF SQL", "USB AF/IF Output > AF Beep/Speech... Output", "IC-7760" },
            { "IC7100", "00 84", "00 83", null, "ACC/USB Output Select", "USB Audio SQL", null, "IC-7100" },
            { "IC7410", null, "00 36", null, null, "USB Audio SQL", null, "IC-7410" },
            { "IC9100", null, "00 53", null, null, "USB Audio SQL", null, "IC-9100" },
            { "IC7600", null, "00 28", null, null, "USB Audio SQL", null, "IC-7600" },
        };

        // The row for this radio, and its address, or -1.
        int PcSoundRow(OmniRig.RigX rig, out string address)
        {
            address = null;
            if (!Properties.Settings.Default.EnableOmniRigCAT || rig == null || rig.Status != OmniRig.RigStatusX.ST_ONLINE) return -1;
            string rigType = NormalizeRigType(rig.RigType);
            string key = VoiceModelKey(rigType);
            for (int i = 0; i < PcSoundItems.GetLength(0); i++)
                if (key.StartsWith(PcSoundItems[i, 0], StringComparison.Ordinal))
                {
                    address = GetIcomCivAddress(rigType);
                    return address == null ? -1 : i;
                }
            return -1;
        }

        // A command to this rig - TrySendOmniRigCustomCommand's way, but to the rig named rather than the one in use.
        // replyLength 0 = an order: wait for the radio's OK (plus its echo, when it echoes), as that method does.
        bool SendToRig(OmniRig.RigX rig, string command, int replyLength, string replyEnd = "")
        {
            try
            {
                byte[] raw = ParseCustomCommand(command);
                if (replyLength == 0 && string.IsNullOrEmpty(replyEnd))
                {
                    bool? echo = IcomEchoesCommands(NormalizeRigType(rig.RigType));
                    if (echo.HasValue) replyLength = (echo.Value ? raw.Length : 0) + 6;
                }
                rig.SendCustomCommand(raw, replyLength, replyEnd ?? string.Empty);
                return true;
            }
            catch (Exception swallowed)
            {
                Log.Swallow(swallowed);
                return false;
            }
        }

        bool SetPcSoundItem(OmniRig.RigX rig, string address, string item, string value)
        {
            return SendToRig(rig, "FE FE " + address + " E0 1A 05 " + item + " " + value + " FD", 0);
        }

        // THE RIG TICKED IN OPTIONS, not the one in use (his screen, 2026-11-15): "2: ID-5100A" was ticked and the
        // window spoke to the IC-7610 on Rig 1, because a new choice of rig only takes effect when Options closes.
        // The window is opened from Options, so it means the rig Options shows.
        OmniRig.RigX OptionsRig()
        {
            if (OmniRigEngine == null) return null;
            return Properties.Settings.Default.SelectedOmniRig2 ? OmniRigEngine.Rig2 : OmniRigEngine.Rig1;
        }

        void TurnOnSideToneToPc()
        {
            try
            {
                string address;
                int row = PcSoundRow(Rig, out address);
                if (row >= 0 && PcSoundItems[row, 3] != null) SetPcSoundItem(Rig, address, PcSoundItems[row, 3], "01");
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // ── OPTIONS' "SET THE RADIO FOR CW DECODING" (his design, 2026-11-15) ────────────────
        //
        // A SENSITIVE ACTION GETS FEEDBACK: the button opens RadioPcSoundWindow, which shows each setting, what the
        // radio has now, what it will be set to and the exact command; nothing goes to the radio before OK; then
        // each setting is read back from the radio and shown as taken or not.
        //
        // THE KENWOOD TS-590 (his request, 2026-11-15: "many CW users use it"). One item: the beeps - and on the
        // TS-590SG, by its manual (Menu No. 75), the side tone and the voice guide too - mixed into the USB sound.
        // PC Control Command Reference Guide (ts_590_g_pc_command_e): EX P1 (3 digits) P2 (00) P3 (0) P4 (0) P5;
        // TS-590S menu 068 and TS-590SG menu 075 "External AF output beep mix", 0=OFF 1=ON. THE TWO NUMBERS SWAP
        // MEANINGS: on the TS-590SG menu 068 is the USB port's speed, on the TS-590S 075 is CTCSS mute - and OmniRig
        // has one file, TS-590, for both. So the radio is asked first: ID; answers ID021; (TS-590S) or ID023;
        // (TS-590SG), same guide. When it does not say, nothing is sent at all.

        /// <summary>One radio menu item the window sets.</summary>
        internal sealed class PcSoundSetting
        {
            public string Name;                  // as the radio's own menu names it
            public int Wanted;                   // the value to set
            public string[] Values;              // what each value means, in the radio's words
            public string Command;               // the set command, shown to him and sent
            public string Ask;                   // the command that reads it back
            public int AskLength;                // the bytes OmniRig is to wait for (Icom), or 0
            public string AskEnd;                // or the character the answer ends with (Kenwood)
            public Func<byte[], int> ValueIn;    // the value in an answer, or -1
        }

        internal sealed class PcSoundPlanResult
        {
            public System.Collections.Generic.List<PcSoundSetting> Settings;   // null when nothing can be done
            public string Radio;                                                  // the radio, as he will know it
            public string Why;                                                    // when Settings is null
        }

        // The rig the window is working on - Options' choice when it opened (see OptionsRig).
        OmniRig.RigX _pcSoundRig;

        /// <summary>What the window will set on the radio ticked in Options - or why nothing.</summary>
        internal async System.Threading.Tasks.Task<PcSoundPlanResult> PcSoundPlanAsync()
        {
            var result = new PcSoundPlanResult();
            try
            {
                var rig = _pcSoundRig = OptionsRig();
                if (!Properties.Settings.Default.EnableOmniRigCAT || rig == null || rig.Status != OmniRig.RigStatusX.ST_ONLINE)
                {
                    result.Why = rig != null && Properties.Settings.Default.EnableOmniRigCAT
                        ? "Rig " + (Properties.Settings.Default.SelectedOmniRig2 ? "2" : "1") + " (" + rig.RigType + ") is not connected."
                        : "HolyLogger is not connected to the radio.";
                    return result;
                }
                result.Radio = rig.RigType;

                string address;
                int row = PcSoundRow(rig, out address);
                if (row >= 0) { result.Settings = IcomPcSoundPlan(rig, row, address); return result; }

                if (VoiceModelKey(NormalizeRigType(rig.RigType)).StartsWith("TS590", StringComparison.Ordinal))
                {
                    int id = await AskRig(rig, "ID;", 0, ";", AsciiNumberAfter("ID"));
                    if (id == 21) { result.Radio = "TS-590S"; result.Settings = KenwoodBeepMix("068", "Menu 68, External AF output beep mix"); }
                    else if (id == 23) { result.Radio = "TS-590SG"; result.Settings = KenwoodBeepMix("075", "Menu 75, Mixing beep tones for ACC2/USB audio output"); }
                    else result.Why = "The radio did not say whether it is a TS-590S or a TS-590SG, so nothing will be sent.";
                    return result;
                }

                // THE KENWOOD TS-890S (2026-11-15). Its PC Control Command Reference Guide (ts890_pc_command_en_rev1):
                // EX P1 (0) P2 (07) P3 (11) P4 (space) P5 (3 digits); menu 0-07-11 "Audio Output Type (Rear Connectors)",
                // 000 = All, 001 = Received Audio Only. Its manual (16-7): "All: Enables output of beep tone, voice
                // guidance, audio of voice message memory and recorded audio or sidetone" to the USB-B connector - and
                // All is the factory setting. Asked first like the TS-590 (ID; answers ID024;), since OmniRig has no
                // file of its own for it and a file made for another Kenwood could carry the name.
                if (VoiceModelKey(NormalizeRigType(rig.RigType)).StartsWith("TS890", StringComparison.Ordinal))
                {
                    int id = await AskRig(rig, "ID;", 0, ";", AsciiNumberAfter("ID"));
                    if (id == 24)
                    {
                        result.Radio = "TS-890S";
                        result.Settings = new System.Collections.Generic.List<PcSoundSetting>
                        {
                            new PcSoundSetting
                            {
                                Name = "Menu 7-11, Audio Output Type (Rear Connectors)",
                                Wanted = 0,
                                Values = new[] { "All", "Received Audio Only" },
                                Command = "EX00711 000;",
                                Ask = "EX00711;",
                                AskLength = 0,
                                AskEnd = ";",
                                ValueIn = AsciiNumberAfter("EX00711 "),
                            },
                        };
                    }
                    else result.Why = "The radio did not say it is a TS-890S, so nothing will be sent.";
                    return result;
                }

                // THE LIST ONLY WHEN IT HELPS (his choice, 2026-11-15): a supported radio's owner never sees it.
                var names = new System.Collections.Generic.List<string>();
                for (int i = 0; i < PcSoundItems.GetLength(0); i++) names.Add(PcSoundItems[i, 7]);
                names.Add("TS-590S");
                names.Add("TS-590SG");
                names.Add("TS-890S");
                result.Why = "Your " + rig.RigType + " is not supported yet. Supported: " + string.Join(", ", names) + ".";
                return result;
            }
            catch (Exception ex)
            {
                Log.Swallow(ex);
                result.Settings = null;
                result.Why = "HolyLogger could not talk to the radio.";
                return result;
            }
        }

        System.Collections.Generic.List<PcSoundSetting> IcomPcSoundPlan(OmniRig.RigX rig, int row, string address)
        {
            // Nine bytes of question, echoed back when this radio echoes, then the ten of the answer.
            bool? echo = IcomEchoesCommands(NormalizeRigType(rig.RigType));
            var plan = new System.Collections.Generic.List<PcSoundSetting>();
            string[][] values = { new[] { "AF", "IF" }, new[] { "OFF (Open)", "ON" }, new[] { "OFF", "ON" } };
            int[] wanted = { 0, 0, 1 };
            for (int k = 0; k < 3; k++)
            {
                string item = PcSoundItems[row, k + 1];
                if (item == null) continue;            // this radio has no such item
                plan.Add(new PcSoundSetting
                {
                    Name = PcSoundItems[row, k + 4],
                    Wanted = wanted[k],
                    Values = values[k],
                    Command = "FE FE " + address + " E0 1A 05 " + item + " " + wanted[k].ToString("X2") + " FD",
                    Ask = "FE FE " + address + " E0 1A 05 " + item + " FD",
                    AskLength = (echo == false ? 0 : 9) + 10,
                    AskEnd = string.Empty,
                    ValueIn = IcomValueOf(item),
                });
            }
            return plan;
        }

        // The answer FE FE E0 <radio> 1A 05 i1 i2 dd FD - E0 in the TO place tells it from the echo.
        static Func<byte[], int> IcomValueOf(string item)
        {
            string[] two = item.Split(' ');
            byte i1 = Convert.ToByte(two[0], 16), i2 = Convert.ToByte(two[1], 16);
            return b =>
            {
                for (int i = 0; i + 9 < b.Length; i++)
                    if (b[i] == 0xFE && b[i + 1] == 0xFE && b[i + 2] == 0xE0 && b[i + 4] == 0x1A && b[i + 5] == 0x05
                        && b[i + 6] == i1 && b[i + 7] == i2 && b[i + 9] == 0xFD)
                        return b[i + 8];
                return -1;
            };
        }

        static System.Collections.Generic.List<PcSoundSetting> KenwoodBeepMix(string menu, string name)
        {
            return new System.Collections.Generic.List<PcSoundSetting>
            {
                new PcSoundSetting
                {
                    Name = name,
                    Wanted = 1,
                    Values = new[] { "OFF", "ON" },
                    Command = "EX" + menu + "00001;",
                    Ask = "EX" + menu + "0000;",
                    AskLength = 0,
                    AskEnd = ";",
                    ValueIn = AsciiNumberAfter("EX" + menu + "0000"),
                },
            };
        }

        // The number written after `prefix` and before ";" in a Kenwood answer, or -1.
        static Func<byte[], int> AsciiNumberAfter(string prefix)
        {
            var pattern = new System.Text.RegularExpressions.Regex(System.Text.RegularExpressions.Regex.Escape(prefix) + @"(\d+);");
            return b =>
            {
                var m = pattern.Match(System.Text.Encoding.ASCII.GetString(b));
                int value;
                return m.Success && int.TryParse(m.Groups[1].Value, out value) ? value : -1;
            };
        }

        internal bool WritePcSoundSetting(PcSoundSetting s)
        {
            return _pcSoundRig != null && SendToRig(_pcSoundRig, s.Command, 0);
        }

        /// <summary>The value the radio has for this item now, or -1 when it did not answer in 2 s.</summary>
        internal System.Threading.Tasks.Task<int> ReadPcSoundSetting(PcSoundSetting s)
        {
            return AskRig(_pcSoundRig, s.Ask, s.AskLength, s.AskEnd, s.ValueIn);
        }

        // The window waiting for one answer from the radio; OmniRigEngine_CustomReply hands every reply to it.
        volatile Action<byte[]> _pcSoundReply;

        internal void PcSoundReplyArrived(object reply)
        {
            var listener = _pcSoundReply;
            if (listener == null) return;
            byte[] raw = ReplyBytes(reply);
            if (raw != null) listener(raw);
        }

        // A question to the rig, and the value read from its answer - or -1 when none came in 2 s.
        async System.Threading.Tasks.Task<int> AskRig(OmniRig.RigX rig, string ask, int length, string end, Func<byte[], int> valueIn)
        {
            if (rig == null) return -1;
            var done = new System.Threading.Tasks.TaskCompletionSource<int>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            _pcSoundReply = b => { int v = valueIn(b); if (v >= 0) done.TrySetResult(v); };
            try
            {
                if (!SendToRig(rig, ask, length, end)) return -1;
                var first = await System.Threading.Tasks.Task.WhenAny(done.Task, System.Threading.Tasks.Task.Delay(2000));
                return first == done.Task ? done.Task.Result : -1;
            }
            catch (Exception ex)
            {
                Log.Swallow(ex);
                return -1;
            }
            finally { _pcSoundReply = null; }
        }

        static void RememberCwDecodeOpen(bool open)
        {
            if (Properties.Settings.Default.CwDecodeWindowWasOpen == open) return;
            Properties.Settings.Default.CwDecodeWindowWasOpen = open;
            SettingsFlush.RequestSave();
        }
    }

    /// <summary>
    /// "Set the radio for CW decoding" (his design, 2026-11-15): the settings, what the radio has now, what they
    /// will be and the exact commands; OK sends them; then each is read back and marked taken or not. Kept in
    /// this file rather than one of its own so the project file need not change while Visual Studio has it open.
    /// </summary>
    internal sealed class RadioPcSoundWindow : Window
    {
        readonly MainWindow _main;
        readonly System.Windows.Controls.StackPanel _stack;
        readonly System.Windows.Controls.TextBlock _say;
        readonly System.Windows.Controls.Button _ok, _cancel;
        System.Collections.Generic.List<MainWindow.PcSoundSetting> _plan;
        System.Windows.Controls.TextBlock[] _now, _result;

        internal RadioPcSoundWindow(Window owner, MainWindow main)
        {
            _main = main;
            Owner = owner;
            Title = "Set the radio for CW decoding";
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Background = ThemeManager.Brush("WindowBg");
            FontSize = 16;

            _stack = new System.Windows.Controls.StackPanel { Margin = new Thickness(18, 14, 18, 14), MinWidth = 420 };
            _say = Words("Asking the radio...", 0);
            _say.MaxWidth = 560;                                  // a long list of radios wraps instead of widening
            _say.TextWrapping = TextWrapping.Wrap;
            _stack.Children.Add(_say);

            _ok = new System.Windows.Controls.Button { Content = "OK", MinWidth = 110, Height = 34, Margin = new Thickness(0, 0, 10, 0), IsDefault = true, IsEnabled = false };
            _cancel = new System.Windows.Controls.Button { Content = "Cancel", MinWidth = 110, Height = 34, IsCancel = true };
            _cancel.Click += (s, e) => Close();
            _ok.Click += async (s, e) => await SendAndCheck();

            var buttons = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0),
            };
            buttons.Children.Add(_ok);
            buttons.Children.Add(_cancel);
            _stack.Children.Add(buttons);
            Content = _stack;

            // The plan may need the radio's own answer first (which TS-590 it is), so it is made once the window is up.
            Loaded += async (s, e) => await Fill();
        }

        async System.Threading.Tasks.Task Fill()
        {
            var made = await _main.PcSoundPlanAsync();
            _plan = made.Settings;
            if (_plan == null)
            {
                _say.Text = made.Why;
                _ok.Visibility = Visibility.Collapsed;
                _cancel.Content = "Close";
                return;
            }
            _say.Text = "Radio: " + made.Radio + ". These settings will be sent:";

            var grid = new System.Windows.Controls.Grid { Margin = new Thickness(0, 12, 0, 0) };
            for (int c = 0; c < 5; c++) grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
            string[] heads = { "Setting", "Now", "Set to", "Command", "" };
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition());
            for (int c = 0; c < heads.Length; c++) Put(grid, Words(heads[c], 0, true), 0, c);

            _now = new System.Windows.Controls.TextBlock[_plan.Count];
            _result = new System.Windows.Controls.TextBlock[_plan.Count];
            for (int r = 0; r < _plan.Count; r++)
            {
                var p = _plan[r];
                grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition());
                Put(grid, Words(p.Name, 0), r + 1, 0);
                Put(grid, _now[r] = Words("reading...", 0), r + 1, 1);
                Put(grid, Words(p.Values[p.Wanted], 0), r + 1, 2);
                var cmd = Words(p.Command, 0);
                cmd.FontFamily = new System.Windows.Media.FontFamily("Consolas");
                Put(grid, cmd, r + 1, 3);
                Put(grid, _result[r] = Words("", 0, true), r + 1, 4);
            }
            _stack.Children.Insert(1, grid);

            // What the radio has now - read before anything is sent, so he sees what will change.
            for (int r = 0; r < _plan.Count; r++)
                _now[r].Text = Shown(_plan[r], await _main.ReadPcSoundSetting(_plan[r]));
            _ok.IsEnabled = true;
        }

        static System.Windows.Controls.TextBlock Words(string text, double top, bool bold = false)
        {
            var t = new System.Windows.Controls.TextBlock
            {
                Text = text ?? string.Empty,
                FontSize = 16,
                Margin = new Thickness(0, top, 18, 6),
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            };
            t.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextBrush");
            return t;
        }

        static void Put(System.Windows.Controls.Grid grid, UIElement e, int row, int col)
        {
            System.Windows.Controls.Grid.SetRow(e, row);
            System.Windows.Controls.Grid.SetColumn(e, col);
            grid.Children.Add(e);
        }

        static string Shown(MainWindow.PcSoundSetting p, int value)
        {
            if (value < 0) return "no answer";
            return value < p.Values.Length ? p.Values[value] : value.ToString();
        }

        async System.Threading.Tasks.Task SendAndCheck()
        {
            _ok.IsEnabled = false;
            _cancel.IsEnabled = false;
            _say.Text = "Sending...";
            for (int r = 0; r < _plan.Count; r++) _main.WritePcSoundSetting(_plan[r]);

            int taken = 0;
            for (int r = 0; r < _plan.Count; r++)
            {
                int value = await _main.ReadPcSoundSetting(_plan[r]);
                bool ok = value == _plan[r].Wanted;
                if (ok) taken++;
                _now[r].Text = Shown(_plan[r], value);
                _result[r].Text = ok ? "✓ taken" : "✗ not taken";
                _result[r].Foreground = ok ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2E, 0x9E, 0x44))
                                           : System.Windows.Media.Brushes.Firebrick;
            }
            _say.Text = taken == _plan.Count ? "Done. The radio took every setting." : "The radio did not take every setting - see the marks.";
            _ok.Visibility = Visibility.Collapsed;
            _cancel.Content = "Close";
            _cancel.IsEnabled = true;
        }
    }
}
