using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
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
    }
}
