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
            UnsubscribeFromEvents();
            Process[] workers = Process.GetProcessesByName("OmniRig");
            foreach (Process worker in workers)
            {
                worker.Kill();
                worker.WaitForExit();
                worker.Dispose();
            }
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
