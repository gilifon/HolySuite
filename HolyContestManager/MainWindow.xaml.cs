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
        

        private BackgroundWorker bg;

        public MainWindow()
        {
            Report = new List<Participant>(200);
            FilteredReport = new List<Participant>(200);
            InitializeComponent();
            //GetData();
            bg = new BackgroundWorker();
            bg.WorkerReportsProgress = true;
            bg.DoWork += Bg_DoWork;
            bg.RunWorkerCompleted += Bg_RunWorkerCompleted;
            bg.ProgressChanged += Bg_ProgressChanged;
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

        private void Bg_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            DataContext = FilteredReport;
            pbStatus.Value = 0;
        }

        private void Bg_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            pbStatus.Value = e.ProgressPercentage;
        }

        private void Bg_DoWork(object sender, DoWorkEventArgs e)
        {
            Report.Clear();
            GetData();
            int a = 0;
            int z = RawData.participants.Count();
            foreach (Participant p in RawData.participants)
            {
                a++;
                IEnumerable<QSO> qsos = from q in RawData.log where q.my_call == p.callsign select q;
                int numOfSquers = qsos.DistinctBy(q => q.my_square).Count();

                HolyLogParser lop = new HolyLogParser(Services.GenerateAdif(qsos), HolyLogParser.IsIsraeliStation(p.callsign) ? HolyLogParser.Operator.Israeli : HolyLogParser.Operator.Foreign);
                lop.Parse();

                Participant n = p;
                n.qsos = qsos.Count().ToString();
                n.score = lop.Result.ToString();
                n.squers = numOfSquers.ToString();
                Report.Add(n);
                (sender as BackgroundWorker).ReportProgress(100*a/z);
            }
            Report = Report.OrderBy(p => p.score).ToList();
            FilteredReport = new List<Participant>(Report);
        }

        private void CalculateBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!bg.IsBusy)
                bg.RunWorkerAsync();
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

            //if (CategoryStation != "No Filter")
            //    if (CategoryStation == "Fixed" || CategoryStation == "Portable")
            //        FilteredReport.RemoveAll(p => p.squers != "1");
            //    else if (CategoryStation == "Mobile")
            //        FilteredReport.RemoveAll(p => p.squers == "1");

            DataContext = FilteredReport.OrderBy(p => p.score).ToList();
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
        public string qsos { get; set; }
        public string mults { get; set; }
        public string squers { get; set; }
        public string score { get; set; }

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
