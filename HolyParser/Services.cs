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
            adif.AppendLine("<adif_ver:5>3.1.4");
            adif.AppendLine("<programid:10>HolyLogger");
            adif.AppendFormat("<programversion:{0}>{1}", version.Length, version);
            adif.AppendLine();
            adif.AppendLine("<eoh>");
            adif.AppendLine();

            foreach (QSO qso in qso_list)
            {
                string[] datetime = string.IsNullOrWhiteSpace(qso.Date) ? new string[] { "", "" } : qso.Date.Split(new char[] { ' ' });
                if (datetime.Length > 1)
                {
                    qso.Date = datetime[0];
                    qso.Time = datetime[1];
                }

                if (!string.IsNullOrWhiteSpace(qso.MyCall)) adif.AppendFormat("<station_callsign:{0}>{1}", qso.MyCall.Length, qso.MyCall);
                if (!string.IsNullOrWhiteSpace(qso.DXCall)) adif.AppendFormat("<call:{0}>{1}", qso.DXCall.Length, qso.DXCall);
                if (!string.IsNullOrWhiteSpace(qso.Name)) adif.AppendFormat("<name:{0}>{1}", qso.Name.Length, qso.Name);
                if (!string.IsNullOrWhiteSpace(qso.Country)) adif.AppendFormat("<country:{0}>{1}", qso.Country.Length, qso.Country);
                if (!string.IsNullOrWhiteSpace(qso.CQZone)) adif.AppendFormat("<cqz:{0}>{1}", qso.CQZone.Length, qso.CQZone);
                if (!string.IsNullOrWhiteSpace(qso.ITUZone)) adif.AppendFormat("<ituz:{0}>{1}", qso.ITUZone.Length, qso.ITUZone);
                if (!string.IsNullOrWhiteSpace(qso.Freq)) adif.AppendFormat("<freq:{0}>{1}", qso.Freq.Length, qso.Freq);
                if (!string.IsNullOrWhiteSpace(qso.Band)) adif.AppendFormat("<band:{0}>{1}", qso.Band.Length, qso.Band);
                if (!string.IsNullOrWhiteSpace(qso.Mode)) adif.AppendFormat("<mode:{0}>{1}", qso.Mode.Length, qso.Mode);
                if (!string.IsNullOrWhiteSpace(qso.SUBMode)) adif.AppendFormat("<submode:{0}>{1}", qso.SUBMode.Length, qso.SUBMode);
                if (!string.IsNullOrWhiteSpace(qso.RST_RCVD)) adif.AppendFormat("<rst_rcvd:{0}>{1}", qso.RST_RCVD.Length, qso.RST_RCVD);
                if (!string.IsNullOrWhiteSpace(qso.RST_SENT)) adif.AppendFormat("<rst_sent:{0}>{1}", qso.RST_SENT.Length, qso.RST_SENT);
                if (!string.IsNullOrWhiteSpace(qso.Date)) adif.AppendFormat("<qso_date:{0}>{1}", qso.Date.Length, qso.Date);
                if (!string.IsNullOrWhiteSpace(qso.Time)) adif.AppendFormat("<time_on:{0}>{1}", qso.Time.Length, qso.Time);
                if (!string.IsNullOrWhiteSpace(qso.Time)) adif.AppendFormat("<time_off:{0}>{1}", qso.Time.Length, qso.Time);
                if (!string.IsNullOrWhiteSpace(qso.Comment)) adif.AppendFormat("<comment:{0}>{1}", qso.Comment.Length, qso.Comment);
                if (!string.IsNullOrWhiteSpace(qso.MyLocator)) adif.AppendFormat("<my_gridsquare:{0}>{1}", qso.MyLocator.Length, qso.MyLocator);
                if (!string.IsNullOrWhiteSpace(qso.DXLocator)) adif.AppendFormat("<gridsquare:{0}>{1}", qso.DXLocator.Length, qso.DXLocator);
                if (!string.IsNullOrWhiteSpace(qso.Operator)) adif.AppendFormat("<operator:{0}>{1}", qso.Operator.Length, qso.Operator);
                if (!string.IsNullOrWhiteSpace(qso.SRX)) adif.AppendFormat("<srx_string:{0}>{1}", qso.SRX.Length, qso.SRX);
                if (!string.IsNullOrWhiteSpace(qso.STX)) adif.AppendFormat("<stx_string:{0}>{1}", qso.STX.Length, qso.STX);
                if (!string.IsNullOrWhiteSpace(qso.SRX)) adif.AppendFormat("<sig:{0}>{1}", qso.SRX.Length, qso.SRX);
                if (!string.IsNullOrWhiteSpace(qso.STX)) adif.AppendFormat("<my_sig:{0}>{1}", qso.STX.Length, qso.STX);
                if (!string.IsNullOrWhiteSpace(qso.PROP_MODE)) adif.AppendFormat("<prop_mode:{0}>{1}", qso.PROP_MODE.Length, qso.PROP_MODE);
                else if (qso.Band == "13CM") adif.AppendFormat("<prop_mode:{0}>{1}", 3, "SAT");
                if (!string.IsNullOrWhiteSpace(qso.SAT_NAME)) adif.AppendFormat("<sat_name:{0}>{1}", qso.SAT_NAME.Length, qso.SAT_NAME);
                else if (qso.Band == "13CM") adif.AppendFormat("<sat_name:{0}>{1}", 6, "QO-100");
                if (!string.IsNullOrWhiteSpace(qso.SOAPBOX)) adif.AppendFormat("<soapbox:{0}>{1}", qso.SOAPBOX.Length, qso.SOAPBOX);
                // LoTW sent status, so the upload queue survives an export/re-import round trip.
                adif.AppendFormat("<lotw_qsl_sent:1>{0}", qso.LotwStatus == 1 ? "Y" : "N");
                adif.AppendLine("<eor>");
            }

            return adif.ToString();
        }

        public static string GenerateCabrillo(IEnumerable<QSO> qso_list, Contester participant)
        {
            string _convert_mode(string mode)
            {
                if (mode.Trim().ToLower() == "ssb")
                    return "ph";
                return mode;
            }
            
            string version = Assembly.GetExecutingAssembly().GetName().Version.ToString();

            StringBuilder cbr = new StringBuilder(200);
            cbr.AppendLine("START-OF-LOG: 3.0");
            if (!string.IsNullOrEmpty(participant.Contest)) { cbr.AppendFormat("CONTEST: {0}", participant.Contest); cbr.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.Callsign)) {cbr.AppendFormat("CALLSIGN: {0}", participant.Callsign); cbr.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.Email)) { cbr.AppendFormat("EMAIL: {0}", participant.Email); cbr.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.Location)) {cbr.AppendFormat("LOCATION: {0}", participant.Location); cbr.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.Category_Operator)) {cbr.AppendFormat("CATEGORY-OPERATOR: {0}", participant.Category_Operator); cbr.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.Category_Assisted)) {cbr.AppendFormat("CATEGORY-ASSISTED: {0}", participant.Category_Assisted); cbr.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.Category_Band)) {cbr.AppendFormat("CATEGORY-BAND: {0}", participant.Category_Band); cbr.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.Category_Power)) {cbr.AppendFormat("CATEGORY-POWER: {0}", participant.Category_Power); cbr.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.Category_Mode)) {cbr.AppendFormat("CATEGORY-MODE: {0}", participant.Category_Mode); cbr.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.Category_Transmitter)) { cbr.AppendFormat("CATEGORY-TRANSMITTER: {0}", participant.Category_Transmitter); cbr.AppendLine(); }
            //if (!string.IsNullOrEmpty(participant.Category_Overlay)) { cbr.AppendFormat("CATEGORY-OVERLAY: {0}", participant.Category_Overlay); cbr.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.Grid)) {cbr.AppendFormat("GRID-LOCATOR: {0}", participant.Grid); cbr.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.Score)) {cbr.AppendFormat("CLAIMED-SCORE: {0}", participant.Score); cbr.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.Club)) {cbr.AppendFormat("CLUB: {0}", participant.Club); cbr.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.Name)) {cbr.AppendFormat("NAME: {0}", participant.Name); cbr.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.Address)) {cbr.AppendFormat("ADDRESS: {0}", participant.Address); cbr.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.City)) {cbr.AppendFormat("ADDRESS-CITY: {0}", participant.City); cbr.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.Country)) {cbr.AppendFormat("ADDRESS-COUNTRY: {0}", participant.Country); cbr.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.Operators)) {cbr.AppendFormat("OPERATORS: {0}", participant.Operators); cbr.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.Soapbox)) {cbr.AppendFormat("SOAPBOX: {0}", participant.Soapbox); cbr.AppendLine(); }

            foreach (QSO qso in qso_list)
            {
                string[] datetime = string.IsNullOrWhiteSpace(qso.Date) ? new string[] { "", "" } : qso.Date.Split(new char[] { ' ' });
                if (datetime.Length > 1)
                {
                    qso.Date = datetime[0];
                    qso.Time = datetime[1];
                }

                cbr.Append("QSO: ");
                if (qso.Freq != null) cbr.AppendFormat("{0} ", qso.Freq);
                if (qso.Mode != null) cbr.AppendFormat("{0} ", _convert_mode(qso.Mode));
                if (qso.Date != null) cbr.AppendFormat("{0} ", qso.Date);
                if (qso.Time != null) cbr.AppendFormat("{0} ", qso.Time);
                if (qso.MyCall != null) cbr.AppendFormat("{0} ", qso.MyCall);
                if (qso.RST_SENT != null) cbr.AppendFormat("{0} ", qso.RST_SENT);
                if (qso.STX != null) cbr.AppendFormat("{0} ", qso.STX);
                if (qso.DXCall != null) cbr.AppendFormat("{0} ", qso.DXCall);
                if (qso.RST_RCVD != null) cbr.AppendFormat("{0} ", qso.RST_RCVD);
                if (qso.SRX != null) cbr.AppendFormat("{0} ", qso.SRX);
                cbr.AppendLine();
            }
            cbr.AppendLine("END-OF-LOG:");
            return cbr.ToString();
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

    
}
