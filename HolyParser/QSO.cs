using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace HolyParser
{
    public class QSO : IEquatable<QSO>, INotifyPropertyChanged
    {
        [JsonProperty("id")]
        public int id { get; set; }
        public bool IsIsraeli { get; set; }
        public bool IsValid { get; set; }

        [JsonProperty("my_callsign")]
        public string MyCall { get; set; }

        [JsonProperty("dx_callsign")]
        public string DXCall { get; set; }

        [JsonProperty("date")]
        public string Date { get; set; }

        [JsonProperty("time")]
        public string Time { get; set; }

        [JsonProperty("band")]
        public string Band { get; set; }

        [JsonProperty("mode")]
        public string Mode { get; set; }

        [JsonProperty("submode")]
        public string SUBMode { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("country")]
        public string Country { get; set; }

        [JsonProperty("frequency")]
        public string Freq { get; set; }

        [JsonProperty("comment")]
        public string Comment { get; set; }

        public string DXCC { get; set; }

        // THE ADIF DXCC ENTITY NUMBER - the identity of the country, as opposed to its name.
        //
        // A name is not an identity: two databases spell the same country differently, either may
        // re-spell it, and a name nothing recognises reads as a country that no longer exists. The
        // number is fixed, unique and never reused, and it is what an award is actually counted on.
        //
        // 0 means "not known for this QSO" - either nothing could be resolved, or the contact belongs
        // to no entity at all (a station at sea counts for nobody, and ADIF says so with 0).
        [JsonProperty("dxcc_code")]
        public int DxccCode { get; set; }

        // For a grid cell: blank rather than "0" when the contact belongs to no entity. A zero sitting
        // in a column of country numbers reads as a country numbered zero, which is not what it means.
        [JsonIgnore]
        public string DxccCodeText => DxccCode > 0 ? DxccCode.ToString() : "";

        [JsonProperty("exchange")]
        public string SRX { get; set; }

        [JsonProperty("my_square")]
        public string STX { get; set; }

        [JsonProperty("my_locator")]
        public string MyLocator { get; set; }

        [JsonProperty("dx_locator")]
        public string DXLocator { get; set; }

        public string HASH { get; set; }
        public string ERROR { get; set; }

        [JsonProperty("rst_rcvd")]
        public string RST_RCVD { get; set; }

        [JsonProperty("rst_sent")]
        public string RST_SENT { get; set; }

        public bool IsAllowWARC { get; set; }

        [JsonProperty("prop_mode")]
        public string PROP_MODE { get; set; }

        [JsonProperty("sat_name")]
        public string SAT_NAME { get; set; }

        [JsonProperty("continent")]
        public string Continent { get; set; }

        [JsonProperty("cq_zone")]
        public string CQZone { get; set; }

        [JsonProperty("itu_zone")]
        public string ITUZone { get; set; }

        // The worked station's Primary Administrative Subdivision (ADIF STATE, e.g. a US state "CA").
        [JsonProperty("state")]
        public string State { get; set; }

        // The worked station's town (ADIF QTH, e.g. "Haifa"). QRZ.com has no field of that name - its
        // mailing address splits into addr1 (house number and street) and addr2 (the city), and addr2 is
        // what ADIF calls QTH, which is where the main form fills this from.
        [JsonProperty("qth")]
        public string Qth { get; set; }

        // ACTIVITY PROGRAM REFERENCES ("on the air" programs).
        //
        // ADIF gives its own field to exactly four programs - islands, summits, parks and nature
        // reserves - and hands everything else (castles, mills, lighthouses, and whatever is founded
        // next year) to the generic SIG / SIG_INFO pair. That is why there are six fields here and not
        // one per program: the list of programs has no end, so we store the four the standard
        // names and let the fifth pair carry the rest by name.
        //
        // All six describe the CONTACTED station. The MY_* counterparts (what the operator sends when
        // they are the one on the summit) are a separate matter and are not stored per QSO.

        // Islands on the Air, format CC-XXX where CC is a continent code, e.g. EU-005.
        [JsonProperty("iota")]
        public string Iota { get; set; }

        // Summits on the Air, e.g. W2/WE-003. Always contains a stroke.
        [JsonProperty("sota_ref")]
        public string SotaRef { get; set; }

        // Parks on the Air, e.g. K-0001. ADIF's POTARefList allows a COMMA-SEPARATED LIST here,
        // because one contact can be inside two overlapping parks at once.
        [JsonProperty("pota_ref")]
        public string PotaRef { get; set; }

        // World Wide Flora & Fauna, e.g. 4XFF-0016. Always contains "FF-".
        [JsonProperty("wwff_ref")]
        public string WwffRef { get; set; }

        // The name of any other program, e.g. WCA - and the reference within it, e.g. OK-00234.
        // Two fields rather than one so that everyone spells the program the same way and an award
        // check can group them; that is exactly why ADIF splits them.
        [JsonProperty("sig")]
        public string Sig { get; set; }

        [JsonProperty("sig_info")]
        public string SigInfo { get; set; }

        // The activity references as one short line for a log column: "IOTA EU-005", or several
        // separated by a space when a QSO carries more than one (a summit inside a park is normal).
        // Display only - never stored, never exported, hence [JsonIgnore].
        [JsonIgnore]
        public string ActivitySummary
        {
            get
            {
                var parts = new List<string>(5);
                if (!string.IsNullOrWhiteSpace(Iota)) parts.Add("IOTA " + Iota.Trim());
                if (!string.IsNullOrWhiteSpace(SotaRef)) parts.Add("SOTA " + SotaRef.Trim());
                if (!string.IsNullOrWhiteSpace(PotaRef)) parts.Add("POTA " + PotaRef.Trim());
                if (!string.IsNullOrWhiteSpace(WwffRef)) parts.Add("WWFF " + WwffRef.Trim());
                if (!string.IsNullOrWhiteSpace(Sig))
                    parts.Add((Sig.Trim() + " " + (SigInfo ?? string.Empty).Trim()).Trim());
                return string.Join("  ", parts.ToArray());
            }
        }

        [JsonProperty("operator")]
        public string Operator { get; set; }

        [JsonProperty("soapbox")]
        public string SOAPBOX { get; set; }

        // eQSL upload state: 0 = pending (waiting to be sent), 1 = sent/handled (won't be auto-sent),
        // 2 = permanently rejected by eQSL. Not serialized to the contest server.
        public int EqslStatus { get; set; }

        // QRZ.com Logbook real-time upload state: 0 = pending (waiting to be pushed), 1 = uploaded,
        // 2 = permanently rejected by QRZ (auth/subscription/bad record). Not serialized to the server.
        public int QrzStatus { get; set; }

        // The transaction id QRZ returns (LOGID) after a successful Logbook insert, kept locally next
        // to the QSO for cross-referencing or a future deletion routine.
        public string QrzLogId { get; set; }

        // LoTW (ARRL Logbook of the World) upload state: 0 = pending (waiting to be signed and
        // uploaded), 1 = uploaded (accepted by ARRL gateway), 2 = permanently rejected.
        // Not serialized to the contest server.
        public int LotwStatus { get; set; }

        // Club Log upload state: 0 = pending (waiting to be sent), 1 = uploaded (accepted by Club Log),
        // 2 = permanently rejected. Not serialized to the contest server.
        public int ClublogStatus { get; set; }

        // WHAT THE LOG FIXER HAS ALREADY DONE ABOUT THIS CONTACT.
        //
        //   0 - never reviewed. The Fixer reports it whenever it finds something.
        //   1 - reviewed and corrected. It was ticked and the correction was written.
        //   2 - reviewed and left as it was. He looked, and decided the log is right.
        //
        // 1 and 2 both mean silence. The Fixer has already put this row to him once and been answered,
        // and raising the same row on every run is how a useful check turns into a list nobody reads.
        // Only 0 is offered again - the Fixer's own "Include the ones I left" box brings the rest back
        // for anyone who wants to look at them a second time.
        //
        // NOT AN ADIF FIELD, and never exported: it is a note about this operator's own reviewing, not
        // a fact about the contact, and it means nothing in anybody else's log.
        [JsonIgnore]
        public int ReviewState { get; set; }

        // LoTW CONFIRMATION: 1 when the other station has confirmed this QSO in Logbook of The World.
        // Not the same thing as LotwStatus, which only says whether WE uploaded it - a QSO can be
        // uploaded for years and never confirmed. Set from the confirmations LoTW returns.
        public int LotwQslRcvd { get; set; }

        // The date LoTW recorded the confirmation (ADIF QSLRDATE, yyyyMMdd). Empty when unconfirmed.
        public string LotwQslRDate { get; set; }

        // 1 when the confirmed entity is a DELETED DXCC entity (East Germany, Czechoslovakia, ...).
        // Set from the DXCC code LoTW returns for the confirmation; meaningless unless LotwQslRcvd = 1.
        public int LotwDeletedEntity { get; set; }

        // Three-level LoTW status for sorting the LoTW column: confirmed-active (2), confirmed-deleted
        // (1), not-confirmed (0). Descending gives the operator's requested order.
        public int LotwStatusRank =>
            LotwQslRcvd == 1 ? (LotwDeletedEntity == 1 ? 1 : 2) : 0;

        // QRZ.com CONFIRMATION: 1 when QRZ says this QSO is confirmed (app_qrzlog_status = C). This is
        // a DIFFERENT, broader universe than LoTW - QRZ confirms on a QRZ-to-QRZ logbook match - so it
        // is tracked and shown independently of LotwQslRcvd. Set from the QRZ FETCH confirmations.
        public int QrzQslRcvd { get; set; }

        // The date QRZ recorded the confirmation (app_qrzlog_qsldate, yyyyMMdd). Empty when unconfirmed.
        public string QrzQslRDate { get; set; }

        // 1 when the QRZ-confirmed entity is a DELETED DXCC entity. Set from the DXCC code QRZ returns
        // for the confirmation; meaningless unless QrzQslRcvd = 1.
        public int QrzDeletedEntity { get; set; }

        // Three-level QRZ status for sorting the QRZ column, exactly like LotwStatusRank: confirmed-
        // active (2), confirmed-deleted (1), not-confirmed (0).
        public int QrzStatusRank =>
            QrzQslRcvd == 1 ? (QrzDeletedEntity == 1 ? 1 : 2) : 0;

        // eQSL CONFIRMATION: 1 when the QSO is confirmed in the eQSL In Box (EQSL_QSL_RCVD = Y). Tracked
        // separately from the eQSL UPLOAD state (EqslStatus). Set from the eQSL In Box download.
        public int EqslQslRcvd { get; set; }

        // The date eQSL recorded the confirmation (EQSL_QSLRDATE, yyyyMMdd). Empty when unconfirmed.
        public string EqslQslRDate { get; set; }

        // 1 when the eQSL-confirmed entity is a DELETED DXCC entity. eQSL's download carries no <DXCC>,
        // so this is resolved from the callsign via cty.dat (current-only) and is only approximate.
        public int EqslDeletedEntity { get; set; }

        // Three-level eQSL status for sorting the eQSL column, like LotwStatusRank.
        public int EqslStatusRank =>
            EqslQslRcvd == 1 ? (EqslDeletedEntity == 1 ? 1 : 2) : 0;

        // Club Log CONFIRMATION: 1 when the QSO comes back QSL_RCVD = Y/V in Club Log's getadif.php
        // whole-log export. Tracked separately from Club Log UPLOAD state. Unlike eQSL, Club Log's
        // export DOES carry a numeric <DXCC>, so the deleted-entity flag below is authoritative.
        public int ClublogQslRcvd { get; set; }

        // The date Club Log recorded the confirmation (QSLRDATE, yyyyMMdd). Empty when unconfirmed.
        public string ClublogQslRDate { get; set; }

        // 1 when the Club Log-confirmed entity is a DELETED DXCC entity. Set from the DXCC code Club Log
        // returns for the confirmation; meaningless unless ClublogQslRcvd = 1.
        public int ClublogDeletedEntity { get; set; }

        // Three-level Club Log status for sorting the Club Log column, like LotwStatusRank.
        public int ClublogStatusRank =>
            ClublogQslRcvd == 1 ? (ClublogDeletedEntity == 1 ? 1 : 2) : 0;

        // PAPER QSL CONFIRMATION: 1 when the operator has received this QSO's paper QSL card by post.
        // Unlike the other sources this is MANUAL - there is nothing to download - so it is edited
        // directly in the log grid (the checkbox binds to PaperQslConfirmed below). Exported to ADIF like
        // every other confirmation field so it round-trips.
        public int PaperQslRcvd { get; set; }

        // Two-way bool view of PaperQslRcvd for the editable grid checkbox. [JsonIgnore] so the contest
        // server payload is unchanged (PaperQslRcvd already carries the value for persistence/export).
        [JsonIgnore]
        public bool PaperQslConfirmed
        {
            get => PaperQslRcvd == 1;
            set => PaperQslRcvd = value ? 1 : 0;
        }

        // Two-level paper-QSL status for sorting the Paper QSL column. There is no deleted-entity split
        // (a manual paper card carries no DXCC code), so it is simply confirmed (2) or not (0).
        public int PaperQslStatusRank => PaperQslRcvd == 1 ? 2 : 0;

        // ── fields an operator's award and QSL record is made of ──────────

        // ADIF CREDIT_GRANTED: the awards the ARRL has actually GRANTED for this QSO, as the comma list
        // ADIF defines ("DXCC,DXCC-M,DXCC-CHAL,DXCC-5B,DXCC-20…").
        //
        // This is not a confirmation and must never be confused with one. A confirmation says the other
        // station agrees the contact happened; a granted credit says the ARRL has counted it towards an
        // award. They genuinely differ: measured on a real 28,366-QSO log, its owner had 325 current
        // entities granted, while his logger's award page said 326 - the extra one was Bouvet, confirmed
        // at LoTW but never submitted for credit. Only the log itself can tell you that, which is why
        // this field is worth keeping.
        public string CreditGranted { get; set; }

        // ADIF CNTY: the worked station's county ("Greenfield Park"), the unit the USA-CA award counts.
        public string Cnty { get; set; }

        // ADIF QSL_VIA: how a card travels for this station - a manager's callsign, "Bureau", "Direct".
        public string QslVia { get; set; }

        // ADIF QSLRDATE / QSL_SENT: when the paper card was RECEIVED (pairs with PaperQslRcvd, which only
        // says whether it arrived), and whether one has been SENT.
        public string QslRDate { get; set; }
        public string QslSent { get; set; }

        // ADIF CONTEST_ID: which contest this QSO belongs to ("CQ-WW-SSB"), so a contact's history is not
        // lost when it comes in from another program.
        public string ContestId { get; set; }

        // ADIF TIME_OFF / QSO_DATE_OFF: when the contact ENDED. HolyLogger records only a start time of
        // its own, so these are carried for imported QSOs rather than invented.
        public string TimeOff { get; set; }
        public string DateOff { get; set; }

        // EVERY OTHER ADIF FIELD OF THE IMPORTED RECORD THAT HOLYLOGGER HAS NO COLUMN FOR, kept verbatim as the
        // raw "<field:len>value" text in the order the source program wrote it.
        //
        // The point is that an operator's log must survive a trip through HolyLogger with nothing lost.
        // The importer understands ~36 fields; a Log4OM export carries 101, and measured on a real 28,366
        // QSO log, 64% of the file was being dropped on the floor - award credits (CREDIT_GRANTED), the
        // counties a USA-CA chase is built on, QSL routes, contest IDs, rig and antenna, years of work
        // that the operator can never get back once the original file is gone.
        //
        // Nothing is interpreted here: fields we do not model are simply carried, and written back out on
        // export. That is what makes the guarantee hold for fields no version of HolyLogger has heard of,
        // including ones ADIF has not defined yet. Empty for a QSO logged in HolyLogger itself, which has
        // no foreign fields to carry.
        //
        // [JsonIgnore] - the contest server has no use for it and its payloads stay exactly as they are.
        [JsonIgnore]
        public string ExtraAdif { get; set; }

        // Name of the log this QSO belongs to. Display-only: filled in by the upload-queue queries so the
        // (global) queue window can show which log each pending/dismissed QSO came from. Not persisted.
        public string LogName { get; set; }

        // TICKED IN THE LOG WORKSHOP'S SELECTION COLUMN. A screen state only: never saved, never exported
        // and never sent to the contest server, hence [JsonIgnore] exactly like PaperQslConfirmed.
        //
        // It lives on the QSO rather than in the window because a DataGrid recycles row containers as you
        // scroll: state kept on the row would move to whatever QSO that container is reused for, so a tick
        // made at the top of the log would reappear on an unrelated row further down.
        [JsonIgnore]
        public bool IsPicked
        {
            get => _isPicked;
            set
            {
                if (_isPicked == value) return;
                _isPicked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPicked)));
            }
        }
        private bool _isPicked;

        // Raised for IsPicked and nothing else. The rest of a QSO's fields are re-read when the grid
        // rebuilds a row, but a ticked row has to change colour the instant the box is clicked.
        public event PropertyChangedEventHandler PropertyChanged;

        public QSO()
        {
            IsAllowWARC = false;
        }

        // BUILT ONCE FOR THE PROGRAM, NOT ONCE FOR EVERY QSO.
        //
        // Both of these used to be constructed inside StandartizeQSO, which runs on every QSO as the
        // log is read - 10,946 of them on this operator's log, every time the program starts. Building
        // a Regex PARSES its pattern and builds a machine for it; doing that ten thousand times to
        // match a handful of characters is the expensive part of reading a log. The first one was even
        // built when SRX was empty and there was nothing to match at all.
        //
        // Same patterns, same options, so the answers are unchanged. Compiled because they are now
        // built once and used for the life of the program, which is exactly what Compiled is for.
        private static readonly Regex GridExchange = new Regex(
            @"([a-zA-Z]{1,2})[-/\\_ ]*([0-9]{1,2})[-/\\_ ]*([a-zA-Z]{2})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex FirstNumber = new Regex(
            @"(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public void StandartizeQSO()
        {
            IsValid = false;
            IsIsraeli = HolyLogParser.IsIsraeliStation(DXCall);
            Hash();
            if (!string.IsNullOrWhiteSpace(SRX))//srx not empty -> good, try match
            {
                Match match = GridExchange.Match(SRX);
                if (match.Success) //srx matches grid
                {
                    this.SRX = match.Groups[1].Value + match.Groups[2].Value + match.Groups[3].Value;
                    IsValid = IsValidCall() && IsValidBand() && IsValidMode() && IsValidSRX() && IsValidDXCC();// && IsIsraeli;
                }
                else //srx does NOT matche grid
                {
                    match = FirstNumber.Match(SRX);
                    if (match.Success)
                    {
                        this.SRX = match.Groups[1].Value;
                        IsValid = IsValidCall() && IsValidBand() && IsValidMode() && IsValidSRX() && IsValidDXCC();// && !IsIsraeli;
                    }
                    else
                    {
                        IsValid = false;
                    }
                }
            }
            else
            {
                IsValid = false;
            }
        }

        private bool IsValidBand()
        {
            if ((string.IsNullOrWhiteSpace(Band) || string.IsNullOrWhiteSpace(Band.ToLower().Replace("m", "")) || string.IsNullOrWhiteSpace(Band.ToLower().Replace("cm", ""))) && !string.IsNullOrWhiteSpace(Freq))
            {
                Band = HolyLogParser.convertFreqToBand(Freq.Trim());
            }
            if (!string.IsNullOrWhiteSpace(Band) && string.IsNullOrWhiteSpace(Freq))
            {
                Freq = HolyLogParser.convertBandToFreq(Band);
            }
            bool isValid = false;
            if (IsAllowWARC)
            {
                isValid = !string.IsNullOrWhiteSpace(Band) && (Band.Contains("13CM") || Band.Contains("70CM") || Band.Contains("2M") || Band.Contains("6M") || Band.Contains("10M") || Band.Contains("12M") || Band.Contains("15M") || Band.Contains("17M") || Band.Contains("20M") || Band.Contains("30M") || Band.Contains("40M") || Band.Contains("80M") || Band.Contains("160M"));
            }
            else
            {
                isValid = !string.IsNullOrWhiteSpace(Band) && (Band.Contains("13CM") || Band.Contains("70CM") || Band.Contains("2M") || Band.Contains("10M") || Band.Contains("15M") || Band.Contains("20M") || Band.Contains("40M") || Band.Contains("80M") || Band.Contains("160M"));
            }
            if (!isValid)
            {
                this.ERROR += "Band is not valid: " + Band + " - ";
            }
            return isValid;
        }
        private bool IsValidMode()
        {
            return true;
            //bool isValid = !string.IsNullOrWhiteSpace(Mode) && (Mode.ToLower().Contains("ph") || Mode.ToLower().Contains("fm") || Mode.ToLower().Contains("ry") || Mode.ToLower().Contains("ssb") || Mode.ToLower().Contains("lsb") || Mode.ToLower().Contains("usb") || Mode.ToLower().Contains("cw") || Mode.ToLower().Contains("rtty") || Mode.ToLower().Contains("psk") || Mode.ToLower().Contains("digi") || Mode.ToLower().Contains("ps") || Mode.ToLower().Contains("pk"));
            //if (!isValid) this.ERROR += "Mode is not valid: " + Mode + " - ";
            //return isValid;
        }
        private bool IsValidCall()
        {
            bool isValid = !string.IsNullOrWhiteSpace(DXCall);
            if (!isValid) this.ERROR += "Call is empty -";
            return isValid;
        }
        private bool IsValidSRX()
        {
            bool isValid = !string.IsNullOrWhiteSpace(SRX);
            if (!isValid) this.ERROR += "SRX is empty -";
            return isValid;
        }
        private bool IsValidDXCC()
        {
            //return true;
            bool isValid = !string.IsNullOrWhiteSpace(DXCC);
            if (!isValid) this.ERROR += "DXCC is empty -";
            return isValid;
        }
        // WHAT MAKES TWO RECORDS THE SAME CONTACT, for the whole program: the callsign worked, the
        // date, the band, the mode and the MINUTE. Null when the record is too incomplete to identify,
        // which is safer than matching it to the wrong QSO.
        //
        // IT LIVES HERE so that there is one of it. The import's merge, Tools > Remove Duplicates and
        // the Log Fixer all answer to this rule through DataAccess.MatchKey; the "Import Duplicates"
        // option used to answer to a different one of its own (HASH - no time at all), so a file
        // holding one station twice on one band and mode on one day lost the second contact, however
        // many hours apart, while the Log Fixer looking at the same two called them two proper
        // contacts. One program, one answer.
        //
        // NOT the frequency, the station callsign or the operator - deliberately. A file exported by
        // another program rounds the frequency and often carries no operator at all, so demanding
        // those would make every re-import look new and DOUBLE the log.
        public static string MatchKey(QSO q)
        {
            if (q == null) return null;
            string call = (q.DXCall ?? string.Empty).Trim();
            string date = (q.Date ?? string.Empty).Trim();
            string band = (q.Band ?? string.Empty).Trim();
            string mode = (q.Mode ?? string.Empty).Trim();
            if (call.Length == 0 || date.Length == 0) return null;

            // The date sometimes arrives as "yyyyMMdd HHmmss"; only the day identifies the contact.
            int space = date.IndexOf(' ');
            if (space > 0) date = date.Substring(0, space);

            // "HHmmss" or "HHmm" -> "HHmm". A record with no readable time keeps an empty slot, so it
            // can still only ever match another record that has none either.
            string time = (q.Time ?? string.Empty).Trim();
            if (time.Length > 4) time = time.Substring(0, 4);

            return call.ToUpperInvariant() + "|" + date + "|" + band.ToUpperInvariant() + "|"
                   + mode.ToUpperInvariant() + "|" + time;
        }

        private void Hash()
        {
            string mycall = !string.IsNullOrWhiteSpace(MyCall) ? MyCall : "MyCall";
            string dxcall = IsValidCall() ? DXCall : "DXCall";
            string band = IsValidBand() ? Band : "Band";
            string mode = IsValidMode() ? Mode : "Mode";

            HASH = mycall + dxcall + band + mode + Date;// + SRX + STX;
        }
        // SOAPBOX used to be filled on every new QSO with "<GUID> <UTC ticks>" as a unique stamp, and
        // nothing ever read it back. It cost the QSO its soapbox - an official ADIF field for the
        // operator's OWN words - and that machine ID travelled out in every export and every upload,
        // as well as showing up in the editor as if the operator had typed it. The generator is gone;
        // a soapbox is now either what an imported file carried or what the operator writes.
        //
        // The IDs already in the database stay where they are, and this recognises them so they can be
        // treated as the empty field they really are - not displayed, not exported.
        public static bool IsGeneratedSoapboxId(string soapbox)
        {
            if (string.IsNullOrWhiteSpace(soapbox)) return false;
            string[] parts = soapbox.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 1 || parts.Length > 2) return false;
            Guid ignored;
            if (!Guid.TryParse(parts[0], out ignored)) return false;
            if (parts.Length == 1) return true;
            foreach (char c in parts[1]) if (!char.IsDigit(c)) return false;   // the ticks half
            return true;
        }

        // The soapbox as something worth showing: the generated IDs read as nothing at all.
        public static string SoapboxText(string soapbox)
        {
            return IsGeneratedSoapboxId(soapbox) ? string.Empty : soapbox;
        }
        // IS THIS THE VERY SAME QSO? - which is not the same question as "are these the same contact?".
        //
        // It used to answer with HASH: station, callsign, band, mode and DATE, no time. So two real
        // contacts with one station on one band and mode on one day - 06:00 and 19:00 - were ONE QSO to
        // every list that asked, and picking one row picked the other with it.
        //
        // Worse, it did not even answer the same way twice. GetHashCode was never written to match, so
        // a List said "the same" (it asks Equals) while a HashSet and Distinct said "two" (they ask
        // GetHashCode first and never reach Equals). One question, two answers, in one run.
        //
        // A STORED QSO IS ITS ROW. Every QSO the database holds has its own Id, and that is what makes
        // it itself: it is how the Log Fixer can say "delete row 812, keep row 44" about two rows that
        // are duplicates of each other - which is exactly the case where the duplicate rule cannot tell
        // them apart, because saying they are the same contact is its whole purpose.
        //
        // A record not yet stored has no Id (0), and there the only honest answer is the object itself.
        // Two parsed records that look alike are still two records; whether they are the same CONTACT is
        // asked of QSO.MatchKey, by the code whose business that is.
        public bool Equals(QSO other)
        {
            if (ReferenceEquals(this, other)) return true;
            if (other is null) return false;
            if (id != 0 || other.id != 0) return id == other.id;
            return false;   // neither is stored: only the same object is the same QSO
        }

        public override bool Equals(object obj) { return Equals(obj as QSO); }

        // MUST agree with Equals, or the answer depends on which kind of list is asking - which is the
        // fault this pair was written to end. A stored QSO hashes by its row; an unstored one keeps the
        // object's own hash, so it can only ever match itself.
        public override int GetHashCode()
        {
            return id != 0 ? id : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
        }
    }
}
