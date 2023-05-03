using DXCCManager;
using HolyParser;
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

namespace HolyParser
{
    public static class Services
    {        
        public static HamQTH getHamQth(string callsign)
        {
            HamQTH hqth = new HamQTH();
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
                    IEnumerable<XElement> property = xDoc.Root.Descendants(xDoc.Root.GetDefaultNamespace‌​() + "callsign");
                    if (property.Count() > 0)
                        hqth.Callsign = property.FirstOrDefault().Value;

                    property = xDoc.Root.Descendants(xDoc.Root.GetDefaultNamespace‌​() + "name");
                    if (property.Count() > 0)
                        hqth.Name = property.FirstOrDefault().Value;

                    property = xDoc.Root.Descendants(xDoc.Root.GetDefaultNamespace‌​() + "details");
                    if (property.Count() > 0)
                        hqth.Details = property.FirstOrDefault().Value;

                    property = xDoc.Root.Descendants(xDoc.Root.GetDefaultNamespace‌​() + "continent");
                    if (property.Count() > 0)
                        hqth.Continent = property.FirstOrDefault().Value;

                    property = xDoc.Root.Descendants(xDoc.Root.GetDefaultNamespace‌​() + "utc");
                    if (property.Count() > 0)
                        hqth.Utc = property.FirstOrDefault().Value;

                    property = xDoc.Root.Descendants(xDoc.Root.GetDefaultNamespace‌​() + "waz");
                    if (property.Count() > 0)
                        hqth.Waz = property.FirstOrDefault().Value;

                    property = xDoc.Root.Descendants(xDoc.Root.GetDefaultNamespace‌​() + "itu");
                    if (property.Count() > 0)
                        hqth.Itu = property.FirstOrDefault().Value;

                    property = xDoc.Root.Descendants(xDoc.Root.GetDefaultNamespace‌​() + "lat");
                    if (property.Count() > 0)
                        hqth.Lat = property.FirstOrDefault().Value;

                    property = xDoc.Root.Descendants(xDoc.Root.GetDefaultNamespace‌​() + "lng");
                    if (property.Count() > 0)
                        hqth.Lng = property.FirstOrDefault().Value;

                    property = xDoc.Root.Descendants(xDoc.Root.GetDefaultNamespace‌​() + "adif");
                    if (property.Count() > 0)
                        hqth.Adif = property.FirstOrDefault().Value;

                    return hqth;
                }
                catch (Exception)
                {
                    return hqth;
                }
            }
            else
            {
                return hqth;
            }
        }

        public static string GenerateAdif(IEnumerable<QSO> qso_list)
        {
            string version = Assembly.GetExecutingAssembly().GetName().Version.ToString();

            StringBuilder adif = new StringBuilder(200);
            adif.AppendLine("<ADIF_VERS:3>3.1 ");
            adif.AppendLine("<PROGRAMID:10>HolyLogger ");
            //adif.AppendLine("<PROGRAMVERSION:15>Version 1.0.0.0 ");
            adif.AppendFormat("<PROGRAMVERSION:{0}>{1} ", version.Length, version);
            adif.AppendLine();
            adif.AppendLine("<EOH>");
            adif.AppendLine();

            foreach (QSO qso in qso_list)
            {
                string[] datetime = string.IsNullOrWhiteSpace(qso.Date) ? new string[] { "", "" } : qso.Date.Split(new char[] { ' ' });
                if (datetime.Length > 1)
                {
                    qso.Date = datetime[0];
                    qso.Time = datetime[1];
                }

                if (qso.MyCall != null) adif.AppendFormat("<station_callsign:{0}>{1} ", qso.MyCall.Length, qso.MyCall);
                if (qso.DXCall != null) adif.AppendFormat("<call:{0}>{1} ", qso.DXCall.Length, qso.DXCall);
                if (qso.Name != null) adif.AppendFormat("<name:{0}>{1} ", qso.Name.Length, qso.Name);
                if (qso.Country != null) adif.AppendFormat("<country:{0}>{1} ", qso.Country.Length, qso.Country);
                if (qso.Freq != null) adif.AppendFormat("<freq:{0}>{1} ", qso.Freq.Length, qso.Freq);
                if (qso.Band != null) adif.AppendFormat("<band:{0}>{1} ", qso.Band.Length + 1, qso.Band);
                if (qso.Mode != null) adif.AppendFormat("<mode:{0}>{1} ", qso.Mode.Length, qso.Mode);
                if (qso.SUBMode != null) adif.AppendFormat("<submode:{0}>{1} ", qso.SUBMode.Length, qso.SUBMode);
                if (qso.RST_RCVD != null) adif.AppendFormat("<rst_rcvd:{0}>{1} ", qso.RST_RCVD.Length, qso.RST_RCVD);
                if (qso.RST_SENT != null) adif.AppendFormat("<rst_sent:{0}>{1} ", qso.RST_SENT.Length, qso.RST_SENT);
                if (qso.Date != null) adif.AppendFormat("<qso_date:{0}>{1} ", qso.Date.Length, qso.Date);
                if (qso.Time != null) adif.AppendFormat("<time_on:{0}>{1} ", qso.Time.Length, qso.Time);
                if (qso.Time != null) adif.AppendFormat("<time_off:{0}>{1} ", qso.Time.Length, qso.Time);
                if (qso.Comment != null) adif.AppendFormat("<comment:{0}>{1} ", qso.Comment.Length, qso.Comment);
                if (qso.MyLocator != null) adif.AppendFormat("<my_gridsquare:{0}>{1} ", qso.MyLocator.Length, qso.MyLocator);
                if (qso.DXLocator != null) adif.AppendFormat("<gridsquare:{0}>{1} ", qso.DXLocator.Length, qso.DXLocator);
                if (qso.Operator != null) adif.AppendFormat("<operator:{0}>{1} ", qso.Operator.Length, qso.Operator);
                if (qso.SRX != null) adif.AppendFormat("<srx_string:{0}>{1} ", qso.SRX.Length, qso.SRX);
                if (qso.STX != null) adif.AppendFormat("<stx_string:{0}>{1} ", qso.STX.Length, qso.STX);
                if (qso.SRX != null) adif.AppendFormat("<sig:{0}>{1} ", qso.SRX.Length, qso.SRX);
                if (qso.STX != null) adif.AppendFormat("<my_sig:{0}>{1} ", qso.STX.Length, qso.STX);
                if (qso.PROP_MODE != null) adif.AppendFormat("<prop_mode:{0}>{1} ", qso.PROP_MODE.Length, qso.PROP_MODE);
                else if (qso.Band == "13CM") adif.AppendFormat("<prop_mode:{0}>{1} ", 3, "SAT");
                if (qso.SAT_NAME != null) adif.AppendFormat("<sat_name:{0}>{1} ", qso.SAT_NAME.Length, qso.SAT_NAME);
                else if (qso.Band == "13CM") adif.AppendFormat("<sat_name:{0}>{1} ", 6, "QO-100");
                if (qso.SOAPBOX != null) adif.AppendFormat("<soapbox:{0}>{1} ", qso.SOAPBOX.Length, qso.SOAPBOX);
                adif.AppendLine("<EOR>");
            }

            return adif.ToString();
        }

        public static string GenerateCabrillo(IEnumerable<QSO> qso_list, Contester participant)
        {
            string version = Assembly.GetExecutingAssembly().GetName().Version.ToString();

            StringBuilder adif = new StringBuilder(200);
            adif.AppendLine("START-OF-LOG: 3.0");
            if (!string.IsNullOrEmpty(participant.Contest)) { adif.AppendFormat("CONTEST: {0}", participant.Contest); adif.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.Callsign)) {adif.AppendFormat("CALLSIGN: {0}", participant.Callsign); adif.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.Location)) {adif.AppendFormat("LOCATION: {0}", participant.Location); adif.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.Category_Operator)) {adif.AppendFormat("CATEGORY-OPERATOR: {0}", participant.Category_Operator); adif.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.Category_Assisted)) {adif.AppendFormat("CATEGORY-ASSISTED: {0}", participant.Category_Assisted); adif.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.Category_Band)) {adif.AppendFormat("CATEGORY-BAND: {0}", participant.Category_Band); adif.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.Category_Power)) {adif.AppendFormat("CATEGORY-POWER: {0}", participant.Category_Power); adif.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.Category_Mode)) {adif.AppendFormat("CATEGORY-MODE: {0}", participant.Category_Mode); adif.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.Category_Transmitter)) {adif.AppendFormat("CATEGORY-TRANSMITTER: {0}", participant.Category_Transmitter); adif.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.Grid)) {adif.AppendFormat("GRID-LOCATOR: {0}", participant.Grid); adif.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.Score)) {adif.AppendFormat("CLAIMED-SCORE: {0}", participant.Score); adif.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.Club)) {adif.AppendFormat("CLUB: {0}", participant.Club); adif.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.Name)) {adif.AppendFormat("NAME: {0}", participant.Name); adif.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.Address)) {adif.AppendFormat("ADDRESS: {0}", participant.Address); adif.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.City)) {adif.AppendFormat("ADDRESS-CITY: {0}", participant.City); adif.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.Country)) {adif.AppendFormat("ADDRESS-COUNTRY: {0}", participant.Country); adif.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.Operators)) {adif.AppendFormat("OPERATORS: {0}", participant.Operators); adif.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.Soapbox)) {adif.AppendFormat("SOAPBOX: {0}", participant.Soapbox); adif.AppendLine(); }

            foreach (QSO qso in qso_list)
            {
                string[] datetime = string.IsNullOrWhiteSpace(qso.Date) ? new string[] { "", "" } : qso.Date.Split(new char[] { ' ' });
                if (datetime.Length > 1)
                {
                    qso.Date = datetime[0];
                    qso.Time = datetime[1];
                }

                adif.Append("QSO: ");
                if (qso.Freq != null) adif.AppendFormat("{0} ", qso.Freq);
                if (qso.Mode != null) adif.AppendFormat("{0} ", qso.Mode);
                if (qso.Date != null) adif.AppendFormat("{0} ", qso.Date);
                if (qso.Time != null) adif.AppendFormat("{0} ", qso.Time);
                if (qso.MyCall != null) adif.AppendFormat("{0} ", qso.MyCall);
                if (qso.RST_SENT != null) adif.AppendFormat("{0} ", qso.RST_SENT);
                if (qso.STX != null) adif.AppendFormat("{0} ", qso.STX);
                if (qso.DXCall != null) adif.AppendFormat("{0} ", qso.DXCall);
                if (qso.RST_RCVD != null) adif.AppendFormat("{0} ", qso.RST_RCVD);
                if (qso.SRX != null) adif.AppendFormat("{0} ", qso.SRX);
                adif.AppendLine();
            }
            adif.AppendLine("END-OF-LOG:");
            return adif.ToString();
        }
        public static string GenerateCSV(IEnumerable<QSO> qso_list)
        {
            StringBuilder csv = new StringBuilder(200);
            EntityResolver rem = new EntityResolver();

            int index = 1;

            csv.AppendFormat("{0},", "No");
            csv.AppendFormat("{0},", "Station Callsign");
            csv.AppendFormat("{0},", "Operator");
            csv.AppendFormat("{0},", "DX Callsign");
            csv.AppendFormat("{0},", "Band");
            csv.AppendFormat("{0},", "Mode");
            csv.AppendFormat("{0},", "Date");
            csv.AppendFormat("{0},", "UTC");                        
            csv.AppendFormat("{0},", "RST Rcvd");
            csv.AppendFormat("{0},", "RST Sent");
            csv.AppendFormat("{0},", "Country");
            csv.AppendFormat("{0},", "Name");
            csv.AppendFormat("{0},", "QTH");
            csv.AppendFormat("{0}\r\n", "Exchange");

            foreach (QSO qso in qso_list)
            {
                string date = qso.Date;
                string time = qso.Time;

                csv.AppendFormat("{0},", index++);
                csv.AppendFormat("{0},", qso.MyCall);
                csv.AppendFormat("{0},", qso.Operator);
                csv.AppendFormat("{0},", qso.DXCall);
                csv.AppendFormat("{0},", qso.Band);
                csv.AppendFormat("{0},", qso.Mode);
                csv.AppendFormat("{0},", date);
                csv.AppendFormat("{0},", time);
                csv.AppendFormat("{0},", qso.RST_RCVD);
                csv.AppendFormat("{0},", qso.RST_SENT);
                csv.AppendFormat("{0},", rem.GetDXCC(qso.DXCall).Name);
                csv.AppendFormat("{0},", qso.Name);
                csv.AppendFormat("{0},", "");
                csv.AppendFormat("{0}\r\n", qso.SRX);
            }
            return csv.ToString();
        }

        public static string getBareCallsign(string callsign)
        {
            string[] callParts = callsign.Trim().Split('/');
            if (callParts.Length == 1) return callsign;
            if (callParts.Length > 2) return callParts[1];
            if (callParts.Length == 2)
            {
                if (callParts[0].Length > callParts[1].Length) return callParts[0];
                return callParts[1].Trim();
            }
            return callsign;
        }

    }

    public class HamQTH
    {
        public string Callsign { get; set; }
        public string Name { get; set; }
        public string Details { get; set; }
        public string Continent { get; set; }
        public string Utc { get; set; }
        public string Waz { get; set; }
        public string Itu { get; set; }
        public string Lat { get; set; }
        public string Lng { get; set; }
        public string Adif { get; set; }
    }

    public class Contester
    {
        public string Contest { get; set; }
        public string Callsign { get; set; }
        public string Name { get; set; }
        public string Email { get; set; }
        public string Category_Operator { get; set; }
        public string Category_Band { get; set; }
        public string Category_Mode { get; set; }
        public string Category_Power { get; set; }
        public string Location { get; set; }
        public string Category_Assisted { get; set; }
        public string Category_Transmitter { get; set; }
        public string Grid { get; set; }
        public string Score { get; set; }
        public string Club { get; set; }
        public string Address { get; set; }
        public string City { get; set; }
        public string Country { get; set; }
        public string Operators { get; set; }
        public string Soapbox { get; set; }
    }
}
