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

        public void StandartizeQSO()
        {
            IsValid = false;
            IsIsraeli = HolyLogParser.IsIsraeliStation(DXCall);
            Hash();
            string pattern = @"([a-zA-Z]{1,2})[-/\\_ ]*([0-9]{1,2})[-/\\_ ]*([a-zA-Z]{2})";
            Regex regex = new Regex(pattern, RegexOptions.IgnoreCase);
            if (!string.IsNullOrWhiteSpace(SRX))//srx not empty -> good, try match
            {
                Match match = regex.Match(SRX);
                if (match.Success) //srx matches grid
                {
                    this.SRX = match.Groups[1].Value + match.Groups[2].Value + match.Groups[3].Value;
                    IsValid = IsValidCall() && IsValidBand() && IsValidMode() && IsValidSRX() && IsValidDXCC();// && IsIsraeli;
                }
                else //srx does NOT matche grid
                {
                    pattern = @"(\d+)";
                    regex = new Regex(pattern, RegexOptions.IgnoreCase);
                    match = regex.Match(SRX);
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
        public bool Equals(QSO other)
        {
            return (this.HASH == other.HASH);
        }
    }
}
