using HolyParser;
using HolyServer.Properties;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HolyServer
{
    public class MyCustomApplicationContext : ApplicationContext
    {
        private NotifyIcon trayIcon;
        public static UdpClient Client;
        HolyLogParser _holyLogParser;

        public MyCustomApplicationContext()
        {
            // Initialize Tray Icon
            trayIcon = new NotifyIcon()
            {
                Icon = Resources.AppIcon,
                ContextMenu = new ContextMenu(new MenuItem[] {
                new MenuItem("Exit", Exit)
            }),
                Visible = true
            };
            InitUDPServer();
        }

        private void InitUDPServer()
        {
            try
            {
                Client = new UdpClient(Settings.Default.Port);//65301
                Client.BeginReceive(new AsyncCallback(StartUDPClient), null);
            }
            catch
            {
                System.Windows.Forms.MessageBox.Show("Failed to open UDP port");
                trayIcon.Visible = false;
                Application.Exit();
            }
        }

        private async void StartUDPClient(IAsyncResult res)
        {
            IPEndPoint RemoteIpEndPoint = new IPEndPoint(IPAddress.Any, 0);
            byte[] received = Client.EndReceive(res, ref RemoteIpEndPoint);
            string data = Encoding.UTF8.GetString(received);

            _holyLogParser = new HolyLogParser();
            QSO qso = _holyLogParser.ParseRawQSO(data);
            string resp = await sendToIARC(qso);

            Client.BeginReceive(new AsyncCallback(StartUDPClient), null);
        }

        private async Task<string> sendToIARC(QSO qso)
        {
            using (var client = new HttpClient())
            {
                var values = new Dictionary<string, string>
                    {
                        { "insertlog", GenerateMultipleInsert(new List<QSO>(){ qso}) }
                    };
                var content = new FormUrlEncodedContent(values);
                try
                {
                    var response = await client.PostAsync(Properties.Settings.Default.baseURL + "/Holyland/Server/AddLog.php", content);
                    return await response.Content.ReadAsStringAsync();
                }
                catch (Exception)
                {
                    return "Connection with server failed! Check your internet connection";
                }
            }
        }

        private string GenerateMultipleInsert(IList<QSO> qsos)
        {
            StringBuilder sb = new StringBuilder("INSERT INTO `log` ", 500);
            sb.Append("(`my_callsign`, `operator`, `my_square`, `my_locator`, `dx_locator`, `frequency`, `band`, `dx_callsign`, `rst_rcvd`, `rst_sent`, `timestamp`, `mode`, `exchange`, `comment`, `name`, `country`, `continent`, `prop_mode`, `sat_name` ) VALUES ");
            foreach (QSO qso in qsos)
            {
                sb.Append("(");
                sb.Append("'"); if (qso.MyCall != null) { sb.Append(qso.MyCall.Trim().Replace("'", "\"")); }
                sb.Append("',");
                sb.Append("'"); if (qso.Operator != null) { sb.Append(qso.Operator.Trim().Replace("'", "\"")); }
                sb.Append("',");
                sb.Append("'"); if (qso.STX != null) { sb.Append(qso.STX.Trim().Replace("'", "\"")); }
                sb.Append("',");
                sb.Append("'"); if (qso.MyLocator != null) { sb.Append(qso.MyLocator.Trim().Replace("'", "\"")); }
                sb.Append("',");
                sb.Append("'"); if (qso.DXLocator != null) { sb.Append(qso.DXLocator.Trim().Replace("'", "\"")); }
                sb.Append("',");
                sb.Append("'"); if (qso.Freq != null) { sb.Append(qso.Freq.Trim().Replace("'", "\"")); }
                sb.Append("',");
                sb.Append("'"); if (qso.Band != null) { sb.Append(qso.Band.Trim().Replace("'", "\"")); }
                sb.Append("',");
                sb.Append("'"); if (qso.DXCall != null) { sb.Append(qso.DXCall.Trim().Replace("'", "\"")); }
                sb.Append("',");
                sb.Append("'"); if (qso.RST_RCVD != null) { sb.Append(qso.RST_RCVD.Trim().Replace("'", "\"")); }
                sb.Append("',");
                sb.Append("'"); if (qso.RST_SENT != null) { sb.Append(qso.RST_SENT.Trim().Replace("'", "\"")); }
                sb.Append("',");
                sb.Append("'"); if (qso.Date != null) { sb.Append(qso.Date.Trim().Replace("'", "\"") + " " + qso.Time.Replace("'", "\"")); }
                sb.Append("',");
                sb.Append("'"); if (qso.Mode != null) { sb.Append(qso.Mode.Trim().Replace("'", "\"")); }
                sb.Append("',");
                sb.Append("'"); if (qso.SRX != null) { sb.Append(qso.SRX.Trim().Replace("'", "\"")); }
                sb.Append("',");
                sb.Append("'"); if (qso.Comment != null) { sb.Append(qso.Comment.Trim().Replace("'", "\"")); }
                sb.Append("',");
                sb.Append("'"); if (qso.Name != null) { sb.Append(qso.Name.Trim().Replace("'", "\"")); }
                sb.Append("',");
                sb.Append("'"); if (qso.Country != null) { sb.Append(qso.Country.Trim().Replace("'", "\"")); }
                sb.Append("',");
                sb.Append("'"); if (qso.Continent != null) { sb.Append(qso.Continent.Trim().Replace("'", "\"")); }
                sb.Append("',");
                sb.Append("'"); if (qso.PROP_MODE != null) { sb.Append(qso.PROP_MODE.Trim().Replace("'", "\"")); }
                sb.Append("',");
                sb.Append("'"); if (qso.SAT_NAME != null) { sb.Append(qso.SAT_NAME.Trim().Replace("'", "\"")); }
                sb.Append("'),");
            }
            string result = sb.ToString().TrimEnd(',');
            result += " ON DUPLICATE KEY UPDATE my_callsign=my_callsign";
            return result;
        }


        void Exit(object sender, EventArgs e)
        {
            // Hide tray icon, otherwise it will remain shown until user mouses over it
            trayIcon.Visible = false;

            Application.Exit();
        }
    }
}
