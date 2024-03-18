using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace HolyParser
{
    public class GenericItem
    {
        [JsonProperty("id")]
        public int id { get; set; }
        
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("event_id")]
        public int EventId { get; set; }

        public GenericItem()
        {
        }

    }
}
