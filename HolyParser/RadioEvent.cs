using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace HolyParser
{
    public class RadioEvent
    {
        [JsonProperty("id")]
        public int id { get; set; }
        
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("is_categories")]
        public bool IsCategories { get; set; }

        public RadioEvent()
        {
        }

    }
}
