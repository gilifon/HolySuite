using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace HolyLogger
{
    // One station location as defined locally in TQSL (the "house" entries under each callsign
    // certificate). "Name" is the exact string TQSL expects in its -l argument.
    public class TqslStationLocation
    {
        public string Name { get; set; }   // station location name (the -l argument)
        public string Call { get; set; }   // callsign certificate this location is bound to
        public string Dxcc { get; set; }
        public string Grid { get; set; }
        public string Cqz { get; set; }
        public string Ituz { get; set; }
    }

    // Reads TQSL's own "station_data" file (%APPDATA%\TrustedQSL\station_data). This is the same
    // data TQSL shows in its Station Locations tab. Read-only: HolyLogger never writes it — TQSL
    // stays the single source of truth for certificates and locations.
    public static class TqslStationData
    {
        public static string DefaultPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "TrustedQSL", "station_data");
            }
        }

        // Returns every station location found, or an empty list if the file is missing/unreadable.
        public static List<TqslStationLocation> Read(string path = null)
        {
            var list = new List<TqslStationLocation>();
            try
            {
                path = path ?? DefaultPath;
                if (!File.Exists(path)) return list;

                var doc = XDocument.Load(path);
                foreach (var sd in doc.Descendants("StationData"))
                {
                    string name = (string)sd.Attribute("name") ?? string.Empty;
                    list.Add(new TqslStationLocation
                    {
                        Name = name.Trim(),
                        Call = ((string)sd.Element("CALL") ?? string.Empty).Trim(),
                        Dxcc = ((string)sd.Element("DXCC") ?? string.Empty).Trim(),
                        Grid = ((string)sd.Element("GRIDSQUARE") ?? string.Empty).Trim(),
                        Cqz = ((string)sd.Element("CQZ") ?? string.Empty).Trim(),
                        Ituz = ((string)sd.Element("ITUZ") ?? string.Empty).Trim(),
                    });
                }
            }
            catch { }
            return list;
        }

        // All locations bound to a given callsign (case-insensitive).
        public static List<TqslStationLocation> ForCallsign(string call, string path = null)
        {
            if (string.IsNullOrWhiteSpace(call)) return new List<TqslStationLocation>();
            call = call.Trim();
            return Read(path)
                .Where(s => string.Equals(s.Call, call, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    // The outcome of resolving "which TQSL station location should sign QSOs for this callsign".
    public class LotwLocationChoice
    {
        public string Callsign;
        public string LocationName;        // the resolved -l name, or null if it can't be resolved
        public bool Ambiguous;             // callsign has >1 location and no saved pick
        public bool NoCertificate;         // callsign has no station location in TQSL
        public List<string> Options = new List<string>();  // all location names for this callsign
    }

    // Maps a station callsign to the TQSL station location that should sign its QSOs.
    //
    // A TQSL station location is bound to ONE callsign certificate, so there is no generic "default"
    // location that works for an arbitrary callsign — signing a callsign with another call's location
    // would be rejected (or misfiled) by TQSL. Resolution is therefore strictly by certificate:
    //   1 location in TQSL  -> use it (automatic).
    //   >1 location in TQSL -> use the saved per-callsign pick; otherwise "Ambiguous" (must be chosen).
    //   0 locations in TQSL -> "NoCertificate": the callsign cannot be uploaded to LoTW.
    public static class LotwStationResolver
    {
        // Saved per-callsign picks are stored as "CALL=Location" pairs separated by ';'.
        public static Dictionary<string, string> ParsePicks(string raw)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(raw)) return map;
            foreach (var part in raw.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string call = part.Substring(0, eq).Trim();
                string loc = part.Substring(eq + 1).Trim();
                if (call.Length > 0 && loc.Length > 0) map[call] = loc;
            }
            return map;
        }

        public static string FormatPicks(Dictionary<string, string> map)
        {
            return string.Join(";", map.Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                                       .Select(kv => kv.Key + "=" + kv.Value));
        }

        public static LotwLocationChoice Resolve(string callsign, string savedPicksRaw,
                                                 string stationDataPath = null)
        {
            var choice = new LotwLocationChoice { Callsign = (callsign ?? string.Empty).Trim() };

            var locations = TqslStationData.ForCallsign(choice.Callsign, stationDataPath);
            choice.Options = locations.Select(l => l.Name).ToList();

            if (locations.Count == 1)
            {
                choice.LocationName = locations[0].Name;
                return choice;
            }

            if (locations.Count > 1)
            {
                var picks = ParsePicks(savedPicksRaw);
                if (picks.TryGetValue(choice.Callsign, out string saved) &&
                    choice.Options.Any(o => string.Equals(o, saved, StringComparison.OrdinalIgnoreCase)))
                {
                    choice.LocationName = saved;
                    return choice;
                }
                choice.Ambiguous = true;
                return choice;
            }

            // No station location in TQSL for this callsign — it has no certificate, so it cannot
            // be signed/uploaded to LoTW.
            choice.NoCertificate = true;
            return choice;
        }
    }
}
