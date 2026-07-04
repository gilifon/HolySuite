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
        private void ApplyContestModeForActiveLog()
        {
            string eventType = null;
            try { eventType = dal.GetLogEventType(dal.ActiveLogId); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }

            if (string.IsNullOrWhiteSpace(eventType))
            {
                if (Properties.Settings.Default.ContestMode) ExitContest();
                return;
            }

            var contest = Contests.ContestService.FindById(eventType);
            if (contest != null)
            {
                Contests.ContestService.Activate(contest);
                Properties.Settings.Default.ContestMode = true;
                Properties.Settings.Default.ActiveContestId = contest.Id;
                Properties.Settings.Default.Save();
                UpdateContestModeMenuHeader();
                ApplyContestExchangeUI();
                UpdateDup();
            }
            else if (Properties.Settings.Default.ContestMode)
            {
                ExitContest();
            }
        }

        // Contest Mode on/off (Tools menu). When on, exact-match QSOs (same callsigns + band + mode)
        // are flagged as "Duplicate"; when off, the program never reports a duplicate and instead
        // shows how many times the station was worked before.
        // Both the Tools-menu item and the status-bar trophy open the log window filtered to contest
        // logs, where the user can select an existing contest log or create a new one. There is no
        // standalone "exit contest" action: opening or creating a non-contest log exits contest mode
        // (see ApplyContestModeForActiveLog).
        private void ContestModeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            OpenContestLogsWindow();
        }

        private void ContestIndicator_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            OpenContestLogsWindow();
        }

        private void OpenContestLogsWindow()
        {
            var win = new ViewLogsWindow(this, dal, contestOnly: true) { Owner = this };
            win.ShowDialog();
        }

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
                "Name the log for the contest \"" + c.Name + "\":", suggested) { Owner = this };
            if (dlg.ShowDialog() != true) return false;   // cancelled -> do not enter the contest

            long id = dal.CreateLog(dlg.LogName, c.Id);

            // A freshly selected contest starts clean: serial back to 001 and no zone override (use
            // cty.dat). Set these before switching; ApplyContestModeForActiveLog won't reset them.
            // A restart mid-contest goes through Activate (not here), so it resumes instead.
            Properties.Settings.Default.ContestNextSerial = 1;
            Properties.Settings.Default.ContestMyZoneOverride = string.Empty;
            Properties.Settings.Default.Save();

            // Switch to the new (empty) log; this activates the contest via its Event Type and
            // refreshes the entry form, title bar, counts and dup check.
            SwitchActiveLog(id);
            return true;
        }

        private void ExitContest()
        {
            Contests.ContestService.Deactivate();
            Properties.Settings.Default.ContestMode = false;
            Properties.Settings.Default.ActiveContestId = "";
            Properties.Settings.Default.Save();
            UpdateContestModeMenuHeader();
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
            // The received label is "Exchange" alone outside a contest, "Exchange / received" inside one.
            SetExchangeLabel(L_ExchangeLabel, "Exchange", "received", inContest);

            if (!inContest)
            {
                ContestRxPanel.Visibility = Visibility.Collapsed;
                if (ContestTxPanel != null) ContestTxPanel.Visibility = Visibility.Collapsed;
                if (L_SendLabel != null) L_SendLabel.Visibility = Visibility.Collapsed;
                ApplyContestLayout(false);
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

            // RECEIVED frame: RST(R) first, then the contest items. Tab flows DX callsign -> RST-R ->
            // items. The RST you send lives in the SEND frame instead.
            int tab = 9;

            TextBox rstr = AddContestCell("RST R", 52, tab++, ContestRxPanel);
            rstr.Text = TB_RSTRcvd != null ? TB_RSTRcvd.Text : "59";
            rstr.TextChanged += (s, e2) => { if (TB_RSTRcvd != null) TB_RSTRcvd.Text = rstr.Text; };

            foreach (string field in fields)
            {
                ContestFieldUi(field, out string label, out double width);
                TextBox box = AddContestCell(label, width, tab++, ContestRxPanel);
                box.Text = _contestRxBoxes.Count == 0 && TB_Exchange != null ? TB_Exchange.Text : string.Empty;
                box.TextChanged += ContestRxBox_TextChanged;
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
                    if (fieldKey == "SERIAL") _contestSendSerialBox = sbox;
                    _contestSendBoxes.Add(sbox);
                }
                ContestTxPanel.Visibility = Visibility.Visible;
            }
            SetExchangeLabel(L_SendLabel, "Exchange", "send", true);
            if (L_SendLabel != null) L_SendLabel.Visibility = Visibility.Visible;

            ContestRxPanel.Visibility = Visibility.Visible;
            ApplyContestLayout(true);
        }

        // Tracks the current received-field signature and the auto serial box so callsign changes can
        // rebuild the received frame, and Add can advance the serial, without a full re-render otherwise.
        private string _contestRxSig = string.Empty;
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
            var blue = new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0));
            var col = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 10, 0) };
            col.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = blue,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            var box = new TextBox
            {
                Width = width,
                Height = 28,
                FontSize = 16,
                CharacterCasing = CharacterCasing.Upper,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            if (tabIndex.HasValue) box.TabIndex = tabIndex.Value; else box.IsTabStop = false;
            col.Children.Add(box);
            target.Children.Add(col);
            return box;
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
        }

        private void ContestRxBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TB_Exchange != null)
                TB_Exchange.Text = string.Join(" ",
                    _contestRxBoxes.Select(b => b.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
        }

        // Friendly label + box width for a contest exchange field name.
        private static void ContestFieldUi(string field, out string label, out double width)
        {
            switch ((field ?? string.Empty).ToUpperInvariant())
            {
                case "SERIAL": label = "Serial"; width = 60; break;
                case "CQ_ZONE": label = "CQ Zone"; width = 52; break;
                case "ITU_ZONE": label = "ITU"; width = 48; break;
                case "HOLYLAND_AREA": label = "Area"; width = 72; break;
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
                case "GRID": label = "Grid"; width = 64; break;
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

        private void UpdateContestModeMenuHeader()
        {
            bool on = Properties.Settings.Default.ContestMode;
            var gold = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07));
            var gray = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));
            Brush blue = new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0));

            if (ContestModeMenuItem != null)
                ContestModeMenuItem.Header = on ? "Contest Mode - ON" : "Contest Mode - OFF";

            // Tools-menu icon: gold trophy on a blue tile when contesting, plain gray when not.
            if (ContestTrophyPath != null)
                ContestTrophyPath.Fill = on ? gold : gray;
            if (ContestTrophyBg != null)
                ContestTrophyBg.Background = on ? blue : Brushes.Transparent;

            // Main-screen state indicator (display only) mirrors the same look, and its tooltip
            // explains the current mode on hover.
            if (ContestIndicatorPath != null)
                ContestIndicatorPath.Fill = on ? gold : gray;
            if (ContestIndicator != null)
            {
                ContestIndicator.Background = on ? blue : Brushes.Transparent;
                ContestIndicator.ToolTip = on
                    ? "In contest — duplicates are flagged.\nClick to view contest logs or start a new one."
                    : "Not in a contest.\nClick to view or start a contest log.";
            }

            // Contest name beside the trophy, e.g. "World Wide Holyland DX — Active".
            if (L_ContestName != null)
                L_ContestName.Text = (on && Contests.ContestService.Active != null)
                    ? Contests.ContestService.Active.Name + " — Active"
                    : "";
        }

        private Color ParseContestExchangeColor(string colorText)
        {
            try { return (Color)ColorConverter.ConvertFromString(colorText); }
            catch { return (Color)ColorConverter.ConvertFromString("#FFF6C8"); }
        }

        private void ApplyContestExchangeColorFromSettings()
        {
            if (ContestExchangeFrame == null) return;
            ContestExchangeFrame.Background =
                new SolidColorBrush(ParseContestExchangeColor(Properties.Settings.Default.ContestExchangeColor));
        }

        // Right-click anywhere on the contest exchange frame to pick its colour, remembered in settings.
        private void ContestExchangeFrame_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            Color current = ParseContestExchangeColor(Properties.Settings.Default.ContestExchangeColor);
            using (var dlg = new System.Windows.Forms.ColorDialog())
            {
                dlg.FullOpen = true;
                dlg.Color = System.Drawing.Color.FromArgb(current.A, current.R, current.G, current.B);
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    string hex = string.Format("#{0:X2}{1:X2}{2:X2}", dlg.Color.R, dlg.Color.G, dlg.Color.B);
                    Properties.Settings.Default.ContestExchangeColor = hex;
                    Properties.Settings.Default.Save();
                    ApplyContestExchangeColorFromSettings();
                }
            }
            e.Handled = true;
        }

        private Color ParseContestSendColor(string colorText)
        {
            try { return (Color)ColorConverter.ConvertFromString(colorText); }
            catch { return (Color)ColorConverter.ConvertFromString("#E1F5EE"); }
        }

        private void ApplyContestSendColorFromSettings()
        {
            if (ContestSendBand == null) return;
            ContestSendBand.Background =
                new SolidColorBrush(ParseContestSendColor(Properties.Settings.Default.ContestSendColor));
        }

        // Right-click anywhere on the "You send" band to pick its colour, remembered in settings.
        private void ContestSendBand_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            Color current = ParseContestSendColor(Properties.Settings.Default.ContestSendColor);
            using (var dlg = new System.Windows.Forms.ColorDialog())
            {
                dlg.FullOpen = true;
                dlg.Color = System.Drawing.Color.FromArgb(current.A, current.R, current.G, current.B);
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    string hex = string.Format("#{0:X2}{1:X2}{2:X2}", dlg.Color.R, dlg.Color.G, dlg.Color.B);
                    Properties.Settings.Default.ContestSendColor = hex;
                    Properties.Settings.Default.Save();
                    ApplyContestSendColorFromSettings();
                }
            }
            e.Handled = true;
        }
    }
}
