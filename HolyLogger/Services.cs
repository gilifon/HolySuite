using DXCCManager;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
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

        public static string GenerateCSV(IEnumerable<QSO> qso_list)
        {
            StringBuilder csv = new StringBuilder(200);
            RadioEntityResolver rem = new RadioEntityResolver();

            int index = 1;

            csv.AppendFormat("{0},", "No");
            csv.AppendFormat("{0},", "Date");
            csv.AppendFormat("{0},", "UTC Start");
            csv.AppendFormat("{0},", "Callsign");
            csv.AppendFormat("{0},", "Country");
            csv.AppendFormat("{0},", "Name");
            csv.AppendFormat("{0},", "QTH");
            csv.AppendFormat("{0},", "Band");
            csv.AppendFormat("{0},", "Mode");
            csv.AppendFormat("{0},", "Rcvd");
            csv.AppendFormat("{0},", "Sent");
            csv.AppendFormat("{0},", "UTC End");
            csv.AppendFormat("{0}\r\n", "Exchange");

            foreach (QSO qso in qso_list)
            {
                string date = qso.timestamp.ToString("dd/MM/yyyy");
                string time = qso.timestamp.ToString("HH.mm");

                csv.AppendFormat("{0},", index++);
                csv.AppendFormat("{0},", date);
                csv.AppendFormat("{0},", time);
                csv.AppendFormat("{0},", qso.callsign);
                csv.AppendFormat("{0},", rem.GetEntity(qso.callsign));
                csv.AppendFormat("{0},", "");
                csv.AppendFormat("{0},", "");
                csv.AppendFormat("{0},", qso.band);
                csv.AppendFormat("{0},", qso.mode);
                csv.AppendFormat("{0},", qso.rst_rcvd);
                csv.AppendFormat("{0},", qso.rst_sent);
                csv.AppendFormat("{0},", time);
                csv.AppendFormat("{0}\r\n", qso.exchange);
            }
            return csv.ToString();
        }


        public static string getBareCallsign(string callsign)
        {
            return callsign;
            //string[] callParts = callsign.Split('/');
            //if (callParts.Length == 1) return callsign;
            //if (callParts.Length > 2) return callParts[1];
            //if (callParts.Length == 2)
            //{
            //    if (callParts[0].Length > callParts[1].Length) return callParts[0];
            //    return callParts[1];
            //}
            //return callsign;
        }

        public static async Task<string> SendMail(string from, string to, string subject, string body)
        {
            MailMessage mail = new MailMessage(from, to);
            SmtpClient client = new SmtpClient();
            client.Port = 25;
            client.DeliveryMethod = SmtpDeliveryMethod.Network;
            client.UseDefaultCredentials = false;
            client.Host = "host406.hostmonster.com";
            client.UseDefaultCredentials = false;
            client.Credentials = new System.Net.NetworkCredential("iarcorg", "Rw6Ach!@");

            mail.IsBodyHtml = true;
            mail.Subject = subject;
            mail.Body = body;

            try
            {
                await client.SendMailAsync(mail);
                return "email successfully sent";
            }
            catch (Exception)
            {
                return "Connection with server failed! Check your internet connection";
            }            
        }
    }
}
