using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HolyParser
{
    public class ServerResponse
    {

        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("msg")]
        public string Msg { get; set; }

    }
}
