using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;

namespace LIPO
{
    
    public partial class Form1 : Form
    {
        private static System.Timers.Timer aTimer;
        List<Station> stations;
        BackgroundWorker bw;

        public Form1()
        {
            InitializeComponent();
            
            stations = new List<Station>(6);            

            var xml = XDocument.Load(@"stations.xml");
            var query = from c in xml.Root.Descendants("station")
                        //where (int)c.Attribute("id") < 7
                        select new Station
                        {
                            Name = c.Element("name").Value,
                            IP = c.Element("ip").Value,
                            btn = new Button()
                        };            
            stations.InsertRange(0,query);

            foreach (Station s in stations)
            {
                s.InitializeStation(new Size(150, 150));
                MainPanel.Controls.Add(s.btn);
            }
                                    
            bw = new BackgroundWorker();
            bw.DoWork += bw_DoWork;
            bw.RunWorkerCompleted += bw_RunWorkerCompleted;
            bw.RunWorkerAsync();
        }

        void bw_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            toolStripStatusLabel1.Text = "Done";
            if(!bw.IsBusy)
                bw.RunWorkerAsync();            
        }

        void bw_DoWork(object sender, DoWorkEventArgs e)
        {
            toolStripStatusLabel1.Text = "Busy...";
            foreach (Station s in stations)
            {
                Pinger(s.btn, s.IP);
            }
            System.Threading.Thread.Sleep(2000);
        }

        public static bool Ping(string ip)
        {
            Ping pingSender = new Ping();
            IPAddress address;
            if (IPAddress.TryParse(ip, out address))
            {
                PingReply reply = pingSender.Send(address,1000);

                if (reply.Status == IPStatus.Success)
                {
                    return true;
                }
                else
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        private void Pinger(Button sender, string ip)
        {
            if (Ping(ip))
            {
                sender.BackColor = Color.LightGreen;
            }
            else
            {
                sender.BackColor = Color.Red;
            }
        }

        private void setStation(int index)
        {
            Settings settings = new Settings(stations[index].Name, stations[index].IP);
            settings.ShowDialog();
            if (settings.DialogResult == System.Windows.Forms.DialogResult.OK)
            {
                stations[index].Name = settings.StationName;
                stations[index].IP = settings.StationIP;
                stations[index].btn.Text = settings.StationName;
            }
        }
    }
    public class Station
    {
        public Button btn { get; set; }
        public string Name { get; set; }
        public string IP { get; set; }

        public Station()
        {
            Name = "";
            IP = "";
        }

        public Station(string Name, string IP)
        {
            Name = this.Name;
            IP = this.IP;
        }

        public void InitializeStation(Size BtnSize)
        {
            btn.Size = BtnSize;
            btn.Text = this.Name;
        }
    }
}
