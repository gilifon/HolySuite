using HolyParser;
using MoreLinq;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
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
    public partial class MainWindow : Window
    {
        public HolylandData RawData{ get; set; }
        private List<Participant> Report { get; set; }

        public MainWindow()
        {
            Report = new List<Participant>(200);
            DataContext = Report;
            InitializeComponent();
            GetData();
            CalculatePoints();
        }

        private void CalculatePoints()
        {
            foreach (Participant p in RawData.participants)
            {
                IEnumerable<QSO> qsos = from q in RawData.log where q.my_call == p.callsign select q;
                int numOfSquers = qsos.DistinctBy(q => q.my_square).Count();

                LogParser lop = new LogParser(Services.GenerateAdif(qsos), LogParser.IsIsraeliStation(p.callsign) ? LogParser.Operator.Israeli : LogParser.Operator.Foreign);
                lop.Parse();

                Participant n = p;
                n.qsos = qsos.Count().ToString();
                n.score = lop.Result.ToString();
                n.squers = numOfSquers.ToString();
                Report.Add(n);
            }
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
    }

    public struct HolylandData
    {
        public bool success { get; set; }
        public List<Participant> participants { get; set; }
        public List<QSO> log { get; set; }
    }

    public struct Participant
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
