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
        // Columns: model, Output Select, AF SQL, AF Beep/Speech... Output - each the item after 1A 05 - and the
        // words the radio's own menu puts before the item names, so the window shows the names he will find there -
        // and the radio's name as people write it, for the list an unsupported radio is shown.
        static readonly string[,] PcSoundItems =
        {
            { "IC7610", "00 80", "00 83", "00 84", "USB AF/IF Output > ", "IC-7610" },
            { "IC7300MK2", "00 69", "00 71", "00 72", "USB AF/IF Output > ", "IC-7300MK2" },
            { "IC7300", "00 59", "00 61", "00 62", "ACC/USB ", "IC-7300" },
        };

        // The row for this radio, and its address, or -1.
        int PcSoundRow(out string address)
        {
            address = null;
            if (!IsCatLive()) return -1;
            string rigType = NormalizeRigType(Rig.RigType);
            string key = VoiceModelKey(rigType);
            for (int i = 0; i < PcSoundItems.GetLength(0); i++)
                if (key.StartsWith(PcSoundItems[i, 0], StringComparison.Ordinal))
                {
                    address = GetIcomCivAddress(rigType);
                    return address == null ? -1 : i;
                }
            return -1;
        }

        bool SetPcSoundItem(string address, string item, string value)
        {
            return TrySendOmniRigCustomCommand("FE FE " + address + " E0 1A 05 " + item + " " + value + " FD");
        }

        void TurnOnSideToneToPc()
        {
            try
            {
                string address;
                int row = PcSoundRow(out address);
                if (row >= 0) SetPcSoundItem(address, PcSoundItems[row, 3], "01");
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // ── OPTIONS' "SET THE RADIO FOR CW DECODING" (his design, 2026-11-15) ────────────────
        //
        // A SENSITIVE ACTION GETS FEEDBACK: the button opens RadioPcSoundWindow, which shows each setting, what the
        // radio has now, what it will be set to and the exact command; nothing goes to the radio before OK; then
        // each setting is read back from the radio and shown as taken or not.

        /// <summary>One radio menu item the window sets.</summary>
        internal sealed class PcSoundSetting
        {
            public string Name;        // as the radio's own menu names it
            public string Item;        // the two bytes after 1A 05
            public int Wanted;         // the value byte to set
            public string[] Values;    // what each value byte means, in the radio's words
            public string Command;     // the whole set command, shown to him
        }

        /// <summary>The settings for the radio connected now - or null, with why in plain words.</summary>
        internal System.Collections.Generic.List<PcSoundSetting> PcSoundPlan(out string address, out string radio, out string why)
        {
            address = null;
            radio = null;
            why = null;
            try
            {
                if (!IsCatLive()) { why = "HolyLogger is not connected to the radio."; return null; }
                radio = Rig.RigType;
                int row = PcSoundRow(out address);
                // THE LIST ONLY WHEN IT HELPS (his choice, 2026-11-15): a supported radio's owner never sees it.
                if (row < 0)
                {
                    var names = new System.Collections.Generic.List<string>();
                    for (int i = 0; i < PcSoundItems.GetLength(0); i++) names.Add(PcSoundItems[i, 5]);
                    why = "Your " + radio + " is not supported yet. Supported: " + string.Join(", ", names) + ".";
                    return null;
                }

                string menu = PcSoundItems[row, 4];
                var plan = new System.Collections.Generic.List<PcSoundSetting>
                {
                    new PcSoundSetting { Name = menu + "Output Select", Item = PcSoundItems[row, 1], Wanted = 0, Values = new[] { "AF", "IF" } },
                    new PcSoundSetting { Name = menu + "AF SQL", Item = PcSoundItems[row, 2], Wanted = 0, Values = new[] { "OFF (Open)", "ON" } },
                    new PcSoundSetting { Name = menu + "AF Beep/Speech... Output", Item = PcSoundItems[row, 3], Wanted = 1, Values = new[] { "OFF", "ON" } },
                };
                foreach (var s in plan)
                    s.Command = "FE FE " + address + " E0 1A 05 " + s.Item + " " + s.Wanted.ToString("X2") + " FD";
                return plan;
            }
            catch (Exception ex)
            {
                Log.Swallow(ex);
                why = "HolyLogger could not talk to the radio.";
                return null;
            }
        }

        internal bool WritePcSoundSetting(string address, PcSoundSetting s)
        {
            return SetPcSoundItem(address, s.Item, s.Wanted.ToString("X2"));
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

        /// <summary>The value the radio has for this item now, or -1 when it did not answer in 2 s.</summary>
        internal async System.Threading.Tasks.Task<int> ReadPcSoundSetting(string address, PcSoundSetting s)
        {
            var done = new System.Threading.Tasks.TaskCompletionSource<int>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            string[] item = s.Item.Split(' ');
            byte i1 = Convert.ToByte(item[0], 16), i2 = Convert.ToByte(item[1], 16);
            // The answer: FE FE E0 <radio> 1A 05 i1 i2 dd FD - E0 in the TO place tells it from the echo.
            _pcSoundReply = b =>
            {
                for (int i = 0; i + 9 < b.Length; i++)
                    if (b[i] == 0xFE && b[i + 1] == 0xFE && b[i + 2] == 0xE0 && b[i + 4] == 0x1A && b[i + 5] == 0x05
                        && b[i + 6] == i1 && b[i + 7] == i2 && b[i + 9] == 0xFD)
                    {
                        done.TrySetResult(b[i + 8]);
                        return;
                    }
            };
            try
            {
                // Nine bytes of question, echoed back when this radio echoes, then the ten of the answer.
                bool? echo = IcomEchoesCommands(NormalizeRigType(Rig.RigType));
                string ask = "FE FE " + address + " E0 1A 05 " + s.Item + " FD";
                if (!TrySendOmniRigCustomCommand(ask, (echo == false ? 0 : 9) + 10, string.Empty)) return -1;
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
        readonly string _address;
        readonly System.Collections.Generic.List<MainWindow.PcSoundSetting> _plan;
        readonly System.Windows.Controls.TextBlock[] _now, _result;
        readonly System.Windows.Controls.TextBlock _say;
        readonly System.Windows.Controls.Button _ok, _cancel;

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

            string radio, why;
            _plan = main.PcSoundPlan(out _address, out radio, out why);

            var stack = new System.Windows.Controls.StackPanel { Margin = new Thickness(18, 14, 18, 14) };
            _say = Words(_plan == null ? why : "Radio: " + radio + ". These settings will be sent:", 0);
            _say.MaxWidth = 560;                                  // a long list of radios wraps instead of widening
            _say.TextWrapping = TextWrapping.Wrap;
            stack.Children.Add(_say);

            _ok = new System.Windows.Controls.Button { Content = "OK", MinWidth = 110, Height = 34, Margin = new Thickness(0, 0, 10, 0), IsDefault = true };
            _cancel = new System.Windows.Controls.Button { Content = _plan == null ? "Close" : "Cancel", MinWidth = 110, Height = 34, IsCancel = true };
            _cancel.Click += (s, e) => Close();
            _ok.Click += async (s, e) => await SendAndCheck();

            if (_plan != null)
            {
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
                stack.Children.Add(grid);
            }

            var buttons = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0),
            };
            if (_plan != null) buttons.Children.Add(_ok);
            buttons.Children.Add(_cancel);
            stack.Children.Add(buttons);
            Content = stack;

            _ok.IsEnabled = false;
            if (_plan != null) Loaded += async (s, e) => await ReadNow();
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

        string Shown(MainWindow.PcSoundSetting p, int value)
        {
            if (value < 0) return "no answer";
            return value < p.Values.Length ? p.Values[value] : value.ToString("X2");
        }

        // What the radio has now - read before anything is sent, so he sees what will change.
        async System.Threading.Tasks.Task ReadNow()
        {
            for (int r = 0; r < _plan.Count; r++)
                _now[r].Text = Shown(_plan[r], await _main.ReadPcSoundSetting(_address, _plan[r]));
            _ok.IsEnabled = true;
        }

        async System.Threading.Tasks.Task SendAndCheck()
        {
            _ok.IsEnabled = false;
            _cancel.IsEnabled = false;
            _say.Text = "Sending...";
            for (int r = 0; r < _plan.Count; r++) _main.WritePcSoundSetting(_address, _plan[r]);

            int taken = 0;
            for (int r = 0; r < _plan.Count; r++)
            {
                int value = await _main.ReadPcSoundSetting(_address, _plan[r]);
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
