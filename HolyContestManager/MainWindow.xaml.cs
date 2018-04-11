using DXCCManager;
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
            SendMailingList();

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

        private async void SendMailingList()
        {
            List<Member> memlist = new List<Member>();
            //memlist.Add(new Member("4X1AG", "arogud@gmail.com", "אהרון", "https://iarc.org/squarereg/?user=ecc7d9ec-8f8e-4a30-9c4a-9702649e612c"));
            //memlist.Add(new Member("4X1BE", "beteshr@gmail.com", "רן", "https://iarc.org/squarereg/?user=8ca133a9-e100-4e5e-b209-66f89e23be7e"));
            //memlist.Add(new Member("4X1BY", "4x4by@iarc.org", "צבי", "https://iarc.org/squarereg/?user=f325549e-3d02-41a5-81d6-5fa57114f93c"));
            //memlist.Add(new Member("4X1DA", "4x1da@iarc.org", "ריץ'", "https://iarc.org/squarereg/?user=e4074e9d-58b1-413a-9e88-a5b82fe7b571"));
            //memlist.Add(new Member("4X1DG", "david.greenberg@bezeqint.net", "דוד", "https://iarc.org/squarereg/?user=bfe8b0a3-a3eb-4809-b8f6-8776395ffb35"));
            //memlist.Add(new Member("4X1DX", "seth@barak.net.il", "משה", "https://iarc.org/squarereg/?user=682aa587-c389-45e5-8034-cb307e1dbf23"));
            //memlist.Add(new Member("4X1EM", "norman@bezeqint.net", "נעם", "https://iarc.org/squarereg/?user=7edb3ab8-8ede-4df7-aa7f-0f48bb972eb2"));
            //memlist.Add(new Member("4X1ET", "eli4x1et@gmail.com", "אלי", "https://iarc.org/squarereg/?user=472a7ae8-9d3b-4d7c-bae3-b76604f31201"));
            //memlist.Add(new Member("4X1GE", "adrory@bezeqint.net", "אבנר", "https://iarc.org/squarereg/?user=294e42d6-fa0a-4c1b-8334-d2249d1e1bb2"));
            //memlist.Add(new Member("4X1GM", "geoffreymendelson@gmail.com", "ג'פרי", "https://iarc.org/squarereg/?user=ad67cbb7-2fad-49c2-bdc5-272c6b65852a"));
            //memlist.Add(new Member("4X1GP", "lapid@systems.ccc-cloud.com", "פלג", "https://iarc.org/squarereg/?user=4767b63b-1886-469f-82f0-ac02d83b1335"));
            //memlist.Add(new Member("4X1GT", "gtal@netvision.net.il", "גדעון", "https://iarc.org/squarereg/?user=3cb63b96-d5e0-446e-a210-ddc7ec39e76c"));
            //memlist.Add(new Member("4X1HF", "avinoam.albo@gmail.com", "אבינועם", "https://iarc.org/squarereg/?user=aeede350-dbc6-4610-b94c-c7be7e64a5e6"));
            //memlist.Add(new Member("4X1JT", "haramaty@gmail.com", "ישראל", "https://iarc.org/squarereg/?user=90886fdc-e848-49db-ac72-845bacbc150e"));
            //memlist.Add(new Member("4X1KF", "michael.barak@gmail.com", "מיכאל", "https://iarc.org/squarereg/?user=c06f5de6-b32b-42e4-a9b8-c4f65b15e096"));
            //memlist.Add(new Member("4X1KO", "jamaica@actcom.net.il", "עודד", "https://iarc.org/squarereg/?user=9b1451bf-988b-412d-9355-2c5ad4697a7b"));
            //memlist.Add(new Member("4X1LM", "milu111@gmail.com", "מיכאל", "https://iarc.org/squarereg/?user=1094d1d7-62fb-4086-bea2-9359162ee12e"));
            //memlist.Add(new Member("4X1MA", "4z5rzohad@gmail.com", "אוהד", "https://iarc.org/squarereg/?user=492327e5-7fb8-4cff-8832-9cb483aa4dec"));
            //memlist.Add(new Member("4X1MK", "gang@urim.org.il", "רון", "https://iarc.org/squarereg/?user=acaa3054-ffff-4542-861a-5bad0aeac725"));
            //memlist.Add(new Member("4X1OM", "4x1om@iarc.org", "ישראל", "https://iarc.org/squarereg/?user=3a47adb2-777e-4928-bfec-640220ec64e6"));
            //memlist.Add(new Member("4X1PD", "dovalep@gmail.com", "דב", "https://iarc.org/squarereg/?user=2a2cba3e-1e1a-42de-b190-19f01ec3e216"));
            //memlist.Add(new Member("4X1PF", "gideonch@netvision.net.il", "גדעון", "https://iarc.org/squarereg/?user=7b1d75c1-7e0a-47c6-92b6-057bf27c9a9e"));
            //memlist.Add(new Member("4X1PS", "4x1psavibar@gmail.com", "אבי", "https://iarc.org/squarereg/?user=094954fb-ae7c-4336-a6d3-5b6f7a41254d"));
            //memlist.Add(new Member("4X1SK", "rosenned@netvision.net.il", "דניאל", "https://iarc.org/squarereg/?user=96900e01-0f64-4365-afc6-2d842ee880c8"));
            //memlist.Add(new Member("4X1TI", "efi4x1ti@gmail.com", "אפרים", "https://iarc.org/squarereg/?user=5c35cdef-3002-4c80-b0ed-603d58c42472"));
            //memlist.Add(new Member("4X1UF", "ham.4x1uf@gmail.com", "ישראל", "https://iarc.org/squarereg/?user=ad20e50b-5c6c-4338-bdd5-22e5bdce8634"));
            //memlist.Add(new Member("4X1UH", "dikogold@gmail.com", "דוד", "https://iarc.org/squarereg/?user=16d9f523-d0da-4d03-8227-4c444187c743"));
            //memlist.Add(new Member("4X1UK", "giladind@gmail.com", "זיו", "https://iarc.org/squarereg/?user=7211d8eb-6ae1-4d91-9e3d-0ec7078a7380"));
            //memlist.Add(new Member("4X1VE", "arye.reinharz@gmail.com", "אריה", "https://iarc.org/squarereg/?user=6e3aea2c-b87f-4228-aee5-a01d43ea5371"));
            //memlist.Add(new Member("4X1YR", "dizitech@zahav.net.il", "יצחק", "https://iarc.org/squarereg/?user=dca816a3-961d-4b61-b56e-35e973d594e7"));
            //memlist.Add(new Member("4X1YV", "avri.dotan@gmail.com", "אברי", "https://iarc.org/squarereg/?user=425f967e-aa20-4f0b-8ff6-f1f40f53c9b4"));
            //memlist.Add(new Member("4X1ZQ", "avishay.4x1zq@gmail.com", "אבישי", "https://iarc.org/squarereg/?user=af9a93a7-c440-4d96-a532-2c704de5ac04"));
            //memlist.Add(new Member("4X1ZX", "doronav@mcc.org.il", "דורון", "https://iarc.org/squarereg/?user=af9b7c3a-312b-4ec5-abba-e89144c346dc"));
            //memlist.Add(new Member("4X1ZZ", "hsilverwater@gmail.com", "האוורד", "https://iarc.org/squarereg/?user=01647873-0c5f-4bfe-ae56-8076975a1424"));
            //memlist.Add(new Member("4X4AW", "arie@4x4aw.net", "אריה", "https://iarc.org/squarereg/?user=1d834aaf-006c-4b61-80ae-e99edc9656f6"));
            //memlist.Add(new Member("4X4CP", "a4x4cp@netvision.net.il", "עוזי", "https://iarc.org/squarereg/?user=f26f0fc3-b249-4c12-93bd-753c17c9a0f5"));
            //memlist.Add(new Member("4X4CQ", "tzvika@post.tau.ac.il", "צבי", "https://iarc.org/squarereg/?user=13c125f5-3165-49b2-b763-6567cbf6ef47"));
            //memlist.Add(new Member("4X4EB", "eib@netvision.net.il", "איתן", "https://iarc.org/squarereg/?user=d1dc12f8-77c1-47b2-ac53-f98f38fac1cc"));
            //memlist.Add(new Member("4X4FD", "4x4fdil@gmail.com", "אלי", "https://iarc.org/squarereg/?user=9eff9398-d1e2-427c-b998-dd93c1764787"));
            //memlist.Add(new Member("4X4JW", "heizler@netvision.net.il", "נפתלי", "https://iarc.org/squarereg/?user=f678d306-6aca-480d-9f41-bb4e671a9884"));
            //memlist.Add(new Member("4X4KR", "bilbaz@gmail.com", "בצלאל", "https://iarc.org/squarereg/?user=a02a1d4e-0b53-40c7-9fb0-ba2c4331cb24"));
            //memlist.Add(new Member("4X4LF", "4x4lf@kissufim.org.il", "שלמה", "https://iarc.org/squarereg/?user=04db5d84-be54-4edb-aa70-2b7fef853d38"));
            //memlist.Add(new Member("4X4MF", "asobel@netvision.net.il", "עמוס", "https://iarc.org/squarereg/?user=49414e62-7eb8-437b-b6b4-359bec974f45"));
            //memlist.Add(new Member("4X4OE", "hilik@polegfamily.com", "יחיאל", "https://iarc.org/squarereg/?user=56a5e593-31a3-4359-8be2-e761bf97501b"));
            //memlist.Add(new Member("4X4OQ", "zviamit1@bezeqint.net", "צבי", "https://iarc.org/squarereg/?user=3ad11467-cb8c-42d1-a9fc-6c2a0a677295"));
            //memlist.Add(new Member("4X4WH", "dd7373@hotmail.com", "דוד", "https://iarc.org/squarereg/?user=abde926e-d1a7-42e9-bbb2-ae2548d8bb47"));
            //memlist.Add(new Member("4X5AA", "taldo1@gmail.com", "טל", "https://iarc.org/squarereg/?user=1949d064-521e-4c89-93e9-ddc697594c8c"));
            //memlist.Add(new Member("4X5AF", "konstkiselyov@gmail.com", "קונסטנטין", "https://iarc.org/squarereg/?user=6cebdbd8-e655-4401-a616-75a621d2c820"));
            //memlist.Add(new Member("4X5BG", "josefbg1@gmail.com", "יוסף", "https://iarc.org/squarereg/?user=e54c2ea4-9857-48bc-948d-428dab9ff052"));
            //memlist.Add(new Member("4X5CQ", "yehoshua.4x5cq@gmail.com", "יהושע", "https://iarc.org/squarereg/?user=c2f554fb-6c4c-494c-ac33-ef66cad53b77"));
            //memlist.Add(new Member("4X5DG", "gross.dadi@gmail.com", "דדי", "https://iarc.org/squarereg/?user=e5ff1e8f-39bd-4894-877a-5bb3bdd76c4a"));
            //memlist.Add(new Member("4X5DM", "iosaaris@gmail.com", "דמיטרי", "https://iarc.org/squarereg/?user=121c32c8-23cc-4f8c-bc69-fe45e9518063"));
            //memlist.Add(new Member("4X5DS", "davidsaa2003@walla.co.il", "דוד", "https://iarc.org/squarereg/?user=8003d7a1-ac35-47df-a43c-7a43498323ef"));
            //memlist.Add(new Member("4X5EB", "elisha05@walla.com", "אלישע", "https://iarc.org/squarereg/?user=02fd33e5-8a2f-4f30-8cfc-c928b62a2b78"));
            //memlist.Add(new Member("4X5IP", "paster1@zahav.net.il", "יצחק", "https://iarc.org/squarereg/?user=d2613f55-de7c-4eea-bf85-99ba8baf8630"));
            //memlist.Add(new Member("4X5JG", "gorjaime@gmail.com", "גורדון", "https://iarc.org/squarereg/?user=7261fc43-003a-4964-8d5c-9f8d5dc25236"));
            //memlist.Add(new Member("4X5MG", "ido1990@gmail.com", "עידו", "https://iarc.org/squarereg/?user=abf3c28d-8c51-4a0b-9a4d-55a2a89d3f2e"));
            //memlist.Add(new Member("4X6AA", "alon5131@gmail.com", "אלון", "https://iarc.org/squarereg/?user=a2a4d25f-dfde-4cb5-9d4d-77701686d5ae"));
            //memlist.Add(new Member("4X6AG", "gadi.alon@gmail.com", "גד", "https://iarc.org/squarereg/?user=f53c66b9-11ea-4206-be97-168ff97cfc4e"));
            //memlist.Add(new Member("4X6AV", "oded@sbc-law.co.il", "עודד", "https://iarc.org/squarereg/?user=e2b04ba0-ee55-4eb2-8183-6a0c1cb03662"));

            //memlist.Add(new Member("4X6DK", "dk4x6dk@012.net.il", "דוד", "https://iarc.org/squarereg/?user=84aecf3f-032f-4a17-853c-408b62a5f90a"));
            //memlist.Add(new Member("4X6FK", "nir_i@netvision.net.il", "ניר", "https://iarc.org/squarereg/?user=5f1abfb7-1f69-458c-be78-4adffe7110d1"));
            //memlist.Add(new Member("4X6FS", "a.zafon@gmail.com", "שלמה", "https://iarc.org/squarereg/?user=d9c41648-fdfc-4462-bf57-4bac6d248bd0"));
            //memlist.Add(new Member("4X6FT", "hass.arie@gmail.com", "אריה", "https://iarc.org/squarereg/?user=55c63f81-44ed-4c5f-abbf-cc4b283ba2b4"));
            //memlist.Add(new Member("4X6GO", "marian11@013.net", "מריאן", "https://iarc.org/squarereg/?user=a6636134-589c-4262-bb53-6a585e20f834"));
            //memlist.Add(new Member("4X6HP", "yuli.kaplan@gmail.com", "יולי", "https://iarc.org/squarereg/?user=b646d152-c387-4e33-aae8-94de94b802e5"));
            //memlist.Add(new Member("4X6HT", "ami_r@netvision.net.il", "אמי", "https://iarc.org/squarereg/?user=cbcc9239-6d14-4a06-9a24-e21ef402ee06"));
            //memlist.Add(new Member("4X6HU", "4x6hu73@gmail.com", "רמי", "https://iarc.org/squarereg/?user=627e1ed4-fa89-4fea-87dd-efe1ea38c6d5"));
            //memlist.Add(new Member("4X6HX", "shwartz2000@gmail.com", "מיכאל", "https://iarc.org/squarereg/?user=2e54e095-4ddf-4d7c-be3d-4e53e15871ae"));
            //memlist.Add(new Member("4X6HZ", "4x6hz@iarc.org", "זיו", "https://iarc.org/squarereg/?user=31a3bc00-b1cb-4777-ac7d-85925007f07e"));
            //memlist.Add(new Member("4X6IG", "iarc@effect1.com", "רון", "https://iarc.org/squarereg/?user=21d62b22-a37b-4e8a-bc8f-e3328acc1a0f"));
            //memlist.Add(new Member("4X6IR", "magicjim@bezeqint.net", "בני", "https://iarc.org/squarereg/?user=60d231cd-d1fb-4d56-84b4-373e930a766b"));
            //memlist.Add(new Member("4X6KA", "baron6ka@netvision.net.il", "יאיר", "https://iarc.org/squarereg/?user=6d271bb0-788f-4ce8-a801-8e534dea55ad"));
            //memlist.Add(new Member("4X6KF", "falafel@bezeqint.net", "משה", "https://iarc.org/squarereg/?user=c0b3d7a2-6e4f-495c-a046-79f120028c62"));
            //memlist.Add(new Member("4X6KM", "medic282@gmail.com", "מיכאל", "https://iarc.org/squarereg/?user=5fe64193-5d11-4528-989a-dae28a62f7ff"));
            //memlist.Add(new Member("4X6KZ", "amite@gilat.com", "עמית", "https://iarc.org/squarereg/?user=2434fce0-29ea-4fc7-8d9f-81ba15c8db9b"));
            //memlist.Add(new Member("4X6MI", "4x6mi@globalqsl.com", "עזר", "https://iarc.org/squarereg/?user=3c4a9ce2-6512-4a28-8c23-c97d7cd8c333"));
            //memlist.Add(new Member("4X6RE", "raskineyal@gmail.com", "אייל", "https://iarc.org/squarereg/?user=df53ce09-277a-4dc6-9149-1666b99712b1"));
            //memlist.Add(new Member("4X6RO", "e-daniel@inter.net.il", "דניאל", "https://iarc.org/squarereg/?user=943bf3fc-1380-4ea3-90d6-8f8a8f8a51c6"));
            //memlist.Add(new Member("4X6RW", "arieron@shefayim.org.il", "אריה", "https://iarc.org/squarereg/?user=1acfa94b-d3e6-4efb-a9ee-56e6ec8588a9"));
            //memlist.Add(new Member("4X6TF", "meronoz@zahav.net.il", "עוז", "https://iarc.org/squarereg/?user=c1a99a8a-010c-4b5b-9424-723810dc6266"));
            //memlist.Add(new Member("4X6TT", "amir@4x6tt.com", "אמיר", "https://iarc.org/squarereg/?user=e854e2ad-7dd5-4b98-b58e-c70db2a93a81"));
            //memlist.Add(new Member("4X6UB", "ido.roseman@gmail.com", "עדו", "https://iarc.org/squarereg/?user=3896f67f-df1e-49f0-b3fc-696551bc817a"));
            //memlist.Add(new Member("4X6VO", "ronnie4x6vo@gmail.com", "רון", "https://iarc.org/squarereg/?user=6ca77a1b-c8c5-4ffb-b5d7-d5914d2de1fb"));
            //memlist.Add(new Member("4X6YA", "hilik4x6ya@gmail.com", "חיליק", "https://iarc.org/squarereg/?user=4bec9eec-3da5-4f6c-b06f-11ffe1cf8f15"));
            //memlist.Add(new Member("4X6YJ", "ron641@gmail.com", "רון", "https://iarc.org/squarereg/?user=2539306a-0f52-4ecc-9501-17e47380f137"));
            //memlist.Add(new Member("4X6YW", "yoram_ar@maabarot.org.il", "יורם", "https://iarc.org/squarereg/?user=bdc55579-65a7-4fe3-b7f2-e9552d4f9bad"));
            //memlist.Add(new Member("4X6YZ", "g-arie@zahav.net.il", "אריה", "https://iarc.org/squarereg/?user=dfab8377-be3a-4613-ac41-1f310f9c1508"));
            //memlist.Add(new Member("4X6ZM", "udikedem1@gmail.com", "אודי", "https://iarc.org/squarereg/?user=211faf76-94e3-423c-924a-80922c7b064f"));
            //memlist.Add(new Member("4Z1AC", "iradirad@gmail.com", "עירד", "https://iarc.org/squarereg/?user=16b70d22-e7bc-43fe-8ae3-9096db36ec84"));
            //memlist.Add(new Member("4Z1AL", "abrahamlevyg@gmail.com", "אברהם", "https://iarc.org/squarereg/?user=b8de44bc-8f88-4c9b-a297-61d2ae10bbad"));
            //memlist.Add(new Member("4Z1ED", "eladagan.y@gmail.com", "אלעד", "https://iarc.org/squarereg/?user=8d028edb-e549-41e3-938e-171cd54853b7"));
            //memlist.Add(new Member("4Z1IM", "4z1im.mark@gmail.com", "מרק", "https://iarc.org/squarereg/?user=396d17f9-820c-41fa-bdaf-cf0e6f62a438"));
            //memlist.Add(new Member("4Z1IW", "israelw2000@gmail.com", "ישראל", "https://iarc.org/squarereg/?user=7f29b437-cf1a-4777-bad4-b0150a5056b5"));
            //memlist.Add(new Member("4Z1JS", "4z1js@iarc.org", "יעקב", "https://iarc.org/squarereg/?user=a5a544db-3a4c-468f-969c-c7927ddb151e"));
            //memlist.Add(new Member("4Z1KM", "micha_kl@walla.com", "מיכה", "https://iarc.org/squarereg/?user=c84d786d-fb5f-4c5d-8d3b-8aa1bd770055"));
            //memlist.Add(new Member("4Z1PS", "hamami75@gmail.com", "סימן-טוב", "https://iarc.org/squarereg/?user=59b12e01-71d1-49a2-8032-85afdb1c16c6"));
            //memlist.Add(new Member("4Z1TL", "4z4tl@iarc.org", "צחי", "https://iarc.org/squarereg/?user=dc9d57c7-446c-46e9-b347-c4035e8cbcaa"));
            //memlist.Add(new Member("4Z1UF", "4z1uf@bezeqint.net", "איליה", "https://iarc.org/squarereg/?user=fdb3c1f1-e541-4877-9966-7f1c3e4d3cea"));
            //memlist.Add(new Member("4Z1UG", "4z1ug@guth.us", "אריק", "https://iarc.org/squarereg/?user=648a4aaf-cd38-49ba-a508-e8e7a2763a04"));
            //memlist.Add(new Member("4Z1VC", "yosip@ariel.ac.il", "יוסי", "https://iarc.org/squarereg/?user=e29a42f5-51eb-4df2-bfc4-fce05f638353"));
            //memlist.Add(new Member("4Z1WF", "nemoka2000@yahoo.com", "נסטור/משה", "https://iarc.org/squarereg/?user=4fd8277a-0752-4a0a-aad5-0f0934a78daa"));
            //memlist.Add(new Member("4Z1WS", "4z1ws@amsat.org", "שמאי", "https://iarc.org/squarereg/?user=aa97b397-a5c8-4508-885d-088e5f1500be"));
            //memlist.Add(new Member("4Z1ZV", "zvisegal@yahoo.com", "צבי", "https://iarc.org/squarereg/?user=b722632f-509b-4144-964a-74704dabf09e"));
            //memlist.Add(new Member("4Z4AB", "avi@avgal.co.il", "אבי", "https://iarc.org/squarereg/?user=e0873f6d-6868-4bc4-a3d3-183b3f0f3aec"));
            //memlist.Add(new Member("4Z4BA", "avibuskila@gmail.com", "אבי", "https://iarc.org/squarereg/?user=dcc156df-1cf8-489f-93f0-36d298963ad4"));
            //memlist.Add(new Member("4Z4BS", "4z4bs@walla.com", "שלום", "https://iarc.org/squarereg/?user=d5d94e5c-726d-449d-ab7d-42ebbda3f600"));
            //memlist.Add(new Member("4Z4CH", "4z4ch@iarc.org", "רן", "https://iarc.org/squarereg/?user=9514b946-1004-4e72-a2f5-14d4092d0d48"));
            //memlist.Add(new Member("4Z4DR", "4z4dr@opsimath.net", "ריצרד", "https://iarc.org/squarereg/?user=92b8cd4e-cb1f-4e8a-85d0-2a5de7b98628"));

            memlist.Add(new Member("4Z1KD", "gilifon@gmail.com", "גיל", "https://iarc.org/squarereg/?user=1e633a1b-559b-4fc9-8b85-6f1479827a0a"));

            StringBuilder sb = new StringBuilder();
            
            foreach (Member mem in memlist)
            {
                sb.Clear();
                sb.AppendLine("<div style=\"text-align: right; direction: rtl\">שלום ~name~,<br>").AppendLine("אם תרצה לרשום את ריבוע ארץ הקודש ממנו תפעיל בזמן התחרות השנה,<br>");
                sb.AppendLine("אז הנה הלינק שלך(הוא כולל מזהה אישי שלך במערכת - אל תעביר אותו לאף חובב אחר):<br>");
                sb.AppendLine("~link~<br>");
                sb.AppendLine("73 ובהצלחה,<br>");
                sb.AppendLine("הועדה המארגנת.<br><br><br>");
                sb.AppendLine("אם כבר נרשמת או אם לדעתך קיבלת מייל זה בטעות, אנא התעלם ממנו.<br></div>");

                sb.Replace("~name~", mem.Name);
                sb.Replace("~link~", mem.Link);

                string Sendemail_result = await Services.SendMail("info@iarc.org", mem.Email, "תחרות ארץ הקודש - רישום ריבועים", sb.ToString());
            }            
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
                //if (p.callsign.ToLower() != "ly2ny") continue;
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
            string insert_hlwtest = GenerateMultipleInsert(FilteredReport);
        }

        private string GenerateMultipleInsert(IList<Participant> participants)
        {
            StringBuilder squars = new StringBuilder(10);
            RadioEntityResolver rem = new RadioEntityResolver();
            StringBuilder sb = new StringBuilder("INSERT INTO `hlwtest` ", 500);
            sb.Append("(`active`,`year`,`call`,`uniq_timestamp`,`dxcc`,`continent`,`category`,`qso`,`points`,`mults`,`score`,`operator`,`square`) VALUES ");
            foreach (Participant p in participants)
            {
                IEnumerable<QSO> qsos = from q in RawData.log where Helper.getBareCallsign(q.MyCall) == Helper.getBareCallsign(p.callsign) select q;
                foreach (var item in qsos.Where(xp => xp.MyCall.StartsWith("4X") || xp.MyCall.StartsWith("4Z")).Select(t => t.STX).Distinct())
                {
                    squars.AppendFormat("{0},", item);
                }
                sb.Append("(");
                sb.Append("'"); sb.Append(0); sb.Append("',");
                sb.Append("'"); sb.Append(2017); sb.Append("',");
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
            result += " ON DUPLICATE KEY UPDATE `call`=`call`";
            return result;
        }

        private string getCategory(Participant p)
        {
            if (p.category_op.ToLower() == "checklog") return "CHECKLOG";
            if (p.category_power.ToLower() == "qrp") return "QRP";
            if (p.category_op.ToLower() == "mobile") return "MOBILE";
            if (p.callsign.ToLower().Contains(@"/p")) return "PORTABLE";
            if (p.category_op.ToLower() == "multi-op") return "MULTIOP";
            if (p.category_mode.ToLower() == "ssb") return "SSB";
            if (p.category_mode.ToLower() == "cw") return "CW";
            if (p.category_mode.ToLower() == "digi") return "DIGI";
            if (p.category_mode.ToLower() == "mixed") return "MIX";
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

    public struct Member
    {
        public Member(string call, string email, string name, string link)
        {
            Call = call;
            Email = email;
            Name = name;
            Link = link;
        }
        public string Call { get; set; }
        public string Email { get; set; }
        public string Name { get; set; }
        public string Link { get; set; }
    }
    
}
