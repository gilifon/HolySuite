using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace HolyParser
{
    public class QSO
    {
        [JsonProperty("id")]
        public int id { get; set; }
        public bool IsIsraeli { get; set; }
        public bool IsValid { get; set; }

        [JsonProperty("my_call")]
        public string MyCall { get; set; }

        [JsonProperty("callsign")]
        public string DXCall { get; set; }

        [JsonProperty("date")]
        public string Date { get; set; }

        [JsonProperty("time")]
        public string Time { get; set; }

        [JsonProperty("band")]
        public string Band { get; set; }

        [JsonProperty("mode")]
        public string Mode { get; set; }

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
        public string HASH { get; set; }
        public string ERROR { get; set; }

        [JsonProperty("rst_rcvd")]
        public string RST_RCVD { get; set; }

        [JsonProperty("rst_sent")]
        public string RST_SENT { get; set; }


        public void StandartizeQSO()
        {
            IsValid = false;
            IsIsraeli = HolyLogParser.IsIsraeliStation(DXCall);
            string pattern = @"([a-zA-Z]{1})[-/\\_ ]*([0-9]{1,2})[-/\\_ ]*([a-zA-Z]{2})";
            Regex regex = new Regex(pattern, RegexOptions.IgnoreCase);
            if (!string.IsNullOrEmpty(SRX))//srx not empty -> good, try match
            {
                Match match = regex.Match(SRX);
                if (match.Success) //srx matches grid
                {
                    this.SRX = match.Groups[1].Value + match.Groups[2].Value + match.Groups[3].Value;
                    IsValid = IsValidCall() && IsValidBand() && IsValidMode() && IsValidSRX() && IsValidDXCC() && IsIsraeli;
                    if (IsValid && IsIsraeli)
                        HASH = MyCall + DXCall + Band + Mode;
                }
                else //srx does NOT matche grid
                {
                    pattern = @"(\d+)";
                    regex = new Regex(pattern, RegexOptions.IgnoreCase);
                    match = regex.Match(SRX);
                    if (match.Success)
                    {
                        this.SRX = match.Groups[1].Value;
                        IsValid = IsValidCall() && IsValidBand() && IsValidMode() && IsValidSRX() && IsValidDXCC() && !IsIsraeli;
                        if (IsValid && !IsIsraeli)
                            HASH = MyCall + DXCall + Band + Mode;
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
            if (string.IsNullOrEmpty(Band) && !string.IsNullOrEmpty(Freq.Trim()))
            {
                Band = HolyLogParser.convertFreqToBand(Freq.Trim());
            }
            bool isValid = !string.IsNullOrEmpty(Band) && (Band.Contains("10M") || Band.Contains("15M") || Band.Contains("20M") || Band.Contains("40M") || Band.Contains("80M") || Band.Contains("160M"));
            if (!isValid) this.ERROR += "Band is not valid: " + Band + " - ";
            return isValid;

        }
        private bool IsValidMode()
        {
            return true;
            //bool isValid = !string.IsNullOrEmpty(Mode) && (Mode.ToLower().Contains("ph") || Mode.ToLower().Contains("fm") || Mode.ToLower().Contains("ry") || Mode.ToLower().Contains("ssb") || Mode.ToLower().Contains("lsb") || Mode.ToLower().Contains("usb") || Mode.ToLower().Contains("cw") || Mode.ToLower().Contains("rtty") || Mode.ToLower().Contains("psk") || Mode.ToLower().Contains("digi") || Mode.ToLower().Contains("ps") || Mode.ToLower().Contains("pk"));
            //if (!isValid) this.ERROR += "Mode is not valid: " + Mode + " - ";
            //return isValid;
        }
        private bool IsValidCall()
        {
            bool isValid = !string.IsNullOrEmpty(DXCall);
            if (!isValid) this.ERROR += "Call is empty -";
            return isValid;
        }
        private bool IsValidComment()
        {
            bool isValid = !string.IsNullOrEmpty(Comment);
            if (!isValid) this.ERROR += "Comment is empty -";
            return isValid;
        }
        private bool IsValidSRX()
        {
            bool isValid = !string.IsNullOrEmpty(SRX);
            if (!isValid) this.ERROR += "SRX is empty -";
            return isValid;
        }
        private bool IsValidDXCC()
        {
            //return true;
            bool isValid = !string.IsNullOrEmpty(DXCC);
            if (!isValid) this.ERROR += "DXCC is empty -";
            return isValid;
        }
    }
}
