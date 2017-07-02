using HolyParser;
using MoreLinq;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
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
        

        private BackgroundWorker CalculateWorker;
        private BackgroundWorker GetDataWorker;

        public MainWindow()
        {
            //DirectoryInfo d = new DirectoryInfo(@"C:\Users\gill\Desktop\holylandLogs");
            //FileInfo[] infos = d.GetFiles();
            //infos = infos.ToList().OrderByDescending(p => p.Length).ToArray();
            //int x = 1;
            //foreach (FileInfo f in infos)
            //{
            //    File.Move(f.FullName, f.DirectoryName + "\\kuku\\" + x.ToString() + ".txt");
            //    x++;
            //}

            //DirectoryInfo d = new DirectoryInfo(@"C:\Users\gill\Desktop\holylandLogs");
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

            //DoStuff();
            //sendMail();

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
        private async void DoStuff()
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

        private async void sendMail()
        {
            string to = @"gilifon@gmail.com,k3zo@arrl.net,4x4fw@iarc.org,4x4lf@arrl.net,4x6fk@iarc.org,4z1ed.elad@gmail.com,4z1km@iarc.org,4z1pf@iarc.org,4z4ch@iarc.org,4z4kxx@gmail.com,4z5ko@rambler.ru,4z5la@4z5la.net,4z5lu@rambler.ru,9206552@gmail.com,a.sela.4z1iz@gmail.com,aekyo@inter.net.il,alex4x1qq@gmail.com,amir@4x6tt.com,avi.rochman@gmail.com,ayrane@hotmail.com,berilayber@msn.com,bo_cedwall@hotmail.com,bronius.ly5o@gmail.com,bruno.roe@hispeed.ch,chananz@gmail.com,chikinviguera@yahoo.com,danielr@beitkama.org.il,dg0ks@gmx.de,DG5MLA@DARC.de,DG5OBB@darc.de,DH2URF@arcor.de,DJ6DO@darc.de,DK0SU@qsl.net,DK3AX@darc.de,dkatzman@shamir.co.il,DL1DTL@darc.de,dl3ank@darc.de,dl3drn@t-online.de,DL6KVA@gmx.de,DL7JOM@darc.de,DL7VRG@darc.de,DL8UKW@darc.de,DL9JON@t-online.de,dm5rc.mr@gmail.com,doronf2@gmail.com,dr.r.milker@milker.de,e77ea@blic.net,ea3elz@ymail.com,ea3hka@gmail.com,ea3hso@gmail.com,ea3na@ure.es,ek6raiars.76@mail.ru,elisha06@gmail.com,enio.ea2hw@gmail.com,er3ct@mail.ru,ethanhand@hotmail.com,eu1fq@mail.ru,ew8dx@mail.ru,ew8of@tut.by,ew8om@yandex.ru,f4ckf@orange.fr,f4gft@ref-union.org,f6eqz@numericable.fr,fenyo3jw@yahoo.com,ferdyroynalda@yahoo.com,g0bhk@btinternet.com,ha1dae@t-online.hu,ham.4x1uf@gmail.com,hamor.teleplus@gmail.com,hb3ygd@gmail.com,hegger@bezeqint.net,hf1d@jerzynajda.eu,ik0yuo@gmail.com,ik6xej@libero.it,israel.glockenberg@gmail.com,istvan.biliczky@gmail.com,jaksa.vidan@gmail.com,jan@paclt.cz,jonasleopold@gmx.de,josef.motycka@quick.cz,k2vco.vic@gmail.com,kocijancic.branko@gmail.com,kumasan.jo7kmb@gmail.com,la2hfa@gmail.com,lia@orel.ru,ljatev@yahoo.com,ly2bfn@gmail.com,ly2bmx@gmail.com,ly2ny@inbox.lt,LY3CY@YAHOO.COM,ly5w.sam@gmail.com,lz1cm@abv.bg,lz2zy@abv.bg,m3tqr@btinternet.com,martin.m0hom@gmail.com,mi0sai@hotmail.co.uk,mick_g3lik@ntlworld.com,miguelgonzalezr@gmail.com,n2kw@ymail.com,n8gu@arrl.net,oe1ciw@chello.at,oh4ty@sral.fi,ok1ay@email.cz,ok2ben@ok2ben.com,ok2qx@crk.cz,ok2sg@seznam.cz,om5mx@cq.sk,on3nd@outlook.fr,op4k@telenet.be,OZ1AAR@gmail.com,pa0mir@arrl.net,pa3cgj@amsat.org,pa3evy@amsat.org,pa5gu@kliksafe.nl,pb0acu@hocra.nl,pd3j@veron.nl,PD9BG@amsat.org,pe1ewr@zeelandnet.nl,quim.ea3ayq@orange.es,r2gb@mail.ru,r3aaa@mail.ru,r3zv1@mail.ru,ra4se@yandex.ru,ra7m@mail.ru,rc8sc@yandex.ru,rd1t@yandez.ru,rk9ue@mail.ru,rn6a@qrz.ru,roland.fischer@dl5ans.de,Ron@WQ6X.Info,rt6c@qrz.ru,ru3xb@mail.ru,rv6acc@mail.ru,RW3AI@MAIL.RU,rw9av@yandex.ru,rw9xu@mail.ru,salyi.laszlo@t-online.hu,schnalf53@gmx.de,school12kaz@mail.ru,scottmcleman36@gmail.com,sf3a@ssa.se,sm1tde@ssa.se,sm4dqe@ssa.se,sm5acq@telia.com,sp1jqj@hotmail.com,sp2ady@wp.pl,sp2hmy@op.pl,sp4lvk@gmail.com,sp5auy@gmail.com,sp5pdb@opor.org.pl,sp9clo@wp.pl,sp9fmp@wp.pl,sp9gfi@wp.pl,sp9kju@o2.pl,sq3pmx@wp.pl,sq3swd@jakubiak.pl,sq5ef-poland@o2.pl,SQ8AL@o2.pl,sq9fmu@poczta.onet.pl,sq9s@op.pl,sv3rpq@yahoo.com,ta1bm@hotmail.com,ta3ep@mail.com,tauno.karvo@gmail.com,ts870s@inbox.lv,u1bd@mail.ru,ua6arr@mail.ru,ua6hfi@mail.ru,ud8a@yandex.ru,us6ex@qsl.net,ut1zz@mail.ru,ux3it1@gmail.com,uy5va.victor@gmail.com,varsano5@gmail.com,ve3ukr@yahoo.com,volsson@uol.com.br,vytenis.sciucka@gmail.com,WA6POZ@arrl.net,xqslik@gmail.com,y07nsp@yahoo.com,yl2td@inbox.lv,yl3gaz@gmail.com,yo4aac@yahoo.com,yo7arz@hotmail.com,yo7awz@yahoo.com,yo7msj@gmail.com,yo8sao@yahoo.com,yo9bcm@gmail.com,yo9iab73@yahoo.com,yu1jf1955@gmail.com,ZOLYO5OHY@YAHOO.COM,zvisegal@yahoo.com,zwinczak@kki.net.pl";
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
            foreach (Participant p in RawData.participants.OrderByDescending(t=>t.qsos))
            {
                a++;
                if (p.is_manual == 0)
                {
                    IEnumerable<QSO> qsos = from q in RawData.log where Helper.getBareCallsign(q.MyCall) == Helper.getBareCallsign(p.callsign) select q;
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
        public int is_manual { get; set; }

        public object Clone()
        {
            return new Participant() { callsign = this.callsign };
        }
    }
    
}
