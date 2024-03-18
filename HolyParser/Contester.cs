using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace HolyParser
{
    public class Contester
    {
        [JsonProperty("contest")]
        public string Contest { get; set; }

        [JsonProperty("callsign")]
        public string Callsign { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("email")]
        public string Email { get; set; }

        [JsonProperty("operator")]
        public string Category_Operator { get; set; }

        [JsonProperty("band")]
        public string Category_Band { get; set; }

        [JsonProperty("mode")]
        public string Category_Mode { get; set; }

        [JsonProperty("power")]
        public string Category_Power { get; set; }

        [JsonProperty("location")]
        public string Location { get; set; }

        [JsonProperty("assisted")]
        public string Category_Assisted { get; set; }

        [JsonProperty("transmitter")]
        public string Category_Transmitter { get; set; }

        [JsonProperty("grid")]
        public string Grid { get; set; }

        [JsonProperty("score")]
        public string Score { get; set; }

        [JsonProperty("club")]
        public string Club { get; set; }

        [JsonProperty("address")]
        public string Address { get; set; }

        [JsonProperty("city")]
        public string City { get; set; }

        [JsonProperty("country")]
        public string Country { get; set; }

        [JsonProperty("operators")]
        public string Operators { get; set; }

        [JsonProperty("soapbox")]
        public string Soapbox { get; set; }

        [JsonProperty("filename")]
        public string filename { get; set; }

        [JsonProperty("timestamp")]
        public string timestamp { get; set; }

    }

}

