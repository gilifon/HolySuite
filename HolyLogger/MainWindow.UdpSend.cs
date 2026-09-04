using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using HolyParser;

namespace HolyLogger
{
    // SENDING TO OTHER PROGRAMS - the other half of the UDP Ports Manager.
    //
    // A port HolyLogger LISTENS on can work out for itself what arrived (MainWindow.Udp.cs). A port it
    // SENDS on cannot: there is nothing to examine, so each broadcast line says which format to write
    // and where to send it.
    //
    // What goes out, and when:
    //
    //   ADIF           One ADIF record for the contact, as plain text - the same writer the ADIF
    //                  export uses, so a program reading it sees exactly what a HolyLogger export
    //                  would give it. Sent when a contact is logged.
    //   WSJT-X ADIF    That same record inside the WSJT-X "Logged ADIF" datagram (magic, schema,
    //                  type 12, sender name, ADIF text). Programs written for WSJT-X - JTAlert,
    //                  GridTracker, Log4OM's WSJT-X listener - want this envelope and ignore a bare
    //                  ADIF line. Sent when a contact is logged.
    //   N1MM+ XML      A <contactinfo> document. The tags are the ones HolyLogger's own N1MM+ reader
    //                  looks for (HolyLogParser.ParseN1MMRawQSO): app, call, mycall, operator, band,
    //                  mode, snt, rcv, timestamp - so what we send, we can also read back. NO
    //                  FREQUENCY TAG: N1MM+'s own frequency unit for a contact is not something this
    //                  program can verify, and a wrong frequency is worse than none. Band and mode
    //                  are there. Sent when a contact is logged.
    //   Radio status   Not a contact: where the radio is pointing and what is in the DX Callsign box,
    //                  as the WSJT-X "Status" datagram (type 1) - the same message HolyCluster sends
    //                  US. Sent as you tune, at most once a second, and only when something changed.
    //                  Written as far as the DX grid; a reader wanting the fields after that (the
    //                  transmit flags, sub-mode, and so on) will not find them.
    //
    // NOTHING IS EVER SENT UNPROMPTED: a line has to be ticked in the table.
    public partial class MainWindow
    {
        // One socket for everything we send. UDP needs no connection, so the destination goes with
        // each Send and one socket serves every line.
        private UdpClient _udpSender;

        // The broadcast table, read once and kept - the radio status is offered every second, and
        // deserialising the settings that often would be silly. ApplyUdpListeners refreshes it, so a
        // change made in the manager takes effect when the Options window closes.
        private List<UdpBroadcastEntry> _broadcastLines = new List<UdpBroadcastEntry>();

        // What was last sent as radio status, so an unchanged radio sends nothing.
        private string _lastRadioStatusKey;

        internal void ReloadBroadcastLines()
        {
            try { _broadcastLines = UdpBroadcastStore.Load(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); _broadcastLines = new List<UdpBroadcastEntry>(); }
        }

        internal void CloseUdpSender()
        {
            try
            {
                if (_udpSender != null)
                {
                    var sender = _udpSender;
                    _udpSender = null;
                    sender.Close();
                    ((IDisposable)sender).Dispose();
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // A contact has just been stored. Called from both places that add one: the Add button and a
        // contact that arrived over UDP.
        internal void BroadcastQsoLogged(QSO qso)
        {
            if (qso == null) return;

            var lines = _broadcastLines
                .Where(l => l != null && l.IsOn && l.PortNumber > 0 && !l.IsRadioStatus)
                .ToList();
            if (lines.Count == 0) return;

            // Each format is built at most once, however many lines want it.
            string adif = null;
            byte[] adifBytes = null, wsjtxBytes = null, n1mmBytes = null;

            foreach (var line in lines)
            {
                try
                {
                    byte[] payload;
                    if (line.Format == UdpBroadcastEntry.FormatN1mmXml)
                    {
                        if (n1mmBytes == null) n1mmBytes = Encoding.UTF8.GetBytes(BuildN1mmContactInfo(qso));
                        payload = n1mmBytes;
                    }
                    else
                    {
                        if (adif == null) adif = BuildAdifRecord(qso);
                        if (adif == null) return;   // nothing writable in this contact

                        if (line.Format == UdpBroadcastEntry.FormatWsjtxAdif)
                        {
                            if (wsjtxBytes == null) wsjtxBytes = BuildWsjtxLoggedAdif(adif);
                            payload = wsjtxBytes;
                        }
                        else
                        {
                            if (adifBytes == null) adifBytes = Encoding.UTF8.GetBytes(adif);
                            payload = adifBytes;
                        }
                    }

                    SendUdp(line, payload);
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }
        }

        // Offered once a second by the UTC timer. Sends only when the radio has actually moved, so a
        // station sitting on one frequency puts nothing on the network.
        internal void BroadcastRadioStatusIfChanged()
        {
            try
            {
                var lines = _broadcastLines
                    .Where(l => l != null && l.IsOn && l.PortNumber > 0 && l.IsRadioStatus)
                    .ToList();
                if (lines.Count == 0) return;

                string freqText = (TB_Frequency == null ? string.Empty : TB_Frequency.Text ?? string.Empty).Trim();
                string mode = (CB_Mode == null ? string.Empty : CB_Mode.Text ?? string.Empty).Trim();
                string dxCall = (TB_DXCallsign == null ? string.Empty : TB_DXCallsign.Text ?? string.Empty).Trim();

                string key = freqText + "|" + mode + "|" + dxCall;
                if (key == _lastRadioStatusKey) return;
                _lastRadioStatusKey = key;

                double freqMhz;
                if (!double.TryParse(freqText, NumberStyles.Float, CultureInfo.InvariantCulture, out freqMhz))
                    freqMhz = 0;

                byte[] payload = BuildWsjtxStatus((ulong)Math.Round(freqMhz * 1000000.0), mode, dxCall);
                foreach (var line in lines) SendUdp(line, payload);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        private void SendUdp(UdpBroadcastEntry line, byte[] payload)
        {
            if (payload == null || payload.Length == 0) return;
            try
            {
                if (_udpSender == null) _udpSender = new UdpClient();

                // No address written means this same PC, which is what nearly every line is.
                string host = (line.Address ?? string.Empty).Trim();
                if (host.Length == 0) host = "127.0.0.1";

                _udpSender.Send(payload, payload.Length, host, line.PortNumber);
            }
            catch (Exception swallowed)
            {
                // A bad address or an unreachable machine must never interrupt logging a contact.
                Log.Swallow(swallowed);
            }
        }

        // ── THE FORMATS ─────────────────────────────────────────────────────────────────────────

        // The program's own ADIF writer, for this one contact. Header and all: a receiver that reads
        // ADIF handles the header, and the program's own reader skips past <eoh>.
        private static string BuildAdifRecord(QSO qso)
        {
            try { return Services.GenerateAdif(new List<QSO> { qso }); }
            catch (Exception swallowed) { Log.Swallow(swallowed); return null; }
        }

        // WSJT-X "Logged ADIF" (type 12): magic, schema, type, who is sending, then the ADIF text.
        private static byte[] BuildWsjtxLoggedAdif(string adif)
        {
            var body = new List<byte>(adif.Length + 64);
            WriteUInt32BE(body, 0xADBCCBDA);
            WriteUInt32BE(body, 2);            // schema
            WriteUInt32BE(body, 12);           // Logged ADIF
            WriteUtf8(body, "HolyLogger");     // Id - who is sending
            WriteUtf8(body, adif);
            return body.ToArray();
        }

        // WSJT-X "Status" (type 1), written as far as the DX grid - see the note at the top of this
        // file. The field order matches the reader in MainWindow.HolyClusterUdp.cs, which is what
        // HolyCluster sends us.
        private byte[] BuildWsjtxStatus(ulong dialHz, string mode, string dxCall)
        {
            var body = new List<byte>(128);
            WriteUInt32BE(body, 0xADBCCBDA);
            WriteUInt32BE(body, 2);            // schema
            WriteUInt32BE(body, 1);            // Status
            WriteUtf8(body, "HolyLogger");     // Id
            WriteUInt64BE(body, dialHz);       // Dial frequency, Hz
            WriteUtf8(body, mode ?? string.Empty);
            WriteUtf8(body, dxCall ?? string.Empty);
            WriteUtf8(body, string.Empty);     // Report
            WriteUtf8(body, mode ?? string.Empty);   // Tx mode
            body.Add(0);                       // Tx enabled
            body.Add(0);                       // Transmitting
            body.Add(0);                       // Decoding
            WriteUInt32BE(body, 0);            // Rx DF
            WriteUInt32BE(body, 0);            // Tx DF
            WriteUtf8(body, TB_MyCallsign == null ? string.Empty : (TB_MyCallsign.Text ?? string.Empty).Trim());
            WriteUtf8(body, TB_MyLocator == null ? string.Empty : (TB_MyLocator.Text ?? string.Empty).Trim());
            WriteUtf8(body, TB_DXLocator == null ? string.Empty : (TB_DXLocator.Text ?? string.Empty).Trim());
            return body.ToArray();
        }

        // N1MM+'s <contactinfo>. The tag names are the ones HolyLogParser.ParseN1MMRawQSO reads, so a
        // second copy of HolyLogger listening on that port stores exactly this contact.
        private static string BuildN1mmContactInfo(QSO qso)
        {
            var xml = new StringBuilder(400);
            xml.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            xml.Append("<contactinfo>");
            xml.Append("<app>N1MM</app>");
            xml.Append("<timestamp>").Append(Xml(N1mmTimestamp(qso))).Append("</timestamp>");
            xml.Append("<mycall>").Append(Xml(qso.MyCall)).Append("</mycall>");
            xml.Append("<operator>").Append(Xml(string.IsNullOrWhiteSpace(qso.Operator) ? qso.MyCall : qso.Operator)).Append("</operator>");
            xml.Append("<call>").Append(Xml(qso.DXCall)).Append("</call>");
            xml.Append("<band>").Append(Xml(qso.Band)).Append("</band>");
            xml.Append("<mode>").Append(Xml(qso.Mode)).Append("</mode>");
            xml.Append("<snt>").Append(Xml(qso.RST_SENT)).Append("</snt>");
            xml.Append("<rcv>").Append(Xml(qso.RST_RCVD)).Append("</rcv>");
            if (!string.IsNullOrWhiteSpace(qso.DXLocator))
                xml.Append("<gridsquare>").Append(Xml(qso.DXLocator)).Append("</gridsquare>");
            if (!string.IsNullOrWhiteSpace(qso.Name))
                xml.Append("<name>").Append(Xml(qso.Name)).Append("</name>");
            if (!string.IsNullOrWhiteSpace(qso.Comment))
                xml.Append("<comment>").Append(Xml(qso.Comment)).Append("</comment>");
            xml.Append("</contactinfo>");
            return xml.ToString();
        }

        // "yyyy-MM-dd HH:mm:ss" - the shape ParseN1MMRawQSO splits on a space and strips the dashes
        // and colons from. The log's own date and time are used when they can be read; otherwise now.
        private static string N1mmTimestamp(QSO qso)
        {
            string date = Digits(qso.Date);
            string time = Digits(qso.Time);
            if (date.Length >= 8)
            {
                if (time.Length < 6) time = (time + "000000").Substring(0, 6);
                return date.Substring(0, 4) + "-" + date.Substring(4, 2) + "-" + date.Substring(6, 2)
                     + " " + time.Substring(0, 2) + ":" + time.Substring(2, 2) + ":" + time.Substring(4, 2);
            }
            return DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        private static string Digits(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var sb = new StringBuilder(text.Length);
            foreach (char c in text) if (char.IsDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        private static string Xml(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        // ── QDataStream, the way WSJT-X writes it: big-endian, and a string is its byte count then
        //    its UTF-8 bytes. These are the mirror of the readers in MainWindow.HolyClusterUdp.cs.

        private static void WriteUInt32BE(List<byte> to, uint value)
        {
            to.Add((byte)(value >> 24));
            to.Add((byte)(value >> 16));
            to.Add((byte)(value >> 8));
            to.Add((byte)value);
        }

        private static void WriteUInt64BE(List<byte> to, ulong value)
        {
            WriteUInt32BE(to, (uint)(value >> 32));
            WriteUInt32BE(to, (uint)value);
        }

        private static void WriteUtf8(List<byte> to, string text)
        {
            if (text == null) { WriteUInt32BE(to, 0xFFFFFFFF); return; }   // QDataStream's null string
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            WriteUInt32BE(to, (uint)bytes.Length);
            to.AddRange(bytes);
        }
    }
}
