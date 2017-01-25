using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace HolyLogger
{
    public static class Services
    {
        public static bool LoginToQRZ(out string SessionKey)
        {
            if (string.IsNullOrWhiteSpace(Properties.Settings.Default.qrz_username) || string.IsNullOrWhiteSpace(Properties.Settings.Default.qrz_password))
            {
                SessionKey = "";
                return false;
            }
            try
            {
                WebRequest request = WebRequest.Create("http://xmldata.qrz.com/xml/current/?username=" + Properties.Settings.Default.qrz_username + ";password=" + Properties.Settings.Default.qrz_password);
                WebResponse response = request.GetResponse();
                string status = ((HttpWebResponse)response).StatusDescription;
                Stream dataStream = response.GetResponseStream();
                StreamReader reader = new StreamReader(dataStream);
                string responseFromServer = reader.ReadToEnd();

                XElement xml = XElement.Parse(responseFromServer);
                XElement element = xml.Elements().FirstOrDefault();
                SessionKey = element.Elements().FirstOrDefault().Value;

                reader.Close();
                response.Close();

                if (SessionKey.Contains("incorrect"))
                {
                    SessionKey = "";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show("Connection Failed: " + ex.Message);
                SessionKey = "";
                return false;
            }
        }
        
        public static string getHamQth(string callsign)
        {
            if (!string.IsNullOrWhiteSpace(callsign))
            {
                try
                {
                    string baseRequest = "http://www.hamqth.com/dxcc.php?callsign=";
                    WebRequest request = WebRequest.Create(baseRequest + callsign);
                    WebResponse response = request.GetResponse();
                    string status = ((HttpWebResponse)response).StatusDescription;
                    Stream dataStream = response.GetResponseStream();
                    StreamReader reader = new StreamReader(dataStream);
                    string responseFromServer = reader.ReadToEnd();
                    XDocument xDoc = XDocument.Parse(responseFromServer);
                    IEnumerable<XElement> country = xDoc.Root.Descendants(xDoc.Root.GetDefaultNamespace‌​() + "name");
                    if (country.Count() > 0)
                        return country.FirstOrDefault().Value;
                    else
                        return "";

                }
                catch (Exception)
                {
                    return "";
                }
            }
            else
            {
                return "";
            }
        }

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
                string date = qso.timestamp.ToString("yyyyMMdd");
                string time = qso.timestamp.ToString("HHmmss");

                adif.AppendFormat("<call:{0}>{1} ", qso.callsign.Length, qso.callsign);
                adif.AppendFormat("<srx_string:{0}>{1} ", qso.exchange.Length, qso.exchange);
                adif.AppendFormat("<freq:{0}>{1} ", qso.frequency.Length, qso.frequency);
                adif.AppendFormat("<mode:{0}>{1} ", qso.mode.Length, qso.mode);
                adif.AppendFormat("<station_callsign:{0}>{1} ", qso.my_call.Length, qso.my_call);
                adif.AppendFormat("<operator:{0}>{1} ", qso.my_call.Length, qso.my_call);
                adif.AppendFormat("<stx_string :{0}>{1} ", qso.my_square.Length, qso.my_square);
                adif.AppendFormat("<rst_rcvd:{0}>{1} ", qso.rst_rcvd.Length, qso.rst_rcvd);
                adif.AppendFormat("<rst_sent:{0}>{1} ", qso.rst_sent.Length, qso.rst_sent);
                adif.AppendFormat("<qso_date:{0}>{1} ", date.Length, date);
                adif.AppendFormat("<time_on:{0}>{1} ", time.Length, time);
                adif.AppendFormat("<time_off:{0}>{1} ", time.Length, time);
                adif.AppendFormat("<comment:{0}>{1} ", qso.comment.Length, qso.comment);
                adif.AppendLine("<EOR>");
            }

            return adif.ToString();
        }

        public static string getBareCallsign(string callsign)
        {
            return callsign;
        }
    }
}
