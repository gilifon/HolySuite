using DXCCManager;
using HolyParser;
using MoreLinq;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Xml.Linq;

namespace HolyContestManager
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        #region INotifyProprtyChanged
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string propertyName)
        {
            if (PropertyChanged != null)
            {
                PropertyChanged(this, new PropertyChangedEventArgs(propertyName));
            }
        }
        #endregion

        public HolylandData RawData{ get; set; }
        private List<Participant> Report { get; set; }
        private List<Participant> FilteredReport { get; set; }

        private string _CategoryOperator = "No Filter";
        public string CategoryOperator
        {
            get { return _CategoryOperator; }
            set
            {
                _CategoryOperator = value;
                OnPropertyChanged("CategoryOperator");
            }
        }

        private string _CategoryMode = "No Filter";
        public string CategoryMode
        {
            get { return _CategoryMode; }
            set
            {
                _CategoryMode = value;
                OnPropertyChanged("CategoryMode");
            }
        }

        private string _CategoryPower = "No Filter";
        public string CategoryPower
        {
            get { return _CategoryPower; }
            set
            {
                _CategoryPower = value;
                OnPropertyChanged("CategoryPower");
            }
        }

        private string _CategoryStation = "No Filter";
        public string CategoryStation
        {
            get { return _CategoryStation; }
            set
            {
                _CategoryStation = value;
                OnPropertyChanged("CategoryStation");
            }
        }

        private string _CategoryOrigin = "No Filter";
        public string CategoryOrigin
        {
            get { return _CategoryOrigin; }
            set
            {
                _CategoryOrigin = value;
                OnPropertyChanged("CategoryOrigin");
            }
        }
        
        private const string files_path = @"C:\TEMP\";

        private BackgroundWorker CalculateWorker;
        private BackgroundWorker GetDataWorker;

        public MainWindow()
        {
            Report = new List<Participant>(200);
            FilteredReport = new List<Participant>(200);
            InitializeComponent();

            CalculateBtn.IsEnabled = false;
            L_Status.Content = "Retreiving data from database";

            CalculateWorker = new BackgroundWorker();
            CalculateWorker.WorkerReportsProgress = true;
            CalculateWorker.DoWork += CalculateWorker_DoWork;
            CalculateWorker.RunWorkerCompleted += CalculateWorker_RunWorkerCompleted;
            CalculateWorker.ProgressChanged += CalculateWorker_ProgressChanged;

            GetDataWorker = new BackgroundWorker();
            GetDataWorker.DoWork += GetDataWorker_DoWork;
            GetDataWorker.RunWorkerCompleted += GetDataWorker_RunWorkerCompleted;
            pbStatus.IsIndeterminate = true;
            GetDataWorker.RunWorkerAsync();
        }
        private async void PrintParticipantsEmail()
        {
            WebRequest request = WebRequest.Create("http://xmldata.qrz.com/xml/current/?username=" + "4Z1KD" + ";password=" + "papirus0");
            WebResponse response = request.GetResponse();
            string status = ((HttpWebResponse)response).StatusDescription;
            Stream dataStream = response.GetResponseStream();
            StreamReader reader = new StreamReader(dataStream);
            string responseFromServer = reader.ReadToEnd();

            XElement xml = XElement.Parse(responseFromServer);
            XElement element = xml.Elements().FirstOrDefault();
            string SessionKey = element.Elements().FirstOrDefault().Value;

            using (StreamReader sr = new StreamReader(@"C:\Users\gill\Desktop\Holyland Logs Calls.csv"))
            {
                string line = sr.ReadToEnd();
                string[] calls = line.Split('\n');
                List<string> callList = calls.ToList();
                string name = "";
                foreach (var callsign in callList)
                {
                    using (var client = new HttpClient())
                    {
                        try
                        {
                            string baseRequest = "http://xmldata.qrz.com/xml/current/?s=";
                            var responsex = await client.GetAsync(baseRequest + SessionKey + ";callsign=" + callsign);
                            var responseFromServerx = await responsex.Content.ReadAsStringAsync();
                            XDocument xDoc = XDocument.Parse(responseFromServerx);

                            IEnumerable<XElement> fname = xDoc.Root.Descendants(xDoc.Root.GetDefaultNamespace‌​() + "fname");
                            if (fname.Count() > 0)
                                name = fname.FirstOrDefault().Value;

                            IEnumerable<XElement> lname = xDoc.Root.Descendants(xDoc.Root.GetDefaultNamespace‌​() + "name");
                            if (lname.Count() > 0)
                                name += " " + lname.FirstOrDefault().Value;

                            IEnumerable<XElement> email = xDoc.Root.Descendants(xDoc.Root.GetDefaultNamespace‌​() + "email");
                            if (email.Count() > 0)
                                Console.WriteLine(callsign + "," + name + "," + email.FirstOrDefault().Value);
                            else
                                Console.WriteLine(callsign + "," + name);

                            name = "";
                        }
                        catch (Exception)
                        {

                        }
                    }
                }
            }
        }

        private async void SendMail()
        {
            
            //string to = @"gilifon@gmail.com";

            StringBuilder sb = new StringBuilder(200);
            sb.Append("Dear OM").Append(",<br><br>");

            sb.Append("Thank you for participating in the 'Holyland Contest' and for sending the log.<br>");
            sb.Append("Please be patient, we will publish the result as soon as all the logs are received.<br><br>");

            sb.Append("received Logs:<br>");
            sb.Append("http://www.iarc.org/iarc/#HolylandLogs <br><br>");

            sb.Append("Results:<br>");
            sb.Append("http://www.iarc.org/iarc/#HolylandResults <br><br>");

            sb.Append("73 and Best Regards,<br>");
            sb.Append("The Organizing Committee.<br>");

            //string Sendemail_result = await Services.SendMail("info@iarc.org", to , "Holyland Contest - your log was received", sb.ToString());
        }

        private void GetDataWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            pbStatus.IsIndeterminate = false;
            CalculateBtn.IsEnabled = true;
            L_Status.Content = "Ready";
        }

        private void GetDataWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            GetData();
            bool is_sukot = false;

            ///////////////////////////////////////////////////////// SUKOT ///////////////////////////////////////////////
            if (is_sukot)
            {
                StringBuilder sb2 = new StringBuilder();
                sb2.AppendFormat("{0},", "My Call");
                sb2.AppendFormat("{0},", "My Square");
                sb2.AppendFormat("{0},", "DX Call");
                sb2.AppendFormat("{0},", "DX Square");
                sb2.AppendFormat("{0},", "Distance");
                sb2.AppendFormat("{0},", "Freq");
                sb2.AppendFormat("{0},", "Band");
                sb2.AppendFormat("{0},", "Mode");
                sb2.AppendFormat("{0}\n", "Datetime");

                foreach (var qso in RawData.log)
                {
                    if (qso.MyLocator.ToUpper() == qso.SRX.ToUpper())
                    {
                        qso.Comment = "1";
                    }
                    else
                    {
                        try
                        {
                            qso.Comment = Math.Round(HolyParser.MaidenheadLocator.Distance(qso.MyLocator, qso.SRX)).ToString();
                        }
                        catch (Exception)
                        {
                            qso.Comment = "0";
                        }
                    }
                    sb2.AppendFormat("{0},", qso.MyCall.ToUpper());
                    sb2.AppendFormat("{0},", qso.MyLocator);
                    sb2.AppendFormat("{0},", qso.DXCall);
                    sb2.AppendFormat("{0},", qso.SRX);
                    sb2.AppendFormat("{0},", qso.Comment);
                    sb2.AppendFormat("{0},", qso.Freq);
                    sb2.AppendFormat("{0},", qso.Band);
                    sb2.AppendFormat("{0},", qso.Mode);
                    sb2.AppendFormat("{0}\n", qso.Date);
                }
                System.IO.FileStream fs3 = File.Create(files_path + @"log_info.csv");
                StreamWriter sw3 = new StreamWriter(fs3);
                sw3.Write(sb2.ToString());
                sw3.Close();
                fs3.Close();

                StringBuilder sb = new StringBuilder();

                sb.AppendFormat("{0},", "Callsign");
                sb.AppendFormat("{0},", "Name");
                sb.AppendFormat("{0},", "QSOs");
                sb.AppendFormat("{0},", "Squares");
                sb.AppendFormat("{0}\n", "Score");

                foreach (Participant p in RawData.participants)
                {
                    IEnumerable<QSO> qsos = from q in RawData.log where Helper.getBareCallsign(q.MyCall) == Helper.getBareCallsign(p.callsign) select q;
                    System.IO.FileStream fs2 = File.Create(files_path + Helper.getBareCallsign(p.callsign) + @".adi");
                    StreamWriter sw2 = new StreamWriter(fs2);
                    sw2.Write(Services.GenerateAdif(qsos));
                    sw2.Close();
                    fs2.Close();

                    Participant n = p;
                    n.qsos = qsos.Count();// qsos.Count();
                    n.score = qsos.Sum(x => int.Parse(x.Comment));
                    n.squers = qsos.DistinctBy(x => x.MyLocator.ToLower()).Count();
                    n.mults = 1;
                    n.points = 0;

                    sb.AppendFormat("{0},", p.callsign.ToUpper());
                    sb.AppendFormat("{0},", p.name);
                    sb.AppendFormat("{0},", n.qsos);
                    sb.AppendFormat("{0},", n.squers);
                    sb.AppendFormat("{0}\n", n.score);
                    Report.Add(n);
                }


                System.IO.FileStream fs = File.Create(files_path + @"result.csv");
                StreamWriter sw = new StreamWriter(fs);
                sw.Write(sb.ToString());
                sw.Close();
                fs.Close();

                ///////////////////////////////////////////////////////// END SUKOT ///////////////////////////////////////////////
            }
        }

        private void GetData()
        {
            //WebRequest request = WebRequest.Create("http://www.iarc.org/Holyland/Server/get_holyland_data.php");
            WebRequest request = WebRequest.Create("https://www.iarc.org/iarc75/Server/GetLogForADIF.php");
            WebResponse response = request.GetResponse();
            string status = ((HttpWebResponse)response).StatusDescription;
            Stream dataStream = response.GetResponseStream();
            StreamReader reader = new StreamReader(dataStream);
            string responseFromServer = reader.ReadToEnd();

            RawData = JsonConvert.DeserializeObject<HolylandData>(responseFromServer);
        }

        private void CalculateWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            DataContext = FilteredReport;
            CalculateBtn.IsEnabled = true;
            L_Status.Content = "Ready";
            pbStatus.Value = 0;
            L_NUmOfParticipantsValue.Content = FilteredReport.Count();
        }

        private void CalculateWorker_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            pbStatus.Value = e.ProgressPercentage;
        }

        private void CalculateWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            GenerateLogFile("4X75UT");
            GenerateLogFile("4X75BQ");
            

            Report.Clear();
            
            int iteration = 0;
            int participantsCount = RawData.participants.Count();

            //List<Participant> participants = RawData.participants.Where(q => !q.callsign.StartsWith("4X") && !q.callsign.StartsWith("4Z")).OrderByDescending(t => t.qsos).ToList();
            List<Participant> participants = RawData.participants.Where(q => q.callsign.StartsWith("4X") || q.callsign.StartsWith("4Z")).OrderByDescending(t => t.qsos).ToList();
            

            foreach (Participant p in participants)
            {
                GenerateLogFile(p);
                return;
                iteration++;
                if (p.is_manual == 0) // && Helper.getBareCallsign(p.callsign).ToUpper() == "VU3XIO")
                {
                    IEnumerable<QSO> _qsos = from q in RawData.log where Helper.getBareCallsign(q.MyCall) == Helper.getBareCallsign(p.callsign) select q;//  && IsValidDate(q) select q;

                    List<QSO> qsos = _qsos.ToList();
             
                    int numOfSquers = qsos.DistinctBy(q => q.STX).Count();

                    HolyLogParser lop = new HolyLogParser(Services.GenerateAdif(qsos), HolyLogParser.IsIsraeliStation(p.callsign) ? HolyLogParser.Operator.Israeli : HolyLogParser.Operator.Foreign);
                    lop.Parse();

                    Participant n = p;
                    n.qsos = lop.ValidQsos;// qsos.Count();
                    n.score = lop.Result;
                    n.squers = numOfSquers;
                    n.mults = lop.Mults;
                    n.points = lop.Points;

                    Report.Add(n);
                }
                else
                {
                    Participant n = p;
                    n.qsos = p.qsos;
                    n.score = p.points;
                    n.squers = 0;
                    n.mults = 0;
                    n.points = p.points;

                    Report.Add(n);
                }
                (sender as BackgroundWorker).ReportProgress(100*iteration/participantsCount);
            }
            Report = Report.OrderByDescending(p => p.score).ToList();
            FilteredReport = new List<Participant>(Report);
            string insert_hlwtest = GenerateMultipleInsert(FilteredReport);
        }

        private bool IsValidDate(QSO q)
        {
            DateTime dateValue;
            bool result = DateTime.TryParseExact(q.Date, "yyyyMMdd HHmmss", new CultureInfo("en-US"), DateTimeStyles.AllowWhiteSpaces, out dateValue);
            if (result)
            {
                bool isValid = dateValue >= new DateTime(2021, 04, 16, 21, 00, 00) && dateValue < new DateTime(2021, 04, 17, 21, 00, 00);
                return isValid;
            }
            result = DateTime.TryParseExact(q.Date, "yyyyMMdd HHmm", new CultureInfo("en-US"), DateTimeStyles.AllowWhiteSpaces, out dateValue);
            if (result)
            {
                bool isValid = dateValue >= new DateTime(2021, 04, 16, 21, 00, 00) && dateValue < new DateTime(2021, 04, 17, 21, 00, 00);
                return isValid;
            }
            return false;
        }

        private void GenerateLogFile(Participant p)
        {
            GenerateLogFile(p.callsign);
        }

        private void GenerateLogFile(string callsign)
        {
            IEnumerable<QSO> qsosx = from q in RawData.log where Helper.getBareCallsign(q.MyCall) == Helper.getBareCallsign(callsign) select q;
            string adif = HolyParser.Services.GenerateAdif(qsosx);
            System.IO.FileStream fs = File.Create(files_path + Helper.getBareCallsign(callsign) + ".adi");
            StreamWriter sw = new StreamWriter(fs);
            sw.Write(adif);
            sw.Close();
            fs.Close();
        }

        private string GenerateMultipleInsert(IList<Participant> participants)
        {
            StringBuilder squars = new StringBuilder(10);
            EntityResolver rem = new EntityResolver();
            StringBuilder sb = new StringBuilder("INSERT INTO `hlwtest` ", 500);
            sb.Append("(`active`,`year`,`callsign`,`uniq_timestamp`,`dxcc`,`continent`,`category`,`qso`,`points`,`mults`,`score`,`operator`,`square`) VALUES ");
            foreach (Participant p in participants)
            {
                IEnumerable<QSO> qsos = from q in RawData.log where Helper.getBareCallsign(q.MyCall) == Helper.getBareCallsign(p.callsign) && IsValidDate(q) select q;
                foreach (var item in qsos.Where(xp => xp.MyCall.StartsWith("4X") || xp.MyCall.StartsWith("4Z")).Select(t => t.STX).Distinct())
                {
                    squars.AppendFormat("{0},", item);
                }
                sb.Append("(");
                sb.Append("'"); sb.Append(0); sb.Append("',");
                sb.Append("'"); sb.Append(DateTime.Now.Year); sb.Append("',");
                sb.Append("'"); sb.Append(p.callsign.Replace("'", "\"")); sb.Append("',");
                sb.Append("'"); sb.Append("HolyLogger:" + DateTime.Now.Ticks); sb.Append("',");
                sb.Append("'"); sb.Append(p.country.Replace("'", "\"")); sb.Append("',");
                sb.Append("'"); sb.Append(rem.GetContinent(p.callsign)); sb.Append("',");
                sb.Append("'"); sb.Append(getCategory(p)); sb.Append("',");
                sb.Append("'"); sb.Append(p.qsos); sb.Append("',");
                sb.Append("'"); sb.Append(p.points); sb.Append("',");
                sb.Append("'"); sb.Append(p.mults); sb.Append("',");
                sb.Append("'"); sb.Append(p.score); sb.Append("',");
                sb.Append("'"); sb.Append(p.name.Replace("'", "\"")); sb.Append("',");
                sb.Append("'"); sb.Append(squars.ToString().TrimEnd(',')); sb.Append("'),");
                squars.Clear();
            }
            string result = sb.ToString().TrimEnd(',');
            result += " ON DUPLICATE KEY UPDATE `callsign`=`callsign`";
            return result;
        }

        private string getCategory(Participant p)
        {
            //if (p.category_op.ToLower() == "checklog") return "CHECKLOG";
            //if (p.category_power.ToLower() == "qrp") return "QRP";
            //if (p.category_op.ToLower() == "mobile") return "MOBILE";
            //if (p.callsign.ToLower().Contains(@"/p")) return "PORTABLE";
            //if (p.category_op.ToLower() == "multi-op") return "MULTIOP";
            //if (p.category_mode.ToLower() == "ssb") return "SSB";
            //if (p.category_mode.ToLower() == "cw") return "CW";
            //if (p.category_mode.ToLower() == "digi") return "DIGI";
            //if (p.category_mode.ToLower() == "mixed") return "MIX";
            //if (p.category_mode.ToLower() == "mix") return "MIX";

            if (p.category_mode.ToUpper() == "CHECKLOG") return "CHECKLOG";
            if (p.category_mode.ToUpper() == "MIXED") return "MIX";
            if (p.category_mode.ToUpper() == "MIX") return "MIX";
            if (p.category_mode.ToUpper() == "CW") return "CW";
            if (p.category_mode.ToUpper() == "SSB") return "SSB";
            if (p.category_mode.ToUpper() == "FT8") return "FT8";
            if (p.category_mode.ToUpper() == "DIGI") return "DIGI";
            if (p.category_mode.ToUpper() == "QRP") return "QRP";
            if (p.category_mode.ToUpper() == "SOB 10") return "SOB 10";
            if (p.category_mode.ToUpper() == "SOB 15") return "SOB 15";
            if (p.category_mode.ToUpper() == "SOB 20") return "SOB 20";
            if (p.category_mode.ToUpper() == "SOB 40") return "SOB 40";
            if (p.category_mode.ToUpper() == "SOB 80") return "SOB 80";
            if (p.category_mode.ToUpper() == "SOB 160") return "SOB 160";
            if (p.category_mode.ToUpper() == "M5") return "M5";
            if (p.category_mode.ToUpper() == "M10") return "M10";
            if (p.category_mode.ToUpper() == "POR") return "POR";
            if (p.category_mode.ToUpper() == "MOP") return "MOP";
            if (p.category_mode.ToUpper() == "MM") return "MM";
            if (p.category_mode.ToUpper() == "MMP") return "MMP";
            if (p.category_mode.ToUpper() == "4Z9") return "4Z9";
            if (p.category_mode.ToUpper() == "SHA") return "SHA";
            if (p.category_mode.ToUpper() == "SWL") return "SWL";
            if (p.category_mode.ToUpper() == "NEW") return "NEW";


            return "XX";
        }

        private void CalculateBtn_Click(object sender, RoutedEventArgs e)
        {
            L_Status.Content = "Calculating";
            CalculateBtn.IsEnabled = false;
            pbStatus.Value = 3;

            if (!CalculateWorker.IsBusy)
            {
                CategoryMode = "No Filter";
                CategoryOperator = "No Filter";
                CategoryOrigin= "No Filter";
                CategoryPower = "No Filter";
                CategoryStation = "No Filter";
                CalculateWorker.RunWorkerAsync();
            }
        }
        
        private void CB_Mode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0) return;
            string val = (e.AddedItems[0] as ComboBoxItem).Content as string;
            CategoryMode = val ?? CategoryMode;
            FilterReport();
        }
        
        private void CB_Operator_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0) return;
            string val = (e.AddedItems[0] as ComboBoxItem).Content as string;
            CategoryOperator = val ?? CategoryOperator;
            FilterReport();
        }

        private void CB_Power_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0) return;
            string val = (e.AddedItems[0] as ComboBoxItem).Content as string;
            CategoryPower = val ?? CategoryPower;
            FilterReport();
        }

        private void CB_Station_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0) return;
            string val = (e.AddedItems[0] as ComboBoxItem).Content as string;
            CategoryStation = val ?? CategoryStation;
            FilterReport();
        }

        private void CB_Origin_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0) return;
            string val = (e.AddedItems[0] as ComboBoxItem).Content as string;
            CategoryOrigin = val ?? CategoryOrigin;
            FilterReport();
        }

        private void FilterReport()
        {
            if (FilteredReport == null) return;
            FilteredReport = new List<Participant>(Report);
            if (CategoryMode != "No Filter")
                FilteredReport.RemoveAll(p => p.category_mode.ToLower() != CategoryMode.ToLower());
            if (CategoryOrigin == "Israeli")
                FilteredReport.RemoveAll(p => !HolyLogParser.IsIsraeliStation(p.callsign));
            else if (CategoryOrigin == "Foreign")
                FilteredReport.RemoveAll(p => HolyLogParser.IsIsraeliStation(p.callsign));

            if (CategoryStation != "No Filter")
            {
                if (CategoryStation == "Fixed")
                    FilteredReport.RemoveAll(p => p.squers > 1 || p.callsign.ToLower().Contains(@"/p"));
                else if (CategoryStation == "Mobile")
                    FilteredReport.RemoveAll(p => p.squers < 2 || p.callsign.ToLower().Contains(@"/p"));
                else if (CategoryStation == "Portable")
                    FilteredReport.RemoveAll(p => p.squers > 1 || !p.callsign.ToLower().Contains(@"/p"));
            }
            DataContext = FilteredReport.OrderByDescending(p => p.score).ToList();

            if (L_NUmOfParticipantsValue != null)
                L_NUmOfParticipantsValue.Content = FilteredReport.Count();
            //Console.WriteLine("Category: " + CategoryMode + " : " + CategoryOperator + " : " + CategoryPower + " : " + CategoryStation);
        }

        
    }

    public struct HolylandData
    {
        //public bool success { get; set; }
        public List<Participant> participants { get; set; }
        public List<QSO> log { get; set; }
    }

    public struct Participant : ICloneable
    {
        public int id { get; set; }
        public string callsign { get; set; }
        public string category_mode { get; set; }
        public string email { get; set; }
        public string name { get; set; }
        public string country { get; set; }
        public int qsos { get; set; }
        public int mults { get; set; }
        public int squers { get; set; }
        public int points { get; set; }
        public int score { get; set; }
        public int is_manual { get; set; }

        public object Clone()
        {
            return new Participant() { callsign = this.callsign };
        }
    }

    //public struct Member
    //{
    //    public Member(string call, string email, string name, string link)
    //    {
    //        Call = call;
    //        Email = email;
    //        Name = name;
    //        Link = link;
    //    }
    //    public string Call { get; set; }
    //    public string Email { get; set; }
    //    public string Name { get; set; }
    //    public string Link { get; set; }
    //}
    
}
