﻿using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace HolyParser
{
    public class QSO : IEquatable<QSO>
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

        [JsonProperty("operator")]
        public string Operator { get; set; }

        [JsonProperty("soapbox")]
        public string SOAPBOX { get; set; }

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
        public void GenerateSoapBox()
        {
            SOAPBOX = Guid.NewGuid().ToString() + " " + DateTime.UtcNow.Ticks.ToString();
        }
        public bool Equals(QSO other)
        {
            return (this.HASH == other.HASH);
        }
    }
}
