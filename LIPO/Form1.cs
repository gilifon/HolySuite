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
                        where (int)c.Attribute("id") < 7
                        select new Station
                        {
                            stationName = c.Element("name").Value,
                            stationIP = c.Element("ip").Value
                        };            
            stations.InsertRange(0,query);

            if (stations.Count < 6)
            {
                for (int i=6-stations.Count; i > 0; i--)
                {
                    stations.Add(new Station());
                }
            }
            
            button1.Text = stations[0].stationName;
            button2.Text = stations[1].stationName;
            button3.Text = stations[2].stationName;
            button4.Text = stations[3].stationName;
            button5.Text = stations[4].stationName;
            button6.Text = stations[5].stationName;
            
            
            bw = new BackgroundWorker();
            bw.DoWork += bw_DoWork;
            bw.RunWorkerCompleted += bw_RunWorkerCompleted;
            bw.RunWorkerAsync();
        }

        void bw_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            toolStripStatusLabel1.Text = "Busy...";
            bw.RunWorkerAsync();
        }

        void bw_DoWork(object sender, DoWorkEventArgs e)
        {
            Pinger(button1, stations[0].stationIP);
            Pinger(button2, stations[1].stationIP);
            Pinger(button3, stations[2].stationIP);
            Pinger(button4, stations[3].stationIP);
            Pinger(button5, stations[4].stationIP);
            Pinger(button6, stations[5].stationIP);
            toolStripStatusLabel1.Text = "Done";
            System.Threading.Thread.Sleep(2000);
        }

        public static bool Ping(string ip)
        {
            Ping pingSender = new Ping();
            IPAddress address;
            if (IPAddress.TryParse(ip, out address))
            {
                PingReply reply = pingSender.Send(address);

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
            Settings settings = new Settings(stations[index].stationName, stations[index].stationIP);
            settings.ShowDialog();
            if (settings.DialogResult == System.Windows.Forms.DialogResult.OK)
            {
                stations[index].stationName = settings.StationName;
                stations[index].stationIP = settings.StationIP;
            }
        }

        private void button1_Click(object sender, EventArgs e)
        {
            setStation(0);
            ((Button)sender).Text = stations[0].stationName;
        }

        private void button2_Click(object sender, EventArgs e)
        {
            setStation(1);
            ((Button)sender).Text = stations[1].stationName;
        }

        private void button3_Click(object sender, EventArgs e)
        {
            setStation(2);
            ((Button)sender).Text = stations[2].stationName;
        }

        private void button4_Click(object sender, EventArgs e)
        {
            setStation(3);
            ((Button)sender).Text = stations[3].stationName;
        }

        private void button5_Click(object sender, EventArgs e)
        {
            setStation(4);
            ((Button)sender).Text = stations[4].stationName;
        }

        private void button6_Click(object sender, EventArgs e)
        {
            setStation(5);
            ((Button)sender).Text = stations[5].stationName;
        }
        

        private void propertiesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            
        }
    }
    public class Station
    {
        public string stationName { get; set; }
        public string stationIP { get; set; }

        public Station()
        {
            stationName = "";
            stationIP = "";
        }

        public Station(string Name, string IP)
        {
            stationName = Name;
            stationIP = IP;
        }
    }
}
