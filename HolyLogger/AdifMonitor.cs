using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Newtonsoft.Json;

namespace HolyLogger
{
    // ONE ROW OF THE ADIF MONITOR TABLE.
    //
    // Some programs (WSJT-X, JTDX, MSHV...) write every contact to an ADIF file of their own. A file
    // on this list is watched: whatever is added to it is added to the active log, both while
    // HolyLogger runs and - at the next start - whatever was written while it was closed.
    //
    // Position is how far into the file HolyLogger has already read, in BYTES. Each check jumps
    // straight there and reads only what was added after it, so a file with years of contacts in it
    // is not read again from the start every few seconds.
    public class AdifMonitorEntry : INotifyPropertyChanged
    {
        private bool _isOn = true;
        public bool IsOn
        {
            get => _isOn;
            set { if (_isOn != value) { _isOn = value; Raise(nameof(IsOn)); } }
        }

        private string _path = "";
        public string Path
        {
            get => _path;
            set { if (_path != value) { _path = value; Raise(nameof(Path)); } }
        }

        // 0 for a file just added: the whole file is read once, and the contacts the log already has
        // (the ones that also came in over UDP, say) are passed over by the duplicate rule.
        public long Position { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;
        private void Raise(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    // Where the list lives: one JSON string in the settings, like the UDP Ports table.
    public static class AdifMonitorStore
    {
        public static List<AdifMonitorEntry> Load()
        {
            try
            {
                string json = Properties.Settings.Default.AdifMonitorJson;
                if (!string.IsNullOrWhiteSpace(json))
                    return JsonConvert.DeserializeObject<List<AdifMonitorEntry>>(json) ?? new List<AdifMonitorEntry>();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            return new List<AdifMonitorEntry>();
        }

        // The monitor moves Position forward while the Manager window is open, and that window holds
        // the positions as they were when it opened. So the further of the two is kept - otherwise
        // pressing Save would send the reader back over contacts it has already handled.
        public static void Save(IEnumerable<AdifMonitorEntry> rows)
        {
            try
            {
                var saved = Load();
                var toSave = new List<AdifMonitorEntry>();
                foreach (var row in rows ?? Enumerable.Empty<AdifMonitorEntry>())
                {
                    if (row == null || string.IsNullOrWhiteSpace(row.Path)) continue;
                    if (toSave.Any(r => SamePath(r.Path, row.Path))) continue;
                    var old = saved.FirstOrDefault(r => SamePath(r.Path, row.Path));
                    toSave.Add(new AdifMonitorEntry
                    {
                        IsOn = row.IsOn,
                        Path = row.Path.Trim(),
                        Position = Math.Max(row.Position, old == null ? 0 : old.Position)
                    });
                }
                Properties.Settings.Default.AdifMonitorJson = JsonConvert.SerializeObject(toSave);
                Properties.Settings.Default.Save();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // Called by the monitor after each read: only the position of that one file changes.
        public static void SavePosition(string path, long position)
        {
            try
            {
                var rows = Load();
                var row = rows.FirstOrDefault(r => SamePath(r.Path, path));
                if (row == null || row.Position == position) return;
                row.Position = position;
                Properties.Settings.Default.AdifMonitorJson = JsonConvert.SerializeObject(rows);
                Properties.Settings.Default.Save();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        public static bool SamePath(string a, string b)
        {
            return string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
