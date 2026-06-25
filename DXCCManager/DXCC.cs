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
    }
}
