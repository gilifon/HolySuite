using HolyParser;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Data.SQLite;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HolyLogger
{
    public class DataAccess
    {
        private SQLiteConnection con = null;

        public DataAccess()
        {
            try
            {
                //string executable = System.Reflection.Assembly.GetExecutingAssembly().Location;
                //string path = (System.IO.Path.GetDirectoryName(executable));
                //AppDomain.CurrentDomain.SetData("DataDirectory", path);

                con = new SQLiteConnection(@"Data Source = Data\logDB.db;Version=3");
                con.Open();
            }
            catch (Exception e)
            {
                throw new Exception("Failed to connect to DB: " + e.Message);
            }
        }

        public QSO Insert(QSO qso)
        {
            if (con != null && con.State == System.Data.ConnectionState.Open)
            {
                SQLiteCommand insertSQL = new SQLiteCommand("INSERT INTO qso (my_callsign,my_square,frequency,band,dx_callsign,rst_rcvd,rst_sent,timestamp,mode,exchange,comment,name,country) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?)", con);
                insertSQL.Parameters.Add(new SQLiteParameter("my_callsign", qso.MyCall));
                insertSQL.Parameters.Add(new SQLiteParameter("my_square", qso.STX));
                insertSQL.Parameters.Add(new SQLiteParameter("frequency", qso.Freq));
                insertSQL.Parameters.Add(new SQLiteParameter("band", qso.Band));
                insertSQL.Parameters.Add(new SQLiteParameter("dx_callsign", qso.DXCall));
                insertSQL.Parameters.Add(new SQLiteParameter("rst_rcvd", qso.RST_RCVD));
                insertSQL.Parameters.Add(new SQLiteParameter("rst_sent", qso.RST_SENT));
                insertSQL.Parameters.Add(new SQLiteParameter("timestamp", qso.Time));
                insertSQL.Parameters.Add(new SQLiteParameter("mode", qso.Mode));
                insertSQL.Parameters.Add(new SQLiteParameter("exchange", qso.SRX));
                insertSQL.Parameters.Add(new SQLiteParameter("comment", qso.Comment));
                insertSQL.Parameters.Add(new SQLiteParameter("name", qso.Name));
                insertSQL.Parameters.Add(new SQLiteParameter("country", qso.Country));
                try
                {
                    insertSQL.ExecuteNonQuery();
                    ObservableCollection<QSO> top1 = GetTopQSOs(1);
                    return top1.FirstOrDefault();
                }
                catch (Exception ex)
                {
                    throw new Exception(ex.Message);
                }
            }
            return null;
        }
        public void Delete(int Id)
        {
            if (con != null && con.State == System.Data.ConnectionState.Open)
            {
                SQLiteCommand deleteSQL = new SQLiteCommand("DELETE FROM qso WHERE Id = ?", con);
                deleteSQL.Parameters.Add(new SQLiteParameter("Id", Id));
                try
                {
                    deleteSQL.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    throw new Exception(ex.Message);
                }
            }
        }
        public ObservableCollection<QSO> GetAllQSOs()
        {
            ObservableCollection<QSO> qso_list = new ObservableCollection<QSO>();
            string stm = "SELECT * FROM qso ORDER BY timestamp DESC";
            using (SQLiteCommand cmd = new SQLiteCommand(stm, con))
            {
                using (SQLiteDataReader rdr = cmd.ExecuteReader())
                {
                    while (rdr.Read())
                    {
                        QSO q = new QSO();
                        q.id = int.Parse(rdr["Id"].ToString());
                        q.Comment = (string)rdr["comment"];
                        q.DXCall = (string)rdr["dx_callsign"];
                        q.Mode = (string)rdr["mode"];
                        q.SRX = (string)rdr["exchange"];
                        q.Freq = (string)rdr["frequency"];
                        q.Band = (string)rdr["band"];
                        q.MyCall = (string)rdr["my_callsign"];
                        q.STX = (string)rdr["my_square"];
                        q.RST_RCVD = (string)rdr["rst_rcvd"];
                        q.RST_SENT = (string)rdr["rst_sent"];
                        q.Name = (string)rdr["name"];
                        q.Country = (string)rdr["country"];
                        q.Time = DateTime.Parse((string)rdr["timestamp"]).ToShortTimeString();
                        q.Date = DateTime.Parse((string)rdr["timestamp"]).ToShortDateString();
                        qso_list.Add(q);
                    }
                }
            }
            return qso_list;
        }
        public ObservableCollection<QSO> GetTopQSOs(int i)
        {
            ObservableCollection<QSO> qso_list = new ObservableCollection<QSO>();
            string stm = "SELECT * FROM qso ORDER BY Id DESC LIMIT " + i;
            using (SQLiteCommand cmd = new SQLiteCommand(stm, con))
            {
                using (SQLiteDataReader rdr = cmd.ExecuteReader())
                {
                    while (rdr.Read())
                    {
                        QSO q = new QSO();
                        q.id = int.Parse(rdr["Id"].ToString());
                        q.Comment = (string)rdr["comment"];
                        q.DXCall = (string)rdr["dx_callsign"];
                        q.Mode = (string)rdr["mode"];
                        q.SRX = (string)rdr["exchange"];
                        q.Freq = (string)rdr["frequency"];
                        q.Band = (string)rdr["band"];
                        q.MyCall = (string)rdr["my_callsign"];
                        q.STX = (string)rdr["my_square"];
                        q.RST_RCVD = (string)rdr["rst_rcvd"];
                        q.RST_SENT = (string)rdr["rst_sent"];
                        q.Name = (string)rdr["name"];
                        q.Country = (string)rdr["country"];
                        q.Time = DateTime.Parse((string)rdr["timestamp"]).ToShortTimeString();
                        q.Date = DateTime.Parse((string)rdr["timestamp"]).ToShortDateString();
                        qso_list.Add(q);
                    }
                }
            }
            return qso_list;
        }
        public int GetQsoCount()
        {
            string stm = "SELECT count(Id) FROM qso";
            using (SQLiteCommand cmd = new SQLiteCommand(stm, con))
            {
                cmd.CommandType = CommandType.Text;
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }
    }
}
