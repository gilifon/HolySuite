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

        // contestId (optional) tags every exported QSO with <contest_id:…>. Defaults to null so all
        // existing callers produce byte-identical output; only the contest-log file exports pass it.
        //
        // includeImportedFields writes back the fields an imported QSO carried that HolyLogger has no
        // column for (QSO.ExtraAdif), so a log that came in from another program leaves again intact.
        // OFF by default, and deliberately so: this same method builds the payload UPLOADED to LoTW,
        // eQSL, QRZ and Club Log, and those services must be sent the fields they expect and nothing
        // else - TQSL in particular rejects records carrying fields it does not know. Only the exports
        // that write a FILE for the operator turn it on.
        public static string GenerateAdif(IEnumerable<QSO> qso_list, string contestId = null,
                                          bool includeImportedFields = false)
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
                // The imported fields this QSO carries, and where its record starts in the buffer - the
                // de-duplication below compares the two so no field can be written twice.
                string imported = includeImportedFields ? qso.ExtraAdif : null;
                if (string.IsNullOrWhiteSpace(imported)) imported = null;
                int recordStart = adif.Length;

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
                // ADIF <dxcc> is the ARRL entity NUMBER - what award programs actually match on; the
                // country name beside it is only for people to read. Looked up from the name in this
                // very record so the two can never contradict each other, and left out entirely when no
                // database recognises that wording rather than guessing a number for it.
                // Judged against the QSO's own date, so a wording that belongs to a country which had
                // already ceased to exist cannot be attached to a modern contact.
                int dxccCode = 0;
                try { dxccCode = CountryLookup.Shared.EntityCodeForCountry(qso.Country, CountryLookup.QsoDate(qso.Date)); }
                catch (Exception) { dxccCode = 0; }
                if (dxccCode > 0)
                {
                    string code = dxccCode.ToString();
                    adif.AppendFormat("<dxcc:{0}>{1}", code.Length, code);
                }
                if (!string.IsNullOrWhiteSpace(qso.CQZone)) adif.AppendFormat("<cqz:{0}>{1}", qso.CQZone.Length, qso.CQZone);
                if (!string.IsNullOrWhiteSpace(qso.ITUZone)) adif.AppendFormat("<ituz:{0}>{1}", qso.ITUZone.Length, qso.ITUZone);
                // ADIF STATE: the worked station's primary administrative subdivision (e.g. "CA").
                if (!string.IsNullOrWhiteSpace(qso.State)) adif.AppendFormat("<state:{0}>{1}", qso.State.Length, qso.State);
                // ADIF QTH: the worked station's town.
                if (!string.IsNullOrWhiteSpace(qso.Qth))
                {
                    string qth = qso.Qth.Trim();
                    adif.AppendFormat("<qth:{0}>{1}", qth.Length, qth);
                }
                // ADIF <freq> is MHz. Fall back to the stored text if it cannot be parsed at all, so a
                // value we simply don't understand is passed through rather than silently dropped.
                string freqMhz = HolyLogParser.NormalizeFreqToMhz(qso.Freq) ?? qso.Freq;
                if (!string.IsNullOrWhiteSpace(freqMhz)) adif.AppendFormat("<freq:{0}>{1}", freqMhz.Length, freqMhz);
                if (!string.IsNullOrWhiteSpace(qso.Band)) adif.AppendFormat("<band:{0}>{1}", qso.Band.Length, qso.Band);
                if (!string.IsNullOrWhiteSpace(qso.Mode)) adif.AppendFormat("<mode:{0}>{1}", qso.Mode.Length, qso.Mode);
                if (!string.IsNullOrWhiteSpace(qso.SUBMode)) adif.AppendFormat("<submode:{0}>{1}", qso.SUBMode.Length, qso.SUBMode);
                if (!string.IsNullOrWhiteSpace(qso.RST_RCVD)) adif.AppendFormat("<rst_rcvd:{0}>{1}", qso.RST_RCVD.Length, qso.RST_RCVD);
                if (!string.IsNullOrWhiteSpace(qso.RST_SENT)) adif.AppendFormat("<rst_sent:{0}>{1}", qso.RST_SENT.Length, qso.RST_SENT);
                if (!string.IsNullOrWhiteSpace(qso.Date)) adif.AppendFormat("<qso_date:{0}>{1}", qso.Date.Length, qso.Date);
                if (!string.IsNullOrWhiteSpace(qso.Time)) adif.AppendFormat("<time_on:{0}>{1}", qso.Time.Length, qso.Time);
                // <time_off>: the real end time when the QSO was imported with one, otherwise a copy of
                // time_on - HolyLogger does not record an end time of its own, and the field has always
                // gone out. An imported truth always beats our copy.
                string timeOff = !string.IsNullOrWhiteSpace(qso.TimeOff) ? qso.TimeOff.Trim() : qso.Time;
                if (!string.IsNullOrWhiteSpace(timeOff) && !CarriesField(imported, "time_off"))
                    adif.AppendFormat("<time_off:{0}>{1}", timeOff.Length, timeOff);
                if (!string.IsNullOrWhiteSpace(qso.DateOff))
                    adif.AppendFormat("<qso_date_off:{0}>{1}", qso.DateOff.Trim().Length, qso.DateOff.Trim());
                if (!string.IsNullOrWhiteSpace(qso.Comment)) adif.AppendFormat("<comment:{0}>{1}", qso.Comment.Length, qso.Comment);
                if (!string.IsNullOrWhiteSpace(qso.MyLocator)) adif.AppendFormat("<my_gridsquare:{0}>{1}", qso.MyLocator.Length, qso.MyLocator);
                if (!string.IsNullOrWhiteSpace(qso.DXLocator)) adif.AppendFormat("<gridsquare:{0}>{1}", qso.DXLocator.Length, qso.DXLocator);
                if (!string.IsNullOrWhiteSpace(qso.Operator)) adif.AppendFormat("<operator:{0}>{1}", qso.Operator.Length, qso.Operator);
                if (!string.IsNullOrWhiteSpace(qso.SRX)) adif.AppendFormat("<srx_string:{0}>{1}", qso.SRX.Length, qso.SRX);
                if (!string.IsNullOrWhiteSpace(qso.STX)) adif.AppendFormat("<stx_string:{0}>{1}", qso.STX.Length, qso.STX);
                // ACTIVITY PROGRAM REFERENCES, in the fields ADIF names for them.
                if (!string.IsNullOrWhiteSpace(qso.Iota)) adif.AppendFormat("<iota:{0}>{1}", qso.Iota.Trim().Length, qso.Iota.Trim());
                if (!string.IsNullOrWhiteSpace(qso.SotaRef)) adif.AppendFormat("<sota_ref:{0}>{1}", qso.SotaRef.Trim().Length, qso.SotaRef.Trim());
                if (!string.IsNullOrWhiteSpace(qso.PotaRef)) adif.AppendFormat("<pota_ref:{0}>{1}", qso.PotaRef.Trim().Length, qso.PotaRef.Trim());
                if (!string.IsNullOrWhiteSpace(qso.WwffRef)) adif.AppendFormat("<wwff_ref:{0}>{1}", qso.WwffRef.Trim().Length, qso.WwffRef.Trim());

                // <sig> can only be written once, and two things want it: the activity program the
                // operator entered (which is what the field is FOR - "the contacted station's special
                // activity or interest group") and the long-standing lines below, which put the contest
                // exchange there instead. The program wins when there is one; with no program
                // entered - every QSO logged before this existed, and every contest QSO - the file comes
                // out byte for byte as it always did.
                bool sigTakenByActivity = !string.IsNullOrWhiteSpace(qso.Sig);
                if (sigTakenByActivity)
                {
                    string sig = qso.Sig.Trim();
                    adif.AppendFormat("<sig:{0}>{1}", sig.Length, sig);
                    if (!string.IsNullOrWhiteSpace(qso.SigInfo))
                    {
                        string info = qso.SigInfo.Trim();
                        adif.AppendFormat("<sig_info:{0}>{1}", info.Length, info);
                    }
                }
                if (!sigTakenByActivity && !string.IsNullOrWhiteSpace(qso.SRX)) adif.AppendFormat("<sig:{0}>{1}", qso.SRX.Length, qso.SRX);
                if (!string.IsNullOrWhiteSpace(qso.STX)) adif.AppendFormat("<my_sig:{0}>{1}", qso.STX.Length, qso.STX);

                // ===== PROPOSED contest_id tag — pending Holyland score-processor approval (2026-06) =====
                // Additive only: identifies which contest this log is for, e.g. <contest_id:8>HOLYLAND.
                // Every other field above is unchanged, so existing parsers are unaffected. The value
                // comes from the active contest's Cabrillo name and is passed in only by the full-log
                // file exports. TO REVERT IF NOT APPROVED: delete this block and the `contestId`
                // parameter, and drop the argument at the call sites (search "PROPOSED contest_id").
                if (!string.IsNullOrWhiteSpace(contestId))
                    adif.AppendFormat("<contest_id:{0}>{1}", contestId.Length, contestId);
                // ===== end PROPOSED contest_id tag =====
                if (!string.IsNullOrWhiteSpace(qso.PROP_MODE)) adif.AppendFormat("<prop_mode:{0}>{1}", qso.PROP_MODE.Length, qso.PROP_MODE);
                else if (qso.Band == "13CM") adif.AppendFormat("<prop_mode:{0}>{1}", 3, "SAT");
                if (!string.IsNullOrWhiteSpace(qso.SAT_NAME)) adif.AppendFormat("<sat_name:{0}>{1}", qso.SAT_NAME.Length, qso.SAT_NAME);
                else if (qso.Band == "13CM") adif.AppendFormat("<sat_name:{0}>{1}", 6, "QO-100");
                // Only a real soapbox goes out. Older QSOs hold a generated "<GUID> <ticks>" ID in this
                // field, and it was being uploaded to LoTW / eQSL / QRZ / Club Log as if it were the
                // operator's own commentary.
                string soapbox = QSO.SoapboxText(qso.SOAPBOX);
                if (!string.IsNullOrWhiteSpace(soapbox)) adif.AppendFormat("<soapbox:{0}>{1}", soapbox.Length, soapbox);
                // LoTW sent status, so the upload queue survives an export/re-import round trip.
                adif.AppendFormat("<lotw_qsl_sent:1>{0}", qso.LotwStatus == 1 ? "Y" : "N");

                // Confirmation status, using ONLY official ADIF fields so any logger reads the file and it
                // stays standards-compliant (no HolyLogger-private APP_ fields). ADIF defines received-
                // confirmation fields for exactly three sources - paper (QSL_RCVD), eQSL (EQSL_QSL_RCVD)
                // and LoTW (LOTW_QSL_RCVD). It has NO field for QRZ.com or Club Log confirmations, so those
                // are intentionally NOT written here: they live in the database and are restored by re-
                // downloading from those services. Deleted-entity flags are DERIVED (from the DXCC entity),
                // so they are not written either - the app recomputes them from the entity when needed.
                if (qso.LotwQslRcvd == 1)
                {
                    adif.Append("<lotw_qsl_rcvd:1>Y");
                    if (!string.IsNullOrWhiteSpace(qso.LotwQslRDate))
                        adif.AppendFormat("<lotw_qslrdate:{0}>{1}", qso.LotwQslRDate.Length, qso.LotwQslRDate);
                }
                if (qso.EqslQslRcvd == 1)
                {
                    adif.Append("<eqsl_qsl_rcvd:1>Y");
                    if (!string.IsNullOrWhiteSpace(qso.EqslQslRDate))
                        adif.AppendFormat("<eqsl_qslrdate:{0}>{1}", qso.EqslQslRDate.Length, qso.EqslQslRDate);
                }
                if (qso.PaperQslRcvd == 1)
                {
                    // A received paper / bureau card is exactly what the standard QSL_RCVD field means.
                    adif.Append("<qsl_rcvd:1>Y");
                }

                // THE OPERATOR'S AWARD AND QSL RECORD, in the official ADIF fields ADIF defines for them.
                // CREDIT_GRANTED especially: it is the ARRL's own record of what has been awarded for this
                // contact, it cannot be recovered from anywhere else once lost, and it belongs in every
                // file the operator takes out of here.
                if (!string.IsNullOrWhiteSpace(qso.CreditGranted))
                {
                    string cg = qso.CreditGranted.Trim();
                    adif.AppendFormat("<credit_granted:{0}>{1}", cg.Length, cg);
                }
                if (!string.IsNullOrWhiteSpace(qso.Cnty))
                {
                    string cnty = qso.Cnty.Trim();
                    adif.AppendFormat("<cnty:{0}>{1}", cnty.Length, cnty);
                }
                if (!string.IsNullOrWhiteSpace(qso.QslVia))
                {
                    string via = qso.QslVia.Trim();
                    adif.AppendFormat("<qsl_via:{0}>{1}", via.Length, via);
                }
                if (!string.IsNullOrWhiteSpace(qso.QslRDate))
                {
                    string qrd = qso.QslRDate.Trim();
                    adif.AppendFormat("<qslrdate:{0}>{1}", qrd.Length, qrd);
                }
                if (!string.IsNullOrWhiteSpace(qso.QslSent))
                {
                    string qs = qso.QslSent.Trim();
                    adif.AppendFormat("<qsl_sent:{0}>{1}", qs.Length, qs);
                }
                // The contest this QSO belongs to. An ACTIVE contest names the file being exported (the
                // contestId argument above), so it wins; otherwise the QSO's own imported contest stands.
                if (string.IsNullOrWhiteSpace(contestId) && !string.IsNullOrWhiteSpace(qso.ContestId))
                {
                    string cid = qso.ContestId.Trim();
                    adif.AppendFormat("<contest_id:{0}>{1}", cid.Length, cid);
                }

                // Last in the record: everything this QSO arrived with that HolyLogger has no column for.
                AppendImportedFields(adif, recordStart, imported);

                adif.AppendLine("<eor>");
            }

            return adif.ToString();
        }

        // True when the carried text holds that field, so the exporter can stand aside and let the
        // operator's own imported value be the one that goes out.
        private static bool CarriesField(string imported, string field)
        {
            if (string.IsNullOrEmpty(imported)) return false;
            foreach (System.Text.RegularExpressions.Match m in HolyLogParser.AdifTagPattern.Matches(imported))
                if (string.Equals(m.Groups[1].Value, field, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        // Writes the imported-but-unmodelled fields into the record just built, skipping any field the
        // record ALREADY carries.
        //
        // The skip list is read from the record itself rather than kept as a hand-maintained list of what
        // this method writes: the two can then never drift apart, and a field is suppressed only when a
        // real value for it was actually written (my_sig, for instance, is only written for a QSO that
        // has a contest exchange - so an imported my_sig still goes out on every QSO that has none).
        private static void AppendImportedFields(StringBuilder adif, int recordStart, string imported)
        {
            if (string.IsNullOrEmpty(imported)) return;

            string written = adif.ToString(recordStart, adif.Length - recordStart);
            var already = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Text.RegularExpressions.Match m in HolyLogParser.AdifTagPattern.Matches(written))
                already.Add(m.Groups[1].Value);

            foreach (System.Text.RegularExpressions.Match m in HolyLogParser.AdifTagPattern.Matches(imported))
            {
                if (already.Contains(m.Groups[1].Value)) continue;

                int len;
                if (!int.TryParse(m.Groups[2].Value, out len) || len < 0) continue;
                int start = m.Index + m.Length;
                if (start + len > imported.Length) continue;   // stored text is already normalised

                adif.Append(imported, m.Index, m.Length + len);
            }
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
            cbr.AppendLine("CREATED-BY: HolyLogger " + version);
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
            if (!string.IsNullOrEmpty(participant.Category_Station)) { cbr.AppendFormat("CATEGORY-STATION: {0}", participant.Category_Station); cbr.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.Category_Time)) { cbr.AppendFormat("CATEGORY-TIME: {0}", participant.Category_Time); cbr.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.Category_Overlay)) { cbr.AppendFormat("CATEGORY-OVERLAY: {0}", participant.Category_Overlay); cbr.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.Certificate)) { cbr.AppendFormat("CERTIFICATE: {0}", participant.Certificate); cbr.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.Grid)) {cbr.AppendFormat("GRID-LOCATOR: {0}", participant.Grid); cbr.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.Score)) {cbr.AppendFormat("CLAIMED-SCORE: {0}", participant.Score); cbr.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.Club)) {cbr.AppendFormat("CLUB: {0}", participant.Club); cbr.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.Name)) {cbr.AppendFormat("NAME: {0}", participant.Name); cbr.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.Address)) {cbr.AppendFormat("ADDRESS: {0}", participant.Address); cbr.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.City)) {cbr.AppendFormat("ADDRESS-CITY: {0}", participant.City); cbr.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.StateProvince)) {cbr.AppendFormat("ADDRESS-STATE-PROVINCE: {0}", participant.StateProvince); cbr.AppendLine(); }
            if (!string.IsNullOrEmpty(participant.PostalCode)) {cbr.AppendFormat("ADDRESS-POSTALCODE: {0}", participant.PostalCode); cbr.AppendLine(); }
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
                csv.AppendFormat("{0},", CountryLookup.Shared.Resolve(qso.DXCall, CountryLookup.QsoDate(qso.Date)).Name);
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
