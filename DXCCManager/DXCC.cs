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
    }
}
