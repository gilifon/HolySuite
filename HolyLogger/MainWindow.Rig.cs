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
                HolyMessageBox.ShowWarning("This QSO has no valid frequency.", "Set Radio to Frequency", this);
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
            UpdateLogRadioUndoButtonState();
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
                FontSize = 13,
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
            UpdateLogRadioUndoButtonState();
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
                UpdateLogRadioUndoButtonState();

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
            catch { /* never crash the app from the undo button */ }
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
                    //OmniRigEngine = (OmniRig.OmniRigX)Activator.CreateInstance(Type.GetTypeFromProgID("OmniRig.OmniRigX"));
                    // we want OmniRig interface V.1.1 to 1.99
                    // as V2.0 will likely be incompatible  with 1.x
                    if (OmniRigEngine.InterfaceVersion < 0x101 && OmniRigEngine.InterfaceVersion > 0x299)
                    {
                        OmniRigEngine = null;
                        MessageBox.Show("OmniRig Is Not installed Or has a wrong version number");
                    }
                    GetRigTypes();
                    SubscribeToEvents();
                    SelectRig();
                    ShowRigParams();
                }
            }
            catch (Exception e)
            {
                //Mouse.OverrideCursor = null;
                //MessageBox.Show(ex.Message);
                //throw;
                Status = "Not installed";
            }
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
            QueueShowRigParams();
        }

        //OmniRig StatusChange events
        private void OmniRigEngine_StatusChange(int RigNumber)
        {
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

        private void ShowRigParams()
        {
            ShowRigStatus();

            bool rigOnline = OmniRigEngine != null && Rig != null
                             && Rig.Status == OmniRig.RigStatusX.ST_ONLINE
                             && Properties.Settings.Default.EnableOmniRigCAT;

            // When the radio is online it controls the mode — block user interaction with the combo.
            // When offline the operator must be able to pick the mode manually.
            if (CB_Mode != null) CB_Mode.IsHitTestVisible = !rigOnline;

            if (!rigOnline)
            {
                ClearVoiceMessageState();
                UpdateFreqLed();   // no live rig -> blank the LED instead of showing a stale value
                return;
            }

            if (Properties.Settings.Default.isManualMode || state == State.Edit)
            {
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
    }
}
