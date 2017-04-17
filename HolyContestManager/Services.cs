using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace HolyContestManager
{
    public static class Services
    {
        public static string GenerateAdif(IEnumerable<QSO> qso_list)
        {
            string version = Assembly.GetExecutingAssembly().GetName().Version.ToString();

            StringBuilder adif = new StringBuilder(200);
            adif.AppendLine("<ADIF_VERS:3>2.2 ");
            adif.AppendLine("<PROGRAMID:10>HolyLogger ");
            //adif.AppendLine("<PROGRAMVERSION:15>Version 1.0.0.0 ");
            adif.AppendFormat("<PROGRAMVERSION:{0}>{1} ", version.Length, version);
            adif.AppendLine();
            adif.AppendLine("<EOH>");
            adif.AppendLine();

            foreach (QSO qso in qso_list)
            {
                //string date = qso.timestamp.ToString("yyyyMMdd");
                //string time = qso.timestamp.ToString("HHmmss");

                adif.AppendFormat("<call:{0}>{1} ", qso.callsign.Length, qso.callsign);
                adif.AppendFormat("<srx_string:{0}>{1} ", qso.exchange.Length, qso.exchange);
                adif.AppendFormat("<freq:{0}>{1} ", qso.frequency.Length, qso.frequency);
                adif.AppendFormat("<mode:{0}>{1} ", qso.mode.Length, qso.mode);
                adif.AppendFormat("<station_callsign:{0}>{1} ", qso.my_call.Length, qso.my_call);
                adif.AppendFormat("<operator:{0}>{1} ", qso.my_call.Length, qso.my_call);
                adif.AppendFormat("<stx_string:{0}>{1} ", qso.my_square.Length, qso.my_square);
                adif.AppendFormat("<rst_rcvd:{0}>{1} ", qso.rst_rcvd.Length, qso.rst_rcvd);
                adif.AppendFormat("<rst_sent:{0}>{1} ", qso.rst_sent.Length, qso.rst_sent);
                //adif.AppendFormat("<qso_date:{0}>{1} ", date.Length, date);
                //adif.AppendFormat("<time_on:{0}>{1} ", time.Length, time);
                //adif.AppendFormat("<time_off:{0}>{1} ", time.Length, time);
                adif.AppendFormat("<comment:{0}>{1} ", qso.comment.Length, qso.comment);
                adif.AppendLine("<EOR>");
            }

            return adif.ToString();
        }

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
