using HolyParser;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mail;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace HolyContestManager
{
    public static class Helper
    {
        public static string getBareCallsign(string callsign)
        {
            string[] callParts = callsign.Split('/');
            if (callParts.Length == 1) return callsign;
            if (callParts.Length > 2) return callParts[1];
            if (callParts.Length == 2)
            {
                if (callParts[0].Length > callParts[1].Length) return callParts[0];
                return callParts[1];
            }
            return callsign;
        }
    }
}
