using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using HolyParser;

namespace HolyLogger
{
    // LISTENING TO OTHER LOGGING PROGRAMS.
    //
    // The operator writes his own list of UDP ports (Options > General > UDP Ports, the table in
    // UdpPortsWindow). Every ticked line gets a socket, and a contact arriving on ANY of them is
    // stored in the log. There used to be two fixed ports with two separate readers; the name in the
    // table decides nothing now - the datagram itself says what it is:
    //
    //   <RadioInfo>    N1MM+ telling us where its radio is. Fills the Frequency and Mode boxes, as
    //                  the old fixed N1MM+ port did. It is NOT a contact and is not logged.
    //   <contactinfo>  N1MM+ contact. Logged. N1MM+'s other messages (a deleted or replaced contact,
    //                  a callsign lookup, a score) are left alone - they would otherwise be read as
    //                  contacts and fill the log with rubbish.
    //   ADIF record    Anything holding real ADIF fields and an <eor>, whoever sent it. This is also
    //                  how WSJT-X, JTDX and MSHV arrive: their "logged QSO" datagram is a binary
    //                  envelope with the plain ADIF record sitting inside it, so the record is cut
    //                  out of the bytes and read. Their other datagrams (status, decodes, heartbeat)
    //                  carry no ADIF and are ignored.
    //   WSJT-X Status  Where the sending program is pointing right now. HolyCluster sends one when a
    //                  spot is selected, so the callsign goes into the DX Callsign box (and the
    //                  frequency too, when CAT is not driving the radio). Not a contact, not logged.
    //                  See MainWindow.HolyClusterUdp.cs, which reads it and acts on it.
    //   anything else  Handed to the same reader the old port 2333 used, which is what the programs
    //                  that send a bare field list expect.
    //
    // A contact already in the log is not stored twice, by the program's one duplicate rule
    // (DataAccess.MatchKey): two programs pointed at two ports can both forward the same QSO.
    public partial class MainWindow
    {
        private class UdpListener
        {
            public UdpClient Client;
            public string Name;
            public int Port;
            public int FailuresInARow;   // see UdpReceive: a port that only ever fails is let go
        }

        private readonly List<UdpListener> _udpListeners = new List<UdpListener>();

        // Opens and closes sockets so they match the saved table. Safe to call repeatedly: at startup,
        // and again every time the table is saved, so a changed port takes effect at once.
        internal void ApplyUdpListeners()
        {
            List<UdpPortEntry> rows;
            try { rows = UdpPortStore.Load(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); return; }

            // What should be open, one entry per port (the table window refuses to save the same port
            // twice, but a hand-edited settings file could still hold it).
            var wanted = new Dictionary<int, string>();
            foreach (var row in rows)
            {
                if (row == null || !row.IsOn || row.PortNumber == 0) continue;
                if (wanted.ContainsKey(row.PortNumber)) continue;
                wanted[row.PortNumber] = string.IsNullOrWhiteSpace(row.Name)
                    ? "port " + row.PortNumber
                    : row.Name.Trim();
            }

            // Close the ones that are no longer wanted.
            for (int i = _udpListeners.Count - 1; i >= 0; i--)
            {
                if (wanted.ContainsKey(_udpListeners[i].Port)) continue;
                CloseListener(_udpListeners[i]);
                _udpListeners.RemoveAt(i);
            }

            // Open the ones that are not open yet.
            var failed = new List<string>();
            foreach (var pair in wanted)
            {
                if (_udpListeners.Any(l => l.Port == pair.Key)) continue;

                var listener = new UdpListener { Port = pair.Key, Name = pair.Value };
                try
                {
                    listener.Client = new UdpClient(pair.Key);
                    _udpListeners.Add(listener);
                    listener.Client.BeginReceive(new AsyncCallback(UdpReceive), listener);
                    // A port just opened: forget the last spot, so selecting the same station in
                    // HolyCluster fills the DX box again instead of being taken for a re-send.
                    _lastHolyClusterSpotKey = null;
                }
                catch (Exception swallowed)
                {
                    Log.Swallow(swallowed);
                    failed.Add(pair.Value + " (port " + pair.Key + ")");
                    // Switched off, so the program does not ask about it again at every start. The
                    // line stays in the table with its name and port, ready to be ticked again.
                    foreach (var row in rows)
                        if (row != null && row.PortNumber == pair.Key) row.IsOn = false;
                }
            }

            if (failed.Count > 0)
            {
                try { UdpPortStore.Save(rows); } catch (Exception swallowed) { Log.Swallow(swallowed); }
                HolyMessageBox.ShowWarning(
                    (failed.Count == 1 ? "This UDP port could not be opened:\n\n" : "These UDP ports could not be opened:\n\n")
                    + string.Join("\n", failed.Select(f => "• " + f))
                    + "\n\nAnother program is probably already using it.\n\n"
                    + (failed.Count == 1 ? "That line has been switched off. " : "Those lines have been switched off. ")
                    + "To try another port, open Options → General → UDP Ports Manager.",
                    "UDP Ports Manager", this);
            }
        }

        // Called on the way out, and whenever a port leaves the table.
        internal void CloseUdpListeners()
        {
            foreach (var listener in _udpListeners) CloseListener(listener);
            _udpListeners.Clear();
        }

        private static void CloseListener(UdpListener listener)
        {
            try
            {
                if (listener != null && listener.Client != null)
                {
                    var client = listener.Client;
                    listener.Client = null;   // the pending receive below sees this and stops
                    client.Close();
                    ((IDisposable)client).Dispose();
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // One callback for every port; which port it was arrives as the async state.
        //
        // Closing the window disposes the socket, and that wakes the receive that was still pending up
        // one last time. Nothing below may run then: the socket is already gone (NullReferenceException)
        // and the dispatcher is on its way out (TaskCanceledException). Take one copy of the socket and
        // work through that.
        private async void UdpReceive(IAsyncResult res)
        {
            var listener = res.AsyncState as UdpListener;
            var udp = listener == null ? null : listener.Client;
            if (_isShutdownCleanupDone || udp == null) return;

            try
            {
                IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                byte[] received = udp.EndReceive(res, ref remote);
                listener.FailuresInARow = 0;

                await HandleUdpDatagram(received);

                // Listen again, unless the port was closed while we worked.
                if (!_isShutdownCleanupDone && listener.Client != null)
                    listener.Client.BeginReceive(new AsyncCallback(UdpReceive), listener);
            }
            catch (ObjectDisposedException) { /* socket closed during shutdown - expected */ }
            catch (Exception ex)
            {
                // IT USED TO STOP LISTENING HERE, FOR GOOD, AND SAY NOTHING. The line that asks for the
                // next datagram is on the way out of a CLEAN receive only, so a single failure - and
                // Windows reports a refused datagram as an error on the NEXT receive - left the port
                // dead until the program was restarted. The news went to the debugger, which nobody
                // running the installed build ever sees: the operator simply found that the other
                // program had stopped feeding him.
                Log.Swallow(ex);

                if (_isShutdownCleanupDone || listener == null || listener.Client == null) return;

                // A socket that only ever fails is not worth spinning on. Ten in a row and the port is
                // left closed - with a line in the log saying so, which is the part that was missing.
                if (++listener.FailuresInARow > 10)
                {
                    Log.Swallow(new Exception(
                        "UDP port " + listener.Port + " (" + listener.Name + ") stopped listening after "
                        + "ten errors in a row. Close and reopen the port in Tools to try again."));
                    return;
                }

                try { listener.Client.BeginReceive(new AsyncCallback(UdpReceive), listener); }
                catch (Exception again) { Log.Swallow(again); }
            }
        }

        // Decides what the datagram is (see the note at the top of this file) and acts on it.
        private async Task HandleUdpDatagram(byte[] datagram)
        {
            if (datagram == null || datagram.Length == 0) return;

            // WSJT-X "Status": binary, and read from the bytes. This is how HolyCluster passes a
            // selected spot over, and it is what any WSJT-X-like program sends as it works. It says
            // where that program is pointing - not a contact - so it fills the DX Callsign box and
            // nothing is stored.
            try
            {
                string dxCall;
                double freqMhz;
                if (TryParseWsjtxStatus(datagram, out dxCall, out freqMhz))
                {
                    // Act only when the highlighted station changes; ignore identical re-sends, so an
                    // F9 clear is not undone by the sender reaffirming the same selection.
                    string key = dxCall + "|" + freqMhz.ToString("0.0###", CultureInfo.InvariantCulture);
                    if (key != _lastHolyClusterSpotKey)
                    {
                        _lastHolyClusterSpotKey = key;
                        if (!_isShutdownCleanupDone && !Dispatcher.HasShutdownStarted)
                            this.Dispatcher.Invoke(() => ApplyHolyClusterSpot(dxCall, freqMhz));
                    }
                    return;
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }   // a malformed datagram is not fatal

            // UTF8 for everything else, the binary envelopes included: the bytes we care about - the
            // ADIF record inside - are plain text, and the rest becomes replacement characters that no
            // reader below matches.
            string data = Encoding.UTF8.GetString(datagram);
            if (string.IsNullOrWhiteSpace(data)) return;

            // N1MM+ telling us where its radio is - not a contact.
            if (data.IndexOf("<RadioInfo", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                ApplyN1MMRadioInfo(data);
                return;
            }

            // N1MM+ contact. Its other messages carry <app>N1MM</app> too, and the log must not be
            // filled with them, so only <contactinfo> is taken.
            if (data.IndexOf("<app>", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (data.IndexOf("<contactinfo", StringComparison.OrdinalIgnoreCase) >= 0)
                    await LogQsoFromUdp(data);
                return;
            }

            // An ADIF record, on its own or wrapped in a binary envelope (WSJT-X and its like).
            string record = ExtractAdifRecord(data);

            // Nothing recognisable: hand the whole datagram to the reader the old fixed port used, for
            // the programs that send a bare field list with no <eor> at the end.
            await LogQsoFromUdp(record ?? data);
        }

        // Cuts a single ADIF record out of a datagram: everything after an <eoh> header if there is one,
        // starting at the first real ADIF field (a tag with a length, e.g. "<call:5>"), up to the <eor>.
        // Returns null when the datagram holds no ADIF record at all, which is how the status, decode
        // and heartbeat datagrams of WSJT-X are passed over.
        private static readonly Regex AdifRecordStartRegex = new Regex(@"<[A-Za-z_][A-Za-z0-9_]*:\d", RegexOptions.Compiled);

        internal static string ExtractAdifRecord(string data)
        {
            if (string.IsNullOrEmpty(data)) return null;

            int start = 0;
            int eoh = data.IndexOf("<eoh>", StringComparison.OrdinalIgnoreCase);
            if (eoh >= 0) start = eoh + 5;

            int eor = data.IndexOf("<eor>", start, StringComparison.OrdinalIgnoreCase);
            if (eor < 0) return null;

            Match first = AdifRecordStartRegex.Match(data, start);
            if (!first.Success || first.Index >= eor) return null;

            // Without the <eor> itself, exactly as the file reader hands a record over, and with the
            // line breaks taken out so a record written over several lines reads the same.
            string record = data.Substring(first.Index, eor - first.Index);
            return record.Replace("\r", string.Empty).Replace("\n", string.Empty);
        }

        // Reads a contact sent by another program and stores it. This is the body the old fixed UDP
        // port ran, unchanged apart from the duplicate check.
        private async Task LogQsoFromUdp(string data)
        {
            _holyLogParser = new HolyLogParser();
            QSO qso = _holyLogParser.ParseRawQSO(data);
            if (qso == null) return;

            // Ask QRZ outside the dispatcher so the UI is not held up by the web call.
            string qrzName = string.Empty;
            string qrzGrid = string.Empty;
            if (string.IsNullOrWhiteSpace(qso.Name) && isNetworkAvailable)
            {
                try
                {
                    var result = await GetQrzForCall(qso.DXCall);
                    qrzName = result.Name;
                    qrzGrid = result.Grid;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"QRZ lookup failed for {qso.DXCall}: {ex.Message}");
                }
            }

            // Same again for the seconds spent waiting above: no hop onto a dispatcher that has begun
            // shutting down.
            if (_isShutdownCleanupDone || Dispatcher.HasShutdownStarted) return;
            this.Dispatcher.Invoke(() =>
            {
                try
                {
                    bool isValid = false;
                    if (!string.IsNullOrWhiteSpace(qrzName)) qso.Name = qrzName;
                    if (!string.IsNullOrWhiteSpace(qrzGrid)) qso.DXLocator = qrzGrid;

                    qso.MyCall = string.IsNullOrWhiteSpace(qso.MyCall) ? TB_MyCallsign.Text : qso.MyCall;
                    qso.Operator = string.IsNullOrWhiteSpace(qso.Operator) ? TB_Operator.Text : qso.Operator;
                    if (Properties.Settings.Default.IsOverrideOperator)
                    {
                        qso.Operator = TB_Operator.Text;
                    }

                    qso.Comment = string.IsNullOrWhiteSpace(qso.Comment) ? TB_Comment.Text : qso.Comment;
                    qso.STX = string.IsNullOrWhiteSpace(qso.STX) ? TB_MyHolyland.Text : qso.STX;

                    lock (_syncLock)
                    {
                        if (!string.IsNullOrWhiteSpace(qso.Freq))
                        {
                            qso.Band = HolyLogParser.convertFreqToBand(qso.Freq);
                        }
                        if (!string.IsNullOrWhiteSpace(qso.MyCall) && !string.IsNullOrWhiteSpace(qso.Band)
                            && !string.IsNullOrWhiteSpace(qso.Mode) && !string.IsNullOrWhiteSpace(qso.DXCall))
                        {
                            // More than one program can be forwarding the same contact (one straight
                            // from the radio program, one through a helper), so the same QSO can land
                            // on two ports. The program's one duplicate rule decides: same callsign,
                            // day, band, mode and minute is the contact we already have.
                            if (IsAlreadyInLog(qso)) return;

                            QSO q = dal.Insert(qso);
                            Qsos.Insert(0, q);
                            Properties.Settings.Default.RecentQSOCounter++;
                            isValid = true;
                            CopyLoggedQsoToTargetLog(q);
                        }
                    }
                    if (QSODataGrid.Items != null && QSODataGrid.Items.Count > 0)
                        QSODataGrid.ScrollIntoView(QSODataGrid.Items[0]);

                    if (isValid && Properties.Settings.Default.isAllowLiveLog && isRemoteServerLiveLog)
                    {
                        UploadProgress = "100%";
                        ToggleUploadProgress(Visibility.Visible);

                        // OFF THIS THREAD, AND SOMEBODY WATCHES IT. Started here, the request resolved
                        // the proxy on the window's thread; and the Task was dropped on the floor, so a
                        // failed upload said nothing at all and the progress box just shown stayed on
                        // the screen for the rest of the session.
                        var progress = new Progress<int>(percent => UploadProgress = percent.ToString() + "%");
                        var one = new ObservableCollection<QSO> { qso };
                        Task.Run(async () => await UploadLogToIARC(progress, one))
                            .ContinueWith(t =>
                            {
                                if (t.IsFaulted && t.Exception != null) Log.Swallow(t.Exception.GetBaseException());
                                ToggleUploadProgress(Visibility.Hidden);
                            }, TaskScheduler.FromCurrentSynchronizationContext());
                    }
                    UpdateNumOfQSOs();
                    RestoreDataContext();
                }
                catch (Exception ex)
                {
                    HolyMessageBox.ShowError(
                        "A contact sent by another program was NOT saved.\n\n"
                        + ex.Message + "\n\n"
                        + "That program still has it in its own log — you can add it here by hand.\n"
                        + HolyMessageBox.WhatToDo(ex.Message, null),
                        "Save Error", this);
                }
            });
        }

        // Is this contact already in the open log? Answered against the loaded list, by the same key the
        // import merge and Remove Duplicates use, so all three agree on what "the same contact" means.
        private bool IsAlreadyInLog(QSO qso)
        {
            try
            {
                string key = DataAccess.MatchKey(qso);
                if (string.IsNullOrEmpty(key)) return false;
                foreach (QSO existing in Qsos)
                {
                    if (string.Equals(DataAccess.MatchKey(existing), key, StringComparison.Ordinal))
                        return true;
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            return false;
        }

        // N1MM+'s radio broadcast: put its frequency and mode in the entry boxes. This is the reader the
        // old fixed N1MM+ port ran.
        private void ApplyN1MMRadioInfo(string data)
        {
            if (_isShutdownCleanupDone || Dispatcher.HasShutdownStarted) return;
            this.Dispatcher.Invoke(() =>
            {
                try
                {
                    Match match = N1MMTxFreqRegex.Match(data);
                    if (match.Success)
                    {
                        string freq_str = Regex.Split(data, @"<TXFreq>(.*)?<", RegexOptions.IgnoreCase)[1].Trim().ToUpper();
                        double freq = 0;
                        if (double.TryParse(freq_str, out freq))
                        {
                            // N1MM+ sends TXFreq in units of 10 Hz (e.g. 352211 = 3.52210 MHz). TB_Frequency
                            // holds MHz everywhere else (CAT / cluster setters and convertFreqToBand), so
                            // convert 10-Hz -> MHz (divide by 100000).
                            double freqMhz = freq / 100000.0;
                            TB_Frequency.Text = freqMhz.ToString("0.0#####", System.Globalization.CultureInfo.InvariantCulture);
                        }
                    }

                    match = N1MMModeRegex.Match(data);
                    if (match.Success)
                    {
                        string mode = Regex.Split(data, @"<Mode>(.*)?<", RegexOptions.IgnoreCase)[1].Trim().ToUpper();
                        if (mode == "SSB" || mode == "LSB" || mode == "USB") mode = "SSB";
                        if (mode == "RTTY" || mode == "RTTY-R" || mode == "RTTY-L" || mode == "AFSK" || mode == "AFSK-R" || mode == "AFSK-L") mode = "DIGI";
                        bool item_found = false;
                        foreach (System.Windows.Controls.ComboBoxItem item in CB_Mode.Items)
                        {
                            if ((string)item.Content == mode)
                            {
                                CB_Mode.Text = (string)item.Content;
                                CB_Mode.SelectedItem = item;
                                item_found = true;
                                break;
                            }
                        }
                        if (!item_found)
                        {
                            CB_Mode.SelectedIndex = 0;
                        }
                    }
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            });
        }
    }
}
