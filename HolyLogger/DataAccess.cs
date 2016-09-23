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
                string executable = System.Reflection.Assembly.GetExecutingAssembly().Location;
                string path = (System.IO.Path.GetDirectoryName(executable));
                AppDomain.CurrentDomain.SetData("DataDirectory", path);

                con = new SQLiteConnection(@"Data Source = |DataDirectory|\..\..\Data\logDB.db;Version=3");
                con.Open();
            }
            catch (Exception e)
            {
                throw new Exception("Failed to connect to DB: " + e.Message);
            }
        }

        public void Insert(QSO qso)
        {
            if (con != null && con.State == System.Data.ConnectionState.Open)
            {
                SQLiteCommand insertSQL = new SQLiteCommand("INSERT INTO qso (my_callsign,my_square,frequency,dx_callsign,rst_rcvd,rst_sent,timestamp,exchange,comment) VALUES (?,?,?,?,?,?,?,?,?)", con);
                insertSQL.Parameters.Add(new SQLiteParameter("my_callsign", qso.my_callsign));
                insertSQL.Parameters.Add(new SQLiteParameter("my_square", qso.my_square));
                insertSQL.Parameters.Add(new SQLiteParameter("frequency", qso.frequency));
                insertSQL.Parameters.Add(new SQLiteParameter("dx_callsign", qso.dx_callsign));
                insertSQL.Parameters.Add(new SQLiteParameter("rst_rcvd", qso.rst_rcvd));
                insertSQL.Parameters.Add(new SQLiteParameter("rst_sent", qso.rst_sent));
                insertSQL.Parameters.Add(new SQLiteParameter("timestamp", qso.timestamp));
                insertSQL.Parameters.Add(new SQLiteParameter("exchange", qso.exchange));
                insertSQL.Parameters.Add(new SQLiteParameter("comment", qso.comment));
                try
                {
                    insertSQL.ExecuteNonQuery();
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
                        q.id = rdr["Id"].ToString();
                        q.comment = (string)rdr["comment"];
                        q.dx_callsign = (string)rdr["dx_callsign"];
                        q.exchange = (string)rdr["exchange"];
                        q.frequency = (string)rdr["frequency"];
                        q.my_callsign = (string)rdr["my_callsign"];
                        q.my_square = (string)rdr["my_square"];
                        q.rst_rcvd = (string)rdr["rst_rcvd"];
                        q.rst_sent = (string)rdr["rst_sent"];
                        q.timestamp = DateTime.Parse((string)rdr["timestamp"]);
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
