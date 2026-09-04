using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;

namespace HolyLogger
{
    // ONE ROW OF THE UDP PORTS TABLE.
    //
    // The program used to have two fixed UDP ports wired into the Options page - one that logged a
    // contact sent by another program, one that followed N1MM+'s radio - and no way to add a third.
    // Operators run more than two programs, so the pair became a table the operator writes himself:
    // a name of his choosing, the port, and whether it is open.
    //
    // The name is a LABEL ONLY. Nothing is decided by it: every open port accepts every format the
    // program can read, and the datagram itself says which one it is (see MainWindow.Udp.cs).
    public class UdpPortEntry : INotifyPropertyChanged
    {
        private bool _isOn;
        public bool IsOn
        {
            get => _isOn;
            set { if (_isOn != value) { _isOn = value; Raise(nameof(IsOn)); } }
        }

        private string _name = "";
        public string Name
        {
            get => _name;
            set { if (_name != value) { _name = value; Raise(nameof(Name)); Raise(nameof(IsFilled)); } }
        }

        // Held as text, not a number: the cell is being typed into, and a half-typed port ("23") must
        // not be turned into anything. PortNumber below is the reading of it.
        private string _port = "";
        public string Port
        {
            get => _port;
            set { if (_port != value) { _port = value; Raise(nameof(Port)); Raise(nameof(IsFilled)); } }
        }

        // True once the row has any content. Drives the blank row that always waits at the bottom of
        // the table, the same way the Channels window does it. Not persisted.
        [JsonIgnore]
        public bool IsFilled => !string.IsNullOrWhiteSpace(Name) || !string.IsNullOrWhiteSpace(Port);

        // The port as a number, or 0 when the cell holds nothing usable. Ports below 1024 are the
        // system's own and are not refused here - a few programs do use them - but 0 and anything
        // above 65535 cannot be opened at all.
        [JsonIgnore]
        public int PortNumber
        {
            get
            {
                int p;
                if (int.TryParse((Port ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out p)
                    && p > 0 && p <= 65535)
                    return p;
                return 0;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void Raise(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    // ONE ROW OF THE BROADCAST TABLE - the other direction.
    //
    // A line HolyLogger listens on can work out for itself what arrived. A line it SENDS on cannot:
    // nothing is there to be examined, so the operator says which format to write, and where to send
    // it. Hence the two extra columns that a receiving line does not have.
    public class UdpBroadcastEntry : INotifyPropertyChanged
    {
        // What can be written. The text is what the operator picks in the Format column, so changing
        // one of these strings changes what is stored in old settings too - don't.
        public const string FormatAdif = "ADIF";
        public const string FormatWsjtxAdif = "WSJT-X ADIF";
        public const string FormatN1mmXml = "N1MM+ XML";
        public const string FormatRadioStatus = "Radio status";

        public static readonly string[] Formats =
        {
            FormatAdif, FormatWsjtxAdif, FormatN1mmXml, FormatRadioStatus
        };

        private bool _isOn;
        public bool IsOn
        {
            get => _isOn;
            set { if (_isOn != value) { _isOn = value; Raise(nameof(IsOn)); } }
        }

        private string _name = "";
        public string Name
        {
            get => _name;
            set { if (_name != value) { _name = value; Raise(nameof(Name)); Raise(nameof(IsFilled)); } }
        }

        // Where to send it: a name or an address. 127.0.0.1 is a program on this same PC, which is
        // what nearly every line will be.
        // A new line starts at this PC, because that is what nearly every line is - and it shows the
        // operator the shape of the thing without a word of explanation.
        private string _address = "127.0.0.1";
        public string Address
        {
            get => _address;
            set { if (_address != value) { _address = value; Raise(nameof(Address)); Raise(nameof(IsFilled)); } }
        }

        private string _port = "";
        public string Port
        {
            get => _port;
            set { if (_port != value) { _port = value; Raise(nameof(Port)); Raise(nameof(IsFilled)); } }
        }

        private string _format = FormatAdif;
        public string Format
        {
            get => _format;
            set { if (_format != value) { _format = value; Raise(nameof(Format)); Raise(nameof(IsFilled)); } }
        }

        // NOT the address: a new line comes with 127.0.0.1 already in it, so counting that as content
        // would make the blank row at the bottom look filled and breed another blank row for ever.
        [JsonIgnore]
        public bool IsFilled => !string.IsNullOrWhiteSpace(Name)
                             || !string.IsNullOrWhiteSpace(Port);

        [JsonIgnore]
        public int PortNumber
        {
            get
            {
                int p;
                if (int.TryParse((Port ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out p)
                    && p > 0 && p <= 65535)
                    return p;
                return 0;
            }
        }

        // Radio status goes out as the operator tunes; everything else goes out when a contact is
        // logged. Which one a line is decides when it is asked for anything.
        [JsonIgnore]
        public bool IsRadioStatus => string.Equals(Format, FormatRadioStatus, StringComparison.Ordinal);

        public event PropertyChangedEventHandler PropertyChanged;
        private void Raise(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    public static class UdpBroadcastStore
    {
        public static List<UdpBroadcastEntry> Load()
        {
            try
            {
                string json = Properties.Settings.Default.UdpBroadcastJson;
                if (!string.IsNullOrWhiteSpace(json))
                    return JsonConvert.DeserializeObject<List<UdpBroadcastEntry>>(json) ?? new List<UdpBroadcastEntry>();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            return new List<UdpBroadcastEntry>();
        }

        public static void Save(IEnumerable<UdpBroadcastEntry> rows)
        {
            try
            {
                var toSave = (rows ?? Enumerable.Empty<UdpBroadcastEntry>()).Where(r => r != null && r.IsFilled).ToList();
                Properties.Settings.Default.UdpBroadcastJson = JsonConvert.SerializeObject(toSave);
                Properties.Settings.Default.Save();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }
    }

    // Where the table lives: one JSON string in the settings, like the Channels window's list.
    public static class UdpPortStore
    {
        // Reads the table. An operator upgrading from a version that had the two fixed ports has
        // nothing saved here yet, so his two ports - and whether they were switched on - are handed
        // back as the first two rows. They are only written to UdpPortsJson when he presses Save, so
        // an upgrade that is never opened keeps working exactly as it did.
        public static List<UdpPortEntry> Load()
        {
            var rows = new List<UdpPortEntry>();
            bool hadSavedTable = false;
            try
            {
                string json = Properties.Settings.Default.UdpPortsJson;
                if (!string.IsNullOrWhiteSpace(json))
                {
                    rows = JsonConvert.DeserializeObject<List<UdpPortEntry>>(json) ?? new List<UdpPortEntry>();
                    hadSavedTable = true;
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            var s = Properties.Settings.Default;

            if (!hadSavedTable)
            {
                rows.Add(new UdpPortEntry { IsOn = s.EnableUDPClient,     Name = "Other software", Port = s.UDPPort.ToString(CultureInfo.InvariantCulture) });
                rows.Add(new UdpPortEntry { IsOn = s.EnableN1MMUDPClient, Name = "N1MM+",          Port = s.N1MMUDPPort.ToString(CultureInfo.InvariantCulture) });
            }

            // The HolyCluster port had its own box on the Options page and joined this table later, so
            // it is brought over even for an operator who had already saved a table without it. Save()
            // sets the old port to 0, which is what says "already brought over" - so a line he then
            // deletes stays deleted instead of reappearing at every start.
            if (s.HolyClusterUDPPort > 0)
                rows.Add(new UdpPortEntry { IsOn = s.EnableHolyClusterUDP, Name = "HolyCluster", Port = s.HolyClusterUDPPort.ToString(CultureInfo.InvariantCulture) });

            return rows;
        }

        // Writes the table. Wholly empty rows (the blank one at the bottom, or an abandoned entry)
        // are dropped.
        public static void Save(IEnumerable<UdpPortEntry> rows)
        {
            try
            {
                var toSave = (rows ?? Enumerable.Empty<UdpPortEntry>()).Where(r => r != null && r.IsFilled).ToList();
                Properties.Settings.Default.UdpPortsJson = JsonConvert.SerializeObject(toSave);

                // The HolyCluster port now lives in this table like any other line. Zeroing the old
                // setting is what stops Load() from adding it a second time (see the note there).
                Properties.Settings.Default.HolyClusterUDPPort = 0;
                Properties.Settings.Default.EnableHolyClusterUDP = false;

                Properties.Settings.Default.Save();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }
    }
}
