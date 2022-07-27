using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace HolyParser
{
    public class Participant : IEquatable<Participant>
    {
        [JsonProperty("id")]
        public int id { get; set; }

        [JsonProperty("callsign")]
        public string Callsign { get; set; }

        [JsonProperty("category_op")]
        public string CategoryOp { get; set; }

        [JsonProperty("category_mode")]
        public string CategoryMode { get; set; }

        [JsonProperty("category_power")]
        public string CategoryPower { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("email")]
        public string Email { get; set; }

        [JsonProperty("country")]
        public string Country { get; set; }

        [JsonProperty("year")]
        public int Year { get; set; }

        [JsonProperty("qsos")]
        public int QSOs { get; set; }

        [JsonProperty("points")]
        public string Points { get; set; }


        public string HASH { get; set; }
        

        public Participant()
        {
            
        }

        private void Hash()
        {
            string callsign = !string.IsNullOrWhiteSpace(Callsign) ? Callsign : "Callsign";
            string name = !string.IsNullOrWhiteSpace(Name) ? Name : "Name";
            string year = Year.ToString();

            HASH = callsign + name + year;
        }

        public bool Equals(Participant other)
        {
            return (this.HASH == other.HASH);
        }
    }
}
