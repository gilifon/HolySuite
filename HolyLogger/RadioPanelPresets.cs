using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace HolyLogger
{
    /// <summary>
    /// One band button of the Radio Control Panel: the band it stands for, and the three frequencies
    /// the radio is sent to when it is pressed - one each for SSB, CW and RTTY. AM and FM have no
    /// frequency of their own (see FrequencyFor) - pressing either changes only the mode, at wherever
    /// the radio already is.
    /// </summary>
    public class RadioBandPreset
    {
        public string Label { get; set; }      // what the button says, e.g. "14"
        public string Name { get; set; }       // the band in metres, e.g. "20m" - shown under the label
        public int LowKhz { get; set; }        // band edges, used ONLY to light the button that the
        public int HighKhz { get; set; }       // radio's current frequency falls inside
        public int SsbKhz { get; set; }
        public int CwKhz { get; set; }
        public int RttyKhz { get; set; }

        // Not every radio covers every band this panel offers. True by default (every existing
        // config, with nothing saved yet, keeps every button working exactly as it always did) -
        // unchecked on the Options page, the matching band button goes disabled and grey on the
        // panel, whatever the radio itself is doing.
        public bool Enabled { get; set; } = true;

        public bool Contains(double khz)
        {
            return khz >= LowKhz && khz <= HighKhz;
        }

        public int FrequencyFor(string mode)
        {
            if (string.Equals(mode, "CW", StringComparison.OrdinalIgnoreCase)) return CwKhz;
            if (string.Equals(mode, "RTTY", StringComparison.OrdinalIgnoreCase)) return RttyKhz;
            return SsbKhz;   // SSB, and AM/FM, which have no frequency of their own
        }
    }

    /// <summary>
    /// The band buttons of the Radio Control Panel, and the two frequencies behind each of them.
    ///
    /// The band list itself is FIXED (the panel has exactly these buttons and no more); only the two
    /// frequencies per band are the operator's to change, on Options > Radio Control Panel. They are
    /// kept in one settings string rather than twenty settings, so adding or renaming a band later is
    /// one edit here instead of twenty in Settings.settings.
    /// </summary>
    public static class RadioPanelPresets
    {
        // Region 1 calling / commonly used frequencies. 10 MHz and 18 MHz have no SSB by band plan;
        // their "SSB" slot holds the same CW-part frequency so a press there still does something
        // sensible, and the operator can put whatever he wants in it.
        //
        // 60m, 8m and 4m are secondary/experimental in most administrations and the exact edges vary
        // by country - these are the widest commonly-cited spans (IARU Region 1 / Wikipedia), and like
        // every other row here the two frequencies are only a starting point the operator can edit.
        // 60m: WRC-15 allocation 5351.5-5366.5 kHz, USB dial 5354/5357/5360/5363, CW below 5354.
        // 8m: 40.66-40.70 MHz is the span shared by most of the handful of countries with an
        // allocation (Slovenia, Croatia, Italy, Belgium); no established calling frequency.
        // 4m: 70.0-70.5 MHz (UK/IARU R1); combined CW/SSB calling frequency 70.200 MHz.
        //
        // RTTY IS NOT A SINGLE PUBLISHED FREQUENCY ANYWHERE. The IARU Region 1 HF band plan
        // (iaru-r1.org, "HF band plan", effective 01-June-2016) gives RTTY a shared "Narrow band
        // modes"/"Digimodes" SEGMENT per band, not a point channel: 1838-1840, 3570-3590,
        // 7040-7047, 10130-10150, 14070-14089, 18095-18105, 21070-21090, 24915-24925, 28070-28120.
        // The values below are the one frequency inside each of those official segments that is, in
        // practice, universally cited as "the" RTTY frequency across ham-radio references (RSGB,
        // AA5AU and others agree on all nine) - close to the segment's low edge, where RTTY has
        // always concentrated. 60m, 8m and 4m have no such segment and no established RTTY
        // convention at all, so their RTTY column repeats the CW slot rather than inventing one.
        private static readonly object[][] Factory =
        {
            //   label   name    low     high    ssb     cw      rtty
            new object[] { "1.8", "160m",  1800,   2000,   1843,   1825,   1838 },
            new object[] { "3.5", "80m",   3500,   4000,   3750,   3530,   3580 },
            new object[] { "5",   "60m",   5351,   5367,   5357,   5352,   5352 },
            new object[] { "7",   "40m",   7000,   7200,   7090,   7030,   7040 },
            new object[] { "10",  "30m",  10100,  10150,  10120,  10120,  10140 },
            new object[] { "14",  "20m",  14000,  14350,  14250,  14030,  14080 },
            new object[] { "18",  "17m",  18068,  18168,  18140,  18080,  18100 },
            new object[] { "21",  "15m",  21000,  21450,  21250,  21030,  21080 },
            new object[] { "24",  "12m",  24890,  24990,  24950,  24900,  24920 },
            new object[] { "28",  "10m",  28000,  29700,  28450,  28030,  28080 },
            new object[] { "40",  "8m",   40660,  40700,  40680,  40680,  40680 },
            new object[] { "50",  "6m",   50000,  54000,  50150,  50090,  50090 },
            new object[] { "70",  "4m",   70000,  70500,  70200,  70200,  70200 },
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
                RttyKhz = (int)row[6],
            }).ToList();
        }

        /// <summary>
        /// The bands as the operator left them. Anything missing or unreadable in the saved string
        /// falls back to that band's factory frequencies, so a half-written setting never empties a
        /// button. The saved value has grown over three versions - "ssb/cw", then "ssb/cw/rtty", now
        /// "ssb/cw/rtty/enabled" - and an older, shorter save just leaves whatever it doesn't carry
        /// (RttyKhz, Enabled) at its factory default, which for Enabled is true.
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

                    string[] parts = halves[1].Split('/');
                    if (parts.Length < 2) continue;

                    if (int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int ssb) && ssb > 0)
                        band.SsbKhz = ssb;
                    if (int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int cw) && cw > 0)
                        band.CwKhz = cw;
                    if (parts.Length >= 3
                        && int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int rtty) && rtty > 0)
                        band.RttyKhz = rtty;
                    if (parts.Length >= 4)
                        band.Enabled = parts[3].Trim() != "0";
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
                    .Append(band.CwKhz.ToString(CultureInfo.InvariantCulture)).Append('/')
                    .Append(band.RttyKhz.ToString(CultureInfo.InvariantCulture)).Append('/')
                    .Append(band.Enabled ? "1" : "0");
            }

            Properties.Settings.Default.RadioPanelBands = text.ToString();
            Properties.Settings.Default.Save();
        }
    }
}
