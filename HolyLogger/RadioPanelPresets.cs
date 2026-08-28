using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace HolyLogger
{
    /// <summary>
    /// One band button of the Radio Control Panel: the band it stands for, and the two frequencies
    /// the radio is sent to when it is pressed - one for SSB, one for CW.
    /// </summary>
    public class RadioBandPreset
    {
        public string Label { get; set; }      // what the button says, e.g. "14"
        public string Name { get; set; }       // the band in metres, e.g. "20m" - shown under the label
        public int LowKhz { get; set; }        // band edges, used ONLY to light the button that the
        public int HighKhz { get; set; }       // radio's current frequency falls inside
        public int SsbKhz { get; set; }
        public int CwKhz { get; set; }

        public bool Contains(double khz)
        {
            return khz >= LowKhz && khz <= HighKhz;
        }

        public int FrequencyFor(string mode)
        {
            return string.Equals(mode, "CW", StringComparison.OrdinalIgnoreCase) ? CwKhz : SsbKhz;
        }
    }

    /// <summary>
    /// The ten band buttons of the Radio Control Panel, and the two frequencies behind each of them.
    ///
    /// The band list itself is FIXED (the panel has ten band buttons and no more); only the two
    /// frequencies per band are the operator's to change, on Options > Radio Control Panel. They are
    /// kept in one settings string rather than twenty settings, so adding or renaming a band later is
    /// one edit here instead of twenty in Settings.settings.
    /// </summary>
    public static class RadioPanelPresets
    {
        // Region 1 calling / commonly used frequencies. 10 MHz and 18 MHz have no SSB by band plan;
        // their "SSB" slot holds the same CW-part frequency so a press there still does something
        // sensible, and the operator can put whatever he wants in it.
        private static readonly object[][] Factory =
        {
            //   label   name    low     high    ssb     cw
            new object[] { "1.8", "160m",  1800,   2000,   1843,   1825 },
            new object[] { "3.5", "80m",   3500,   4000,   3750,   3530 },
            new object[] { "7",   "40m",   7000,   7200,   7090,   7030 },
            new object[] { "10",  "30m",  10100,  10150,  10120,  10120 },
            new object[] { "14",  "20m",  14000,  14350,  14250,  14030 },
            new object[] { "18",  "17m",  18068,  18168,  18140,  18080 },
            new object[] { "21",  "15m",  21000,  21450,  21250,  21030 },
            new object[] { "24",  "12m",  24890,  24990,  24950,  24900 },
            new object[] { "28",  "10m",  28000,  29700,  28450,  28030 },
            new object[] { "50",  "6m",   50000,  54000,  50150,  50090 },
        };

        // The band EDGES never change and are not the operator's to edit - only the two frequencies
        // inside each band are - so they are worked out once and answered from here, rather than
        // re-reading and re-parsing the settings string for every notch of the mouse wheel.
        private static readonly List<RadioBandPreset> Edges = Defaults();

        /// <summary>
        /// The band a frequency falls in, or null if it is in none of them (out-of-band receive, a
        /// transverter, 60m - anywhere the panel has no edges to speak for).
        /// </summary>
        public static RadioBandPreset BandFor(double khz)
        {
            if (khz <= 0) return null;
            return Edges.FirstOrDefault(b => b.Contains(khz));
        }

        public static List<RadioBandPreset> Defaults()
        {
            return Factory.Select(row => new RadioBandPreset
            {
                Label = (string)row[0],
                Name = (string)row[1],
                LowKhz = (int)row[2],
                HighKhz = (int)row[3],
                SsbKhz = (int)row[4],
                CwKhz = (int)row[5],
            }).ToList();
        }

        /// <summary>
        /// The bands as the operator left them. Anything missing or unreadable in the saved string
        /// falls back to that band's factory frequencies, so a half-written setting never empties a
        /// button.
        /// </summary>
        public static List<RadioBandPreset> Load()
        {
            var bands = Defaults();

            string saved = Properties.Settings.Default.RadioPanelBands;
            if (string.IsNullOrWhiteSpace(saved)) return bands;

            try
            {
                foreach (string entry in saved.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] halves = entry.Split('=');
                    if (halves.Length != 2) continue;

                    var band = bands.FirstOrDefault(b => string.Equals(b.Label, halves[0].Trim(), StringComparison.OrdinalIgnoreCase));
                    if (band == null) continue;

                    string[] pair = halves[1].Split('/');
                    if (pair.Length != 2) continue;

                    if (int.TryParse(pair[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int ssb) && ssb > 0)
                        band.SsbKhz = ssb;
                    if (int.TryParse(pair[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int cw) && cw > 0)
                        band.CwKhz = cw;
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            return bands;
        }

        public static void Save(IEnumerable<RadioBandPreset> bands)
        {
            var text = new StringBuilder();
            foreach (var band in bands)
            {
                if (text.Length > 0) text.Append(';');
                text.Append(band.Label).Append('=')
                    .Append(band.SsbKhz.ToString(CultureInfo.InvariantCulture)).Append('/')
                    .Append(band.CwKhz.ToString(CultureInfo.InvariantCulture));
            }

            Properties.Settings.Default.RadioPanelBands = text.ToString();
            Properties.Settings.Default.Save();
        }
    }
}
