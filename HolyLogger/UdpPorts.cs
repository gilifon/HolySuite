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

    // Where the table lives: one JSON string in the settings, like the Channels window's list.
    public static class UdpPortStore
    {
        // Reads the table. An operator upgrading from a version that had the two fixed ports has
        // nothing saved here yet, so his two ports - and whether they were switched on - are handed
        // back as the first two rows. They are only written to UdpPortsJson when he presses Save, so
        // an upgrade that is never opened keeps working exactly as it did.
        public static List<UdpPortEntry> Load()
        {
            try
            {
                string json = Properties.Settings.Default.UdpPortsJson;
                if (!string.IsNullOrWhiteSpace(json))
                    return JsonConvert.DeserializeObject<List<UdpPortEntry>>(json) ?? new List<UdpPortEntry>();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            var s = Properties.Settings.Default;
            return new List<UdpPortEntry>
            {
                new UdpPortEntry { IsOn = s.EnableUDPClient,     Name = "Other software", Port = s.UDPPort.ToString(CultureInfo.InvariantCulture) },
                new UdpPortEntry { IsOn = s.EnableN1MMUDPClient, Name = "N1MM+",          Port = s.N1MMUDPPort.ToString(CultureInfo.InvariantCulture) },
            };
        }

        // Writes the table. Wholly empty rows (the blank one at the bottom, or an abandoned entry)
        // are dropped.
        public static void Save(IEnumerable<UdpPortEntry> rows)
        {
            try
            {
                var toSave = (rows ?? Enumerable.Empty<UdpPortEntry>()).Where(r => r != null && r.IsFilled).ToList();
                Properties.Settings.Default.UdpPortsJson = JsonConvert.SerializeObject(toSave);
                Properties.Settings.Default.Save();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }
    }
}
