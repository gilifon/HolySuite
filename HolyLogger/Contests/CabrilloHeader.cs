using System.Collections.Generic;
using System.Linq;

namespace HolyLogger.Contests
{
    // Where a header field's value comes from / is stored.
    //   Personal = stable operator/station info, entered once in Settings and reused for every contest.
    //   Contest  = varies per contest (categories, location, score, comments), stored per contest.
    public enum CabrilloFieldScope { Personal, Contest }

    // What kind of control the field needs, so the info window can render it correctly.
    public enum CabrilloFieldInput { Text, MultiLineText, Choice }

    // One Cabrillo header field the program can collect from the operator and write to the file.
    // (CONTEST and CREATED-BY are filled automatically by the program and are not in this catalog.)
    public class CabrilloHeaderField
    {
        public string Tag { get; }                       // Cabrillo tag, e.g. "CATEGORY-POWER"
        public string Label { get; }                     // human label shown on the form
        public CabrilloFieldScope Scope { get; }
        public CabrilloFieldInput Input { get; }
        public IReadOnlyList<string> Choices { get; }    // for Choice input (the CATEGORY-* menus, etc.)
        public string Hint { get; }                      // optional helper text under the field
        public bool ReadOnly { get; }                    // shown but not editable in the forms (owned elsewhere)

        public CabrilloHeaderField(string tag, string label, CabrilloFieldScope scope,
            CabrilloFieldInput input, IReadOnlyList<string> choices = null, string hint = null, bool readOnly = false)
        {
            Tag = tag;
            Label = label;
            Scope = scope;
            Input = input;
            Choices = choices;
            Hint = hint;
            ReadOnly = readOnly;
        }
    }

    // The Cabrillo v3 header field catalog plus the per-contest required/optional resolution.
    public static class CabrilloHeader
    {
        // Every header field the program can collect, in a sensible display order. Allowed values for
        // the CATEGORY-* menus follow the WWROF Cabrillo v3 specification.
        public static readonly IReadOnlyList<CabrilloHeaderField> Catalog = new List<CabrilloHeaderField>
        {
            // ── Personal / station (stored once in Settings, reused across contests) ──
            new CabrilloHeaderField("CALLSIGN", "Station callsign", CabrilloFieldScope.Personal, CabrilloFieldInput.Text,
                hint: "Set in the main window's Station callsign box — shown here for reference only.", readOnly: true),
            new CabrilloHeaderField("NAME", "Operator name", CabrilloFieldScope.Personal, CabrilloFieldInput.Text),
            new CabrilloHeaderField("EMAIL", "E-mail", CabrilloFieldScope.Personal, CabrilloFieldInput.Text),
            new CabrilloHeaderField("GRID-LOCATOR", "Grid locator", CabrilloFieldScope.Personal, CabrilloFieldInput.Text),
            new CabrilloHeaderField("ADDRESS", "Address", CabrilloFieldScope.Personal, CabrilloFieldInput.MultiLineText),
            new CabrilloHeaderField("ADDRESS-CITY", "City", CabrilloFieldScope.Personal, CabrilloFieldInput.Text),
            new CabrilloHeaderField("ADDRESS-STATE-PROVINCE", "State / Province", CabrilloFieldScope.Personal, CabrilloFieldInput.Text),
            new CabrilloHeaderField("ADDRESS-POSTALCODE", "Postal code", CabrilloFieldScope.Personal, CabrilloFieldInput.Text),
            new CabrilloHeaderField("ADDRESS-COUNTRY", "Country", CabrilloFieldScope.Personal, CabrilloFieldInput.Text),
            new CabrilloHeaderField("CLUB", "Club", CabrilloFieldScope.Personal, CabrilloFieldInput.Text),

            // ── Categories (per contest) ──
            new CabrilloHeaderField("CATEGORY-OPERATOR", "Operator category", CabrilloFieldScope.Contest, CabrilloFieldInput.Choice,
                new[] { "SINGLE-OP", "MULTI-OP", "CHECKLOG" }),
            new CabrilloHeaderField("CATEGORY-ASSISTED", "Assisted", CabrilloFieldScope.Contest, CabrilloFieldInput.Choice,
                new[] { "ASSISTED", "NON-ASSISTED" }),
            new CabrilloHeaderField("CATEGORY-BAND", "Band category", CabrilloFieldScope.Contest, CabrilloFieldInput.Choice,
                new[] { "ALL", "160M", "80M", "40M", "20M", "15M", "10M", "6M", "2M", "70CM" }),
            new CabrilloHeaderField("CATEGORY-MODE", "Mode category", CabrilloFieldScope.Contest, CabrilloFieldInput.Choice,
                new[] { "CW", "SSB", "RTTY", "DIGI", "FM", "MIXED" }),
            new CabrilloHeaderField("CATEGORY-POWER", "Power category", CabrilloFieldScope.Contest, CabrilloFieldInput.Choice,
                new[] { "HIGH", "LOW", "QRP" }),
            new CabrilloHeaderField("CATEGORY-TRANSMITTER", "Transmitter category", CabrilloFieldScope.Contest, CabrilloFieldInput.Choice,
                new[] { "ONE", "TWO", "LIMITED", "UNLIMITED", "SWL" }),
            new CabrilloHeaderField("CATEGORY-STATION", "Station category", CabrilloFieldScope.Contest, CabrilloFieldInput.Choice,
                new[] { "FIXED", "PORTABLE", "MOBILE", "ROVER", "EXPEDITION", "HQ", "SCHOOL", "DISTRIBUTED" }),
            new CabrilloHeaderField("CATEGORY-TIME", "Time category", CabrilloFieldScope.Contest, CabrilloFieldInput.Choice,
                new[] { "6-HOURS", "8-HOURS", "12-HOURS", "24-HOURS" }),
            new CabrilloHeaderField("CATEGORY-OVERLAY", "Overlay", CabrilloFieldScope.Contest, CabrilloFieldInput.Choice,
                new[] { "CLASSIC", "ROOKIE", "TB-WIRES", "YOUTH", "NOVICE-TECH", "YL" }),

            // ── Per-contest values ──
            new CabrilloHeaderField("LOCATION", "Location", CabrilloFieldScope.Contest, CabrilloFieldInput.Text,
                hint: "ARRL/RAC section, IOTA island, RDA number, etc. — see the contest rules"),
            new CabrilloHeaderField("CLAIMED-SCORE", "Claimed score", CabrilloFieldScope.Contest, CabrilloFieldInput.Text),
            new CabrilloHeaderField("OPERATORS", "Operators", CabrilloFieldScope.Contest, CabrilloFieldInput.Text,
                hint: "All operator callsigns (multi-op), space or comma separated"),
            new CabrilloHeaderField("CERTIFICATE", "Request certificate", CabrilloFieldScope.Contest, CabrilloFieldInput.Choice,
                new[] { "YES", "NO" }),
            new CabrilloHeaderField("SOAPBOX", "Soapbox (comments)", CabrilloFieldScope.Contest, CabrilloFieldInput.MultiLineText),
        };

        // Fields required for essentially every Cabrillo contest log unless a contest overrides.
        // CONTEST/CALLSIGN/CREATED-BY are always written by the program, so only catalog fields the
        // operator must supply are listed here.
        private static readonly HashSet<string> DefaultRequired = new HashSet<string>
        {
            "CALLSIGN", "NAME", "EMAIL", "GRID-LOCATOR",
            "CATEGORY-OPERATOR", "CATEGORY-ASSISTED", "CATEGORY-BAND", "CATEGORY-MODE", "CATEGORY-POWER",
        };

        public static CabrilloHeaderField Find(string tag) => Catalog.FirstOrDefault(f => f.Tag == tag);

        // The required tags for a contest: the default set, plus the contest's own additions
        // (cabrillo_required in contests.json), minus any it explicitly drops (cabrillo_optional).
        public static HashSet<string> RequiredFor(Contest contest)
        {
            var set = new HashSet<string>(DefaultRequired);
            if (contest?.CabrilloRequired != null)
                foreach (var t in contest.CabrilloRequired)
                    if (!string.IsNullOrWhiteSpace(t)) set.Add(t.Trim().ToUpperInvariant());
            if (contest?.CabrilloOptional != null)
                foreach (var t in contest.CabrilloOptional)
                    if (!string.IsNullOrWhiteSpace(t)) set.Remove(t.Trim().ToUpperInvariant());
            return set;
        }
    }
}
