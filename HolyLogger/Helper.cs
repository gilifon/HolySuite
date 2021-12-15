using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Xml.Linq;
using System.Device.Location;
using HolyParser;

namespace HolyLogger
{
    public static class Helper
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
                //System.Windows.Forms.MessageBox.Show("Login to QRZ service failed: " + ex.Message);
                SessionKey = "";
                return false;
            }
        }

        public static void SendHeartbeat(string callsign, string op_callsign, string frequency, string mode)
        {
            ServicePointManager.Expect100Continue = true;
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            if (string.IsNullOrEmpty(callsign) || string.IsNullOrEmpty(frequency) || string.IsNullOrEmpty(mode)) return;
            WebRequest request = WebRequest.Create("https://www.iarc.org/Holyland/Server/heartbeat.php?callsign=" + callsign + "&operator=" + op_callsign + "&frequency=" + frequency + "&mode=" + mode);
            try
            {
                var response = request.GetResponseAsync();
            }
            catch (Exception e)
            {

            }            
        }

        public static bool CheckForInternetConnection()
        {
            try
            {
                using (var client = new WebClient())
                using (client.OpenRead("http://clients3.google.com/generate_204"))
                {
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

    }
}
