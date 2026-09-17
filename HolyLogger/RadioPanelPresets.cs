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
        // ABOVE 4m: 2m and 70cm first, because satellite mode needs 70cm - a 432 IF with the 1968 MHz
        // transverter shift is what puts the station on QO-100's 2400 uplink, and the panel could not
        // reach 432 at all. Then the microwave bands, so a station that HAS a radio up there is not
        // told the panel stops at 4m. A radio that cannot reach a band is what the per-band Select
        // checkbox in Options is for: uncheck it and the button greys out.
        //
        // The EDGES are IARU Region 1 allocations: 2m 144-146, 70cm 430-440, 23cm 1240-1300,
        // 13cm 2300-2450, 9cm 3400-3410 (the narrowband slice most R1 countries actually have),
        // 6cm 5650-5850, 3cm 10000-10500 MHz.
        //
        // The FREQUENCIES inside them are the conventional narrowband calling spots - 2m 144.300 SSB
        // / 144.050 CW, 70cm 432.200 / 432.050, 23cm 1296.200 / 1296.050, 6cm 5760.200, 3cm 10368.200
        // - and, on 13cm, QO-100's own transponder rather than the terrestrial 2320.200, because that
        // is what a 13cm station is nearly always doing. Microwave calling frequencies vary more by
        // country than HF ones do, and like every row here they are a starting point the operator
        // edits. The bands with no digimode convention repeat their CW slot, the way 60m, 8m and 4m
        // already do.
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
            new object[] { "144", "2m",  144000, 146000, 144300, 144050, 144600 },
            new object[] { "432", "70cm",430000, 440000, 432200, 432050, 432600 },
            new object[] { "1296","23cm",1240000,1300000,1296200,1296050,1296050 },
            new object[] { "2400","13cm",2300000,2450000,2400200,2400075,2400075 },
            new object[] { "3400","9cm", 3400000,3410000,3400200,3400200,3400200 },
            new object[] { "5760","6cm", 5650000,5850000,5760200,5760200,5760200 },
            new object[] { "10368","3cm",10000000,10500000,10368200,10368200,10368200 },
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

    // ── SPECTRUM WIDTH ──────────────────────────────────────────────────────────────────────────
    //
    // The operator picks, per radio model and per mode, the width the radio's scope should show.
    // HolyLogger sends it right after it sends the radio a mode - and never otherwise: turning the
    // radio's own mode knob changes nothing here. A mode left empty sends nothing at all.
    //
    // EVERY COMMAND AND EVERY WIDTH BELOW IS FROM THE MAKER'S OWN DOCUMENT, read 2026-09-16:
    //   Icom   - CI-V Reference Guides (IC-705, 7300MK2, 7610, 7760, 9700) and the IC-7300
    //            Information sheet: command 27 15, "Scope span settings (in the Center mode ...)".
    //            Data: one byte 00 (MAIN), then the HALF span as 5 BCD bytes in frequency order.
    //            Only ±2.5, 5, 10, 25, 50, 100, 250, 500 kHz exist. It works in Center mode only.
    //   Yaesu  - CAT Operation Reference Manuals. FTDX10, FTDX101, FT-710: "SS" P1=0 P2=5 (SPAN)
    //            P3=0-9 (1 kHz - 1 MHz), P4-P7 = 0. The older sets have no span command, only a
    //            menu item set through "EX": FT-991 menu 120, FT-991A menu 116 (both 03-07 =
    //            50-1000 kHz), FT-891 menu 1302 (0-4 = 37.5-750 kHz), FTDX3000 menu 128 (0-5 =
    //            20-1000 kHz). The FT-991 and FT-991A put the item at DIFFERENT menu numbers, so
    //            they are two rows: the wrong number would change a microphone EQ setting instead.
    //   Kenwood - PC Control Command Reference Guides: "BS4". TS-890S 0-6 = 5-500 kHz. TS-990S
    //            0-7 = ±2.5-±250 kHz from firmware 1.20; firmware 1.13 and older number them
    //            differently, which the row's name says.
    //   Elecraft - K4 Programmer's Reference: "#SPNn;" n = 6000-368000 Hz. The K3/K3S and KX3 have
    //            no scope of their own: the P3 and PX3 do, "#SPNxxxxxx;" in 100 Hz units, 2-200 kHz.
    //   TS-480, TS-590S/SG and KX2 have no scope and no such command, so they are not listed.

    public sealed class SpectrumWidthChoice
    {
        public SpectrumWidthChoice(string code, string label) { Code = code; Label = label; }
        public string Code { get; }    // what is saved, and what the command is built from
        public string Label { get; }   // what the operator sees
        public override string ToString() => Label;
    }

    public enum SpectrumCommandKind { IcomCiv, Ascii }

    public sealed class SpectrumRadio
    {
        public SpectrumRadio(string name, string modelKey, SpectrumCommandKind kind,
                             Func<string, string> command, params SpectrumWidthChoice[] choices)
        {
            Name = name; ModelKey = modelKey; Kind = kind; Command = command; Choices = choices;
        }

        public string Name { get; }            // shown in the table
        public string ModelKey { get; }        // OmniRig's rig name, punctuation out, must START with this
        public SpectrumCommandKind Kind { get; }
        // Icom: the CI-V bytes after "FE FE <address> E0". Everyone else: the whole command.
        public Func<string, string> Command { get; }
        public SpectrumWidthChoice[] Choices { get; }
    }

    public static class SpectrumWidths
    {
        // SSB and CW first: those are the two he uses most.
        public static readonly string[] Modes = { "SSB", "CW", "AM", "RTTY", "FM" };

        private static SpectrumWidthChoice C(string code, string label) => new SpectrumWidthChoice(code, label);

        // Icom: the code is the half span in Hz. 2500 -> "00 25 00 00 00".
        private static string IcomSpan(string hz)
        {
            string d = hz.PadLeft(10, '0');
            return "27 15 00 " + d.Substring(8, 2) + " " + d.Substring(6, 2) + " " + d.Substring(4, 2)
                 + " " + d.Substring(2, 2) + " " + d.Substring(0, 2);
        }

        private static readonly SpectrumWidthChoice[] IcomChoices =
        {
            C("2500", "±2.5 kHz"), C("5000", "±5 kHz"), C("10000", "±10 kHz"), C("25000", "±25 kHz"),
            C("50000", "±50 kHz"), C("100000", "±100 kHz"), C("250000", "±250 kHz"), C("500000", "±500 kHz")
        };

        private static readonly SpectrumWidthChoice[] YaesuSsChoices =
        {
            C("0", "1 kHz"), C("1", "2 kHz"), C("2", "5 kHz"), C("3", "10 kHz"), C("4", "20 kHz"),
            C("5", "50 kHz"), C("6", "100 kHz"), C("7", "200 kHz"), C("8", "500 kHz"), C("9", "1 MHz")
        };

        private static readonly SpectrumWidthChoice[] Ft991Choices =
        {
            C("03", "50 kHz"), C("04", "100 kHz"), C("05", "200 kHz"), C("06", "500 kHz"), C("07", "1000 kHz")
        };

        // P3 and PX3: 100 Hz units, six digits.
        private static readonly SpectrumWidthChoice[] ElecraftP3Choices =
        {
            C("000020", "2 kHz"), C("000050", "5 kHz"), C("000100", "10 kHz"), C("000200", "20 kHz"),
            C("000500", "50 kHz"), C("001000", "100 kHz"), C("002000", "200 kHz")
        };

        public static readonly SpectrumRadio[] Radios =
        {
            new SpectrumRadio("IC-705", "IC705", SpectrumCommandKind.IcomCiv, IcomSpan, IcomChoices),
            new SpectrumRadio("IC-7300", "IC7300", SpectrumCommandKind.IcomCiv, IcomSpan, IcomChoices),
            new SpectrumRadio("IC-7300MK2", "IC7300MK2", SpectrumCommandKind.IcomCiv, IcomSpan, IcomChoices),
            new SpectrumRadio("IC-7610", "IC7610", SpectrumCommandKind.IcomCiv, IcomSpan, IcomChoices),
            new SpectrumRadio("IC-7760", "IC7760", SpectrumCommandKind.IcomCiv, IcomSpan, IcomChoices),
            new SpectrumRadio("IC-9700", "IC9700", SpectrumCommandKind.IcomCiv, IcomSpan, IcomChoices),

            new SpectrumRadio("FTDX10", "FTDX10", SpectrumCommandKind.Ascii, c => "SS05" + c + "0000;", YaesuSsChoices),
            new SpectrumRadio("FTDX101D / MP", "FTDX101", SpectrumCommandKind.Ascii, c => "SS05" + c + "0000;", YaesuSsChoices),
            new SpectrumRadio("FT-710", "FT710", SpectrumCommandKind.Ascii, c => "SS05" + c + "0000;", YaesuSsChoices),
            new SpectrumRadio("FT-991", "FT991", SpectrumCommandKind.Ascii, c => "EX120" + c + ";", Ft991Choices),
            new SpectrumRadio("FT-991A", "FT991A", SpectrumCommandKind.Ascii, c => "EX116" + c + ";", Ft991Choices),
            new SpectrumRadio("FT-891", "FT891", SpectrumCommandKind.Ascii, c => "EX1302" + c + ";",
                C("0", "37.5 kHz"), C("1", "75 kHz"), C("2", "150 kHz"), C("3", "375 kHz"), C("4", "750 kHz")),
            new SpectrumRadio("FTDX3000", "FTDX3000", SpectrumCommandKind.Ascii, c => "EX128" + c + ";",
                C("0", "20 kHz"), C("1", "50 kHz"), C("2", "100 kHz"), C("3", "200 kHz"), C("4", "500 kHz"), C("5", "1000 kHz")),

            new SpectrumRadio("TS-890S", "TS890", SpectrumCommandKind.Ascii, c => "BS4" + c + ";",
                C("0", "5 kHz"), C("1", "10 kHz"), C("2", "25 kHz"), C("3", "50 kHz"), C("4", "100 kHz"),
                C("5", "200 kHz"), C("6", "500 kHz")),
            new SpectrumRadio("TS-990S (firmware 1.20 or later)", "TS990", SpectrumCommandKind.Ascii, c => "BS4" + c + ";",
                C("0", "±2.5 kHz"), C("1", "±5 kHz"), C("2", "±10 kHz"), C("3", "±15 kHz"), C("4", "±25 kHz"),
                C("5", "±50 kHz"), C("6", "±100 kHz"), C("7", "±250 kHz")),

            new SpectrumRadio("K4", "K4", SpectrumCommandKind.Ascii, c => "#SPN" + c + ";",
                C("6000", "6 kHz"), C("10000", "10 kHz"), C("25000", "25 kHz"), C("50000", "50 kHz"),
                C("100000", "100 kHz"), C("200000", "200 kHz"), C("368000", "368 kHz")),
            new SpectrumRadio("K3 / K3S (with a P3)", "K3", SpectrumCommandKind.Ascii, c => "#SPN" + c + ";", ElecraftP3Choices),
            new SpectrumRadio("KX3 (with a PX3)", "KX3", SpectrumCommandKind.Ascii, c => "#SPN" + c + ";", ElecraftP3Choices),
        };

        // OmniRig's name with the punctuation and the word Elecraft taken out, as the voice
        // message table does it: "IC-7610-DATA-FIL1" -> "IC7610DATAFIL1".
        private static string ModelKey(string rigType)
        {
            string name = (rigType ?? string.Empty).Trim().ToUpperInvariant();
            if (name.StartsWith("ELECRAFT", StringComparison.Ordinal))
                name = name.Substring("ELECRAFT".Length);
            var model = new StringBuilder(name.Length);
            foreach (char c in name)
                if (char.IsLetterOrDigit(c)) model.Append(c);
            return model.ToString();
        }

        /// <summary>
        /// The row for the radio OmniRig is running, or null. The LONGEST match wins: "FTDX101D"
        /// also starts with "FTDX10", and "FT991A" with "FT991" - and those are different radios.
        /// </summary>
        public static SpectrumRadio RadioFor(string rigType)
        {
            string key = ModelKey(rigType);
            if (key.Length == 0) return null;
            return Radios.Where(r => key.StartsWith(r.ModelKey, StringComparison.Ordinal))
                         .OrderByDescending(r => r.ModelKey.Length)
                         .FirstOrDefault();
        }

        // Saved as "IC7610|SSB=25000;IC7610|CW=2500". Only filled cells are kept.
        public static Dictionary<string, string> Load()
        {
            var cells = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string text = Properties.Settings.Default.SpectrumWidths ?? string.Empty;
                foreach (string item in text.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = item.IndexOf('=');
                    if (eq <= 0) continue;
                    cells[item.Substring(0, eq).Trim()] = item.Substring(eq + 1).Trim();
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            return cells;
        }

        public static void Save(Dictionary<string, string> cells)
        {
            Properties.Settings.Default.SpectrumWidths = string.Join(";",
                cells.Where(c => !string.IsNullOrEmpty(c.Value)).Select(c => c.Key + "=" + c.Value));
            Properties.Settings.Default.Save();
        }

        public static string CellKey(SpectrumRadio radio, string mode) => radio.ModelKey + "|" + mode;

        /// <summary>
        /// The width chosen for this radio in this mode, or null when the cell is empty - or holds a
        /// code the radio's list no longer has, which is never sent.
        /// </summary>
        public static SpectrumWidthChoice ChosenFor(SpectrumRadio radio, string mode)
        {
            if (radio == null || string.IsNullOrEmpty(mode)) return null;
            string code;
            if (!Load().TryGetValue(CellKey(radio, mode), out code)) return null;
            return radio.Choices.FirstOrDefault(c => c.Code == code);
        }
    }

    /// <summary>
    /// Options > Radio Control Panel > Spectrum width manager. One row per radio, one column per
    /// mode, and in each cell only the widths that radio's manual lists.
    /// </summary>
    internal sealed class SpectrumWidthManagerWindow : System.Windows.Window
    {
        private const string Empty = "—";

        internal static void Show(System.Windows.Window owner, string connectedRig)
        {
            new SpectrumWidthManagerWindow(connectedRig) { Owner = owner }.ShowDialog();
        }

        private readonly Dictionary<string, string> _cells = SpectrumWidths.Load();

        private SpectrumWidthManagerWindow(string connectedRig)
        {
            Title = "Spectrum width manager";
            SizeToContent = System.Windows.SizeToContent.WidthAndHeight;
            ResizeMode = System.Windows.ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty, "WindowBg");

            var stack = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(18, 16, 18, 16) };

            stack.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "Choose a width for each mode. HolyLogger sends it when it changes the radio's mode. "
                     + "Leave “" + Empty + "” and nothing is sent.",
                FontSize = 16,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                MaxWidth = 900,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Margin = new System.Windows.Thickness(0, 0, 0, 4)
            });
            stack.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "Icom: the scope must be in Center mode.",
                FontSize = 16,
                Margin = new System.Windows.Thickness(0, 0, 0, 10)
            });

            SpectrumRadio mine = SpectrumWidths.RadioFor(connectedRig);
            if (!string.IsNullOrWhiteSpace(connectedRig))
            {
                var yours = new System.Windows.Controls.TextBlock
                {
                    Text = mine != null
                        ? "Your radio: " + connectedRig + " (shown in bold)"
                        : "Your radio: " + connectedRig + " - not on this list, so no width is sent.",
                    FontSize = 16,
                    FontWeight = System.Windows.FontWeights.Bold,
                    Margin = new System.Windows.Thickness(0, 0, 0, 10)
                };
                yours.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "AccentBrush");
                stack.Children.Add(yours);
            }

            var grid = new System.Windows.Controls.Grid();
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });
            foreach (string _ in SpectrumWidths.Modes)
                grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(125) });

            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            AddText(grid, "Radio", 0, 0, true);
            for (int m = 0; m < SpectrumWidths.Modes.Length; m++)
                AddText(grid, SpectrumWidths.Modes[m], 0, m + 1, true);

            for (int r = 0; r < SpectrumWidths.Radios.Length; r++)
            {
                SpectrumRadio radio = SpectrumWidths.Radios[r];
                int row = r + 1;
                grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
                AddText(grid, radio.Name, row, 0, ReferenceEquals(radio, mine));

                for (int m = 0; m < SpectrumWidths.Modes.Length; m++)
                {
                    string key = SpectrumWidths.CellKey(radio, SpectrumWidths.Modes[m]);
                    var box = new System.Windows.Controls.ComboBox
                    {
                        FontSize = 16,
                        Width = 115,
                        Margin = new System.Windows.Thickness(0, 3, 10, 3),
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Left
                    };
                    box.Items.Add(Empty);
                    foreach (var choice in radio.Choices) box.Items.Add(choice);

                    string saved;
                    object selected = Empty;
                    if (_cells.TryGetValue(key, out saved))
                        selected = radio.Choices.FirstOrDefault(c => c.Code == saved) ?? (object)Empty;
                    box.SelectedItem = selected;

                    box.SelectionChanged += (s, e) =>
                    {
                        var picked = box.SelectedItem as SpectrumWidthChoice;
                        if (picked == null) _cells.Remove(key);
                        else _cells[key] = picked.Code;
                        SpectrumWidths.Save(_cells);
                    };

                    System.Windows.Controls.Grid.SetRow(box, row);
                    System.Windows.Controls.Grid.SetColumn(box, m + 1);
                    grid.Children.Add(box);
                }
            }

            stack.Children.Add(grid);

            var close = new System.Windows.Controls.Button
            {
                Content = "Close",
                FontSize = 16,
                Height = 34,
                MinWidth = 110,
                Margin = new System.Windows.Thickness(0, 16, 0, 0),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                IsDefault = true,
                IsCancel = true
            };
            close.Click += (s, e) => Close();
            stack.Children.Add(close);

            // Grows to its content, but never past the screen, so Close stays reachable.
            Content = new System.Windows.Controls.ScrollViewer
            {
                Content = stack,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled,
                MaxHeight = Math.Max(300, System.Windows.SystemParameters.WorkArea.Height - 80)
            };
        }

        private static void AddText(System.Windows.Controls.Grid grid, string text, int row, int column, bool bold)
        {
            var block = new System.Windows.Controls.TextBlock
            {
                Text = text,
                FontSize = 16,
                FontWeight = bold ? System.Windows.FontWeights.Bold : System.Windows.FontWeights.Normal,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                Margin = new System.Windows.Thickness(0, 3, 14, 3)
            };
            System.Windows.Controls.Grid.SetRow(block, row);
            System.Windows.Controls.Grid.SetColumn(block, column);
            grid.Children.Add(block);
        }
    }
}
