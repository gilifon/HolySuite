using HolyParser;
using MoreLinq;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
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
        

        private BackgroundWorker CalculateWorker;
        private BackgroundWorker GetDataWorker;

        public MainWindow()
        {
            //DirectoryInfo d = new DirectoryInfo(@"C:\Users\gill\Desktop\holylandLogs2016");
            //FileInfo[] infos = d.GetFiles();
            //infos = infos.ToList().OrderByDescending(p => p.Length).ToArray();
            //int x = 1;
            //foreach (FileInfo f in infos)
            //{
            //    if (f.Extension.ToLower() != ".adi")
            //    {
            //        File.Move(f.FullName, f.DirectoryName + "\\" + x.ToString() + ".log");
            //        x++;
            //    }
            //}
            //FileInfo[] infos2 = d.GetFiles();
            //foreach (FileInfo f in infos2)
            //{
            //    string readText = File.ReadAllText(f.FullName);
            //    if (!readText.Contains("START-OF-LOG"))
            //    {
            //        f.Delete();
            //    }
            //}


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

        private void GetDataWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            pbStatus.IsIndeterminate = false;
            CalculateBtn.IsEnabled = true;
            L_Status.Content = "Ready";
        }

        private void GetDataWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            GetData();
        }

        private void GetData()
        {
            WebRequest request = WebRequest.Create("http://www.iarc.org/ws/get_holyland_data.php");
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
            Report.Clear();
            
            int a = 0;
            int z = RawData.participants.Count();
            foreach (Participant p in RawData.participants)
            {
                a++;
                IEnumerable<QSO> qsos = from q in RawData.log where Services.getBareCallsign(q.my_call) == Services.getBareCallsign(p.callsign) select q;
                int numOfSquers = qsos.DistinctBy(q => q.my_square).Count();

                HolyLogParser lop = new HolyLogParser(Services.GenerateAdif(qsos), HolyLogParser.IsIsraeliStation(p.callsign) ? HolyLogParser.Operator.Israeli : HolyLogParser.Operator.Foreign);
                lop.Parse();

                Participant n = p;
                n.qsos = lop.ValidQsos;// qsos.Count();
                n.score = lop.Result;
                n.squers = numOfSquers;
                n.mults = lop.Mults;
                n.points = lop.Points;
                Report.Add(n);
                (sender as BackgroundWorker).ReportProgress(100*a/z);
            }
            Report = Report.OrderByDescending(p => p.score).ToList();
            FilteredReport = new List<Participant>(Report);

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
            if (CategoryOperator != "No Filter")
                FilteredReport.RemoveAll(p => p.category_op.ToLower() != CategoryOperator.ToLower());
            if (CategoryPower != "No Filter")
                FilteredReport.RemoveAll(p => p.category_power.ToLower() != CategoryPower.ToLower());
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
        public bool success { get; set; }
        public List<Participant> participants { get; set; }
        public List<QSO> log { get; set; }
    }

    public struct Participant : ICloneable
    {
        public int id { get; set; }
        public string callsign { get; set; }
        public string category_op { get; set; }
        public string category_mode { get; set; }
        public string category_power { get; set; }
        public string email { get; set; }
        public string name { get; set; }
        public string country { get; set; }
        public int qsos { get; set; }
        public int mults { get; set; }
        public int squers { get; set; }
        public int points { get; set; }
        public int score { get; set; }

        public object Clone()
        {
            return new Participant() { callsign = this.callsign };
        }
    }

    public class QSO
    {
        public int id { get; set; }
        public string my_call { get; set; }
        public string my_square { get; set; }
        public string frequency { get; set; }
        public string callsign { get; set; }
        public string rst_rcvd { get; set; }
        public string rst_sent { get; set; }
        public DateTime timestamp { get; set; }
        public string mode { get; set; }
        public string exchange { get; set; }
        public string comment { get; set; }
        public string band { get; set; }

        public string niceTimestamp
        {
            get { return timestamp.ToShortDateString() + " " + timestamp.ToLongTimeString(); }
        }
    }
}
