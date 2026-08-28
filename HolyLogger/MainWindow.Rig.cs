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
    // Rig / CAT control via Omni-Rig: engine lifecycle, frequency/mode sync, set-radio-to-QSO, undo.
    // Move-only split from MainWindow.xaml.cs; no behavior change.
    public partial class MainWindow
    {
        
        private string _IsOmniRigEnabled;
        public string IsOmniRigEnabled
        {
            get { return _IsOmniRigEnabled; }
            set
            {
                _IsOmniRigEnabled = value;
                OnPropertyChanged("IsOmniRigEnabled");
            }
        }

        // CAT is "live" only when OmniRig is enabled AND the selected rig is actually online.
        internal bool IsCatLive()
            => Properties.Settings.Default.EnableOmniRigCAT && Rig != null && Rig.Status == OmniRig.RigStatusX.ST_ONLINE;

        // Set the radio to a Channels-window entry (frequency in MHz + mode). Mirrors SetRadioToQsoFreq:
        // captures the current freq/mode onto the log-radio undo stack (which pops the undo icon), reflects
        // the values in the logger fields, and tunes the rig when CAT is live.
        internal async void SetRadioToChannel(double freqMhz, string mode)
        {
            if (freqMhz <= 0) return;

            string normalizedMode = NormalizeClusterModeForLogger(mode);

            // Already on this channel's frequency and mode? Do nothing — no radio change, so don't
            // capture an identical undo state or bump the undo counter.
            if (double.TryParse((TB_Frequency.Text ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double currentMhz)
                && Math.Abs(currentMhz - freqMhz) * 1000.0 < 0.1   // within 0.1 kHz
                && string.Equals((CB_Mode.Text ?? string.Empty).Trim(), normalizedMode, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            CaptureLogRadioUndoState();

            // A channel carries no callsign, so wipe any leftover DX entry (call, name, locator, QRZ
            // photo…) before we move. Setting the frequency below re-runs the on-frequency auto-fill,
            // which repopulates the DX callsign if a cluster spot sits on this frequency, or leaves it
            // blank if none does. Undo (captured above) restores the previous call + frequency.
            TB_DXCallsign.Text = string.Empty;

            TB_Frequency.Text = freqMhz.ToString("0.0###", CultureInfo.InvariantCulture);
            SelectLoggerMode(normalizedMode);

            if (!IsCatLive()) return;

            int freqHz = (int)Math.Round(freqMhz * 1000000.0, MidpointRounding.AwayFromZero);
            // Map from the RAW channel mode (not the SSB-normalized one) so an explicit USB/LSB choice
            // sets that exact sideband on the rig.
            int? rigMode = MapClusterModeToRigMode(mode, freqMhz);
            var modeToSend = (OmniRig.RigParamX)(rigMode ?? PM_DIG_U);
            await TryTuneRigFrequencyAsync(freqHz, modeToSend);
        }

        private async void SetRadioToQsoFreq(QSO qso)
        {
            if (qso == null) return;

            string freqText = (qso.Freq ?? string.Empty).Trim();
            if (!double.TryParse(freqText, NumberStyles.Float, CultureInfo.InvariantCulture, out double freqValue) || freqValue <= 0)
            {
                HolyMessageBox.ShowWarning(
                    "This QSO has no frequency to tune to.\n\n"
                    + "Double-click the QSO and type the frequency in MHz — 14.200 — then try again.",
                    "Set Radio to Frequency", this);
                return;
            }

            double freqMhz = freqValue >= 1000 ? (freqValue / 1000.0) : freqValue;
            string normalizedMode = NormalizeClusterModeForLogger(qso.Mode);

            // Capture the current freq/mode onto the LOG-ROW undo stack (independent of the cluster undo),
            // so the log undo icon's counter increments and the user can step back to the original.
            CaptureLogRadioUndoState();

            // Reflect the QSO's freq/mode in the logger fields (mirrors cluster-spot behavior so undo restores them).
            TB_Frequency.Text = freqMhz.ToString("0.0###", CultureInfo.InvariantCulture);
            SelectLoggerMode(normalizedMode);

            if (!Properties.Settings.Default.EnableOmniRigCAT || Rig == null || Rig.Status != OmniRig.RigStatusX.ST_ONLINE)
            {
                return;
            }

            int freqHz = (int)Math.Round(freqMhz * 1000000.0, MidpointRounding.AwayFromZero);
            int? rigMode = MapClusterModeToRigMode(normalizedMode, freqMhz);
            var modeToSend = (OmniRig.RigParamX)(rigMode ?? PM_DIG_U);
            await TryTuneRigFrequencyAsync(freqHz, modeToSend);
        }

        // ---- Log-row "Set Radio to Freq" undo (independent of the cluster undo) ----

        private void CaptureLogRadioUndoState()
        {
            string frequencyText = (TB_Frequency.Text ?? string.Empty).Trim();
            string modeText = (CB_Mode.Text ?? string.Empty).Trim().ToUpperInvariant();
            string dxCallsignText = (TB_DXCallsign.Text ?? string.Empty).Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(frequencyText) || string.IsNullOrWhiteSpace(modeText))
            {
                return;
            }

            if (logRadioUndoStates.Count > 0)
            {
                var last = logRadioUndoStates.Peek();
                if (string.Equals(last.FrequencyText, frequencyText, StringComparison.Ordinal)
                    && string.Equals(last.ModeText, modeText, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(last.DxCallsignText, dxCallsignText, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            logRadioUndoStates.Push((frequencyText, modeText, dxCallsignText));
            UpdateRadioUndoButtons();   // shared list — refresh the cluster button too
            PulseUndoIcon();   // a new undo state was added — make the icon jump so it's not missed
        }

        // Right-click the undo icon -> a small "Reset undo list" popup, mirroring the cluster undo icon.
        private void MainUndoButton_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (logRadioUndoStates.Count == 0) return;
            e.Handled = true;

            var resetBtn = new System.Windows.Controls.Button
            {
                Content = "Reset undo list",
                Padding = new Thickness(14, 7, 14, 7),
                FontSize = 16,
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8B, 0x22, 0x22)),
                Foreground = System.Windows.Media.Brushes.White,
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE5, 0x73, 0x73)),
                BorderThickness = new Thickness(1)
            };

            var popup = new System.Windows.Controls.Primitives.Popup
            {
                PlacementTarget = MainUndoButton,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                StaysOpen = false,
                AllowsTransparency = true,
                Child = new System.Windows.Controls.Border
                {
                    Background = System.Windows.Media.Brushes.White,
                    BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xAA, 0xAA, 0xAA)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6),
                    Child = resetBtn
                }
            };

            resetBtn.Click += (s, ev) =>
            {
                popup.IsOpen = false;
                ResetLogRadioUndo();
            };

            popup.PreviewKeyDown += (s, ev) =>
            {
                if (ev.Key == System.Windows.Input.Key.Escape)
                {
                    popup.IsOpen = false;
                    ev.Handled = true;
                }
            };

            popup.IsOpen = true;
        }

        // Briefly bounces the undo icon so the operator notices it appear/increment (e.g. after tuning
        // to a channel or a spot). Purely visual; the icon settles back to its normal size.
        private void PulseUndoIcon()
        {
            if (MainUndoIconGrid == null || MainUndoIconScale == null) return;
            try
            {
                var pulse = new System.Windows.Media.Animation.DoubleAnimation
                {
                    From = 1.0,
                    To = 1.7,
                    Duration = TimeSpan.FromMilliseconds(200),
                    AutoReverse = true,
                    RepeatBehavior = new System.Windows.Media.Animation.RepeatBehavior(3),
                    EasingFunction = new System.Windows.Media.Animation.SineEase
                    {
                        EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut
                    }
                };
                MainUndoIconScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, pulse);
                MainUndoIconScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, pulse);
            }
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }
        }

        // Clears the entire log-radio undo stack (the "reset" action triggered by a long press).
        private void ResetLogRadioUndo()
        {
            if (logRadioUndoStates.Count == 0) return;
            logRadioUndoStates.Clear();
            UpdateRadioUndoButtons();   // shared list — refresh the cluster button too
            if (QSODataGrid != null && QSODataGrid.SelectedItem != null)
                QSODataGrid.UnselectAll();
        }

        private async void LogRadioUndoButton_Click(object sender, RoutedEventArgs e)
        {
            // async void: an exception here would be unhandled and crash the app, so the whole body
            // is guarded.
            try
            {
                // If a long press just cleared the stack, swallow this click so it doesn't also undo.
                if (_undoResetFired)
                {
                    _undoResetFired = false;
                    return;
                }

                if (logRadioUndoStates.Count == 0)
                {
                    return;
                }

                var undoState = logRadioUndoStates.Pop();
                UpdateRadioUndoButtons();   // shared list — refresh the cluster button too

                // Clear the log-row blue highlight once an undo step is taken.
                if (QSODataGrid != null && QSODataGrid.SelectedItem != null)
                    QSODataGrid.UnselectAll();

                string freqText = undoState.FrequencyText;
                string modeText = undoState.ModeText;
                string dxCallsignText = undoState.DxCallsignText;

                if (!double.TryParse(freqText, NumberStyles.Float, CultureInfo.InvariantCulture, out double freqMhz) || freqMhz <= 0)
                {
                    return;
                }

                TB_Frequency.Text = freqMhz.ToString("0.0###", CultureInfo.InvariantCulture);
                SelectLoggerMode(modeText);
                // Restored callsign is a complete call, not typing — don't pop the suggestions dropdown.
                suppressNextCallsignSuggestions = true;
                TB_DXCallsign.Text = dxCallsignText;

                if (Properties.Settings.Default.EnableOmniRigCAT && Rig != null && Rig.Status == OmniRig.RigStatusX.ST_ONLINE)
                {
                    int freqHz = (int)Math.Round(freqMhz * 1000000.0, MidpointRounding.AwayFromZero);
                    int? rigMode = MapClusterModeToRigMode(modeText, freqMhz);
                    var modeToSend = (OmniRig.RigParamX)(rigMode ?? PM_DIG_U);
                    await TryTuneRigFrequencyAsync(freqHz, modeToSend);
                }
            }
            catch (Exception ex)
            {
                // NEVER CRASH THE APP FROM THE UNDO BUTTON - but never fail in silence
                // either. What is being undone here is a change to the RADIO; if it does
                // not happen, the rig is left where it was and the screen says otherwise.
                Log.Warn("Radio undo failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void UpdateLogRadioUndoButtonState()
        {
            bool hasUndo = logRadioUndoStates.Count > 0;

            if (MainUndoIconGrid != null)
            {
                MainUndoIconGrid.Visibility = hasUndo ? Visibility.Visible : Visibility.Collapsed;
            }
            if (MainUndoCountText != null)
            {
                MainUndoCountText.Text = hasUndo ? logRadioUndoStates.Count.ToString(CultureInfo.InvariantCulture) : string.Empty;
            }
        }

        private bool TrySendOmniRigCustomCommand(string command)
        {
            try
            {
                byte[] rawCommand = ParseCustomCommand(command);
                Rig.SendCustomCommand(rawCommand, 0, string.Empty);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void RigLabel_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
            => OptionsMenuItemMenuItem_Click(null, null);

        private void GeneralSettingsControlControlInstance_OmniRigEngine_Changed()
        {
            if (Properties.Settings.Default.EnableOmniRigCAT)
            {
                StartOmniRig();
                options.GeneralSettingsControlControlInstance.Rig1 = Rig1;
                options.GeneralSettingsControlControlInstance.Rig2 = Rig2;
            }
            else
                StopOmniRig();

            SelectRig();
            ShowRigParams();
            UpdateVoiceMessageAvailabilityState();
        }

        private void OmnirigMenuItem_Click(object sender, RoutedEventArgs e)
        {
            string url = "http://www.dxatlas.com/OmniRig/";

            try
            {
                System.Diagnostics.Process.Start(url);
            }
            catch (Exception)
            {
                HolyMessageBox.Show("Please install 'Chrome' and try again.", "HolyLogger", HolyMsgType.Info, this);
            }
        }


        public string Rig1 { get; set; }
        public string Rig2 { get; set; }

        /// <summary>
        /// The omni rig engine
        /// </summary>
        OmniRig.OmniRigX OmniRigEngine;
        /// <summary>
        /// The rig
        /// </summary>
        OmniRig.RigX Rig;
        private bool _showRigParamsQueued;

        private void StartOmniRig()
        {
            try
            {
                if (OmniRigEngine != null)
                {
                    //MessageBox.Show("OmniRig Is running");
                }
                else
                {

                    OmniRigEngine = new OmniRig.OmniRigX();

                    // WE WANT OMNIRIG'S 1.x INTERFACE - 1.01 to 1.99 - because a 2.0 would very likely
                    // not be compatible with it.
                    //
                    // THE TEST USED TO BE "&&", which asks for a number that is at once below 0x101 and
                    // above 0x299. No number is, so the check never once fired and the message behind
                    // it was never shown, whatever was installed. It also carried on into GetRigTypes
                    // afterwards, having just set the engine to null.
                    int version = OmniRigEngine.InterfaceVersion;
                    if (version < 0x101 || version > 0x299)
                    {
                        OmniRigEngine = null;
                        Status = "Wrong version";
                        ReportOmniRigProblem(
                            "The OmniRig on this computer answers with interface version "
                            + (version >> 8) + "." + (version & 0xFF).ToString("00")
                            + ", and HolyLogger works with version 1.\n\n"
                            + "Radio control (CAT) will not work until a version 1 OmniRig is installed.");
                        return;
                    }

                    GetRigTypes();
                    SubscribeToEvents();
                    SelectRig();
                    ShowRigParams();
                }
            }
            catch (Exception e)
            {
                Log.Warn("OmniRig could not be started: " + e.GetType().Name + ": " + e.Message);
                OmniRigEngine = null;
                Status = "Not installed";

                // AND THE OPERATOR IS TOLD.
                //
                // This used to fail in silence: the exception was swallowed, a small label read "Not
                // installed", and the radio simply never answered - with nothing on screen to say why.
                // His words: "if the program have problems with OmniRig, the program must tell it to
                // the user".
                //
                // Only reached when CAT is switched ON in Options, so an operator with no radio
                // interface is never shown it.
                ReportOmniRigProblem(
                    "HolyLogger could not start OmniRig, so radio control (CAT) will not work.\n\n"
                    + "Reason: " + e.Message + "\n\n"
                    + "OmniRig is a separate free program that HolyLogger uses to talk to the radio. If "
                    + "it is not installed, install it and start HolyLogger again. If you do not use a "
                    + "radio interface, switch CAT off in Options > General and this will not come back.");
            }
        }

        // SAID ONCE IN A RUN. StartOmniRig is called again every time the CAT setting is touched, and a
        // message box that comes back on every visit to Options is a message box nobody reads.
        private bool _omniRigProblemReported;

        private void ReportOmniRigProblem(string message)
        {
            if (_omniRigProblemReported) return;
            _omniRigProblemReported = true;

            // NOT WHILE THE WINDOW IS STILL COMING UP. One caller is Window_Loaded, and a dialog there
            // stands in front of a window that has not finished appearing - the operator sees a warning
            // over a half-drawn program and cannot tell what it belongs to. At ApplicationIdle the log
            // is on screen and the message can be read for what it is.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { HolyMessageBox.ShowWarning(message, "Radio control (OmniRig)", this); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }), DispatcherPriority.ApplicationIdle);
        }
        private void StopOmniRig()
        {
            // Detach only — do NOT kill OmniRig.exe. OmniRig is a shared automation server that other
            // apps (e.g. HolyCluster's CAT server) also use to control the same radio; killing the
            // process would yank the rig out from under them. Unsubscribing and releasing our COM
            // references lets go of OmniRig cleanly while leaving the process running for others; if
            // nothing else is using it, OmniRig shuts itself down on its own.
            UnsubscribeFromEvents();
            try
            {
                if (Rig != null) System.Runtime.InteropServices.Marshal.ReleaseComObject(Rig);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            try
            {
                if (OmniRigEngine != null) System.Runtime.InteropServices.Marshal.ReleaseComObject(OmniRigEngine);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            OmniRigEngine = null;
            Rig = null;
            UpdateStatus();
        }
        private void SelectRig()
        {
            ForgetWriteableParams();   // another rig, another set of writeable parameters
            UpdateRigLabel();
            if (OmniRigEngine == null) { UpdateFreqLed(); return; }
            if (Properties.Settings.Default.SelectedOmniRig1)
                Rig = OmniRigEngine.Rig1;
            else if (Properties.Settings.Default.SelectedOmniRig2)
                Rig = OmniRigEngine.Rig2;
            UpdateFreqLed();   // reflect the newly-selected rig (or blank if it isn't online)
        }

        //OmniRig ParamsChange events
        private void OmniRigEngine_ParamsChange(int RigNumber, int Params)
        {
            LogRigModeChange();
            QueueShowRigParams();
        }

        // The last raw mode value seen, so only CHANGES are logged - OmniRig raises ParamsChange for
        // every frequency tick, and logging each one would bury the mode changes we are looking for.
        private int _lastLoggedRigMode = -1;

        // Records exactly what OmniRig reports for the rig's mode, and what HolyLogger makes of it.
        //
        // OmniRig has only eight mode values (CW_U/CW_L, SSB_U/SSB_L, DIG_U/DIG_L, AM, FM) - there is
        // no "USB-D". Whether a rig's data sub-mode arrives as DIG_U or as plain SSB_U is decided by
        // that rig's .ini file in OmniRig, not here. This line settles which of the two it is, instead
        // of reasoning about what the rig "should" send.
        private void LogRigModeChange()
        {
            try
            {
                if (Rig == null) return;
                int raw = (int)Rig.Mode;
                if (raw == _lastLoggedRigMode) return;
                _lastLoggedRigMode = raw;

                string name;
                switch (raw)
                {
                    case PM_CW_U:  name = "PM_CW_U";  break;
                    case PM_CW_L:  name = "PM_CW_L";  break;
                    case PM_SSB_U: name = "PM_SSB_U"; break;
                    case PM_SSB_L: name = "PM_SSB_L"; break;
                    case PM_DIG_U: name = "PM_DIG_U"; break;
                    case PM_DIG_L: name = "PM_DIG_L"; break;
                    case PM_AM:    name = "PM_AM";    break;
                    case PM_FM:    name = "PM_FM";    break;
                    default:       name = "(unknown)"; break;
                }

                Log.Warn($"OmniRig mode: {name} (0x{raw:X8}) -> HolyLogger shows \"{GetNormalizedRigMode()}\"");
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        //OmniRig StatusChange events
        private void OmniRigEngine_StatusChange(int RigNumber)
        {
            ForgetWriteableParams();   // a rig that just came online may answer differently
            QueueShowRigParams();
        }

        private void QueueShowRigParams()
        {
            if (_showRigParamsQueued)
            {
                return;
            }

            _showRigParamsQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _showRigParamsQueued = false;
                ShowRigParams();
            }), DispatcherPriority.Background);
        }

        // The Mode combo is frozen while the radio is driving it, and only then — otherwise the operator
        // must be able to set the mode by hand.
        //
        // The three conditions below are exactly the ones ShowRigParams itself checks before it writes
        // CB_Mode.Text: online, not Manual, not editing an existing QSO. It used to lock on "online"
        // alone, which froze the box in the two cases where the rig is online but deliberately ignored -
        // a QSO typed in Manual mode, and a QSO being edited - so neither the radio nor the operator was
        // setting the mode and it simply could not be changed.
        //
        // Called from ShowRigParams (rig events, Manual/CAT switches) and from UpdateState (entering and
        // leaving edit), so it never waits for the next rig poll to catch up.
        private void UpdateModeComboLock()
        {
            if (CB_Mode == null) return;

            bool rigOnline = OmniRigEngine != null && Rig != null
                             && Rig.Status == OmniRig.RigStatusX.ST_ONLINE
                             && Properties.Settings.Default.EnableOmniRigCAT;

            bool rigDrivesMode = rigOnline
                                 && !Properties.Settings.Default.isManualMode
                                 && state != State.Edit;

            CB_Mode.IsHitTestVisible = !rigDrivesMode;
        }

        private void ShowRigParams()
        {
            ShowRigStatus();

            // Before any of the early returns below: the panel must follow the radio even in manual
            // mode or while a QSO is being edited, when the logger's own fields deliberately do not.
            UpdateRadioPanel();

            bool rigOnline = OmniRigEngine != null && Rig != null
                             && Rig.Status == OmniRig.RigStatusX.ST_ONLINE
                             && Properties.Settings.Default.EnableOmniRigCAT;

            UpdateModeComboLock();

            if (!rigOnline)
            {
                ClearVoiceMessageState();
                // THE ROW IS DIMMED HERE, on the event, rather than by a timer noticing half a second
                // later. This is the path taken when the radio goes away - switched off, cable pulled,
                // OmniRig stopped - and it is exactly the moment the send buttons stop being usable.
                UpdateVoiceMessageAvailabilityState();
                UpdateFreqLed();   // no live rig -> blank the LED instead of showing a stale value
                return;
            }

            if (Properties.Settings.Default.isManualMode || state == State.Edit)
            {
                // Still worth refreshing: the radio IS online, and that alone can change whether the
                // buttons may be used, even when this method goes no further.
                UpdateVoiceMessageAvailabilityState();
                return;
            }
            try
            {
                this.Dispatcher.Invoke(() =>
                {
                    double radioRX = (double)Rig.GetRxFrequency() / 1000000;
                    double radioTX = (double)Rig.GetTxFrequency() / 1000000;
                    if (Properties.Settings.Default.IsSatelliteMode)
                        radioRX += Properties.Settings.Default.SatelliteShift;

                    // OmniRig can fire StatusChange before it has polled the rig's frequency
                    // register; GetRxFrequency returns 0 in that window. Skip the update so
                    // the LED keeps showing the previous known frequency instead of blank dashes.
                    // The immediately following ParamsChange will carry the real value.
                    if (radioRX > 0)
                    {
                        RX = radioRX.ToString("###0.000000");
                        TX = radioTX.ToString("###0.000000");
                        TB_Frequency.Text = RX;
                        Properties.Settings.Default.Frequency = RX;
                    }

                    CB_Mode.Text = GetNormalizedRigMode();
                    UpdateVoiceMessageState();
                    UpdateVoiceMessageAvailabilityState();
                    // Always refresh the LED — WPF skips no-op binding updates so TextChanged
                    // may not fire when the frequency value didn't change.
                    UpdateFreqLed();
                });
            }
            catch (Exception e)
            {
                System.Windows.Forms.MessageBox.Show("Error: " + e.Message);
            }

        }

        // ---- Radio Control Panel ------------------------------------------------------------
        //
        // A small window of its own: a frequency box, ten band buttons and SSB/CW. It holds no state
        // about the radio - it asks the radio to move, and what it SHOWS is written by
        // UpdateRadioPanel from what the radio reports, so a band or mode changed on the radio's own
        // knobs lights up here exactly as a button press does.

        private RadioControlPanelWindow radioPanel;

        /// <summary>Opens or closes the panel to match the "Show Control Panel" setting.</summary>
        internal void ApplyRadioControlPanelVisibility()
        {
            if (Properties.Settings.Default.ShowRadioControlPanel)
            {
                if (radioPanel == null)
                {
                    radioPanel = new RadioControlPanelWindow(this);
                    try { radioPanel.Owner = this; } catch (Exception swallowed) { Log.Swallow(swallowed); }
                    radioPanel.Closed += RadioPanel_Closed;
                    radioPanel.Show();
                }
                else
                {
                    try { radioPanel.Activate(); } catch (Exception swallowed) { Log.Swallow(swallowed); }
                }

                UpdateRadioPanel();
                return;
            }

            var panel = radioPanel;
            radioPanel = null;
            if (panel == null) return;

            panel.Closed -= RadioPanel_Closed;   // we are closing it because it was switched off
            try { panel.Close(); } catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        /// <summary>
        /// Called as the program shuts down, BEFORE the main window closes.
        ///
        /// WHY IT EXISTS: the panel is owned by the main window, so Windows closes it as part of the
        /// shutdown - and that ran RadioPanel_Closed, which reads a close as "the operator switched
        /// the panel off" and wrote the setting to false. The tick in Options was therefore cleared
        /// by every exit, and the panel never came back. Letting go of the handler first means only
        /// a close the operator actually performs switches it off.
        /// </summary>
        internal void DetachRadioPanelForShutdown()
        {
            if (radioPanel == null) return;
            try { radioPanel.Closed -= RadioPanel_Closed; }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // The LED on the main window tunes on the wheel exactly as the panel's box does - same class,
        // same first-notch rule - so there is one behaviour to learn and one place it is written.
        private readonly FrequencyWheel _ledWheel = new FrequencyWheel();

        private void FreqLed_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            e.Handled = true;

            // Only when the LED is a live reading. With CAT off, no rig, or manual mode, the number on
            // screen is one the operator typed and there is no radio behind it to move.
            if (!Properties.Settings.Default.EnableOmniRigCAT || Properties.Settings.Default.isManualMode) return;
            if (OmniRigEngine == null || Rig == null || Rig.Status != OmniRig.RigStatusX.ST_ONLINE) return;

            // Not while the inline editor is open over the LED: the wheel there belongs to the text.
            if (TB_FreqLedEdit != null && TB_FreqLedEdit.Visibility == Visibility.Visible) return;

            double khz;
            try { khz = (double)Rig.GetRxFrequency() / 1000.0; }
            catch (Exception swallowed) { Log.Swallow(swallowed); return; }

            double? target = _ledWheel.Next(khz, e.Delta, LedStepUnderPointer(e.GetPosition(FreqLedLive).X));
            if (target == null) return;

            QueueWheelTune(target.Value);
        }

        /// <summary>
        /// Which digits the pointer is over on the LED: 1 kHz to the left of the decimal point, 0.1 kHz
        /// to the right of it - the display is read the way the radio's own dial is, the digit you are
        /// pointing at being the one that moves.
        ///
        /// The LED is right-aligned inside a fixed width, so where the point sits on screen depends on
        /// how many digits the frequency has. The text is measured as it is actually drawn - same font,
        /// same size, same screen - rather than guessed at from character counts, which a seven-segment
        /// font with its narrow dot would get wrong.
        /// </summary>
        private double LedStepUnderPointer(double x)
        {
            try
            {
                if (FreqLedLive == null) return 1.0;

                string text = FreqLedLive.Text ?? string.Empty;
                int dot = text.IndexOf('.');
                if (dot < 0) return 1.0;

                double dpi = VisualTreeHelper.GetDpi(FreqLedLive).PixelsPerDip;
                var typeface = new Typeface(FreqLedLive.FontFamily, FreqLedLive.FontStyle,
                                            FreqLedLive.FontWeight, FreqLedLive.FontStretch);

                double whole = Measure(text, typeface, dpi);
                double uptoDot = Measure(text.Substring(0, dot + 1), typeface, dpi);

                // Right-aligned: the text ends at the right edge, so it begins that far back from it.
                double textStart = FreqLedLive.ActualWidth - whole;
                return x > textStart + uptoDot ? 0.1 : 1.0;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            return 1.0;
        }

        private double Measure(string text, Typeface typeface, double pixelsPerDip)
        {
            return new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                     typeface, FreqLedLive.FontSize, System.Windows.Media.Brushes.Black,
                                     pixelsPerDip).WidthIncludingTrailingWhitespace;
        }

        private void RadioPanel_Closed(object sender, EventArgs e)
        {
            radioPanel = null;
            // Closing the window IS switching the panel off. Without this it would come back on the
            // next start, and a window that reappears after being closed is a window nobody trusts.
            Properties.Settings.Default.ShowRadioControlPanel = false;
        }

        /// <summary>Rebuild the panel's buttons after the frequencies were edited in Options.</summary>
        internal void ReloadRadioPanelPresets()
        {
            try { radioPanel?.ReloadPresets(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        /// <summary>
        /// Tells the panel what the radio is doing. Called from ShowRigParams, which runs on every
        /// OmniRig params/status event, so the panel follows the radio without a timer of its own.
        /// </summary>
        internal void UpdateRadioPanel()
        {
            if (radioPanel == null) return;

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(UpdateRadioPanel));
                return;
            }

            bool online = Properties.Settings.Default.EnableOmniRigCAT
                          && OmniRigEngine != null && Rig != null
                          && Rig.Status == OmniRig.RigStatusX.ST_ONLINE;

            double khz = 0;
            string mode = null;
            bool? transmitting = null;

            if (online)
            {
                try
                {
                    // The radio's real frequency, with no satellite shift applied: the shift belongs to
                    // what is logged, not to which band the radio is actually sitting on.
                    khz = (double)Rig.GetRxFrequency() / 1000.0;
                    mode = GetNormalizedRigMode();

                    // NO POLLING OF OUR OWN. OmniRig polls the radio on its own timer and raises
                    // ParamsChange when anything moves; this is that event, and Rig.Tx is the state it
                    // has already read. The CW keyer reads the same property in the same way.
                    //
                    // A rig whose OmniRig .ini does not read the PTT line answers PM_UNKNOWN, and the
                    // panel then lights neither lamp rather than guess at "receiving".
                    var txState = Rig.Tx;
                    if (txState == (OmniRig.RigParamX)PM_TX) transmitting = true;
                    else if (txState == (OmniRig.RigParamX)PM_RX) transmitting = false;
                }
                catch (Exception swallowed)
                {
                    Log.Swallow(swallowed);
                    online = false;
                }
            }

            try { radioPanel.ShowRigState(online, khz, mode, transmitting); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // ---- the wheel's own way to the radio ------------------------------------------------
        //
        // WHY THE WHEEL DOES NOT GO THROUGH TuneRadioToKhz. That path is written for a jump to a spot:
        // it reads the rig's mode, WRITES the mode, writes the frequency, and then polls GetRxFrequency
        // up to eight times over a second to make sure the radio arrived. Perfectly right for one press
        // of a band button - and ruinous ten times a second, which is what a spun wheel asks for. A ten
        // notch spin cost ten mode writes, ten frequency writes and up to eighty readback polls, and the
        // radio ended up chasing a queue of commands long after the wheel had stopped.
        //
        // This path carries one thing to the radio: the newest frequency. No mode is written (the wheel
        // never changes the mode), nothing is polled back (OmniRig reports the new frequency on its own
        // ParamsChange, which is what draws the LED anyway), and while the wheel is being spun at most
        // one command goes out every 50 ms - always the LATEST target, never a backlog of stale ones.

        private DispatcherTimer _wheelSendTimer;
        private double _wheelPendingKhz;
        private DateTime _wheelSentAtUtc;

        // Rig.WriteableParams over COM, asked once per rig rather than once per notch. Cleared whenever
        // the rig or its status changes, which is the only time the answer can differ.
        private int _writeableParamsCache = -1;

        internal void ForgetWriteableParams()
        {
            _writeableParamsCache = -1;
        }

        /// <summary>
        /// A wheel notch's worth of tuning: kept until the next send slot, so a fast spin costs one
        /// command per 50 ms instead of one per notch.
        /// </summary>
        internal void QueueWheelTune(double khz)
        {
            if (khz <= 0) return;

            _wheelPendingKhz = khz;

            double sinceLast = (DateTime.UtcNow - _wheelSentAtUtc).TotalMilliseconds;
            if (sinceLast >= 50)
            {
                SendWheelTune();
                return;
            }

            if (_wheelSendTimer == null)
            {
                _wheelSendTimer = new DispatcherTimer(DispatcherPriority.Send)
                {
                    Interval = TimeSpan.FromMilliseconds(50)
                };
                _wheelSendTimer.Tick += (s, e) =>
                {
                    _wheelSendTimer.Stop();
                    SendWheelTune();
                };
            }

            if (!_wheelSendTimer.IsEnabled) _wheelSendTimer.Start();
        }

        private void SendWheelTune()
        {
            double khz = _wheelPendingKhz;
            _wheelPendingKhz = 0;
            if (khz <= 0) return;

            if (!Properties.Settings.Default.EnableOmniRigCAT || Rig == null
                || Rig.Status != OmniRig.RigStatusX.ST_ONLINE)
            {
                return;
            }

            _wheelSentAtUtc = DateTime.UtcNow;
            int frequencyHz = (int)Math.Round(khz * 1000.0);

            try
            {
                if (_writeableParamsCache < 0) _writeableParamsCache = (int)Rig.WriteableParams;

                if ((_writeableParamsCache & PM_FREQ) != 0) Rig.Freq = frequencyHz;
                else if ((_writeableParamsCache & PM_FREQA) != 0) Rig.FreqA = frequencyHz;
                else return;
            }
            catch (Exception swallowed)
            {
                Log.Swallow(swallowed);
                _writeableParamsCache = -1;   // ask again next time; the rig may have gone
                return;
            }

            // The LED moves NOW, not when the radio gets round to answering. OmniRig polls the rig on
            // its own interval - half a second on many .ini files - and a display that only caught up
            // then made the wheel feel broken. ShowRigParams overwrites this with the radio's own word
            // as soon as it arrives, so nothing here can survive being wrong.
            try
            {
                TB_Frequency.Text = (frequencyHz / 1000000.0).ToString("###0.000000", CultureInfo.InvariantCulture);
                UpdateFreqLed();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        /// <summary>
        /// The one way the panel and the LED move the radio: a frequency in kHz, and the mode it
        /// should be in - or null for "leave the mode alone", which is what the wheel asks for.
        /// </summary>
        internal async void TuneRadioToKhz(double khz, string mode)
        {
            if (khz <= 0) return;

            if (!Properties.Settings.Default.EnableOmniRigCAT || Rig == null
                || Rig.Status != OmniRig.RigStatusX.ST_ONLINE)
            {
                UpdateRadioPanel();   // the panel says so itself; no dialog to dismiss
                return;
            }

            int frequencyHz = (int)Math.Round(khz * 1000.0);
            string wanted = string.IsNullOrWhiteSpace(mode)
                ? (GetNormalizedRigMode() ?? "SSB")
                : mode.Trim().ToUpperInvariant();

            // Same mapping the cluster and the log use, so SSB below 10 MHz is LSB and above it USB.
            int? rigMode = MapClusterModeToRigMode(wanted, frequencyHz / 1000000.0);
            await TryTuneRigFrequencyAsync(frequencyHz, (OmniRig.RigParamX)(rigMode ?? PM_SSB_U));

            UpdateRadioPanel();
        }
    }
}
