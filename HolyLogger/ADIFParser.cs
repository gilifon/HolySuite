using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DXCC_Counter
{
    class ADIFParser
    {
        public string RawFile { get; set; }
        public string TableName { get; set; }
        public string Reference { get; set; }

        public IList<QSO> QSO_List { get { return _QSO_List; } }
        private IList<QSO> _QSO_List;

        public ProjectType Project { get; set; }

        //patterns
        private string address_pattern = @"<address:(\d{1,2})(?::[a-z]{1})?>";
        private string band_pattern = @"<band:(\d{1,2})(?::[a-z]{1})?>";
        private string call_pattern = @"<call:(\d{1,2})(?::[a-z]{1})?>";
        private string commant_pattern = @"<comment:(\d{1,2})(?::[a-z]{1})?>";
        private string cont_pattern = @"<cont:(\d{1,2})(?::[a-z]{1})?>";
        private string country_pattern = @"<country:(\d{1,2})(?::[a-z]{1})?>";
        private string cqz_pattern = @"<cqz:(\d{1,2})(?::[a-z]{1})?>";
        private string dxcc_pattern = @"<dxcc:(\d{1,2})(?::[a-z]{1})?>";
        private string email_pattern = @"<email:(\d{1,2})(?::[a-z]{1})?>";
        private string freq_pattern = @"<freq:(\d{1,2})(?::[a-z]{1})?>";
        private string gridsquare_pattern = @"<gridsquare:(\d{1,2})(?::[a-z]{1})?>";
        private string ituz_pattern = @"<ituz:(\d{1,2})(?::[a-z]{1})?>";
        private string mode_pattern = @"<mode:(\d{1,2})(?::[a-z]{1})?>";
        private string name_pattern = @"<name:(\d)(?::[a-z]{1})?>";
        private string pfx_pattern = @"<pfx:(\d{1,2})(?::[a-z]{1})?>";
        private string qth_pattern = @"<qth:(\d{1,2})(?::[a-z]{1})?>";
        private string rst_rcvd_pattern = @"<rst_rcvd:(\d{1,2})(?::[a-z]{1})?>";
        private string rst_sent_pattern = @"<rst_sent:(\d{1,2})(?::[a-z]{1})?>";
        private string timeoff_pattern = @"<time_off:(\d{1,2})(?::[a-z]{1})?>";
        private string timeon_pattern = @"<time_on:(\d{1,2})(?::[a-z]{1})?>";
        private string qso_date_pattern = @"<qso_date:(\d{1,2})(?::[a-z]{1})?>";

        public ADIFParser()
            : this("")
        {
        }

        public ADIFParser(string adif)
        {
            this.RawFile = adif;
            _QSO_List = new List<QSO>(200);
        }

        private void convertFreqToBand(QSO qso)
        {
            double parsedFreq;
            if (!double.TryParse(qso.freq, out parsedFreq)) return;
            if (parsedFreq < 30)
            {
                if (parsedFreq > 0 && parsedFreq < 2) qso.band = "160M";
                if (parsedFreq > 2 && parsedFreq < 5) qso.band = "80M";
                if (parsedFreq > 5 && parsedFreq < 8) qso.band = "40M";
                if (parsedFreq > 10 && parsedFreq < 11) qso.band = "30M";
                if (parsedFreq > 12 && parsedFreq < 16) qso.band = "20M";
                if (parsedFreq > 19 && parsedFreq < 23) qso.band = "15M";
                if (parsedFreq > 24 && parsedFreq < 25) qso.band = "15M";
                if (parsedFreq > 25 && parsedFreq < 30) qso.band = "10M";
            }
            else
            {
                if (parsedFreq > 0 && parsedFreq < 2000) qso.band = "160M";
                if (parsedFreq > 2000 && parsedFreq < 5000) qso.band = "80M";
                if (parsedFreq > 5000 && parsedFreq < 8000) qso.band = "40M";
                if (parsedFreq > 10000 && parsedFreq < 11000) qso.band = "30M";
                if (parsedFreq > 12000 && parsedFreq < 16000) qso.band = "20M";
                if (parsedFreq > 19000 && parsedFreq < 23000) qso.band = "15M";
                if (parsedFreq > 24000 && parsedFreq < 25000) qso.band = "12M";
                if (parsedFreq > 25000 && parsedFreq < 30000) qso.band = "10M";
            }

        }

        public bool Parse()
        {
            _QSO_List.Clear();
            //Remove Line breakers
            string oneLiner = Regex.Replace(RawFile, "\r\n", "");
            oneLiner = Regex.Replace(oneLiner, "\r", "");
            oneLiner = Regex.Replace(oneLiner, "\n", "");

            //Splite the Header
            string[] spliteHeader = Regex.Split(oneLiner, "<EOH>", RegexOptions.IgnoreCase);

            if (spliteHeader.Length < 2) return false;

            //Get the body
            string body = spliteHeader[1];

            //Splite body to lines
            string[] raw_qso_string_array = Regex.Split(body, "<EOR>", RegexOptions.IgnoreCase);

            foreach (string raw_qso in raw_qso_string_array)
            {
                //skip empty rows
                if (string.IsNullOrEmpty(raw_qso)) continue;

                QSO qso = new QSO();

                Regex regex = new Regex(address_pattern, RegexOptions.IgnoreCase);
                Match match = regex.Match(raw_qso);
                if (match.Success)
                {
                    qso.address = Regex.Split(raw_qso, address_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value)).Replace("'", "_");
                }

                regex = new Regex(band_pattern, RegexOptions.IgnoreCase);
                match = regex.Match(raw_qso);
                if (match.Success)
                {
                    qso.band = Regex.Split(raw_qso, band_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value)).Replace("'", "_");
                }

                regex = new Regex(call_pattern, RegexOptions.IgnoreCase);
                match = regex.Match(raw_qso);
                if (match.Success)
                {
                    qso.call = Regex.Split(raw_qso, call_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value)).Replace("'", "_");
                }

                regex = new Regex(commant_pattern, RegexOptions.IgnoreCase);
                match = regex.Match(raw_qso);
                if (match.Success)
                {
                    qso.comment = Regex.Split(raw_qso, commant_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value)).Replace("'", "_");
                }

                regex = new Regex(cont_pattern, RegexOptions.IgnoreCase);
                match = regex.Match(raw_qso);
                if (match.Success)
                {
                    qso.cont = Regex.Split(raw_qso, cont_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value)).Replace("'", "_");
                }

                regex = new Regex(country_pattern, RegexOptions.IgnoreCase);
                match = regex.Match(raw_qso);
                if (match.Success)
                {
                    qso.country = Regex.Split(raw_qso, country_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value)).Replace("'", "_");
                }

                regex = new Regex(cqz_pattern, RegexOptions.IgnoreCase);
                match = regex.Match(raw_qso);
                if (match.Success)
                {
                    qso.cqz = Regex.Split(raw_qso, cqz_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value)).Replace("'", "_");
                }

                regex = new Regex(dxcc_pattern, RegexOptions.IgnoreCase);
                match = regex.Match(raw_qso);
                if (match.Success)
                {
                    qso.dxcc = Regex.Split(raw_qso, dxcc_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value)).Replace("'", "_");
                }

                regex = new Regex(email_pattern, RegexOptions.IgnoreCase);
                match = regex.Match(raw_qso);
                if (match.Success)
                {
                    qso.email = Regex.Split(raw_qso, email_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value)).Replace("'", "_");
                }

                regex = new Regex(freq_pattern, RegexOptions.IgnoreCase);
                match = regex.Match(raw_qso);
                if (match.Success)
                {
                    qso.freq = Regex.Split(raw_qso, freq_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value)).Replace("'", "_");
                }

                regex = new Regex(gridsquare_pattern, RegexOptions.IgnoreCase);
                match = regex.Match(raw_qso);
                if (match.Success)
                {
                    qso.gridsquare = Regex.Split(raw_qso, gridsquare_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value)).Replace("'", "_");
                }

                regex = new Regex(ituz_pattern, RegexOptions.IgnoreCase);
                match = regex.Match(raw_qso);
                if (match.Success)
                {
                    qso.ituz = Regex.Split(raw_qso, ituz_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value)).Replace("'", "_");
                }

                regex = new Regex(mode_pattern, RegexOptions.IgnoreCase);
                match = regex.Match(raw_qso);
                if (match.Success)
                {
                    qso.mode = Regex.Split(raw_qso, mode_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value)).Replace("'", "_");
                }

                regex = new Regex(name_pattern, RegexOptions.IgnoreCase);
                match = regex.Match(raw_qso);
                if (match.Success)
                {
                    qso.name = Regex.Split(raw_qso, name_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value)).Replace("'", "_");
                }

                regex = new Regex(pfx_pattern, RegexOptions.IgnoreCase);
                match = regex.Match(raw_qso);
                if (match.Success)
                {
                    qso.pfx = Regex.Split(raw_qso, pfx_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value)).Replace("'", "_");
                }

                regex = new Regex(qth_pattern, RegexOptions.IgnoreCase);
                match = regex.Match(raw_qso);
                if (match.Success)
                {
                    qso.qth = Regex.Split(raw_qso, qth_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value)).Replace("'", "_");
                }

                regex = new Regex(rst_rcvd_pattern, RegexOptions.IgnoreCase);
                match = regex.Match(raw_qso);
                if (match.Success)
                {
                    qso.rst_rcvd = Regex.Split(raw_qso, rst_rcvd_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value)).Replace("'", "_");
                }

                regex = new Regex(rst_sent_pattern, RegexOptions.IgnoreCase);
                match = regex.Match(raw_qso);
                if (match.Success)
                {
                    qso.rst_sent = Regex.Split(raw_qso, rst_sent_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value)).Replace("'", "_");
                }

                regex = new Regex(timeoff_pattern, RegexOptions.IgnoreCase);
                match = regex.Match(raw_qso);
                if (match.Success)
                {
                    qso.time_off = Regex.Split(raw_qso, timeoff_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value)).Replace("'", "_");
                }

                regex = new Regex(timeon_pattern, RegexOptions.IgnoreCase);
                match = regex.Match(raw_qso);
                if (match.Success)
                {
                    qso.time_on = Regex.Split(raw_qso, timeon_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value)).Replace("'", "_");
                }

                regex = new Regex(qso_date_pattern, RegexOptions.IgnoreCase);
                match = regex.Match(raw_qso);
                if (match.Success)
                {
                    qso.qso_date = Regex.Split(raw_qso, qso_date_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value)).Replace("'", "_");
                }

                if (string.IsNullOrWhiteSpace(qso.band) && !string.IsNullOrWhiteSpace(qso.freq))
                {
                    convertFreqToBand(qso);
                }

                _QSO_List.Add(qso);
            }
            return true;
        }

        public string GenerateInsert()
        {
            //validations
            if (string.IsNullOrWhiteSpace(TableName)) return "";
            if (_QSO_List.Count == 0) return "";
            string refTable = "";

            switch (Project)
            {
                case ProjectType._4XFF:
                    refTable = "`wwff_ref`";
                    break;
                case ProjectType._4X4TRAIL:
                    refTable = "`section`";
                    break;
                default:
                    break;
            }

            StringBuilder sb = new StringBuilder("INSERT IGNORE INTO `", 500);
            sb.Append(TableName);
            sb.Append("` (`address`, `band`, `call`, `comment`, `cont`, `country`, `cqz`, `dxcc`, `email`, `freq`, `gridsquare`, `ituz`, `mode`, `name`, `pfx`, `qso_date`, `qth`, `rst_rcvd`, `rst_sent`, `time_off`, `time_on`, " + refTable + ") VALUES ");

            foreach (QSO qso in _QSO_List)
            {
                sb.Append("(");
                sb.Append("'"); sb.Append(qso.address); sb.Append("',");
                sb.Append("'"); sb.Append(qso.band); sb.Append("',");
                sb.Append("'"); sb.Append(qso.call); sb.Append("',");
                sb.Append("'"); sb.Append(qso.comment); sb.Append("',");
                sb.Append("'"); sb.Append(qso.cont); sb.Append("',");
                sb.Append("'"); sb.Append(qso.country); sb.Append("',");
                sb.Append("'"); sb.Append(qso.cqz); sb.Append("',");
                sb.Append("'"); sb.Append(qso.dxcc); sb.Append("',");
                sb.Append("'"); sb.Append(qso.email); sb.Append("',");
                sb.Append("'"); sb.Append(qso.freq); sb.Append("',");
                sb.Append("'"); sb.Append(qso.gridsquare); sb.Append("',");
                sb.Append("'"); sb.Append(qso.ituz); sb.Append("',");
                sb.Append("'"); sb.Append(qso.mode); sb.Append("',");
                sb.Append("'"); sb.Append(qso.name); sb.Append("',");
                sb.Append("'"); sb.Append(qso.pfx); sb.Append("',");
                sb.Append("'"); sb.Append(qso.qso_date); sb.Append("',");
                sb.Append("'"); sb.Append(qso.qth); sb.Append("',");
                sb.Append("'"); sb.Append(qso.rst_rcvd); sb.Append("',");
                sb.Append("'"); sb.Append(qso.rst_sent); sb.Append("',");
                sb.Append("'"); sb.Append(qso.time_off); sb.Append("',");
                sb.Append("'"); sb.Append(qso.time_on); sb.Append("',");
                sb.Append("'"); sb.Append(Reference); sb.Append("'),");
            }
            sb.Remove(sb.Length - 1, 1);
            return sb.ToString();
        }

        
    }

    internal class QSO
    {
        public string address { get; set; }
        public string band { get; set; }
        public string call { get; set; }
        public string comment { get; set; }
        public string cont { get; set; }
        public string country { get; set; }
        public string cqz { get; set; }
        public string dxcc { get; set; }
        public string email { get; set; }
        public string freq { get; set; }
        public string gridsquare { get; set; }
        public string ituz { get; set; }
        public string mode { get; set; }
        public string name { get; set; }
        public string pfx { get; set; }
        public string qso_date { get; set; }
        public string qth { get; set; }
        public string rst_rcvd { get; set; }
        public string rst_sent { get; set; }
        public string time_off { get; set; }
        public string time_on { get; set; }        
    }

    internal enum ProjectType
    {
        _4XFF = 0, _4X4TRAIL
    }
}
