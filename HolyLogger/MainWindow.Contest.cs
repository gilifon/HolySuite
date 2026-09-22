using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.Win32;
using System.Collections.Specialized;
using System.Threading;
using System.Net;
using System.Xml.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using DXCCManager;
using HolyParser;
using System.Diagnostics;
using System.Net.Cache;
using System.Globalization;
using Blue.Windows;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using System.Windows.Documents;
using System.Net.NetworkInformation;
using System.Windows.Media;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Windows.Controls.Primitives;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;
using System.Data.SQLite;

namespace HolyLogger
{
    // Contest mode: enter/exit via logs, exchange UI rebuild, serial, layout shift, colors, Cabrillo-adjacent UI.
    // Move-only split from MainWindow.xaml.cs; no behavior change.
    public partial class MainWindow
    {

        // Contest Mode follows the active log: a normal (day-by-day) log turns it off; a contest log
        // re-activates that contest without resetting its serial (event_type stores the contest Id).
        //
        // ANY TRACE OF A CONTEST IS CLEARED, not only the ContestMode flag. The flag, the saved
        // contest id and the live ContestService can disagree, and a regular log must never be
        // logged into as a contest, whichever of the three still remembers one.
        private void ApplyContestModeForActiveLog()
        {
            string eventType = null;
            try { eventType = dal.GetLogEventType(dal.ActiveLogId); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }

            if (string.IsNullOrWhiteSpace(eventType))
            {
                if (AnyContestRemembered()) ExitContest();
                return;
            }

            var contest = Contests.ContestService.FindById(eventType);
            if (contest != null)
            {
                Contests.ContestService.Activate(contest);
                Properties.Settings.Default.ContestMode = true;
                Properties.Settings.Default.ActiveContestId = contest.Id;
                Properties.Settings.Default.Save();
                UpdateContestIndicator();
                ApplyContestExchangeUI();
                PrepareStationForContest(contest);
                UpdateDup();
            }
            else if (AnyContestRemembered())
            {
                ExitContest();
            }
        }

        // =====================================================================================
        // STATION SET-UP FOR A CONTEST
        //
        // Everything a contest asks of the station, kept together here. Each step is switched on by
        // the contest's own entry in contests.json, so a contest that does not ask for it is left
        // alone. Today only Sukkot asks for any of them:
        //
        //   on opening the log (PrepareStationForContest), in this order:
        //     1. "no_satellite": true   -> Satellite Mode off         (EndSatelliteModeForContest)
        //     2. "channels": [...]      -> back to CAT if CAT is on   (ResumeCatForChannelContest)
        //     3. picked in Options > General > Contest Radio Setup (not in contests.json)
        //                               -> radio to VFO, simplex, no tone, no TSQL
        //                                  (ApplyContestRadioSetup; also when the radio comes on CAT later)
        //     4. "modes": ["FM"]        -> logger and radio to FM     (ApplyContestFmOnly)
        //     Satellite goes first, so nothing after it is read or tuned with the transverter shift.
        //     The radio leaves memory mode before FM is set, so FM lands on the VFO.
        //
        //   while logging:
        //     5. "channels": [...]      -> Channel drop-down in the send bar (AddContestChannelCell,
        //                                  built with the exchange cells); picking a channel fills
        //                                  the frequency, tries CAT, and falls back to Manual
        //                                  (TuneToContestChannelAsync)
        // =====================================================================================

        private void PrepareStationForContest(Contests.Contest c)
        {
            EndSatelliteModeForContest(c);
            ResumeCatForChannelContest(c);
            _contestRadioSetupDoneFor = null;   // a log just opened: the radio gets set again
            ApplyContestRadioSetup();
            ApplyContestFmOnly(c);
        }

        // 1. A contest not worked through satellites turns Satellite Mode off. Left on, every QSO would
        // be saved as a satellite contact, and with CAT the transverter shift would be added to the
        // frequency. The same refreshes as turning it off in Options.
        private void EndSatelliteModeForContest(Contests.Contest c)
        {
            if (c == null || !c.NoSatellite || !Properties.Settings.Default.IsSatelliteMode) return;
            Properties.Settings.Default.IsSatelliteMode = false;
            try { Properties.Settings.Default.Save(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            ShowRigParams();
            UpdateSatelliteModeIcon();      // the dish by the clock goes
            ReloadRadioPanelPresets();      // and the panel gets its HF buttons back
        }

        // 2. A channel contest starts on CAT whenever CAT is switched on, so the frequency box shows the
        // radio's real frequency. Manual is saved with the settings, so without this a Manual left from
        // the last session (e.g. a channel the CAT radio could not reach) would reopen the log showing
        // a stale frequency. Manual comes back only when a picked channel is out of the radio's reach.
        private void ResumeCatForChannelContest(Contests.Contest c)
        {
            if (c?.Channels == null || c.Channels.Count == 0) return;
            if (!Properties.Settings.Default.EnableOmniRigCAT || !Properties.Settings.Default.isManualMode) return;
            ToggleManualMode();     // -> CAT; it re-reads the radio when the radio is online
        }

        // 4. An FM-only contest puts the logger, and the radio when CAT is live, in FM.
        private void ApplyContestFmOnly(Contests.Contest c)
        {
            if (c?.Modes == null || c.Modes.Count != 1
                || !string.Equals(c.Modes[0], "FM", StringComparison.OrdinalIgnoreCase)) return;
            SelectLoggerMode("FM");
            if (Properties.Settings.Default.isManualMode || !IsCatLive()) return;
            try { Rig.Mode = (OmniRig.RigParamX)PM_FM; }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // 3. The contest picked in Options > General > Contest Radio Setup (Sukkot unless another is
        // picked) puts the radio on CAT in VFO mode, simplex, no tone and no TSQL. OmniRig has no way to
        // say any of that for every radio, so these are the radio's own commands, one set per radio
        // model, from that same window. Any other contest leaves the radio alone.
        //
        // SENT ONCE, when both are true: the log is open AND a radio is on CAT. Whichever comes second
        // sends it - opening the log with the radio already on, or switching the radio on (or picking
        // RIG1/RIG2) after the log is open. After that it is not sent again for the same radio, so the
        // operator can still change the radio by hand during the contest. Opening the log again, or a
        // different radio coming on CAT, sends it again.
        private string _contestRadioSetupDoneFor;

        private void ApplyContestRadioSetup()
        {
            var c = Contests.ContestService.Active;
            if (c == null || !IsCatLive()) return;
            var setup = Contests.ContestRadioCommands.Load();
            if (!string.Equals(c.Id, setup.ContestId, StringComparison.OrdinalIgnoreCase)) return;

            string rigType = NormalizeRigType(Rig.RigType);
            string key = (Properties.Settings.Default.SelectedOmniRig2 ? "RIG2|" : "RIG1|") + rigType;
            if (key == _contestRadioSetupDoneFor) return;
            _contestRadioSetupDoneFor = key;

            List<string> commands = Contests.ContestRadioCommands.FindFor(setup, rigType)?.CommandsToSend();
            string radio = string.IsNullOrEmpty(rigType) ? "radio on CAT" : rigType;
            string problem = null;
            if (commands == null || commands.Count == 0)
                problem = "HolyLogger has no commands to set the " + radio + " to VFO, simplex and no tone for the "
                        + c.Name + ".\n\nYou can add them in Options > General > Contest Radio Setup.";
            else if (!SendRadioSetupCommands(rigType, commands))
                problem = "HolyLogger could not send the " + c.Name + " setup to the " + radio + ".";

            // Shown after the log has finished opening, not in the middle of it.
            if (problem != null)
                Dispatcher.BeginInvoke(new Action(() =>
                    HolyMessageBox.Show(problem, "Contest Radio Setup", HolyMsgType.Info, this)),
                    DispatcherPriority.Background);
        }

        // Rig events arrive while other work is going on; the check runs once that has settled.
        private void QueueContestRadioSetup()
            => Dispatcher.BeginInvoke(new Action(ApplyContestRadioSetup), DispatcherPriority.Background);

        private bool SendRadioSetupCommands(string rigType, List<string> commands)
        {
            bool allSent = true;
            foreach (string command in commands)
            {
                bool sent = TrySendOmniRigCustomCommand(command);
                allSent &= sent;
                Log.Warn("Contest radio setup -> " + rigType + ": " + command + (sent ? "" : "  (NOT SENT)"));
            }
            return allSent;
        }

        // The "Send now" button in Options > General > Contest Radio Setup: sends one radio's row, to try it out.
        // Only to that radio - the row's commands mean nothing to another model.
        private string SendContestRadioSetupNow(Contests.RadioCommandSet set)
        {
            if (!IsCatLive()) return "No radio is on CAT now.";
            string rigType = NormalizeRigType(Rig.RigType);
            if (!string.Equals(rigType, (set.Radio ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                return "The radio on CAT now is the " + rigType + ", not the " + (set.Radio ?? "").Trim() + ".";
            List<string> commands = set.CommandsToSend();
            if (commands.Count == 0) return "This radio has no commands yet.";
            return SendRadioSetupCommands(rigType, commands)
                ? "Sent to the " + rigType + "."
                : "Could not send the commands to the " + rigType + ".";
        }

        // Opened from Options > General, under Select Rig.
        internal void OpenContestRadioSetup(Window owner)
        {
            new ContestRadioSetupWindow(owner ?? this, new[] { Rig1, Rig2 }, SendContestRadioSetupNow).ShowDialog();
        }

        // 5. Contest channels. Most radios on the Sukkot channels (2m / 70cm FM) have no CAT, so the
        // frequency cannot come from the radio. The Channel drop-down fills it in instead: pick S16
        // and the frequency becomes 145.400 (and the band follows). It also follows the frequency
        // back, so it always names the channel you are on, or nothing when you are off every channel.

        private ComboBox _contestChannelBox;
        private bool _syncingContestChannel;

        private void AddContestChannelCell(List<Contests.ContestChannel> channels)
        {
            const double comboWidth = 175;   // "U-S414  439.000 MHz" measures 153 at 16px, + 15 for chevron and border

            // Set apart from the exchange cells (the channel is not part of what you send), and its right
            // edge lined up with the Date box's right edge below it. The gap is whatever is left between
            // the cells already in the bar and that edge - measured, since a label can be wider than
            // its box.
            double used = 0;
            foreach (UIElement cell in ContestTxPanel.Children)
            {
                cell.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                used += cell.DesiredSize.Width;     // includes the cell's own right margin
            }
            double dateRight = TP_Date.Margin.Left + TP_Date.Width;
            double gap = Math.Max(10, dateRight - ContestTxPanel.Margin.Left - used - comboWidth);

            var col = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(gap, 0, 10, 0) };
            col.Children.Add(ContestCellLabel("Channel"));
            var combo = new ComboBox
            {
                Width = comboWidth,
                Height = 26,
                Margin = new Thickness(0, 1, 0, 0),
                FontSize = 16,
                IsTabStop = false,
                VerticalContentAlignment = VerticalAlignment.Center,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xAB, 0xAD, 0xB3)),
                ToolTip = "Pick the channel - the frequency is filled in for you"
            };
            combo.SetResourceReference(Control.BackgroundProperty, "ControlBg");
            combo.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            if (TryFindResource("FlatComboTemplate") is ControlTemplate flat) combo.Template = flat;
            foreach (var ch in channels)
                combo.Items.Add(new ComboBoxItem
                {
                    Content = ch.Name + "  " + ch.Mhz.ToString("0.000", CultureInfo.InvariantCulture) + " MHz",
                    Tag = ch
                });
            _contestChannelBox = combo;
            SyncContestChannelPick();
            combo.SelectionChanged += ContestChannel_SelectionChanged;
            col.Children.Add(combo);
            ContestTxPanel.Children.Add(col);
        }

        private async void ContestChannel_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingContestChannel) return;
            var ch = (_contestChannelBox?.SelectedItem as ComboBoxItem)?.Tag as Contests.ContestChannel;
            if (ch == null) return;
            await TuneToContestChannelAsync(ch.Mhz);
        }

        // Shows the channel the frequency box is on (or none). Called whenever the frequency changes.
        private void SyncContestChannelPick()
        {
            if (_contestChannelBox == null) return;
            double.TryParse((TB_Frequency.Text ?? string.Empty).Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double mhz);
            // WITHIN 100 Hz. It used to allow 500 Hz either side, so a radio tuned by hand to 145.249530
            // still showed "S10 145.250" (4Z1ZV, 8.9.14). 100 Hz off or more, the box is empty.
            long hz = (long)Math.Round(mhz * 1000000.0);
            object match = _contestChannelBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => i.Tag is Contests.ContestChannel ch && Math.Abs((long)Math.Round(ch.Mhz * 1000000.0) - hz) < 100);
            if (_contestChannelBox.SelectedItem == match) return;
            _syncingContestChannel = true;
            try { _contestChannelBox.SelectedItem = match; }
            finally { _syncingContestChannel = false; }
        }

        // The frequency box gets the channel first, so the QSO is logged on the channel whatever the
        // radio does. With CAT live the radio is then asked to go there too. If it does not get there -
        // the CAT radio has no 2m/70cm, or it is simply not the radio the contest is being worked on
        // (IC-7610 on CAT, contest on an IC-5100 with no CAT) - the program switches to Manual, so the
        // CAT radio's own frequency can no longer overwrite the channel.
        private async Task TuneToContestChannelAsync(double freqMhz)
        {
            string freqText = freqMhz.ToString("0.0###", CultureInfo.InvariantCulture);
            TB_Frequency.Text = freqText;
            SelectLoggerMode("FM");

            if (Properties.Settings.Default.isManualMode || !IsCatLive()) return;

            int freqHz = (int)Math.Round(freqMhz * 1000000.0, MidpointRounding.AwayFromZero);
            await TryTuneRigFrequencyAsync(freqHz, (OmniRig.RigParamX)PM_FM);
            if (await TryGetRigReadbackAsync(freqHz)) return;

            if (!Properties.Settings.Default.isManualMode) ToggleManualMode();
            // While we waited, the CAT poll may have put the radio's own frequency back in the box.
            TB_Frequency.Text = freqText;
            SelectLoggerMode("FM");
        }

        // =====================================================================================

        private static bool AnyContestRemembered()
        {
            return Properties.Settings.Default.ContestMode
                   || !string.IsNullOrWhiteSpace(Properties.Settings.Default.ActiveContestId)
                   || Contests.ContestService.Active != null;
        }

        // Contest Mode follows the active log, and the ONLY place to enter or leave it is
        // File > Log Manager (open/create a contest log to enter; open/create a regular log to
        // leave). The status-bar trophy is a passive indicator (see UpdateContestIndicator);
        // there is no Tools-menu item and no standalone "exit contest" action.

        // Lets the operator pick a contest and name a brand-new log for it. Used by the "Create New
        // Contest Log" button in ViewLogsWindow. Returns true if a new contest log was created and
        // made active; false if the operator cancelled at any step.
        public bool CreateNewContestLog(Window owner)
        {
            var picker = new ContestPickerWindow(Contests.ContestService.Active) { Owner = owner };
            if (picker.ShowDialog() != true || picker.SelectedContest == null) return false;
            return EnterContest(picker.SelectedContest);
        }

        // Entering a contest selects its profile AND turns on Contest Mode (duplicate flagging).
        // Returns true if the contest log was created and activated; false if cancelled.
        private bool EnterContest(Contests.Contest c)
        {
            // Selecting a contest forces a brand-new log dedicated to it: the user must give it a
            // (unique) name. The log's Event Type is the contest, so contest mode and Cabrillo
            // export follow the log from now on. Cancelling the name dialog aborts entering.
            string suggested = UniqueLogName(c.Name + " " + DateTime.UtcNow.ToString("yyyy-MM-dd"));
            var dlg = new NewLogWindow(dal,
                "Name the log for the contest \"" + c.Name + "\":", suggested, 0,
                showCopyOptions: true, defaultCallsign: CurrentStationCallsign) { Owner = this };
            if (dlg.ShowDialog() != true) return false;   // cancelled -> do not enter the contest

            long id = dal.CreateLog(dlg.LogName, c.Id, dlg.LogCallsign, dlg.CopyTargetLogId);

            // A freshly selected contest starts clean: serial back to 001 and no zone override (use
            // cty.dat). Set these before switching; ApplyContestModeForActiveLog won't reset them.
            // A restart mid-contest goes through Activate (not here), so it resumes instead.
            Properties.Settings.Default.ContestNextSerial = 1;
            Properties.Settings.Default.ContestMyZoneOverride = string.Empty;
            Properties.Settings.Default.Save();

            // Switch to the new (empty) log; this activates the contest via its Event Type and
            // refreshes the entry form, title bar, counts and dup check.
            SwitchActiveLog(id);

            // Offer to fill this contest's Cabrillo header fields now (skippable). Whatever is entered
            // is kept; anything still missing is enforced later at Cabrillo export.
            try
            {
                var values = Contests.ContestHeaderStore.Load(c.Id);
                var info = new ContestInfoWindow(c, values, exportMode: false) { Owner = this };
                info.ShowDialog();
                if (info.Values != null) Contests.ContestHeaderStore.Save(c.Id, info.Values);
            }
            catch (Exception ex) { Log.Swallow(ex); }

            return true;
        }

        private void ExitContest()
        {
            Contests.ContestService.Deactivate();
            Properties.Settings.Default.ContestMode = false;
            Properties.Settings.Default.ActiveContestId = "";
            Properties.Settings.Default.Save();
            UpdateContestIndicator();
            ApplyContestExchangeUI();
            UpdateDup();
        }

        // The contest received-exchange boxes currently shown in the Exchange row.
        private readonly List<TextBox> _contestRxBoxes = new List<TextBox>();

        // Builds the received-exchange boxes for the active contest (one per non-RST received field;
        // RST keeps its own RST-R box), or restores the normal single Exchange box when not in a
        // contest. Each box's value is mirrored into TB_Exchange so the existing save/log path
        // captures the received exchange.
        private void ApplyContestExchangeUI()
        {
            if (ContestRxPanel == null) return;

            foreach (var b in _contestRxBoxes) b.TextChanged -= ContestRxBox_TextChanged;
            _contestRxBoxes.Clear();
            ContestRxPanel.Children.Clear();
            if (ContestTxPanel != null) ContestTxPanel.Children.Clear();
            _contestChannelBox = null;

            Contests.Contest contest = Contests.ContestService.Active;
            bool inContest = contest != null;

            // The original exchange-row controls show only when NOT in a contest; in a contest the
            // received row is rebuilt as ordered cells in ContestRxPanel and the sent row in ContestTxPanel.
            Visibility orig = inContest ? Visibility.Collapsed : Visibility.Visible;
            if (TB_Exchange != null) TB_Exchange.Visibility = orig;
            if (TB_RSTSent != null) TB_RSTSent.Visibility = orig;
            if (TB_RSTRcvd != null) TB_RSTRcvd.Visibility = orig;
            if (L_RstSLabel != null) L_RstSLabel.Visibility = orig;
            if (L_RstRLabel != null) L_RstRLabel.Visibility = orig;
            // QTH shares the exchange row: in a contest the coloured exchange frame covers this strip,
            // so the box would sit under it.
            if (TB_QTH != null) TB_QTH.Visibility = orig;
            if (L_QTHLabel != null) L_QTHLabel.Visibility = orig;
            // The received label is "Exchange" alone outside a contest, "Exchange / received" inside one.
            SetExchangeLabel(L_ExchangeLabel, "Exchange", "received", inContest);

            if (!inContest)
            {
                ContestRxPanel.Visibility = Visibility.Collapsed;
                if (ContestTxPanel != null) ContestTxPanel.Visibility = Visibility.Collapsed;
                if (L_SendLabel != null) L_SendLabel.Visibility = Visibility.Collapsed;
                ApplyContestLayout(false);
                UpdateContestLabelContrast();   // label now on the plain form -> theme text color
                return;
            }

            string myCall = TB_MyCallsign != null ? TB_MyCallsign.Text : string.Empty;
            string dxCall = TB_DXCallsign != null ? TB_DXCallsign.Text : string.Empty;
            string myCont = ContinentOf(myCall);
            string dxCont = ContinentOf(dxCall);

            // RECEIVED: what the WORKED station sends (asymmetric contests switch on the DX callsign).
            var fields = Contests.ContestService.GetReceivedFields(contest, dxCall, dxCont)
                .Where(f => !IsRstField(f)).ToList();
            _contestRxSig = string.Join(",", fields);

            // RECEIVED frame: RST(R) first, then the contest items. Tab flows DX callsign -> items:
            // RST-R is skipped by Tab, because in a contest the report is always 59/599 and the
            // exchange is what the operator actually types (still editable with a click). The RST
            // you send lives in the SEND frame instead.
            int tab = 9;

            TextBox rstr = AddContestCell("RST R", 52, null, ContestRxPanel);
            // AddContestCell only gives tab-stop boxes the edit highlight; RST-R is received data too.
            if (state == State.Edit) rstr.Background = ThemeManager.Brush("EditFieldBg");
            _contestRstRcvdBox = rstr;
            rstr.Text = TB_RSTRcvd != null ? TB_RSTRcvd.Text : "59";
            // The same digit limit the hidden box behind it has - 2 on voice, 3 on CW - since whatever
            // is typed here is copied straight into it.
            rstr.MaxLength = _rstDigits;
            rstr.TextChanged += (s, e2) => { if (TB_RSTRcvd != null) TB_RSTRcvd.Text = rstr.Text; };

            foreach (string field in fields)
            {
                ContestFieldUi(field, out string label, out double width);
                TextBox box = AddContestCell(label, width, tab++, ContestRxPanel);
                box.Text = _contestRxBoxes.Count == 0 && TB_Exchange != null ? TB_Exchange.Text : string.Empty;
                box.TextChanged += ContestRxBox_TextChanged;
                if (IsSerialField(field)) AllowDigitsOnly(box);
                _contestRxBoxes.Add(box);
            }

            // SEND frame: RST(S) first (aligned under RST-R), then EVERY field of the SENT exchange
            // (asymmetric contests switch on MY callsign). Each is auto-filled (serial/zone/area) or a
            // remembered editable value, and skipped by Tab — the operator types received data.
            _contestSendSerialBox = null;
            _contestSendBoxes.Clear();
            if (ContestTxPanel != null)
            {
                TextBox rsts = AddContestCell("RST S", 52, null, ContestTxPanel);
                rsts.Text = TB_RSTSent != null ? TB_RSTSent.Text : "59";
                rsts.MaxLength = _rstDigits;
                rsts.TextChanged += (s, e2) => { if (TB_RSTSent != null) TB_RSTSent.Text = rsts.Text; };

                var sentFields = Contests.ContestService.GetSentFields(contest, myCall, myCont)
                    .Where(f => !IsRstField(f)).ToList();
                foreach (string sf in sentFields)
                {
                    ContestFieldUi(sf, out string slabel, out double swidth);
                    TextBox sbox = AddContestCell(slabel, swidth, null, ContestTxPanel);
                    sbox.IsTabStop = false;                 // auto-filled, but editable; skipped by Tab
                    sbox.Text = GetSendFieldValue(sf, myCall);   // set before wiring, so it isn't "an edit"
                    string fieldKey = (sf ?? string.Empty).ToUpperInvariant();
                    sbox.TextChanged += (s, e2) => SaveSendFieldEdit(fieldKey, sbox.Text);
                    if (IsSerialField(fieldKey)) AllowDigitsOnly(sbox);
                    if (fieldKey == "SERIAL") _contestSendSerialBox = sbox;
                    _contestSendBoxes.Add(sbox);
                }
                if (contest.Channels != null && contest.Channels.Count > 0)
                    AddContestChannelCell(contest.Channels);
                ContestTxPanel.Visibility = Visibility.Visible;
            }
            SetExchangeLabel(L_SendLabel, "Exchange", "send", true);
            if (L_SendLabel != null) L_SendLabel.Visibility = Visibility.Visible;

            ContestRxPanel.Visibility = Visibility.Visible;
            ApplyContestLayout(true);
            UpdateContestLabelContrast();   // labels now on the frames -> contrast per frame color
        }

        // Tracks the current received-field signature and the auto serial box so callsign changes can
        // rebuild the received frame, and Add can advance the serial, without a full re-render otherwise.
        private string _contestRxSig = string.Empty;
        private TextBox _contestRstRcvdBox;   // the RST-R cell in ContestRxPanel (edit-highlight follows the form)
        private TextBox _contestSendSerialBox;
        private readonly List<TextBox> _contestSendBoxes = new List<TextBox>();

        // The sent-exchange string stored on a QSO (ADIF stx_string). In a contest it's the joined
        // send-box values (serial / zone / area / …); otherwise the Holyland square, as before. For
        // Holyland this resolves to the same square value, so existing logs are unchanged.
        private string ContestSendExchangeForLog()
        {
            if (Contests.ContestService.Active != null && _contestSendBoxes.Count > 0)
                return string.Join(" ", _contestSendBoxes
                    .Select(b => (b.Text ?? string.Empty).Trim())
                    .Where(s => s.Length > 0));
            return TB_MyHolyland.Text;
        }

        // After logging a QSO in a contest whose sent exchange is a serial number, bump the running
        // serial and refresh the (read-only) send box so the next QSO shows the new number.
        private void AdvanceContestSerial()
        {
            var c = Contests.ContestService.Active;
            if (c == null) return;
            string myCall = TB_MyCallsign != null ? TB_MyCallsign.Text : string.Empty;
            bool sendsSerial = Contests.ContestService.GetSentFields(c, myCall, ContinentOf(myCall))
                .Any(f => string.Equals(f, "SERIAL", StringComparison.OrdinalIgnoreCase));
            if (!sendsSerial) return;

            Properties.Settings.Default.ContestNextSerial++;
            Properties.Settings.Default.Save();
            if (_contestSendSerialBox != null)
                _contestSendSerialBox.Text = Properties.Settings.Default.ContestNextSerial.ToString("000");
        }

        // In an asymmetric contest the received field can change with the DX callsign (Holyland: Area
        // vs Serial). Rebuild the received/send frames only when that field set actually changes.
        private void RefreshContestRxForCallsign()
        {
            var c = Contests.ContestService.Active;
            if (c == null || !c.Asymmetric) return;
            string dxCall = TB_DXCallsign != null ? TB_DXCallsign.Text : string.Empty;
            var rf = Contests.ContestService.GetReceivedFields(c, dxCall, ContinentOf(dxCall))
                .Where(f => !IsRstField(f));
            if (string.Join(",", rf) != _contestRxSig)
                ApplyContestExchangeUI();
        }

        // Adds one [label-above, box] cell to a contest exchange panel and returns the box. A null
        // tabIndex makes the box skip the Tab order.
        private TextBox AddContestCell(string label, double width, int? tabIndex, StackPanel target)
        {
            var col = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 10, 0) };
            col.Children.Add(ContestCellLabel(label));
            var box = new TextBox
            {
                Width = width,
                Height = 26,
                Margin = new Thickness(0, 1, 0, 0),
                FontSize = 16,
                CharacterCasing = CharacterCasing.Upper,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            if (tabIndex.HasValue) box.TabIndex = tabIndex.Value; else box.IsTabStop = false;
            // Match the plain form: the received (tab-stop) exchange boxes highlight with the
            // "Field being edited" color (EditFieldBg token, editable in the Color Scheme editor)
            // while editing an existing QSO; otherwise the normal input surface. Send boxes are
            // auto-filled, so they keep the plain surface.
            box.Background = (state == State.Edit && tabIndex.HasValue)
                ? ThemeManager.Brush("EditFieldBg")
                : ThemeManager.Brush("ControlBg");
            col.Children.Add(box);
            target.Children.Add(col);
            return box;
        }

        // The blue label above a contest cell.
        private static TextBlock ContestCellLabel(string label)
        {
            // The cell has to fit inside the 48px coloured band (1px border, so 46px of room) with a
            // pixel clear at the top and bottom, and the band cannot grow - the divider line is right
            // under it. A natural 16px line is 21.3px, which with a 28px box made the cell 49.3 and
            // left the boxes hanging out of the band's lower edge.
            // So: LineHeight 17 (same tight-line idiom as L_RstSLabel/L_RstRLabel) + 1px of air + a
            // 26px box = 44. The 1px of air is not decoration: measured, a 'g'/'y'/'q' tail still
            // inks down to y=17 in a 17px line, so without it the box's top edge shaves the tail off
            // labels like "Age" and "Holyland Square".
            return new TextBlock
            {
                Text = label,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0)),
                LineHeight = 17,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                HorizontalAlignment = HorizontalAlignment.Center
            };
        }

        // Switches the lower block (everything below the divider) between its normal positions and the
        // condensed contest positions: the divider drops 44px, the rows tighten and slide down so the
        // bottom row ends just above the band-button (X-icon) array, and the freed strip holds the
        // "You send" band. The X icons and the log table never move.
        private void ApplyContestLayout(bool contest)
        {
            Grid grid = AddLogGrid;
            if (grid == null) return;

            if (_rowOrigTop == null)
            {
                _rowOrigTop = new Dictionary<FrameworkElement, double>();
                foreach (FrameworkElement fe in grid.Children.OfType<FrameworkElement>())
                    if (Grid.GetRow(fe) == 1)
                        _rowOrigTop[fe] = fe.Margin.Top;
            }

            foreach (var kv in _rowOrigTop)
            {
                FrameworkElement fe = kv.Key;
                double baseTop = kv.Value;
                double off = contest ? RowShift(fe, baseTop) : 0;
                Thickness m = fe.Margin;
                fe.Margin = new Thickness(m.Left, baseTop + off, m.Right, m.Bottom);
            }

            if (ContestSendBand != null)
                ContestSendBand.Visibility = contest ? Visibility.Visible : Visibility.Collapsed;
            if (ContestExchangeFrame != null)
                ContestExchangeFrame.Visibility = contest ? Visibility.Visible : Visibility.Collapsed;

            // The activity row (IOTA / SOTA / POTA / WWFF) has nowhere to go in contest mode: the
            // contest layout slides the lower rows down and already reaches the bottom of the form.
            // It is no loss - in a contest the exchange is the contest's own - and hiding it is what
            // keeps every contest position identical to what it was before the row existed.
            SetActivityRowVisible(!contest);

            // The log-row "Set Radio to Freq" undo icon: the generic row-shift above would drop it
            // onto the packed contest exchange row. In contest mode park it just under the Spot (F3)
            // button instead — horizontally centered to that button (button center x 619.5, icon
            // 26 wide) and vertically centered on the send-exchange band (band center y 97). In
            // normal (non-contest) mode leave it in its usual XAML spot.
            if (MainUndoIconGrid != null)
                MainUndoIconGrid.Margin = contest
                    ? new Thickness(607, 84, 0, 0)
                    : new Thickness(16, 144, 0, 0);
        }

        private void ContestRxBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TB_Exchange != null)
                TB_Exchange.Text = string.Join(" ",
                    _contestRxBoxes.Select(b => b.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
        }

        // Empties the contest received-exchange cells beside RST-R. Called from the form Clear (F9/F1)
        // so those cells reset like every other QSO field; the RST-R cell is handled by the normal
        // RST reset. No-op outside contest mode (the list is empty).
        internal void ClearContestReceivedExchange()
        {
            foreach (var b in _contestRxBoxes)
                b.Text = string.Empty;
        }

        private static bool IsSerialField(string field)
            => string.Equals((field ?? string.Empty).Trim(), "SERIAL", StringComparison.OrdinalIgnoreCase);

        // A SERIAL IS A NUMBER. Nothing else can be sent or received in that cell, and a contest log
        // carrying "FIX IT" where a serial belongs is a QSO the adjudicator throws out - so the letters
        // are refused as they are typed rather than found at Cabrillo time. Typing AND pasting: a paste
        // is the way a wrong value most often arrives, and blocking only the keyboard would leave the
        // rule half-enforced. (STATE_OR_SERIAL is deliberately not included: in those contests the cell
        // holds a state OR a serial, and letters are correct there.)
        private static void AllowDigitsOnly(TextBox box)
        {
            if (box == null) return;

            box.PreviewTextInput += (s, e) =>
            {
                foreach (char c in e.Text ?? string.Empty)
                    if (!char.IsDigit(c)) { e.Handled = true; return; }
            };

            // The space bar does not raise PreviewTextInput as printable text in a TextBox.
            box.PreviewKeyDown += (s, e) => { if (e.Key == Key.Space) e.Handled = true; };

            DataObject.AddPastingHandler(box, (s, e) =>
            {
                string pasted = e.DataObject.GetDataPresent(DataFormats.UnicodeText)
                    ? (e.DataObject.GetData(DataFormats.UnicodeText) as string ?? string.Empty)
                    : string.Empty;
                if (pasted.Length > 0 && pasted.All(char.IsDigit)) return;   // clean paste -> let it through
                e.CancelCommand();
            });
        }

        // Friendly label + box width for a contest exchange field name.
        private static void ContestFieldUi(string field, out string label, out double width)
        {
            switch ((field ?? string.Empty).ToUpperInvariant())
            {
                case "SERIAL": label = "Serial"; width = 60; break;
                case "CQ_ZONE": label = "CQ Zone"; width = 52; break;
                case "ITU_ZONE": label = "ITU"; width = 48; break;
                case "CONTINENT": label = "Cont"; width = 52; break;
                case "HOLYLAND_AREA": label = "Holyland Square"; width = 100; break;
                case "STATE": case "STATE_PROVINCE": case "PROVINCE": label = "State/Prov"; width = 72; break;
                case "NAME": label = "Name"; width = 90; break;
                case "AGE": label = "Age"; width = 44; break;
                case "POWER": label = "Power"; width = 52; break;
                case "ARRL_SECTION": case "FIELD_DAY_SECTION": label = "Section"; width = 60; break;
                case "IOTA_REF": label = "IOTA"; width = 64; break;
                case "PRECEDENCE": label = "Prec"; width = 44; break;
                case "CHECK": label = "Check"; width = 48; break;
                case "MEMBER_NR": label = "Member#"; width = 64; break;
                case "CALLSIGN": label = "Call"; width = 90; break;
                case "FIELD_DAY_CLASS": label = "Class"; width = 56; break;
                case "GRID": label = "Grid"; width = 72; break;   // same as My Locator
                case "DXCC": label = "DXCC"; width = 56; break;
                case "STATE_PROVINCE_DXCC": label = "St/Pr/DX"; width = 72; break;
                case "STATE_OR_SERIAL": label = "St/Ser"; width = 64; break;
                case "UTC_TIME": label = "UTC"; width = 56; break;
                case "IARU_HQ_ABBREVIATION": label = "Zone/HQ"; width = 64; break;
                case "AGE_GROUP": label = "Age"; width = 48; break;
                case "DX": label = "Power"; width = 56; break;
                default: label = field; width = 70; break;
            }
        }

        // Status-bar contest indicator: PASSIVE, display-only. Shown (gold trophy on a blue tile +
        // contest name) only while a contest log is active; completely hidden otherwise -- so its
        // mere presence answers "am I in a contest, and which one?". Entering/leaving contest mode
        // happens in exactly one place: File > Log Manager.
        private void UpdateContestIndicator()
        {
            bool on = Properties.Settings.Default.ContestMode && Contests.ContestService.Active != null;

            if (ContestIndicatorPanel != null)
                ContestIndicatorPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (!on) return;

            if (ContestIndicatorPath != null)
                ContestIndicatorPath.Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07));   // gold
            if (ContestIndicator != null)
            {
                ContestIndicator.Background = new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0)); // blue tile
                ContestIndicator.ToolTip = "In contest — duplicates are flagged.\nTo leave, open a regular log in File > Log Manager.";
            }
            if (L_ContestName != null)
                L_ContestName.Text = Contests.ContestService.Active.Name + " — Active";
        }

        // Right-click either contest frame to pick its colour in place. The frames are palette
        // tokens (ContestRxBg / ContestTxBg), so this shortcut writes the choice into the user's
        // Custom color scheme -- the exact same place View > Color Scheme > Customize Colors edits
        // it -- and DynamicResource repaints the frame instantly.
        private void ContestExchangeFrame_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            PickCustomTokenColor("ContestRxBg");
            e.Handled = true;
        }

        private void ContestSendBand_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            PickCustomTokenColor("ContestTxBg");
            e.Handled = true;
        }

        // The contest labels sit ON the user-editable frames WHILE contesting; outside a contest
        // the received-exchange label sits on the plain (themed) form instead. So its color must
        // follow where it actually is: contrast against the frame in contest, the theme text color
        // otherwise. The send label only ever shows in contest, always on its frame.
        // Called at startup and on every theme/scheme/color change (OnThemeChanged).
        internal void UpdateContestLabelContrast()
        {
            bool inContest = Contests.ContestService.Active != null;
            if (L_ExchangeLabel != null)
            {
                if (inContest)
                    L_ExchangeLabel.Foreground = ContrastTextFor("ContestRxBg");
                else
                    // Re-establish the live theme binding (not a frozen snapshot) so the label keeps
                    // following scheme changes while outside a contest.
                    L_ExchangeLabel.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextBrush");
            }
            if (L_SendLabel != null)
                L_SendLabel.Foreground = ContrastTextFor("ContestTxBg");
        }

        private static Brush ContrastTextFor(string token)
        {
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(ThemeManager.CurrentHex(token));
                double luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
                return luminance > 0.5 ? Brushes.Black : Brushes.White;
            }
            catch (Exception ex)
            {
                Log.Swallow(ex);
                return Brushes.Black;
            }
        }

        private static void PickCustomTokenColor(string token)
        {
            Color current;
            try { current = (Color)ColorConverter.ConvertFromString(ThemeManager.CurrentHex(token)); }
            catch { current = Colors.Gray; }

            using (var dlg = new System.Windows.Forms.ColorDialog())
            {
                dlg.FullOpen = true;
                dlg.Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B);
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    ThemeManager.SetOverride(token,
                        string.Format("#{0:X2}{1:X2}{2:X2}", dlg.Color.R, dlg.Color.G, dlg.Color.B));
                }
            }
        }
    }
}
