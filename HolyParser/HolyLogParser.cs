using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MoreLinq;
using DXCCManager;
using System.Globalization;
using ModeManager;
using System;

namespace HolyParser
{
    public class HolyLogParser
    {
        private int _result;
        public int Result { get { return _result; } }

        private int _mults;
        public int Mults { get { return _mults; } }

        private int _workedSquers;
        public int WorkedSquers { get { return _workedSquers; } }

        private int _validQsos;
        public int ValidQsos { get { return _validQsos; } }

        private int _points;
        public int Points { get { return _points; } }

        private string _description;
        public string Description { get { return _description; } }

        private bool _isParseDuplicates = true;
        public bool IsParseDuplicates { get { return _isParseDuplicates; } set { _isParseDuplicates = value; } }

        private bool _isParseWARC = true;
        public bool IsParseWARC { get { return _isParseWARC; } set { _isParseWARC = value; } }

        public Operator LogType { get; set; }

        public static List<string> validSquares = new List<string>() { "H-03-AK", "H-04-AK", "H-05-AK", "H-06-AK", "J-03-AK", "J-04-AK", "J-05-AK", "J-06-AK", "J-07-AK", "K-03-AK", "K-04-AK", "K-05-AK", "K-06-AK", "L-03-AK", "L-04-AK", "L-05-AK", "M-04-AK", "B-21-AS", "C-18-AS", "C-19-AS", "C-20-AS", "C-21-AS", "D-16-AS", "D-17-AS", "D-18-AS", "D-19-AS", "D-20-AS", "D-21-AS", "E-16-AS", "E-17-AS", "E-18-AS", "E-19-AS", "E-20-AS", "E-21-AS", "F-17-AS", "F-18-AS", "F-19-AS", "F-20-AS", "F-21-AS", "G-19-AS", "G-20-AS", "G-21-AS", "A-21-AZ", "A-22-AZ", "A-23-AZ", "B-20-AZ", "B-21-AZ", "B-22-AZ", "B-23-AZ", "C-19-AZ", "C-20-AZ", "C-21-AZ", "Z-22-AZ", "Z-23-AZ", "H-18-BL", "H-19-BL", "J-18-BL", "J-19-BL", "K-17-BL", "K-18-BL", "K-19-BL", "K-20-BL", "K-21-BL", "L-17-BL", "L-18-BL", "L-19-BL", "L-20-BL", "L-21-BL", "M-17-BL", "M-18-BL", "A-22-BS", "A-23-BS", "A-24-BS", "A-25-BS", "A-26-BS", "A-27-BS", "B-21-BS", "B-22-BS", "B-23-BS", "B-24-BS", "B-25-BS", "B-26-BS", "B-27-BS", "B-28-BS", "B-29-BS", "C-21-BS", "C-22-BS", "C-23-BS", "C-24-BS", "C-25-BS", "C-26-BS", "C-27-BS", "C-28-BS", "C-29-BS", "C-30-BS", "C-31-BS", "C-32-BS", "C-33-BS", "D-20-BS", "D-21-BS", "D-22-BS", "D-23-BS", "D-24-BS", "D-25-BS", "D-26-BS", "D-27-BS", "D-28-BS", "D-29-BS", "D-30-BS", "D-31-BS", "D-32-BS", "D-33-BS", "D-34-BS", "D-35-BS", "E-21-BS", "E-22-BS", "E-23-BS", "E-24-BS", "E-25-BS", "E-26-BS", "E-27-BS", "E-28-BS", "E-29-BS", "E-30-BS", "E-31-BS", "E-32-BS", "E-33-BS", "E-34-BS", "E-35-BS", "E-36-BS", "E-37-BS", "E-38-BS", "F-21-BS", "F-22-BS", "F-23-BS", "F-24-BS", "F-25-BS", "F-26-BS", "F-27-BS", "F-28-BS", "F-29-BS", "F-30-BS", "F-31-BS", "F-32-BS", "F-33-BS", "F-34-BS", "F-35-BS", "F-36-BS", "F-37-BS", "F-38-BS", "F-39-BS", "F-40-BS", "F-41-BS", "F-42-BS", "F-43-BS", "G-22-BS", "G-23-BS", "G-24-BS", "G-25-BS", "G-26-BS", "G-27-BS", "G-28-BS", "G-29-BS", "G-30-BS", "G-31-BS", "G-32-BS", "G-33-BS", "G-34-BS", "G-35-BS", "G-36-BS", "G-37-BS", "G-38-BS", "G-39-BS", "G-40-BS", "G-41-BS", "G-42-BS", "G-43-BS", "H-22-BS", "H-23-BS", "H-24-BS", "H-25-BS", "H-26-BS", "H-27-BS", "H-28-BS", "H-29-BS", "H-30-BS", "H-31-BS", "H-32-BS", "H-33-BS", "H-34-BS", "H-35-BS", "H-36-BS", "H-37-BS", "H-38-BS", "H-39-BS", "H-40-BS", "H-41-BS", "J-22-BS", "J-23-BS", "J-24-BS", "J-25-BS", "J-26-BS", "J-27-BS", "J-28-BS", "J-29-BS", "J-30-BS", "J-31-BS", "J-32-BS", "J-33-BS", "J-34-BS", "J-35-BS", "J-36-BS", "J-37-BS", "K-21-BS", "K-22-BS", "K-23-BS", "K-24-BS", "K-25-BS", "K-26-BS", "K-27-BS", "K-28-BS", "K-29-BS", "K-30-BS", "L-20-BS", "L-21-BS", "L-22-BS", "L-23-BS", "L-24-BS", "L-25-BS", "L-26-BS", "L-27-BS", "L-28-BS", "M-25-BS", "M-26-BS", "F-21-HB", "F-22-HB", "G-19-HB", "G-20-HB", "G-21-HB", "G-22-HB", "H-18-HB", "H-19-HB", "H-20-HB", "H-21-HB", "H-22-HB", "J-19-HB", "J-20-HB", "J-21-HB", "J-22-HB", "K-19-HB", "K-20-HB", "K-21-HB", "K-22-HB", "L-21-HB", "F-09-HD", "F-10-HD", "G-06-HD", "G-07-HD", "G-08-HD", "G-09-HD", "G-10-HD", "H-07-HD", "H-08-HD", "H-09-HD", "H-10-HD", "H-11-HD", "J-09-HD", "J-10-HD", "G-06-HF", "G-07-HF", "H-05-HF", "H-06-HF", "H-07-HF", "H-08-HF", "J-05-HF", "J-06-HF", "J-07-HF", "N-01-HG", "N-03-HG", "N-04-HG", "N-05-HG", "O-00-HG", "O-01-HG", "O-02-HG", "O-03-HG", "O-04-HG", "O-05-HG", "O-06-HG", "O-07-HG", "P-00-HG", "P-01-HG", "P-02-HG", "P-03-HG", "P-04-HG", "P-05-HG", "P-06-HG", "P-07-HG", "Q-03-HG", "Q-04-HG", "Q-05-HG", "F-10-HS", "F-11-HS", "F-12-HS", "F-13-HS", "G-10-HS", "G-11-HS", "G-12-HS", "H-11-HS", "H-12-HS", "H-10-JN", "J-09-JN", "J-10-JN", "J-11-JN", "K-09-JN", "K-10-JN", "K-11-JN", "L-09-JN", "L-10-JN", "L-11-JN", "L-12-JN", "M-10-JN", "M-11-JN", "M-12-JN", "F-17-JS", "F-18-JS", "F-19-JS", "G-16-JS", "G-17-JS", "G-18-JS", "G-19-JS", "H-16-JS", "H-17-JS", "H-18-JS", "H-19-JS", "J-16-JS", "J-17-JS", "J-18-JS", "K-16-JS", "K-17-JS", "K-18-JS", "L-05-KT", "L-06-KT", "L-07-KT", "M-05-KT", "M-06-KT", "M-07-KT", "M-08-KT", "N-04-KT", "N-05-KT", "N-06-KT", "N-07-KT", "N-08-KT", "O-05-KT", "O-06-KT", "O-07-KT", "F-12-PT", "F-13-PT", "F-14-PT", "F-15-PT", "G-12-PT", "G-13-PT", "G-14-PT", "G-15-PT", "H-12-PT", "H-14-PT", "H-15-PT", "G-15-RA", "G-16-RA", "G-17-RA", "H-14-RA", "H-15-RA", "H-16-RA", "H-17-RA", "J-14-RA", "J-15-RA", "J-16-RA", "J-17-RA", "K-14-RA", "K-15-RA", "K-16-RA", "K-17-RA", "L-14-RA", "L-15-RA", "L-16-RA", "L-17-RA", "D-16-RH", "D-17-RH", "E-15-RH", "E-16-RH", "E-17-RH", "E-18-RH", "F-15-RH", "F-16-RH", "F-17-RH", "F-18-RH", "F-15-RM", "F-16-RM", "F-17-RM", "G-15-RM", "G-16-RM", "G-17-RM", "H-15-RM", "H-16-RM", "J-11-SM", "J-12-SM", "J-13-SM", "K-11-SM", "K-12-SM", "K-13-SM", "K-14-SM", "L-12-SM", "L-13-SM", "L-14-SM", "E-13-TA", "E-14-TA", "E-15-TA", "F-13-TA", "F-14-TA", "F-15-TA", "G-12-TK", "G-13-TK", "G-14-TK", "H-10-TK", "H-11-TK", "H-12-TK", "H-13-TK", "H-14-TK", "J-10-TK", "J-11-TK", "J-12-TK", "J-13-TK", "J-14-TK", "K-13-TK", "K-14-TK", "L-11-YN", "L-12-YN", "L-13-YN", "L-14-YN", "L-15-YN", "L-16-YN", "L-17-YN", "L-19-YN", "L-20-YN", "L-21-YN", "M-10-YN", "M-11-YN", "M-12-YN", "M-13-YN", "M-14-YN", "M-15-YN", "M-16-YN", "M-17-YN", "M-18-YN", "M-19-YN", "N-11-YN", "N-12-YN", "N-13-YN", "N-14-YN", "N-15-YN", "N-16-YN", "N-17-YN", "N-18-YN", "H-07-YZ", "H-08-YZ", "H-09-YZ", "J-06-YZ", "J-07-YZ", "J-08-YZ", "J-09-YZ", "K-06-YZ", "K-07-YZ", "K-08-YZ", "K-09-YZ", "L-06-YZ", "L-07-YZ", "L-08-YZ", "L-09-YZ", "L-10-YZ", "M-08-YZ", "M-09-YZ", "M-10-YZ", "M-11-YZ", "N-08-YZ", "N-09-YZ", "N-10-YZ", "N-11-YZ", "L-03-ZF", "L-04-ZF", "L-05-ZF", "M-02-ZF", "M-03-ZF", "M-04-ZF", "M-05-ZF", "N-01-ZF", "N-02-ZF", "N-03-ZF", "N-04-ZF", "N-05-ZF", "O-01-ZF", "O-02-ZF", "O-03-ZF" };

        private string m_fileText;
        private List<QSO> m_qsoList;

        private DXCC ER_Dxcc;
        private ModeResolver mr = new ModeResolver();
        private EntityResolver rem;

        private int _qso160;
        private int _qso80;
        private int _qso40;
        private int _qso30;
        private int _qso20;
        private int _qso17;
        private int _qso15;
        private int _qso12;
        private int _qso10;
        private int _qsoCW;
        private int _qsoSSB;
        private int _qsoDIGI;

        public int qso160 { get { return _qso160; } }
        public int qso80 { get { return _qso80; } }
        public int qso40 { get { return _qso40; } }
        public int qso30 { get { return _qso30; } }
        public int qso20 { get { return _qso20; } }
        public int qso17 { get { return _qso17; } }
        public int qso15 { get { return _qso15; } }
        public int qso12 { get { return _qso12; } }
        public int qso10 { get { return _qso10; } }

        public int qsoCW { get { return _qsoCW; } }
        public int qsoSSB { get { return _qsoSSB; } }
        public int qsoDIGI { get { return _qsoDIGI; } }
        
        //patterns
        private string mycall_pattern = @"<station_callsign:(\d{1,2})(?::[a-z]{1})?>";
        private string operator_call_pattern = @"<operator:(\d{1,2})(?::[a-z]{1})?>";
        private string rst_rcvd_pattern = @"<rst_rcvd:(\d{1,2})(?::[a-z]{1})?>";
        private string rst_sent_pattern = @"<rst_sent:(\d{1,2})(?::[a-z]{1})?>";
        private string dxcall_pattern = @"<call:(\d{1,2})(?::[a-z]{1})?>";
        private string date_pattern = @"<qso_date:(\d{1,2})(?::[a-z]{1})?>";
        private string time_pattern = @"<time_on:(\d{1,2})(?::[a-z]{1})?>";
        private string band_pattern = @"<band:(\d{1,2})(?::[a-z]{1})?>";
        private string mode_pattern = @"<mode:(\d{1,2})(?::[a-z]{1})?>";
        private string submode_pattern = @"<submode:(\d{1,2})(?::[a-z]{1})?>";
        private string commant_pattern = @"<comment:(\d{1,2})(?::[a-z]{1})?>";
        private string dxcc_pattern = @"<dxcc:(\d{1,2})(?::[a-z]{1})?>";
        private string freq_pattern = @"<freq:(\d{1,2})(?::[a-z]{1})?>";
        private string srx_pattern = @"<srx_string:(\d{1,2})(?::[a-z]{1})?>";
        private string stx_pattern = @"<stx_string:(\d{1,2})(?::[a-z]{1})?>";
        private string srx_short_pattern = @"<srx:(\d{1,2})(?::[a-z]{1})?>";
        private string stx_short_pattern = @"<stx:(\d{1,2})(?::[a-z]{1})?>";
        private string name_pattern = @"<name:(\d{1,2})(?::[a-z]{1})?>";
        private string country_pattern = @"<country:(\d{1,2})(?::[a-z]{1})?>";
        private string dxlocator_pattern = @"<gridsquare:(\d{1,2})(?::[a-z]{1})?>";
        private string mylocator_pattern = @"<my_gridsquare:(\d{1,2})(?::[a-z]{1})?>";
        private string prop_mode_pattern = @"<prop_mode:(\d{1,2})(?::[a-z]{1})?>";
        private string sat_name_pattern = @"<sat_name:(\d{1,2})(?::[a-z]{1})?>";
        private string soapbox_pattern = @"<soapbox:(\d{1,2})(?::[a-z]{1})?>";

        public HolyLogParser() : this("", Operator.Israeli)
        {
            
        }

        public HolyLogParser(string rawData, Operator logType, bool isParseDuplicates = true, bool isParseWarc = true)
        {
            m_fileText = rawData;
            LogType = logType;
            IsParseDuplicates = isParseDuplicates;
            IsParseWARC = isParseWarc;

            rem = new EntityResolver();
            m_qsoList = new List<QSO>();
        }

        public void Parse()
        {
            PopulateQSOList();
            CalculateResult();
        }

        private void PopulateQSOList()
        {
            m_qsoList.Clear();
            //Remove Line breakers
            string oneLiner = Regex.Replace(m_fileText, "\r\n", "");
            oneLiner = Regex.Replace(oneLiner, "\r", "");
            oneLiner = Regex.Replace(oneLiner, "\n", "");

            //Splite the Header
            string[] spliteHeader = Regex.Split(oneLiner, "<EOH>", RegexOptions.IgnoreCase);

            //Get the body
            string body = spliteHeader[1];

            //Splite body to lines
            string[] rows = Regex.Split(body, "<EOR>", RegexOptions.IgnoreCase);

            int debugCounter = 0;

            foreach (string row in rows)
            {
                //skip empty rows
                if (string.IsNullOrWhiteSpace(row)) continue;
                try
                {
                    QSO qso_row = ParseRawQSO(row);
                    m_qsoList.Add(qso_row);
                    //if (IsParseDuplicates)
                    //{
                    //    m_qsoList.Add(qso_row);
                    //}
                    //else if (!IsParseDuplicates && !m_qsoList.Contains(qso_row))
                    //{
                    //    m_qsoList.Add(qso_row);
                    //}
                }
                catch (Exception)
                {
                    debugCounter++;
                }
            }
            if (!IsParseDuplicates)
            {
                m_qsoList= m_qsoList.DistinctBy(p => p.HASH).ToList();
            }
            m_qsoList = m_qsoList.OrderBy(p => p.Date).ThenBy(p => p.Time).ToList();
        }

        public QSO ParseRawQSO(string row)
        {
            if (string.IsNullOrWhiteSpace(row)) return null;

            if (isN1MM(row))
                return ParseN1MMRawQSO(row);

            QSO qso_row = new QSO();
            qso_row.IsAllowWARC = IsParseWARC;
            qso_row.Continent = "";
            qso_row.Operator = "";
            qso_row.Comment = "";
            qso_row.MyLocator = "";
            qso_row.PROP_MODE = "";
            qso_row.SAT_NAME = "";
            qso_row.SRX = "";
            qso_row.STX = "";
            qso_row.Name = "";
            qso_row.GenerateSoapBox();

            Regex regex = new Regex(band_pattern, RegexOptions.IgnoreCase);
            Match match = regex.Match(row);
            if (match.Success)
            {
                qso_row.Band = Regex.Split(row, band_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value)).Trim().ToUpper();
            }

            //if the file contains country, let it be the default
            regex = new Regex(country_pattern, RegexOptions.IgnoreCase);
            match = regex.Match(row);
            if (match.Success)
            {
                qso_row.Country = Regex.Split(row, country_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value));
            }

            regex = new Regex(dxcall_pattern, RegexOptions.IgnoreCase);
            match = regex.Match(row);
            if (match.Success)
            {
                qso_row.DXCall = Regex.Split(row, dxcall_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value)).ToUpper();

                //use the callsign to resolve the entity info
                ER_Dxcc = rem.GetDXCC(qso_row.DXCall);

                qso_row.Country = ER_Dxcc.Name != "Unknown" ? ER_Dxcc.Name : qso_row.Country;
                qso_row.DXCC = ER_Dxcc.Entity;
                qso_row.Continent = ER_Dxcc.Continent;

            }

            //if the file contains dxcc, prefer it over the EntityResolver
            regex = new Regex(dxcc_pattern, RegexOptions.IgnoreCase);
            match = regex.Match(row);
            if (match.Success)
            {
                qso_row.DXCC = Regex.Split(row, dxcc_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value));

                ER_Dxcc = rem.GetDXCC(qso_row.DXCall);
                qso_row.Country = ER_Dxcc.Name != "Unknown" ? ER_Dxcc.Name : qso_row.Country;
                qso_row.Continent = ER_Dxcc.Continent;
            }

            

            regex = new Regex(mycall_pattern, RegexOptions.IgnoreCase);
            match = regex.Match(row);
            if (match.Success)
            {
                qso_row.MyCall = Regex.Split(row, mycall_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value)).ToUpper();
            }
            else
            {
                regex = new Regex(operator_call_pattern, RegexOptions.IgnoreCase);
                match = regex.Match(row);
                if (match.Success)
                {
                    qso_row.MyCall = Regex.Split(row, operator_call_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value)).ToUpper();
                }
            }

            regex = new Regex(operator_call_pattern, RegexOptions.IgnoreCase);
            match = regex.Match(row);
            if (match.Success)
            {
                qso_row.Operator = Regex.Split(row, operator_call_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value)).ToUpper();
            }

            regex = new Regex(rst_rcvd_pattern, RegexOptions.IgnoreCase);
            match = regex.Match(row);
            if (match.Success)
            {
                qso_row.RST_RCVD = Regex.Split(row, rst_rcvd_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value));
            }

            regex = new Regex(rst_sent_pattern, RegexOptions.IgnoreCase);
            match = regex.Match(row);
            if (match.Success)
            {
                qso_row.RST_SENT = Regex.Split(row, rst_sent_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value));
            }
            if (string.IsNullOrWhiteSpace(qso_row.RST_RCVD) && !string.IsNullOrWhiteSpace(qso_row.RST_SENT)) qso_row.RST_RCVD = qso_row.RST_SENT;
            if (string.IsNullOrWhiteSpace(qso_row.RST_SENT) && !string.IsNullOrWhiteSpace(qso_row.RST_RCVD)) qso_row.RST_SENT = qso_row.RST_RCVD;


            regex = new Regex(date_pattern, RegexOptions.IgnoreCase);
            match = regex.Match(row);
            if (match.Success)
            {
                qso_row.Date = Regex.Split(row, date_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value));
            }

            regex = new Regex(mode_pattern, RegexOptions.IgnoreCase);
            match = regex.Match(row);
            if (match.Success)
            {
                try
                {
                    qso_row.Mode = mr.GetValidMode(Regex.Split(row, mode_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value)));
                }
                catch
                {

                }
            }

            regex = new Regex(submode_pattern, RegexOptions.IgnoreCase);
            match = regex.Match(row);
            if (match.Success)
            {
                try
                {
                    qso_row.SUBMode = mr.GetValidMode(Regex.Split(row, submode_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value)));
                }
                catch
                {
                    
                }                
            }
            

            regex = new Regex(time_pattern, RegexOptions.IgnoreCase);
            match = regex.Match(row);
            if (match.Success)
            {
                qso_row.Time = Regex.Split(row, time_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value));
            }

            regex = new Regex(commant_pattern, RegexOptions.IgnoreCase);
            match = regex.Match(row);
            if (match.Success)
            {
                try
                {
                    qso_row.Comment = Regex.Split(row, commant_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value));
                }
                catch
                {

                }
            }

            regex = new Regex(dxlocator_pattern, RegexOptions.IgnoreCase);
            match = regex.Match(row);
            if (match.Success)
            {
                qso_row.DXLocator = Regex.Split(row, dxlocator_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value));
            }

            regex = new Regex(mylocator_pattern, RegexOptions.IgnoreCase);
            match = regex.Match(row);
            if (match.Success)
            {
                qso_row.MyLocator = Regex.Split(row, mylocator_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value));
            }

            regex = new Regex(prop_mode_pattern, RegexOptions.IgnoreCase);
            match = regex.Match(row);
            if (match.Success)
            {
                qso_row.PROP_MODE = Regex.Split(row, prop_mode_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value));
            }

            regex = new Regex(sat_name_pattern, RegexOptions.IgnoreCase);
            match = regex.Match(row);
            if (match.Success)
            {
                qso_row.SAT_NAME = Regex.Split(row, sat_name_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value));
            }

            regex = new Regex(srx_pattern, RegexOptions.IgnoreCase);
            match = regex.Match(row);
            if (match.Success)
            {
                qso_row.SRX = Regex.Split(row, srx_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value));
                if (string.IsNullOrWhiteSpace(qso_row.SRX)) qso_row.SRX = "";
            }
            else
            {
                regex = new Regex(srx_short_pattern, RegexOptions.IgnoreCase);
                match = regex.Match(row);
                if (match.Success)
                {
                    qso_row.SRX = Regex.Split(row, srx_short_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value));
                    if (string.IsNullOrWhiteSpace(qso_row.SRX)) qso_row.SRX = "";
                }
            }

            regex = new Regex(stx_pattern, RegexOptions.IgnoreCase);
            match = regex.Match(row);
            if (match.Success)
            {
                qso_row.STX = Regex.Split(row, stx_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value));
            }
            else
            {
                regex = new Regex(stx_short_pattern, RegexOptions.IgnoreCase);
                match = regex.Match(row);
                if (match.Success)
                {
                    qso_row.STX = Regex.Split(row, stx_short_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value));
                }
            }

            regex = new Regex(freq_pattern, RegexOptions.IgnoreCase);
            match = regex.Match(row);
            if (match.Success)
            {
                qso_row.Freq = Regex.Split(row, freq_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value));
            }

            regex = new Regex(soapbox_pattern, RegexOptions.IgnoreCase);
            match = regex.Match(row);
            if (match.Success)
            {
                string sb = Regex.Split(row, soapbox_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value));
                qso_row.SOAPBOX = string.IsNullOrWhiteSpace(sb) ? qso_row.SOAPBOX : sb;
            }

            regex = new Regex(name_pattern, RegexOptions.IgnoreCase);
            match = regex.Match(row);
            if (match.Success)
            {
                try
                {
                    qso_row.Name = Regex.Split(row, name_pattern, RegexOptions.IgnoreCase)[2].Substring(0, int.Parse(match.Groups[1].Value));
                }
                catch
                {
                    
                }
            }
            qso_row.StandartizeQSO();
            return qso_row;
        }

        public QSO ParseN1MMRawQSO(string row)
        {
            if (string.IsNullOrWhiteSpace(row)) return null;

            QSO qso_row = new QSO();
            qso_row.IsAllowWARC = IsParseWARC;
            qso_row.Continent = "";
            qso_row.Operator = "";
            qso_row.Comment = "";
            qso_row.MyLocator = "";
            qso_row.PROP_MODE = "";
            qso_row.SAT_NAME = "";
            qso_row.SRX = "";
            qso_row.STX = "";
            qso_row.Name = "";
            qso_row.GenerateSoapBox();

            Regex regex = new Regex(@"<band>(.*)?<", RegexOptions.IgnoreCase);
            Match match = regex.Match(row);
            if (match.Success)
            {
                qso_row.Band = Regex.Split(row, @"<band>(.*)?<", RegexOptions.IgnoreCase)[1].Trim().ToUpper();
            }

            regex = new Regex(@"<call>(.*)?<", RegexOptions.IgnoreCase);
            match = regex.Match(row);
            if (match.Success)
            {
                qso_row.DXCall = Regex.Split(row, @"<call>(.*)?<", RegexOptions.IgnoreCase)[1].Trim().ToUpper();

                //use the callsign to resolve the entity info
                ER_Dxcc = rem.GetDXCC(qso_row.DXCall);

                qso_row.Country = ER_Dxcc.Name != "Unknown" ? ER_Dxcc.Name : qso_row.Country;
                qso_row.DXCC = ER_Dxcc.Entity;
                qso_row.Continent = ER_Dxcc.Continent;

            }

            regex = new Regex(@"<mycall>(.*)?<", RegexOptions.IgnoreCase);
            match = regex.Match(row);
            if (match.Success)
            {
                qso_row.MyCall = Regex.Split(row, @"<mycall>(.*)?<", RegexOptions.IgnoreCase)[1].Trim().ToUpper();
            }
            else
            {
                regex = new Regex(@"<operator>(.*)?<", RegexOptions.IgnoreCase);
                match = regex.Match(row);
                if (match.Success)
                {
                    qso_row.MyCall = Regex.Split(row, @"<operator>(.*)?<", RegexOptions.IgnoreCase)[1].Trim().ToUpper();
                }
            }

            regex = new Regex(@"<operator>(.*)?<", RegexOptions.IgnoreCase);
            match = regex.Match(row);
            if (match.Success)
            {
                qso_row.Operator = Regex.Split(row, @"<operator>(.*)?<", RegexOptions.IgnoreCase)[1].Trim().ToUpper();
            }

            regex = new Regex(@"<rcv>(.*)?<", RegexOptions.IgnoreCase);
            match = regex.Match(row);
            if (match.Success)
            {
                qso_row.RST_RCVD = Regex.Split(row, @"<rcv>(.*)?<", RegexOptions.IgnoreCase)[1].Trim().ToUpper();
            }

            regex = new Regex(@"<snt>(.*)?<", RegexOptions.IgnoreCase);
            match = regex.Match(row);
            if (match.Success)
            {
                qso_row.RST_SENT = Regex.Split(row, @"<snt>(.*)?<", RegexOptions.IgnoreCase)[1].Trim().ToUpper();
            }
            if (string.IsNullOrWhiteSpace(qso_row.RST_RCVD) && !string.IsNullOrWhiteSpace(qso_row.RST_SENT)) qso_row.RST_RCVD = qso_row.RST_SENT;
            if (string.IsNullOrWhiteSpace(qso_row.RST_SENT) && !string.IsNullOrWhiteSpace(qso_row.RST_RCVD)) qso_row.RST_SENT = qso_row.RST_RCVD;


            regex = new Regex(@"<timestamp>(.*)?<", RegexOptions.IgnoreCase);
            match = regex.Match(row);
            if (match.Success)
            {
                qso_row.Date = Regex.Split(Regex.Split(row, @"<timestamp>(.*)?<", RegexOptions.IgnoreCase)[1].Trim().ToUpper(), " ")[0].Replace("-","");
            }

            regex = new Regex(@"<mode>(.*)?<", RegexOptions.IgnoreCase);
            match = regex.Match(row);
            if (match.Success)
            {
                try
                {
                    qso_row.Mode = mr.GetValidMode(Regex.Split(row, @"<mode>(.*)?<", RegexOptions.IgnoreCase)[1].Trim().ToUpper());
                }
                catch
                {

                }
            }

            regex = new Regex(@"<timestamp>(.*)?<", RegexOptions.IgnoreCase);
            match = regex.Match(row);
            if (match.Success)
            {
                qso_row.Time = Regex.Split(Regex.Split(row, @"<timestamp>(.*)?<", RegexOptions.IgnoreCase)[1].Trim().ToUpper(), " ")[1].Replace(":", "");
            }

            regex = new Regex(@"<comment>(.*)?<", RegexOptions.IgnoreCase);
            match = regex.Match(row);
            if (match.Success)
            {
                try
                {
                    qso_row.Comment = Regex.Split(row, @"<comment>(.*)?<", RegexOptions.IgnoreCase)[1].Trim().ToUpper();
                }
                catch
                {

                }
            }

            regex = new Regex(@"<gridsquare>(.*)?<", RegexOptions.IgnoreCase);
            match = regex.Match(row);
            if (match.Success)
            {
                qso_row.DXLocator = Regex.Split(row, @"<gridsquare>(.*)?<", RegexOptions.IgnoreCase)[1].Trim().ToUpper();
            }

            regex = new Regex(@"<rcvnr>(.*)?<", RegexOptions.IgnoreCase);
            match = regex.Match(row);
            if (match.Success)
            {
                qso_row.SRX = Regex.Split(row, @"<rcvnr>(.*)?<", RegexOptions.IgnoreCase)[1].Trim().ToUpper();
                if (string.IsNullOrWhiteSpace(qso_row.SRX)) qso_row.SRX = "";
            }
            
            regex = new Regex(@"<exchangel>(.*)?<", RegexOptions.IgnoreCase);
            match = regex.Match(row);
            if (match.Success)
            {
                qso_row.STX = Regex.Split(row, @"<exchangel>(.*)?<", RegexOptions.IgnoreCase)[1].Trim().ToUpper();
            }
            else
            {
                regex = new Regex(@"<sntnr>(.*)?<", RegexOptions.IgnoreCase);
                match = regex.Match(row);
                if (match.Success)
                {
                    qso_row.STX = Regex.Split(row, @"<sntnr>(.*)?<", RegexOptions.IgnoreCase)[1].Trim().ToUpper();
                }
            }

            regex = new Regex(@"<txfreq>(.*)?<", RegexOptions.IgnoreCase);
            match = regex.Match(row);
            if (match.Success)
            {
                qso_row.Freq = Regex.Split(row, @"<txfreq>(.*)?<", RegexOptions.IgnoreCase)[1].Trim().ToUpper();
            }

            regex = new Regex(@"<name>(.*)?<", RegexOptions.IgnoreCase);
            match = regex.Match(row);
            if (match.Success)
            {
                try
                {
                    qso_row.Name = Regex.Split(row, @"<name>(.*)?<", RegexOptions.IgnoreCase)[1].Trim().ToUpper();
                }
                catch
                {

                }
            }
            qso_row.StandartizeQSO();
            return qso_row;
        }

        private bool isN1MM(string row)
        {
            Regex regex = new Regex(@"<app>(.*)?<", RegexOptions.IgnoreCase);
            Match match = regex.Match(row);
            if (match.Success)
            {
                return Regex.Split(row, @"<app>(.*)?<", RegexOptions.IgnoreCase)[1].Trim().ToUpper() == "N1MM";
            }
            return false;
        }

        private void CalculateResult()
        {
            StringBuilder log = new StringBuilder();
            IEnumerable<QSO> validQSOs = null;
            if (LogType == Operator.Foreign)
            {
                //validQSOs = m_qsoList.Where(p => p.IsValid && p.IsIsraeli).DistinctBy(p => p.HASH);
                validQSOs = m_qsoList.Where(p => p.IsValid).DistinctBy(p => p.HASH);
            }
            else if (LogType == Operator.Israeli)
            {
                validQSOs = m_qsoList.Where(p => p.IsValid).DistinctBy(p => p.HASH);
            }
            _validQsos = validQSOs.Count();

            log.Append("You sent a total of "); log.Append(m_qsoList.Count()); log.Append(" QSO's, "); log.Append(validQSOs.Count()); log.Append(" are valid\r\n");
            log.Append("-----------------------------------------------------------------------------------------------------------\r\n");

            int single_point = validQSOs.Count(p => p.Band.Contains("10") || p.Band.Contains("15") || p.Band.Contains("20"));
            int double_point = validQSOs.Count(p => p.Band.Contains("40") || p.Band.Contains("80") || p.Band.Contains("160"));

            int extra_points = 0;

            DateTime dt;

            foreach (var qso in validQSOs)
            {
                if (DateTime.TryParseExact(qso.Time, "HHmmss", CultureInfo.CurrentCulture, DateTimeStyles.None, out dt))
                {
                    if (dt.TimeOfDay > new TimeSpan(17, 0, 0) && dt.TimeOfDay < new TimeSpan(21, 0, 0))
                    {
                        if (qso.Band.Contains("10") || qso.Band.Contains("15") || qso.Band.Contains("20"))
                        {
                            extra_points += 1;
                        }
                        else if (qso.Band.Contains("40") || qso.Band.Contains("80") || qso.Band.Contains("160"))
                        {
                            extra_points += 2;
                        }
                    }
                }
            }

            int total_points = extra_points + single_point + double_point * 2;
            _points = total_points;

            _qso160 = validQSOs.Count(p => p.Band.Contains("160"));
            _qso80 = validQSOs.Count(p => p.Band.Contains("80"));
            _qso40 = validQSOs.Count(p => p.Band.Contains("40"));
            _qso30 = validQSOs.Count(p => p.Band.Contains("30"));
            _qso20 = validQSOs.Count(p => p.Band.Contains("20"));
            _qso17 = validQSOs.Count(p => p.Band.Contains("17"));
            _qso15 = validQSOs.Count(p => p.Band.Contains("15"));
            _qso12 = validQSOs.Count(p => p.Band.Contains("12"));
            _qso10 = validQSOs.Count(p => p.Band.Contains("10"));

            _qsoCW = validQSOs.Count(p => p.Mode.Contains("CW"));
            _qsoSSB = validQSOs.Count(p => p.Mode.Contains("SSB"));


            log.Append("You get a score of: "); log.Append(total_points); log.Append(" points\r\n");
            log.Append("-----------------------------------------------------------------------------------------------------------\r\n");

            //var DistinctContacts10 = validQSOs.Where(p => p.Band.Contains("10")).DistinctBy(p => p.HASH);
            //log.Append(DistinctContacts10.Count()); log.Append(" distinct Contacts on 10m\r\n");
            //var DistinctContacts15 = validQSOs.Where(p => p.Band.Contains("15")).DistinctBy(p => p.HASH);
            //log.Append(DistinctContacts15.Count()); log.Append(" distinct Contacts on 15m\r\n");
            //var DistinctContacts20 = validQSOs.Where(p => p.Band.Contains("20")).DistinctBy(p => p.HASH);
            //log.Append(DistinctContacts20.Count()); log.Append(" distinct Contacts on 20m\r\n");
            //var DistinctContacts40 = validQSOs.Where(p => p.Band.Contains("40")).DistinctBy(p => p.HASH);
            //log.Append(DistinctContacts40.Count()); log.Append(" distinct Contacts on 40m\r\n");
            //var DistinctContacts80 = validQSOs.Where(p => p.Band.Contains("80")).DistinctBy(p => p.HASH);
            //log.Append(DistinctContacts80.Count()); log.Append(" distinct Contacts on 80m\r\n");
            //var DistinctContacts160 = validQSOs.Where(p => p.Band.Contains("160")).DistinctBy(p => p.HASH);
            //log.Append(DistinctContacts160.Count()); log.Append(" distinct Contacts on 160m\r\n");
            //int AllBandContacts = DistinctContacts10.Count() + DistinctContacts15.Count() + DistinctContacts20.Count() + DistinctContacts40.Count() + DistinctContacts80.Count() + DistinctContacts160.Count();
            //log.Append("-----------------------------------------------------------------------------------------------------------\r\n");

            var DistinctSquares10 = validQSOs.Where(p => p.Band.Contains("10") && p.IsIsraeli).DistinctBy(p => p.SRX.ToLower());
            log.Append(DistinctSquares10.Count()); log.Append(" distinct squares on 10m\r\n");
            var DistinctSquares15 = validQSOs.Where(p => p.Band.Contains("15") && p.IsIsraeli).DistinctBy(p => p.SRX.ToLower());
            log.Append(DistinctSquares15.Count()); log.Append(" distinct squares on 15m\r\n");
            var DistinctSquares20 = validQSOs.Where(p => p.Band.Contains("20") && p.IsIsraeli).DistinctBy(p => p.SRX.ToLower());
            log.Append(DistinctSquares20.Count()); log.Append(" distinct squares on 20m\r\n");
            var DistinctSquares40 = validQSOs.Where(p => p.Band.Contains("40") && p.IsIsraeli).DistinctBy(p => p.SRX.ToLower());
            log.Append(DistinctSquares40.Count()); log.Append(" distinct squares on 40m\r\n");
            var DistinctSquares80 = validQSOs.Where(p => p.Band.Contains("80") && p.IsIsraeli).DistinctBy(p => p.SRX.ToLower());
            log.Append(DistinctSquares80.Count()); log.Append(" distinct squares on 80m\r\n");
            var DistinctSquares160 = validQSOs.Where(p => p.Band.Contains("160") && p.IsIsraeli).DistinctBy(p => p.SRX.ToLower());
            log.Append(DistinctSquares160.Count()); log.Append(" distinct squares on 160m\r\n");
            int AllBandSquares = DistinctSquares10.Count() + DistinctSquares15.Count() + DistinctSquares20.Count() + DistinctSquares40.Count() + DistinctSquares80.Count() + DistinctSquares160.Count();
            _workedSquers = AllBandSquares;

            //log.Append(AllBandSquares); log.Append(" squares in all bands\r\n");
            log.Append("-----------------------------------------------------------------------------------------------------------\r\n");
            int IsraeliOn10 = DistinctSquares10.Count() > 0 ? 1 : 0;
            int IsraeliOn15 = DistinctSquares15.Count() > 0 ? 1 : 0;
            int IsraeliOn20 = DistinctSquares20.Count() > 0 ? 1 : 0;
            int IsraeliOn40 = DistinctSquares40.Count() > 0 ? 1 : 0;
            int IsraeliOn80 = DistinctSquares80.Count() > 0 ? 1 : 0;
            int IsraeliOn160 = DistinctSquares160.Count() > 0 ? 1 : 0;
            int AllBandIsraeliStations = IsraeliOn10 + IsraeliOn15 + IsraeliOn20 + IsraeliOn40 + IsraeliOn80 + IsraeliOn160;
            //log.Append("You have contacted Israeli stations on "); log.Append(AllBandIsraeliStations); log.Append(" bands\r\n");

            var DistinctDXCC10 = validQSOs.Where(p => p.Band.Contains("10") && !p.IsIsraeli).DistinctBy(p => p.DXCC.ToLower());
            log.Append(DistinctDXCC10.Count()); log.Append(" distinct DXCC on 10m\r\n");
            var DistinctDXCC15 = validQSOs.Where(p => p.Band.Contains("15") && !p.IsIsraeli).DistinctBy(p => p.DXCC.ToLower());
            log.Append(DistinctDXCC15.Count()); log.Append(" distinct DXCC on 15m\r\n");
            var DistinctDXCC20 = validQSOs.Where(p => p.Band.Contains("20") && !p.IsIsraeli).DistinctBy(p => p.DXCC.ToLower());
            log.Append(DistinctDXCC20.Count()); log.Append(" distinct DXCC on 20m\r\n");
            var DistinctDXCC40 = validQSOs.Where(p => p.Band.Contains("40") && !p.IsIsraeli).DistinctBy(p => p.DXCC.ToLower());
            log.Append(DistinctDXCC40.Count()); log.Append(" distinct DXCC on 40m\r\n");
            var DistinctDXCC80 = validQSOs.Where(p => p.Band.Contains("80") && !p.IsIsraeli).DistinctBy(p => p.DXCC.ToLower());
            log.Append(DistinctDXCC80.Count()); log.Append(" distinct DXCC on 80m\r\n");
            var DistinctDXCC160 = validQSOs.Where(p => p.Band.Contains("160") && !p.IsIsraeli).DistinctBy(p => p.DXCC.ToLower());
            log.Append(DistinctDXCC160.Count()); log.Append(" distinct DXCC on 160m\r\n");
            int AllBandDXCC = DistinctDXCC10.Count() + DistinctDXCC15.Count() + DistinctDXCC20.Count() + DistinctDXCC40.Count() + DistinctDXCC80.Count() + DistinctDXCC160.Count();
            //log.Append(AllBandDXCC); log.Append(" DXCC in all bands\r\n");
            log.Append("-----------------------------------------------------------------------------------------------------------\r\n");

            if (LogType == Operator.Foreign)
            {
                log.Append(AllBandSquares); log.Append(" squares in all bands\r\n");
                log.Append("You have contacted Israeli stations on "); log.Append(AllBandIsraeliStations); log.Append(" bands\r\n");
                log.Append("-----------------------------------------------------------------------------------------------------------\r\n");
                _result = total_points * (AllBandSquares + AllBandDXCC);// + AllBandIsraeliStations);
                //_mults = AllBandSquares;// + AllBandIsraeliStations;
                _mults = AllBandSquares + AllBandDXCC;// + AllBandIsraeliStations;
            }
            else if (LogType == Operator.Israeli)
            {
                log.Append(AllBandSquares); log.Append(" squares in all bands\r\n");
                log.Append(AllBandDXCC); log.Append(" DXCC entities in all bands\r\n");
                log.Append("You have contacted Israeli stations on "); log.Append(AllBandIsraeliStations); log.Append(" bands\r\n");
                log.Append("-----------------------------------------------------------------------------------------------------------\r\n");
                _result = total_points * (AllBandSquares + AllBandIsraeliStations + AllBandDXCC);
                _mults = AllBandSquares + AllBandIsraeliStations + AllBandDXCC;
            }
            log.Append("Your total score is "); log.Append(_result); log.Append("\r\n");
            log.Append("\r\n"); log.Append("\r\n"); log.Append("Thank you for sending the log. Good luck in the contest");
            _description = log.ToString();
        }
        
        public List<QSO> GetRawQSO()
        {
            return m_qsoList;
        }

        public string getErrors()
        {
            var invalidQSOs = m_qsoList.Where(p => !p.IsValid);
            StringBuilder s = new StringBuilder();
            foreach (QSO qso in invalidQSOs)
            {
                s.Append(qso.ERROR);
                s.Append("\r\n");
            }
            return s.ToString();
        }

        public static string convertFreqToBand(string freq)
        {
            double parsedFreq;
            CultureInfo provider = CultureInfo.InvariantCulture;

            //if (freq.IndexOf(".") > 0)
            //    freq = freq.Substring(0, freq.IndexOf("."));
            //if (freq.IndexOf(",") > 0)
            //    freq = freq.Substring(0, freq.IndexOf(","));

            if (!double.TryParse(freq, NumberStyles.Number, provider, out parsedFreq)) return string.Empty;
            if (parsedFreq < 1000)
            {
                if (parsedFreq >= 1.8 && parsedFreq <= 2) return "160M";
                if (parsedFreq >= 2.4 && parsedFreq <= 2.5) return "13CM";
                if (parsedFreq >= 3.5 && parsedFreq <= 4) return "80M";
                if (parsedFreq >= 5 && parsedFreq <= 5.4) return "60M";
                if (parsedFreq >= 7 && parsedFreq <= 7.3) return "40M";
                if (parsedFreq >= 10 && parsedFreq <= 10.15) return "30M";
                if (parsedFreq >= 14 && parsedFreq <= 14.35) return "20M";
                if (parsedFreq >= 18 && parsedFreq <= 18.168) return "17M";
                if (parsedFreq >= 21 && parsedFreq <= 21.45) return "15M";
                if (parsedFreq >= 24.89 && parsedFreq <= 24.99) return "12M";
                if (parsedFreq >= 28 && parsedFreq <= 29.7) return "10M";
                if (parsedFreq >= 50 && parsedFreq <= 54) return "6M";
                if (parsedFreq >= 70 && parsedFreq <= 71) return "4M";
                if (parsedFreq >= 144 && parsedFreq <= 148) return "2M";
                if (parsedFreq >= 420 && parsedFreq <= 450) return "70CM";
            }
            else if (parsedFreq < 1000000)
            {
                if (parsedFreq >= 1800 && parsedFreq <= 2000) return "160M";
                if (parsedFreq >= 2400 && parsedFreq <= 2500) return "13CM";
                if (parsedFreq >= 3500 && parsedFreq <= 4000) return "80M";
                if (parsedFreq >= 5000 && parsedFreq <= 5400) return "60M";
                if (parsedFreq >= 7000 && parsedFreq <= 7300) return "40M";
                if (parsedFreq >= 10000 && parsedFreq <= 10150) return "30M";
                if (parsedFreq >= 14000 && parsedFreq <= 14350) return "20M";
                if (parsedFreq >= 18000 && parsedFreq <= 18168) return "17M";
                if (parsedFreq >= 21000 && parsedFreq <= 21450) return "15M";
                if (parsedFreq >= 24890 && parsedFreq <= 24990) return "12M";
                if (parsedFreq >= 28000 && parsedFreq <= 29700) return "10M";
                if (parsedFreq >= 50000 && parsedFreq <= 54000) return "6M";
                if (parsedFreq >= 70000 && parsedFreq <= 71000) return "4M";
                if (parsedFreq >= 144000 && parsedFreq <= 148000) return "2M";
                if (parsedFreq >= 420000 && parsedFreq <= 450000) return "70CM";

                if (parsedFreq >= 18000 && parsedFreq <= 20000) return "160M";
                if (parsedFreq >= 24000 && parsedFreq <= 25000) return "13CM";
                if (parsedFreq >= 35000 && parsedFreq <= 40000) return "80M";
                if (parsedFreq >= 50000 && parsedFreq <= 54000) return "60M";
                if (parsedFreq >= 70000 && parsedFreq <= 73000) return "40M";
                if (parsedFreq >= 100000 && parsedFreq <= 101500) return "30M";
                if (parsedFreq >= 140000 && parsedFreq <= 143500) return "20M";
                if (parsedFreq >= 180000 && parsedFreq <= 181680) return "17M";
                if (parsedFreq >= 210000 && parsedFreq <= 214500) return "15M";
                if (parsedFreq >= 248900 && parsedFreq <= 249900) return "12M";
                if (parsedFreq >= 280000 && parsedFreq <= 297000) return "10M";
                if (parsedFreq >= 500000 && parsedFreq <= 540000) return "6M";
                if (parsedFreq >= 700000 && parsedFreq <= 710000) return "4M";
            }
            else if (parsedFreq < 10000000)
            {
                if (parsedFreq >= 18000 && parsedFreq <= 20000) return "160M";
                if (parsedFreq >= 24000 && parsedFreq <= 25000) return "13CM";
                if (parsedFreq >= 35000 && parsedFreq <= 40000) return "80M";
                if (parsedFreq >= 50000 && parsedFreq <= 54000) return "60M";
                if (parsedFreq >= 70000 && parsedFreq <= 73000) return "40M";
                if (parsedFreq >= 100000 && parsedFreq <= 101500) return "30M";
                if (parsedFreq >= 140000 && parsedFreq <= 143500) return "20M";
                if (parsedFreq >= 180000 && parsedFreq <= 181680) return "17M";
                if (parsedFreq >= 210000 && parsedFreq <= 214500) return "15M";
                if (parsedFreq >= 248900 && parsedFreq <= 249900) return "12M";
                if (parsedFreq >= 280000 && parsedFreq <= 297000) return "10M";
                if (parsedFreq >= 500000 && parsedFreq <= 540000) return "6M";
                if (parsedFreq >= 700000 && parsedFreq <= 710000) return "4M";
                if (parsedFreq >= 1440000 && parsedFreq <= 1480000) return "2M";
                if (parsedFreq >= 4200000 && parsedFreq <= 4500000) return "70CM";
            }
            else if (parsedFreq < 1000000000)
            {
                if (parsedFreq >= 1800000 && parsedFreq <= 2000000) return "160M";
                if (parsedFreq >= 2400000 && parsedFreq <= 2500000) return "13CM";
                if (parsedFreq >= 3500000 && parsedFreq <= 4000000) return "80M";
                if (parsedFreq >= 5000000 && parsedFreq <= 5400000) return "60M";
                if (parsedFreq >= 7000000 && parsedFreq <= 7300000) return "40M";
                if (parsedFreq >= 10000000 && parsedFreq <= 10150000) return "30M";
                if (parsedFreq >= 14000000 && parsedFreq <= 14350000) return "20M";
                if (parsedFreq >= 18000000 && parsedFreq <= 18168000) return "17M";
                if (parsedFreq >= 21000000 && parsedFreq <= 21450000) return "15M";
                if (parsedFreq >= 24890000 && parsedFreq <= 24990000) return "12M";
                if (parsedFreq >= 28000000 && parsedFreq <= 29700000) return "10M";
                if (parsedFreq >= 50000000 && parsedFreq <= 54000000) return "6M";
                if (parsedFreq >= 70000000 && parsedFreq <= 71000000) return "4M";
                if (parsedFreq >= 144000000 && parsedFreq <= 148000000) return "2M";
                if (parsedFreq >= 420000000 && parsedFreq <= 450000000) return "70CM";
            }
            if (parsedFreq < 100000000) //n1mm
            {
                if (parsedFreq >= 180000 && parsedFreq <= 200000) return "160M";
                if (parsedFreq >= 240000 && parsedFreq <= 250000) return "13CM";
                if (parsedFreq >= 350000 && parsedFreq <= 400000) return "80M";
                if (parsedFreq >= 500000 && parsedFreq <= 540000) return "60M";
                if (parsedFreq >= 700000 && parsedFreq <= 730000) return "40M";
                if (parsedFreq >= 1000000 && parsedFreq <= 1015000) return "30M";
                if (parsedFreq >= 1400000 && parsedFreq <= 1435000) return "20M";
                if (parsedFreq >= 1800000 && parsedFreq <= 1816800) return "17M";
                if (parsedFreq >= 2100000 && parsedFreq <= 2145000) return "15M";
                if (parsedFreq >= 2489000 && parsedFreq <= 2499000) return "12M";
                if (parsedFreq >= 2800000 && parsedFreq <= 2970000) return "10M";
                if (parsedFreq >= 5000000 && parsedFreq <= 5400000) return "6M";
                if (parsedFreq >= 7000000 && parsedFreq <= 7100000) return "4M";
                if (parsedFreq >= 14400000 && parsedFreq <= 14800000) return "2M";
                if (parsedFreq >= 42000000 && parsedFreq <= 45000000) return "70CM";
            }
            return string.Empty;
        }

        public static string convertBandToFreq(string band)
        {

            if (band.ToLower() == "160m") return "1800";
            if (band.ToLower() == "80m") return "3600";
            if (band.ToLower() == "60m") return "5360";
            if (band.ToLower() == "40m") return "7100";
            if (band.ToLower() == "30m") return "10000";
            if (band.ToLower() == "20m") return "14200";
            if (band.ToLower() == "17m") return "18100";
            if (band.ToLower() == "15m") return "21300";
            if (band.ToLower() == "12m") return "24900";
            if (band.ToLower() == "10m") return "28400";
            if (band.ToLower() == "6m") return "50000";
            if (band.ToLower() == "7m") return "70000";
            if (band.ToLower() == "2m") return "145000";
            if (band.ToLower() == "70cm") return "433000";
            return string.Empty;
        }

        public static bool IsIsraeliStation(string callsign)
        {
            return !string.IsNullOrWhiteSpace(callsign) && (callsign.StartsWith("4X", true, System.Globalization.CultureInfo.CurrentCulture) || callsign.StartsWith("4Z", true, System.Globalization.CultureInfo.CurrentCulture));
        }

        

        public enum Operator
        {
            Israeli = 0, Foreign
        }
    }
}
