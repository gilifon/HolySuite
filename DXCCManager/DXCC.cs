using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DXCCManager
{
    public class DXCC
    {
        public string Prefixes { get; set; }
        public string Name { get; set; }
        public string Entity { get; set; }
        public string Continent { get; set; }
        public string Locator { get; set; }
        // Default/most-specific zones from cty.dat for the matched callsign (0 = unknown).
        public int CqZone { get; set; }
        public int ItuZone { get; set; }
        // The ARRL/ADIF entity code, when the answer came from a source that knows it (Club Log).
        // 0 means unknown, which is also what Club Log uses for "no DXCC entity" - see NoEntity.
        public int DxccCode { get; set; }
        // Set when the answer came from Club Log's list of operations that never counted (pirates and
        // the like), so the logger can warn instead of silently recording a country.
        public bool InvalidOperation { get; set; }
        // Which database answered - "cty.dat" or "Club Log". Diagnostic only; nothing branches on it.
        public string ResolvedBy { get; set; }
        // How many characters of the callsign the match covered (0 = nothing matched). Lets two
        // databases be compared on how SPECIFIC their answers are, not just on which one spoke.
        public int MatchedLength { get; set; }

        // ── IS THIS ANSWER A DXCC COUNTRY AT ALL? ─────────────────────────
        //
        // Two of the answers this class carries are NOT countries and must never be counted as one:
        //
        //   "Unknown" (Entity "-1") - no database recognised the callsign.
        //   Entity "0"              - Club Log says the operation belongs to no DXCC entity. It has
        //                             exactly four of these, all with <adif>0</adif>: MARITIME MOBILE,
        //                             AERONAUTICAL MOBILE, SATELLITE/INTERNET OR REPEATER, and INVALID.
        //                             A station at sea counts for nobody; that is the whole point of the
        //                             answer, and "Maritime Mobile" is a statement of that, not a place.
        //
        // Counting one of these as a country is not a cosmetic slip. It adds a country that does not
        // exist to the worked total, and because no current-entity list contains it, it is then filed as
        // a DELETED entity - so a log with three maritime-mobile contacts reports three deleted countries
        // chased and confirmed that nobody has ever worked.
        //
        // DxccCode is deliberately NOT the test: a perfectly real country resolved from cty.dat alone
        // carries code 0 simply because cty.dat has no entity numbers.
        public bool IsDxccEntity
        {
            get
            {
                return !string.IsNullOrEmpty(Name)
                    && !string.Equals(Name, "Unknown", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(Entity, "0", StringComparison.Ordinal);
            }
        }
    }
}
