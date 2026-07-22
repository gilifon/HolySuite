using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;

namespace HolyLogger
{
    // HolyCluster spot listener.
    //
    // HolyCluster's CAT server, when you select (highlight) a spot, sends a WSJT-X-format UDP
    // "Status" message to 127.0.0.1:<port> — the very same datagram it sends to Log4OM so the
    // logger can pre-fill the DX callsign. We open a UDP socket on that port and catch it:
    //   - the DX callsign always goes into TB_DXCallsign (a fresh spot replaces whatever was there),
    //     via .Text so the normal callbook-lookup / suggestions pipeline runs as if it were typed;
    //   - the frequency is filled ONLY when CAT is not driving the radio. When OmniRig is online the
    //     rig owns the frequency; when it isn't, we drop the spot's frequency into the entry form.
    //
    // Wire format (WSJT-X UDP protocol, big-endian, QDataStream): magic 0xADBCCBDA, schema (u32),
    // type (u32; Status = 1), then Id (utf8), Dial Frequency in Hz (u64), Mode (utf8), DX call (utf8),
    // ... (further fields we don't need). Strings are a u32 byte-count followed by UTF-8; a count of
    // 0xFFFFFFFF denotes a null string.
    public partial class MainWindow
    {
        public static UdpClient HolyClusterClient;

        private const uint WsjtxMagic = 0xADBCCBDA;
        private const uint WsjtxStatusType = 1;

        // The last spot we acted on (callsign|frequency). HolyCluster re-sends the SAME status for the
        // currently-selected spot every time its UI refreshes (new spots arrive every few seconds), not
        // only when you click. We act only when this key changes, so clearing the form (F9) isn't undone
        // a few seconds later by an identical re-send.
        private string _lastHolyClusterSpotKey;

        // The exact DX callsign of the station last selected in HolyCluster. HolyCluster tunes the radio
        // onto the spot, which also triggers HolyLogger's own frequency-based on-frequency auto-fill; that
        // heuristic would otherwise pick a DIFFERENT spot sharing the frequency. The cluster on-frequency
        // code (MainWindow.Cluster.cs) holds this call in the DX box — never overwritten or cleared by
        // the frequency guess — until the radio moves off the spot's frequency or the operator acts.
        private string _holyClusterSelectedCall;

        // The selected spot's frequency (MHz), used to release the hold above once the radio tunes away.
        private double _holyClusterSelectedFreqMhz;

        // Whether the radio has actually reached the selected spot's frequency since the hold was set.
        // The hold is only released by tuning AWAY after this becomes true, so a stale-frequency recompute
        // during the brief CAT slew (or before HolyCluster tunes at all) can't drop the selection early.
        private bool _holyClusterReachedFreq;

        // When the selection arrived, so the hold above can be given up if the radio never reaches that
        // frequency at all (see SuspensionTimeoutSeconds in MainWindow.Cluster.cs).
        private DateTime _holyClusterSelectedAtUtc = DateTime.UtcNow;

        // Open or close the listener to match the current setting. Safe to call repeatedly (startup,
        // and whenever the options are applied). Any existing listener is torn down first, so a changed
        // port takes effect immediately on Apply.
        private void ApplyHolyClusterListener()
        {
            if (HolyClusterClient != null)
            {
                try { HolyClusterClient.Close(); } catch (Exception swallowed) { Log.Swallow(swallowed); }
                HolyClusterClient = null;
            }

            if (!Properties.Settings.Default.EnableHolyClusterUDP) return;

            // Fresh listener: forget the last spot so re-enabling lets the same station fill again.
            _lastHolyClusterSpotKey = null;

            try
            {
                HolyClusterClient = new UdpClient(Properties.Settings.Default.HolyClusterUDPPort);
                HolyClusterClient.BeginReceive(new AsyncCallback(StartHolyClusterUDPClient), null);
            }
            catch
            {
                HolyMessageBox.ShowWarning("Failed to open the HolyCluster UDP port.", "HolyCluster Listener", this);
                Properties.Settings.Default.EnableHolyClusterUDP = false;
                HolyClusterClient = null;
            }
        }

        private void StartHolyClusterUDPClient(IAsyncResult res)
        {
            if (HolyClusterClient == null) return;

            byte[] datagram;
            IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            try
            {
                datagram = HolyClusterClient.EndReceive(res, ref remote);
            }
            catch (ObjectDisposedException) { return; }   // socket closed (shutdown / disabled / port change)
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("HolyCluster UDP receive error: " + ex.Message);
                return;
            }

            // Parse + apply, but never let a malformed packet kill the receive loop.
            try
            {
                if (Properties.Settings.Default.EnableHolyClusterUDP
                    && TryParseWsjtxStatus(datagram, out string dxCall, out double freqMhz))
                {
                    // Act only when the highlighted station changes; ignore identical re-sends so an
                    // F9 clear isn't undone by HolyCluster reaffirming the same selection.
                    string key = dxCall + "|" + freqMhz.ToString("0.0###", CultureInfo.InvariantCulture);
                    if (key != _lastHolyClusterSpotKey)
                    {
                        _lastHolyClusterSpotKey = key;
                        this.Dispatcher.Invoke(() => ApplyHolyClusterSpot(dxCall, freqMhz));
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("HolyCluster UDP parse error: " + ex.Message);
            }

            // Re-arm for the next datagram (unless the client was torn down meanwhile).
            try
            {
                HolyClusterClient?.BeginReceive(new AsyncCallback(StartHolyClusterUDPClient), null);
            }
            catch (ObjectDisposedException) { /* closed during teardown - expected */ }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("HolyCluster UDP re-arm error: " + ex.Message);
            }
        }

        // Runs on the UI thread. Fills the DX callsign always; the frequency only when CAT is not live.
        private void ApplyHolyClusterSpot(string dxCall, double freqMhz)
        {
            try
            {
                string call = (dxCall ?? string.Empty).Trim().ToUpperInvariant();

                // Record the exact clicked call (and its frequency) so the frequency-based on-frequency
                // auto-fill holds it and doesn't overwrite it with another spot (see MainWindow.Cluster.cs).
                _holyClusterSelectedCall = string.IsNullOrWhiteSpace(call) ? null : call;
                _holyClusterSelectedFreqMhz = freqMhz;
                _holyClusterReachedFreq = false;   // wait until the radio actually lands on this frequency
                _holyClusterSelectedAtUtc = DateTime.UtcNow;   // ...but not forever (see SuspensionTimeoutSeconds)

                if (!string.IsNullOrWhiteSpace(call) && TB_DXCallsign != null)
                {
                    // Fill it ourselves too — this is the only fill path when CAT is off and the
                    // on-frequency feature can't run. Mirror the cluster auto-fill: suppress the
                    // "focused edit = manual change" handling and the suggestions dropdown, and mark it
                    // cluster-auto-filled so leaving the frequency (F9) clears it like any cluster fill.
                    _clusterFillingDXCall = true;
                    try
                    {
                        suppressNextCallsignSuggestions = true;
                        TB_DXCallsign.Text = call;
                        TB_DXCallsign.CaretIndex = TB_DXCallsign.Text.Length;
                        _clusterAutoFilledDXCall = true;
                        // Record WHERE it was filled, exactly as the double-click path does. Without
                        // this the "is that station still on the radio's frequency?" rule in
                        // UpdateClusterFrequencyHighlight would judge this call against whatever
                        // frequency some earlier fill had left behind. Reached=false because CAT may
                        // still be slewing to the spot.
                        _clusterAutoFilledFreqMhz = freqMhz;
                        _clusterAutoFilledReached = false;
                        _clusterAutoFilledAtUtc = DateTime.UtcNow;
                        TB_DXCallsign_TextChanged(TB_DXCallsign, null);
                        TB_DXCallsign_LostFocus(TB_DXCallsign, new RoutedEventArgs());
                    }
                    finally { _clusterFillingDXCall = false; }
                }

                // CAT is "live" only when OmniRig is enabled AND the selected rig is actually online —
                // the same test the rig code uses. When it is, the radio owns the frequency, so we
                // leave it alone; otherwise we fill the spot's frequency.
                bool catLive = Properties.Settings.Default.EnableOmniRigCAT
                               && Rig != null
                               && Rig.Status == OmniRig.RigStatusX.ST_ONLINE;
                if (!catLive && freqMhz > 0 && TB_Frequency != null)
                {
                    // TB_Frequency holds the canonical value in MHz; writing it flows through
                    // UpdateFreqLed -> FillFreqNoCatFromFrequency into the visible no-CAT box.
                    TB_Frequency.Text = freqMhz.ToString("0.0###", CultureInfo.InvariantCulture);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("ApplyHolyClusterSpot error: " + ex.Message);
            }
        }

        // Parses a WSJT-X Status datagram, extracting the DX callsign and dial frequency (as MHz).
        // Returns false for anything that isn't a Status message with a callsign.
        private static bool TryParseWsjtxStatus(byte[] data, out string dxCall, out double freqMhz)
        {
            dxCall = null;
            freqMhz = 0;
            if (data == null || data.Length < 12) return false;

            int pos = 0;
            uint magic = ReadUInt32BE(data, ref pos);
            if (magic != WsjtxMagic) return false;
            ReadUInt32BE(data, ref pos);                 // schema version - accept any
            uint type = ReadUInt32BE(data, ref pos);
            if (type != WsjtxStatusType) return false;   // only the Status message carries the highlighted spot

            ReadUtf8String(data, ref pos);               // Id (e.g. "WSJT-X") - skip
            ulong dialHz = ReadUInt64BE(data, ref pos);  // Dial Frequency (Hz)
            ReadUtf8String(data, ref pos);               // Mode - skip (we leave the logger's mode alone)
            dxCall = ReadUtf8String(data, ref pos);      // DX call - what we want

            freqMhz = dialHz / 1000000.0;
            return !string.IsNullOrWhiteSpace(dxCall);
        }

        private static uint ReadUInt32BE(byte[] d, ref int p)
        {
            if (p + 4 > d.Length) throw new FormatException("WSJT-X datagram truncated (u32).");
            uint v = ((uint)d[p] << 24) | ((uint)d[p + 1] << 16) | ((uint)d[p + 2] << 8) | d[p + 3];
            p += 4;
            return v;
        }

        private static ulong ReadUInt64BE(byte[] d, ref int p)
        {
            ulong hi = ReadUInt32BE(d, ref p);
            ulong lo = ReadUInt32BE(d, ref p);
            return (hi << 32) | lo;
        }

        private static string ReadUtf8String(byte[] d, ref int p)
        {
            uint size = ReadUInt32BE(d, ref p);
            if (size == 0xFFFFFFFF) return string.Empty;   // QDataStream null string
            if (size > (uint)(d.Length - p)) throw new FormatException("WSJT-X string length overruns datagram.");
            string s = Encoding.UTF8.GetString(d, p, (int)size);
            p += (int)size;
            return s;
        }
    }
}
