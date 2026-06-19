using HolyParser;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HolyLogger
{
    public class DataAccess
    {
        // Private static instance variable to hold the single instance of the class.
        private static DataAccess instance;
        private SQLiteConnection con = null;
        string dbPath = "";

        // Serializes every access to the single SQLite connection. The connection is not safe for
        // concurrent use, and it is touched from the UI thread, the UDP logging threads and the ADIF
        // import worker. Every public method takes this lock; it is re-entrant, so a public method
        // that calls another (e.g. Insert -> GetTopQSOs) is fine. The lock is only ever taken inside
        // DataAccess, so it cannot deadlock against outer locks held by callers.
        private readonly object _dbLock = new object();
        private static readonly object _instanceLock = new object();

        public bool SchemaHasChanged { get; set; }

        private DataAccess()
        {
            try
            {

                //string executable = System.Reflection.Assembly.GetExecutingAssembly().Location;
                //string path = (System.IO.Path.GetDirectoryName(executable));
                //AppDomain.CurrentDomain.SetData("DataDirectory", path);

                System.Reflection.Assembly assembly = System.Reflection.Assembly.GetExecutingAssembly();
                System.Diagnostics.FileVersionInfo fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly.Location);
                string company = fvi.CompanyName;
                string product = fvi.ProductName;
                string ApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

                dbPath = Path.Combine(ApplicationData, company, product, "logDB.db");

                SchemaHasChanged = false;

                Directory.CreateDirectory(Path.GetDirectoryName(dbPath));

                con = new SQLiteConnection(@"DataSource = " + dbPath + @";Version=3");
                con.Open();
                UpdateSchema();

            }
            catch (Exception e)
            {
                throw new Exception("Failed to connect to DB: " + e.Message);
            }
            
        }

        // Public static method to get the single instance of the class.
        public static DataAccess GetInstance()
        {
            // Double-checked locking so the singleton is created exactly once even if two threads
            // race here at startup.
            if (instance == null)
            {
                lock (_instanceLock)
                {
                    if (instance == null)
                        instance = new DataAccess();
                }
            }
            return instance;
        }

        public void Close()
        {
            lock (_dbLock)
            {
                con.Close();
                con.Dispose();
                instance = null;
            }
        }

        public QSO Insert(QSO qso)
        {
            lock (_dbLock)
            {
            if (con != null && con.State == System.Data.ConnectionState.Open)
            {
                SQLiteCommand insertSQL = new SQLiteCommand("INSERT INTO qso (my_callsign,operator,my_square,my_locator,dx_locator,frequency,band,dx_callsign,rst_rcvd,rst_sent,date,time,mode,submode,exchange,comment,name,country,continent,prop_mode,sat_name,soapbox) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)", con);
                insertSQL.Parameters.Add(new SQLiteParameter("my_callsign", qso.MyCall));
                insertSQL.Parameters.Add(new SQLiteParameter("operator", qso.Operator));
                insertSQL.Parameters.Add(new SQLiteParameter("my_square", qso.STX));
                insertSQL.Parameters.Add(new SQLiteParameter("my_locator", qso.MyLocator));
                insertSQL.Parameters.Add(new SQLiteParameter("dx_locator", qso.DXLocator));
                insertSQL.Parameters.Add(new SQLiteParameter("frequency", qso.Freq));
                insertSQL.Parameters.Add(new SQLiteParameter("band", qso.Band));
                insertSQL.Parameters.Add(new SQLiteParameter("dx_callsign", qso.DXCall));
                insertSQL.Parameters.Add(new SQLiteParameter("rst_rcvd", qso.RST_RCVD));
                insertSQL.Parameters.Add(new SQLiteParameter("rst_sent", qso.RST_SENT));
                insertSQL.Parameters.Add(new SQLiteParameter("date", qso.Date));
                insertSQL.Parameters.Add(new SQLiteParameter("time", qso.Time));
                insertSQL.Parameters.Add(new SQLiteParameter("mode", qso.Mode));
                insertSQL.Parameters.Add(new SQLiteParameter("submode", qso.SUBMode));
                insertSQL.Parameters.Add(new SQLiteParameter("exchange", qso.SRX));
                insertSQL.Parameters.Add(new SQLiteParameter("comment", qso.Comment));
                insertSQL.Parameters.Add(new SQLiteParameter("name", qso.Name));
                insertSQL.Parameters.Add(new SQLiteParameter("country", qso.Country));
                insertSQL.Parameters.Add(new SQLiteParameter("continent", qso.Continent));
                insertSQL.Parameters.Add(new SQLiteParameter("prop_mode", qso.PROP_MODE));
                insertSQL.Parameters.Add(new SQLiteParameter("sat_name", qso.SAT_NAME));
                insertSQL.Parameters.Add(new SQLiteParameter("soapbox", qso.SOAPBOX));
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
        }
        public bool Insert(IEnumerable<QSO> qsos)
        {
            lock (_dbLock)
            {
            if (con != null && con.State == System.Data.ConnectionState.Open)
            {
                SQLiteTransaction T = con.BeginTransaction();
                foreach (var qso in qsos)
                {
                    SQLiteCommand insertSQL = new SQLiteCommand("INSERT INTO qso (my_callsign,operator,my_square,my_locator,dx_locator,frequency,band,dx_callsign,rst_rcvd,rst_sent,date,time,mode,submode,exchange,comment,name,country,continent,prop_mode,sat_name,soapbox,eqsl_status,qrz_status,lotw_status) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,1,1,1)", con);
                    insertSQL.Transaction = T;
                    insertSQL.Parameters.Add(new SQLiteParameter("my_callsign", qso.MyCall));
                    insertSQL.Parameters.Add(new SQLiteParameter("operator", qso.Operator));
                    insertSQL.Parameters.Add(new SQLiteParameter("my_square", qso.STX));
                    insertSQL.Parameters.Add(new SQLiteParameter("my_locator", qso.MyLocator));
                    insertSQL.Parameters.Add(new SQLiteParameter("dx_locator", qso.DXLocator));
                    insertSQL.Parameters.Add(new SQLiteParameter("frequency", qso.Freq));
                    insertSQL.Parameters.Add(new SQLiteParameter("band", qso.Band));
                    insertSQL.Parameters.Add(new SQLiteParameter("dx_callsign", qso.DXCall));
                    insertSQL.Parameters.Add(new SQLiteParameter("rst_rcvd", qso.RST_RCVD));
                    insertSQL.Parameters.Add(new SQLiteParameter("rst_sent", qso.RST_SENT));
                    insertSQL.Parameters.Add(new SQLiteParameter("date", qso.Date));
                    insertSQL.Parameters.Add(new SQLiteParameter("time", qso.Time));
                    insertSQL.Parameters.Add(new SQLiteParameter("mode", qso.Mode));
                    insertSQL.Parameters.Add(new SQLiteParameter("submode", qso.SUBMode));
                    insertSQL.Parameters.Add(new SQLiteParameter("exchange", qso.SRX));
                    insertSQL.Parameters.Add(new SQLiteParameter("comment", qso.Comment));
                    insertSQL.Parameters.Add(new SQLiteParameter("name", qso.Name));
                    insertSQL.Parameters.Add(new SQLiteParameter("country", qso.Country));
                    insertSQL.Parameters.Add(new SQLiteParameter("continent", qso.Continent));
                    insertSQL.Parameters.Add(new SQLiteParameter("prop_mode", qso.PROP_MODE));
                    insertSQL.Parameters.Add(new SQLiteParameter("sat_name", qso.SAT_NAME));
                    insertSQL.Parameters.Add(new SQLiteParameter("soapbox", qso.SOAPBOX));
                }
                try
                {
                    T.Commit();
                    return true;
                }
                catch (Exception e)
                {
                    T.Rollback();
                    return false;
                }
            }
            return false;
            }
        }

        public int InsertBatch(IEnumerable<QSO> qsos, Action<int> progressCallback = null)
        {
            lock (_dbLock)
            {
            if (con == null || con.State != System.Data.ConnectionState.Open)
                throw new InvalidOperationException("Database connection is not open.");

            int faultyQso = 0;
            int processedQso = 0;

            using (SQLiteTransaction transaction = con.BeginTransaction())
            using (SQLiteCommand insertSQL = new SQLiteCommand("INSERT INTO qso (my_callsign,operator,my_square,my_locator,dx_locator,frequency,band,dx_callsign,rst_rcvd,rst_sent,date,time,mode,submode,exchange,comment,name,country,continent,prop_mode,sat_name,soapbox,eqsl_status,qrz_status,lotw_status) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,1,1,?)", con, transaction))
            {
                insertSQL.Parameters.Add(new SQLiteParameter("my_callsign"));
                insertSQL.Parameters.Add(new SQLiteParameter("operator"));
                insertSQL.Parameters.Add(new SQLiteParameter("my_square"));
                insertSQL.Parameters.Add(new SQLiteParameter("my_locator"));
                insertSQL.Parameters.Add(new SQLiteParameter("dx_locator"));
                insertSQL.Parameters.Add(new SQLiteParameter("frequency"));
                insertSQL.Parameters.Add(new SQLiteParameter("band"));
                insertSQL.Parameters.Add(new SQLiteParameter("dx_callsign"));
                insertSQL.Parameters.Add(new SQLiteParameter("rst_rcvd"));
                insertSQL.Parameters.Add(new SQLiteParameter("rst_sent"));
                insertSQL.Parameters.Add(new SQLiteParameter("date"));
                insertSQL.Parameters.Add(new SQLiteParameter("time"));
                insertSQL.Parameters.Add(new SQLiteParameter("mode"));
                insertSQL.Parameters.Add(new SQLiteParameter("submode"));
                insertSQL.Parameters.Add(new SQLiteParameter("exchange"));
                insertSQL.Parameters.Add(new SQLiteParameter("comment"));
                insertSQL.Parameters.Add(new SQLiteParameter("name"));
                insertSQL.Parameters.Add(new SQLiteParameter("country"));
                insertSQL.Parameters.Add(new SQLiteParameter("continent"));
                insertSQL.Parameters.Add(new SQLiteParameter("prop_mode"));
                insertSQL.Parameters.Add(new SQLiteParameter("sat_name"));
                insertSQL.Parameters.Add(new SQLiteParameter("soapbox"));
                insertSQL.Parameters.Add(new SQLiteParameter("lotw_status"));

                foreach (var qso in qsos)
                {
                    insertSQL.Parameters[0].Value = (object)qso.MyCall ?? DBNull.Value;
                    insertSQL.Parameters[1].Value = (object)qso.Operator ?? DBNull.Value;
                    insertSQL.Parameters[2].Value = (object)qso.STX ?? DBNull.Value;
                    insertSQL.Parameters[3].Value = (object)qso.MyLocator ?? DBNull.Value;
                    insertSQL.Parameters[4].Value = (object)qso.DXLocator ?? DBNull.Value;
                    insertSQL.Parameters[5].Value = (object)qso.Freq ?? DBNull.Value;
                    insertSQL.Parameters[6].Value = (object)qso.Band ?? DBNull.Value;
                    insertSQL.Parameters[7].Value = (object)qso.DXCall ?? DBNull.Value;
                    insertSQL.Parameters[8].Value = (object)qso.RST_RCVD ?? DBNull.Value;
                    insertSQL.Parameters[9].Value = (object)qso.RST_SENT ?? DBNull.Value;
                    insertSQL.Parameters[10].Value = (object)qso.Date ?? DBNull.Value;
                    insertSQL.Parameters[11].Value = (object)qso.Time ?? DBNull.Value;
                    insertSQL.Parameters[12].Value = (object)qso.Mode ?? DBNull.Value;
                    insertSQL.Parameters[13].Value = (object)qso.SUBMode ?? DBNull.Value;
                    insertSQL.Parameters[14].Value = (object)qso.SRX ?? DBNull.Value;
                    insertSQL.Parameters[15].Value = (object)qso.Comment ?? DBNull.Value;
                    insertSQL.Parameters[16].Value = (object)qso.Name ?? DBNull.Value;
                    insertSQL.Parameters[17].Value = (object)qso.Country ?? DBNull.Value;
                    insertSQL.Parameters[18].Value = (object)qso.Continent ?? DBNull.Value;
                    insertSQL.Parameters[19].Value = (object)qso.PROP_MODE ?? DBNull.Value;
                    insertSQL.Parameters[20].Value = (object)qso.SAT_NAME ?? DBNull.Value;
                    insertSQL.Parameters[21].Value = (object)qso.SOAPBOX ?? DBNull.Value;
                    insertSQL.Parameters[22].Value = qso.LotwStatus;

                    try
                    {
                        insertSQL.ExecuteNonQuery();
                    }
                    catch (Exception ex)
                    {
                        faultyQso++;
                        System.Diagnostics.Debug.WriteLine($"Failed to insert QSO in batch: {ex.Message}");
                    }

                    processedQso++;
                    progressCallback?.Invoke(processedQso);
                }

                try
                {
                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }

            return faultyQso;
            }
        }
        public void Update(QSO qso)
        {
            lock (_dbLock)
            {
            if (con != null && con.State == System.Data.ConnectionState.Open)
            {
                SQLiteCommand insertSQL = new SQLiteCommand("UPDATE qso SET my_callsign = @my_callsign ,operator = @operator ,my_square = @my_square,my_locator = @my_locator,dx_locator = @dx_locator,frequency = @frequency,band = @band,dx_callsign = @dx_callsign,rst_rcvd = @rst_rcvd,rst_sent = @rst_sent,date = @date,time = @time,mode = @mode,submode = @submode,exchange = @exchange,comment = @comment,name = @name,country = @country,continent = @continent,prop_mode = @prop_mode,sat_name = @sat_name, soapbox = @soapbox WHERE id = @id", con);
                insertSQL.Parameters.Add(new SQLiteParameter("@my_callsign", qso.MyCall));
                insertSQL.Parameters.Add(new SQLiteParameter("@operator", qso.Operator));
                insertSQL.Parameters.Add(new SQLiteParameter("@my_square", qso.STX));
                insertSQL.Parameters.Add(new SQLiteParameter("@my_locator", qso.MyLocator));
                insertSQL.Parameters.Add(new SQLiteParameter("@dx_locator", qso.DXLocator));
                insertSQL.Parameters.Add(new SQLiteParameter("@frequency", qso.Freq));
                insertSQL.Parameters.Add(new SQLiteParameter("@band", qso.Band));
                insertSQL.Parameters.Add(new SQLiteParameter("@dx_callsign", qso.DXCall));
                insertSQL.Parameters.Add(new SQLiteParameter("@rst_rcvd", qso.RST_RCVD));
                insertSQL.Parameters.Add(new SQLiteParameter("@rst_sent", qso.RST_SENT));
                insertSQL.Parameters.Add(new SQLiteParameter("@date", qso.Date));
                insertSQL.Parameters.Add(new SQLiteParameter("@time", qso.Time));
                insertSQL.Parameters.Add(new SQLiteParameter("@mode", qso.Mode));
                insertSQL.Parameters.Add(new SQLiteParameter("@submode", qso.SUBMode));
                insertSQL.Parameters.Add(new SQLiteParameter("@exchange", qso.SRX));
                insertSQL.Parameters.Add(new SQLiteParameter("@comment", qso.Comment));
                insertSQL.Parameters.Add(new SQLiteParameter("@name", qso.Name));
                insertSQL.Parameters.Add(new SQLiteParameter("@country", qso.Country));
                insertSQL.Parameters.Add(new SQLiteParameter("@continent", qso.Continent));
                insertSQL.Parameters.Add(new SQLiteParameter("@prop_mode", qso.PROP_MODE));
                insertSQL.Parameters.Add(new SQLiteParameter("@sat_name", qso.SAT_NAME));
                insertSQL.Parameters.Add(new SQLiteParameter("@soapbox", qso.SOAPBOX));
                insertSQL.Parameters.Add(new SQLiteParameter("@id", qso.id));

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
        }
        public void Delete(int Id)
        {
            lock (_dbLock)
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
        }
        public void DeleteAll()
        {
            lock (_dbLock)
            {
            if (con != null && con.State == System.Data.ConnectionState.Open)
            {
                SQLiteCommand deleteSQL = new SQLiteCommand("DELETE FROM qso", con);
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
        }
        public ObservableCollection<QSO> GetAllQSOs(Action<int> progressCallback = null)
        {
            lock (_dbLock)
            {
            CultureInfo enUS = new CultureInfo("en-US");
            ObservableCollection<QSO> qso_list = new ObservableCollection<QSO>();
            int totalCount = GetQsoCount();
            int processedCount = 0;
            int lastReportedProgress = -1;
            string stm = "SELECT * FROM qso ORDER BY date DESC, time DESC";
            using (SQLiteCommand cmd = new SQLiteCommand(stm, con))
            {
                using (SQLiteDataReader rdr = cmd.ExecuteReader())
                {
                    while (rdr.Read())
                    {
                        QSO q = new QSO();
                        if (rdr["Id"] != null) q.id = int.Parse(rdr["Id"].ToString());
                        if (rdr["comment"] != null) q.Comment = rdr["comment"].ToString();
                        if (rdr["dx_callsign"] != null) q.DXCall = rdr["dx_callsign"].ToString();
                        if (rdr["mode"] != null) q.Mode = rdr["mode"].ToString();
                        if (rdr["submode"] != null) q.SUBMode = rdr["submode"].ToString();
                        if (rdr["exchange"] != null) q.SRX = rdr["exchange"].ToString();
                        if (rdr["frequency"] != null) q.Freq = rdr["frequency"].ToString();
                        if (rdr["band"] != null) q.Band = rdr["band"].ToString();
                        if (rdr["my_callsign"] != null) q.MyCall = rdr["my_callsign"].ToString();
                        if (rdr["operator"] != null) q.Operator = rdr["operator"].ToString();
                        if (rdr["my_square"] != null) q.STX = rdr["my_square"].ToString();
                        if (rdr["my_locator"] != null) q.MyLocator = rdr["my_locator"].ToString();
                        if (rdr["dx_locator"] != null) q.DXLocator = rdr["dx_locator"].ToString();
                        if (rdr["rst_rcvd"] != null) q.RST_RCVD = rdr["rst_rcvd"].ToString();
                        if (rdr["rst_sent"] != null) q.RST_SENT = rdr["rst_sent"].ToString();
                        if (rdr["name"] != null) q.Name = rdr["name"].ToString();
                        if (rdr["country"] != null) q.Country = rdr["country"].ToString();
                        if (rdr["continent"] != null) q.Continent = rdr["continent"].ToString();
                        if (rdr["time"] != null) q.Time = rdr["time"].ToString();
                        if (rdr["date"] != null) q.Date = rdr["date"].ToString();
                        if (rdr["prop_mode"] != null) q.PROP_MODE = rdr["prop_mode"].ToString();
                        if (rdr["sat_name"] != null) q.SAT_NAME = rdr["sat_name"].ToString();
                        if (rdr["soapbox"] != null) q.SOAPBOX = rdr["soapbox"].ToString();
                        if (rdr["eqsl_status"] != null && rdr["eqsl_status"] != DBNull.Value) q.EqslStatus = Convert.ToInt32(rdr["eqsl_status"]);
                        if (rdr["lotw_status"] != null && rdr["lotw_status"] != DBNull.Value) q.LotwStatus = Convert.ToInt32(rdr["lotw_status"]);
                        q.StandartizeQSO();
                        qso_list.Add(q);

                        processedCount++;
                        if (totalCount > 0)
                        {
                            int progress = (int)Math.Floor((double)processedCount * 100 / totalCount);
                            if (progress > lastReportedProgress)
                            {
                                lastReportedProgress = progress;
                                progressCallback?.Invoke(progress);
                            }
                        }
                    }
                }
            }
            return qso_list;
            }
        }
        public ObservableCollection<QSO> GetTopQSOs(int i)
        {
            lock (_dbLock)
            {
            CultureInfo enUS = new CultureInfo("en-US");
            ObservableCollection<QSO> qso_list = new ObservableCollection<QSO>();
            string stm = "SELECT * FROM qso ORDER BY Id DESC LIMIT " + i;
            using (SQLiteCommand cmd = new SQLiteCommand(stm, con))
            {
                using (SQLiteDataReader rdr = cmd.ExecuteReader())
                {
                    while (rdr.Read())
                    {
                        QSO q = new QSO();
                        if (rdr["Id"] != null) q.id = int.Parse(rdr["Id"].ToString());
                        if (rdr["comment"] != null) q.Comment = (string)rdr["comment"];
                        if (rdr["dx_callsign"] != null) q.DXCall = (string)rdr["dx_callsign"];
                        if (rdr["mode"] != null) q.Mode = (string)rdr["mode"];
                        if (rdr["submode"] != null) q.SUBMode = rdr["submode"].ToString();
                        if (rdr["exchange"] != null) q.SRX = (string)rdr["exchange"];
                        if (rdr["frequency"] != null) q.Freq = (string)rdr["frequency"];
                        if (rdr["band"] != null) q.Band = (string)rdr["band"];
                        if (rdr["my_callsign"] != null) q.MyCall = (string)rdr["my_callsign"];
                        if (rdr["operator"] != null) q.Operator = rdr["operator"].ToString();
                        if (rdr["my_square"] != null) q.STX = (string)rdr["my_square"];
                        if (rdr["my_locator"] != null) q.MyLocator = rdr["my_locator"].ToString();
                        if (rdr["dx_locator"] != null) q.DXLocator = rdr["dx_locator"].ToString();
                        if (rdr["rst_rcvd"] != null) q.RST_RCVD = (string)rdr["rst_rcvd"];
                        if (rdr["rst_sent"] != null) q.RST_SENT = (string)rdr["rst_sent"];
                        if (rdr["name"] != null) q.Name = (string)rdr["name"];
                        if (rdr["country"] != null) q.Country = rdr["country"].ToString();
                        if (rdr["continent"] != null) q.Continent = rdr["continent"].ToString();
                        if (rdr["time"] != null) q.Time = (string)rdr["time"];
                        if (rdr["date"] != null) q.Date = (string)rdr["date"];
                        if (rdr["prop_mode"] != null) q.PROP_MODE = rdr["prop_mode"].ToString();
                        if (rdr["sat_name"] != null) q.SAT_NAME = rdr["sat_name"].ToString();
                        if (rdr["soapbox"] != null) q.SOAPBOX = rdr["soapbox"].ToString();
                        if (rdr["eqsl_status"] != null && rdr["eqsl_status"] != DBNull.Value) q.EqslStatus = Convert.ToInt32(rdr["eqsl_status"]);
                        if (rdr["lotw_status"] != null && rdr["lotw_status"] != DBNull.Value) q.LotwStatus = Convert.ToInt32(rdr["lotw_status"]);
                        q.StandartizeQSO();
                        qso_list.Add(q);
                    }
                }
            }
            return qso_list;
            }
        }
        public int GetQsoCount()
        {
            lock (_dbLock)
            {
            string stm = "SELECT count(Id) FROM qso";
            using (SQLiteCommand cmd = new SQLiteCommand(stm, con))
            {
                cmd.CommandType = CommandType.Text;
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
            }
        }
        public int GetGridCount()
        {
            lock (_dbLock)
            {
            string stm = "SELECT count(distinct exchange) FROM qso where dx_callsign like '4X%' or dx_callsign like '4Z%'";
            using (SQLiteCommand cmd = new SQLiteCommand(stm, con))
            {
                cmd.CommandType = CommandType.Text;
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
            }
        }
        public int GetDXCCCount()
        {
            lock (_dbLock)
            {
            string stm = "SELECT count(distinct country) FROM qso";
            using (SQLiteCommand cmd = new SQLiteCommand(stm, con))
            {
                cmd.CommandType = CommandType.Text;
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
            }
        }

        public ObservableCollection<RadioEvent> GetRadioEvents()
        {
            lock (_dbLock)
            {
            CultureInfo enUS = new CultureInfo("en-US");
            ObservableCollection<RadioEvent> radioEvent_list = new ObservableCollection<RadioEvent>();
            string stm = "SELECT * FROM radio_events ORDER BY Id ASC";
            using (SQLiteCommand cmd = new SQLiteCommand(stm, con))
            {
                using (SQLiteDataReader rdr = cmd.ExecuteReader())
                {
                    while (rdr.Read())
                    {
                        RadioEvent q = new RadioEvent();
                        if (rdr["Id"] != null) q.id = int.Parse(rdr["Id"].ToString());
                        if (rdr["name"] != null) q.Name = rdr["name"].ToString();
                        if (rdr["description"] != null) q.Description = rdr["description"].ToString();
                        if (rdr["is_categories"] != null) q.IsCategories = int.Parse(rdr["is_categories"].ToString()) == 1;
                        radioEvent_list.Add(q);
                    }
                }
            }
            return radioEvent_list;
            }
        }

        public ObservableCollection<GenericItem> GetTableData(string tableName, int eventId=1)
        {
            lock (_dbLock)
            {
            CultureInfo enUS = new CultureInfo("en-US");
            ObservableCollection<GenericItem> category_list = new ObservableCollection<GenericItem>();
            string stm = "SELECT * FROM " + tableName + " WHERE event_id = @eventId ORDER BY Id ASC";
            using (SQLiteCommand cmd = new SQLiteCommand(stm, con))
            {
                cmd.Parameters.Add(new SQLiteParameter("@eventId", eventId));
                using (SQLiteDataReader rdr = cmd.ExecuteReader())
                {
                    while (rdr.Read())
                    {
                        GenericItem q = new GenericItem();
                        if (rdr["Id"] != null) q.id = int.Parse(rdr["Id"].ToString());
                        if (rdr["name"] != null) q.Name = rdr["name"].ToString();
                        if (rdr["description"] != null) q.Description = rdr["description"].ToString();
                        if (rdr["event_id"] != null) q.EventId = int.Parse(rdr["event_id"].ToString());
                        category_list.Add(q);
                    }
                }
            }
            return category_list;
            }
        }

        private void AddColToTable(string tableName, string colName, string definition)
        {

            string stm = $"SELECT count(*) FROM pragma_table_info('{tableName}') WHERE name = '{colName}'";
            SQLiteCommand cmd = new SQLiteCommand(stm, con);
            try
            {
                int colCount = Convert.ToInt32(cmd.ExecuteScalar());
                if (colCount == 0)
                {
                    stm = $"ALTER TABLE {tableName} ADD COLUMN [" + colName + "] " + definition;
                    cmd = new SQLiteCommand(stm, con);
                    try
                    {
                        cmd.ExecuteNonQuery();
                        SchemaHasChanged = true;
                    }
                    catch (Exception ex)
                    {
                        throw new Exception(ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception(ex.Message);
            }

        }

        // Adds the eqsl_status column to an existing qso table the first time the user runs a build
        // that has the eQSL queue feature. Existing rows are back-filled to 1 ("already handled") so
        // that upgrading does NOT suddenly queue the user's entire historical log for eQSL upload.
        // Only QSOs logged after the upgrade (inserted with the default 0) become pending.
        private void AddEqslStatusColumn()
        {
            string check = "SELECT count(*) FROM pragma_table_info('qso') WHERE name = 'eqsl_status'";
            using (var cmd = new SQLiteCommand(check, con))
            {
                int colCount = Convert.ToInt32(cmd.ExecuteScalar());
                if (colCount == 0)
                {
                    using (var alter = new SQLiteCommand("ALTER TABLE qso ADD COLUMN [eqsl_status] INTEGER NOT NULL DEFAULT 0", con))
                        alter.ExecuteNonQuery();
                    using (var backfill = new SQLiteCommand("UPDATE qso SET eqsl_status = 1", con))
                        backfill.ExecuteNonQuery();
                    SchemaHasChanged = true;
                }
            }
        }

        // Adds the qrz_status and qrz_logid columns to an existing qso table the first time the user
        // runs a build that has the QRZ Logbook real-time push feature. Existing rows are back-filled
        // to qrz_status = 1 ("already handled") so upgrading does NOT suddenly queue the user's whole
        // historical log for upload to QRZ. Only QSOs logged after the upgrade (inserted with the
        // default 0) become pending.
        private void AddQrzColumns()
        {
            string check = "SELECT count(*) FROM pragma_table_info('qso') WHERE name = 'qrz_status'";
            using (var cmd = new SQLiteCommand(check, con))
            {
                int colCount = Convert.ToInt32(cmd.ExecuteScalar());
                if (colCount == 0)
                {
                    using (var alter = new SQLiteCommand("ALTER TABLE qso ADD COLUMN [qrz_status] INTEGER NOT NULL DEFAULT 0", con))
                        alter.ExecuteNonQuery();
                    using (var alter2 = new SQLiteCommand("ALTER TABLE qso ADD COLUMN [qrz_logid] nvarchar(50) NULL", con))
                        alter2.ExecuteNonQuery();
                    using (var backfill = new SQLiteCommand("UPDATE qso SET qrz_status = 1", con))
                        backfill.ExecuteNonQuery();
                    SchemaHasChanged = true;
                }
            }
        }

        // Returns the QSOs still waiting to be uploaded to QRZ Logbook (status 0), oldest first so they
        // are pushed in the order they were logged. Unlike eQSL there is no per-callsign opt-in table:
        // the single account API key plus the feature toggle govern whether these are actually sent.
        public List<QSO> GetPendingQrzQsos()
        {
            lock (_dbLock)
            {
            var list = new List<QSO>();
            if (con == null || con.State != ConnectionState.Open) return list;
            string stm = "SELECT * FROM qso WHERE qrz_status = 0 ORDER BY date ASC, time ASC, Id ASC";
            using (SQLiteCommand cmd = new SQLiteCommand(stm, con))
            using (SQLiteDataReader rdr = cmd.ExecuteReader())
            {
                while (rdr.Read())
                {
                    QSO q = new QSO();
                    if (rdr["Id"] != null) q.id = int.Parse(rdr["Id"].ToString());
                    if (rdr["comment"] != null) q.Comment = rdr["comment"].ToString();
                    if (rdr["dx_callsign"] != null) q.DXCall = rdr["dx_callsign"].ToString();
                    if (rdr["mode"] != null) q.Mode = rdr["mode"].ToString();
                    if (rdr["submode"] != null) q.SUBMode = rdr["submode"].ToString();
                    if (rdr["exchange"] != null) q.SRX = rdr["exchange"].ToString();
                    if (rdr["frequency"] != null) q.Freq = rdr["frequency"].ToString();
                    if (rdr["band"] != null) q.Band = rdr["band"].ToString();
                    if (rdr["my_callsign"] != null) q.MyCall = rdr["my_callsign"].ToString();
                    if (rdr["operator"] != null) q.Operator = rdr["operator"].ToString();
                    if (rdr["my_square"] != null) q.STX = rdr["my_square"].ToString();
                    if (rdr["my_locator"] != null) q.MyLocator = rdr["my_locator"].ToString();
                    if (rdr["dx_locator"] != null) q.DXLocator = rdr["dx_locator"].ToString();
                    if (rdr["rst_rcvd"] != null) q.RST_RCVD = rdr["rst_rcvd"].ToString();
                    if (rdr["rst_sent"] != null) q.RST_SENT = rdr["rst_sent"].ToString();
                    if (rdr["name"] != null) q.Name = rdr["name"].ToString();
                    if (rdr["country"] != null) q.Country = rdr["country"].ToString();
                    if (rdr["continent"] != null) q.Continent = rdr["continent"].ToString();
                    if (rdr["time"] != null) q.Time = rdr["time"].ToString();
                    if (rdr["date"] != null) q.Date = rdr["date"].ToString();
                    if (rdr["prop_mode"] != null) q.PROP_MODE = rdr["prop_mode"].ToString();
                    if (rdr["sat_name"] != null) q.SAT_NAME = rdr["sat_name"].ToString();
                    if (rdr["soapbox"] != null) q.SOAPBOX = rdr["soapbox"].ToString();
                    q.QrzStatus = 0;
                    list.Add(q);
                }
            }
            return list;
            }
        }

        // Number of QSOs still waiting to be uploaded to QRZ Logbook.
        public int GetPendingQrzCount()
        {
            lock (_dbLock)
            {
            if (con == null || con.State != ConnectionState.Open) return 0;
            using (SQLiteCommand cmd = new SQLiteCommand("SELECT count(Id) FROM qso WHERE qrz_status = 0", con))
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        // Updates the QRZ Logbook upload state of a single QSO (0 pending, 1 uploaded, 2 rejected) and,
        // on success, stores the LOGID transaction id QRZ returned next to the record.
        public void SetQrzStatus(int id, int status, string logId = null)
        {
            lock (_dbLock)
            {
            if (con == null || con.State != ConnectionState.Open) return;
            using (SQLiteCommand cmd = new SQLiteCommand("UPDATE qso SET qrz_status = @s, qrz_logid = @logid WHERE Id = @id", con))
            {
                cmd.Parameters.Add(new SQLiteParameter("@s", status));
                cmd.Parameters.Add(new SQLiteParameter("@logid", (object)logId ?? DBNull.Value));
                cmd.Parameters.Add(new SQLiteParameter("@id", id));
                cmd.ExecuteNonQuery();
            }
            }
        }

        // Returns the QSOs still waiting to be uploaded to eQSL (status 0), oldest first so they
        // are sent in the order they were logged.
        public List<QSO> GetPendingEqslQsos()
        {
            lock (_dbLock)
            {
            var list = new List<QSO>();
            if (con == null || con.State != ConnectionState.Open) return list;
            // Not-yet-sent QSOs whose station callsign is in the eQSL accounts table (the opt-in list).
            // QSOs under a callsign that isn't in the table are intentionally left out (the user chose
            // not to upload them).
            string stm = "SELECT * FROM qso WHERE eqsl_status = 0 AND my_callsign IN (SELECT callsign FROM eqsl_accounts) ORDER BY date ASC, time ASC, Id ASC";
            using (SQLiteCommand cmd = new SQLiteCommand(stm, con))
            using (SQLiteDataReader rdr = cmd.ExecuteReader())
            {
                while (rdr.Read())
                {
                    QSO q = new QSO();
                    if (rdr["Id"] != null) q.id = int.Parse(rdr["Id"].ToString());
                    if (rdr["comment"] != null) q.Comment = rdr["comment"].ToString();
                    if (rdr["dx_callsign"] != null) q.DXCall = rdr["dx_callsign"].ToString();
                    if (rdr["mode"] != null) q.Mode = rdr["mode"].ToString();
                    if (rdr["submode"] != null) q.SUBMode = rdr["submode"].ToString();
                    if (rdr["exchange"] != null) q.SRX = rdr["exchange"].ToString();
                    if (rdr["frequency"] != null) q.Freq = rdr["frequency"].ToString();
                    if (rdr["band"] != null) q.Band = rdr["band"].ToString();
                    if (rdr["my_callsign"] != null) q.MyCall = rdr["my_callsign"].ToString();
                    if (rdr["operator"] != null) q.Operator = rdr["operator"].ToString();
                    if (rdr["my_square"] != null) q.STX = rdr["my_square"].ToString();
                    if (rdr["my_locator"] != null) q.MyLocator = rdr["my_locator"].ToString();
                    if (rdr["dx_locator"] != null) q.DXLocator = rdr["dx_locator"].ToString();
                    if (rdr["rst_rcvd"] != null) q.RST_RCVD = rdr["rst_rcvd"].ToString();
                    if (rdr["rst_sent"] != null) q.RST_SENT = rdr["rst_sent"].ToString();
                    if (rdr["name"] != null) q.Name = rdr["name"].ToString();
                    if (rdr["country"] != null) q.Country = rdr["country"].ToString();
                    if (rdr["continent"] != null) q.Continent = rdr["continent"].ToString();
                    if (rdr["time"] != null) q.Time = rdr["time"].ToString();
                    if (rdr["date"] != null) q.Date = rdr["date"].ToString();
                    if (rdr["prop_mode"] != null) q.PROP_MODE = rdr["prop_mode"].ToString();
                    if (rdr["sat_name"] != null) q.SAT_NAME = rdr["sat_name"].ToString();
                    if (rdr["soapbox"] != null) q.SOAPBOX = rdr["soapbox"].ToString();
                    q.EqslStatus = 0;
                    list.Add(q);
                }
            }
            return list;
            }
        }

        // Number of QSOs still waiting to be uploaded to eQSL.
        public int GetPendingEqslCount()
        {
            lock (_dbLock)
            {
            if (con == null || con.State != ConnectionState.Open) return 0;
            using (SQLiteCommand cmd = new SQLiteCommand("SELECT count(Id) FROM qso WHERE eqsl_status = 0 AND my_callsign IN (SELECT callsign FROM eqsl_accounts)", con))
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        // Updates the eQSL upload state of a single QSO (0 pending, 1 sent, 2 rejected).
        public void SetEqslStatus(int id, int status)
        {
            lock (_dbLock)
            {
            if (con == null || con.State != ConnectionState.Open) return;
            using (SQLiteCommand cmd = new SQLiteCommand("UPDATE qso SET eqsl_status = @s WHERE Id = @id", con))
            {
                cmd.Parameters.Add(new SQLiteParameter("@s", status));
                cmd.Parameters.Add(new SQLiteParameter("@id", id));
                cmd.ExecuteNonQuery();
            }
            }
        }

        // ---- eQSL accounts (one per station callsign) -------------------------------------------

        // True if a station callsign appears in the eQSL accounts table. The table is the user's
        // explicit opt-in list: a callsign that is NOT in the table means "do not upload my QSOs
        // under this callsign to eQSL" (so no "!" badge, no upload).
        public bool IsCallsignInEqslTable(string callsign)
        {
            lock (_dbLock)
            {
            if (string.IsNullOrWhiteSpace(callsign) || con == null || con.State != ConnectionState.Open) return false;
            using (var cmd = new SQLiteCommand("SELECT count(*) FROM eqsl_accounts WHERE callsign = @c COLLATE NOCASE", con))
            {
                cmd.Parameters.Add(new SQLiteParameter("@c", callsign.Trim()));
                return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
            }
            }
        }

        // Creates the index that backs the eQSL queue lookups. The badge/queue queries filter on
        // eqsl_status (and my_callsign), so without this they scan the whole qso table on every
        // refresh. Idempotent (IF NOT EXISTS), so it is effectively a one-time cost.
        private void EnsureEqslIndexes()
        {
            try
            {
                using (var cmd = new SQLiteCommand("CREATE INDEX IF NOT EXISTS idx_qso_eqsl_status ON qso(eqsl_status, my_callsign)", con))
                    cmd.ExecuteNonQuery();
            }
            catch { /* an index is an optimization only; never block startup on it */ }
        }

        // Index that backs the QRZ Logbook pending-queue lookups (filter on qrz_status). Idempotent.
        private void EnsureQrzIndexes()
        {
            try
            {
                using (var cmd = new SQLiteCommand("CREATE INDEX IF NOT EXISTS idx_qso_qrz_status ON qso(qrz_status)", con))
                    cmd.ExecuteNonQuery();
            }
            catch { /* an index is an optimization only; never block startup on it */ }
        }

        // Adds the lotw_status column the first time the user runs a build that has the LoTW upload
        // feature. Existing rows are back-filled to 1 ("already handled") so upgrading does NOT
        // suddenly queue the user's whole historical log for upload to LoTW.
        private void AddLotwColumns()
        {
            string check = "SELECT count(*) FROM pragma_table_info('qso') WHERE name = 'lotw_status'";
            using (var cmd = new SQLiteCommand(check, con))
            {
                int colCount = Convert.ToInt32(cmd.ExecuteScalar());
                if (colCount == 0)
                {
                    using (var alter = new SQLiteCommand("ALTER TABLE qso ADD COLUMN [lotw_status] INTEGER NOT NULL DEFAULT 0", con))
                        alter.ExecuteNonQuery();
                    using (var backfill = new SQLiteCommand("UPDATE qso SET lotw_status = 1", con))
                        backfill.ExecuteNonQuery();
                    SchemaHasChanged = true;
                }
            }
        }

        // Index that backs the LoTW pending-queue lookups (filter on lotw_status). Idempotent.
        private void EnsureLotwIndex()
        {
            try
            {
                using (var cmd = new SQLiteCommand("CREATE INDEX IF NOT EXISTS idx_qso_lotw_status ON qso(lotw_status)", con))
                    cmd.ExecuteNonQuery();
            }
            catch { /* optimization only */ }
        }

        // Returns the QSOs still waiting to be uploaded to LoTW (status 0), oldest first.
        public List<QSO> GetPendingLotwQsos()
        {
            lock (_dbLock)
            {
            var list = new List<QSO>();
            if (con == null || con.State != ConnectionState.Open) return list;
            string stm = "SELECT * FROM qso WHERE lotw_status = 0 ORDER BY date ASC, time ASC, Id ASC";
            using (SQLiteCommand cmd = new SQLiteCommand(stm, con))
            using (SQLiteDataReader rdr = cmd.ExecuteReader())
            {
                while (rdr.Read())
                {
                    QSO q = new QSO();
                    if (rdr["Id"] != null) q.id = int.Parse(rdr["Id"].ToString());
                    if (rdr["comment"] != null) q.Comment = rdr["comment"].ToString();
                    if (rdr["dx_callsign"] != null) q.DXCall = rdr["dx_callsign"].ToString();
                    if (rdr["mode"] != null) q.Mode = rdr["mode"].ToString();
                    if (rdr["submode"] != null) q.SUBMode = rdr["submode"].ToString();
                    if (rdr["exchange"] != null) q.SRX = rdr["exchange"].ToString();
                    if (rdr["frequency"] != null) q.Freq = rdr["frequency"].ToString();
                    if (rdr["band"] != null) q.Band = rdr["band"].ToString();
                    if (rdr["my_callsign"] != null) q.MyCall = rdr["my_callsign"].ToString();
                    if (rdr["operator"] != null) q.Operator = rdr["operator"].ToString();
                    if (rdr["my_square"] != null) q.STX = rdr["my_square"].ToString();
                    if (rdr["my_locator"] != null) q.MyLocator = rdr["my_locator"].ToString();
                    if (rdr["dx_locator"] != null) q.DXLocator = rdr["dx_locator"].ToString();
                    if (rdr["rst_rcvd"] != null) q.RST_RCVD = rdr["rst_rcvd"].ToString();
                    if (rdr["rst_sent"] != null) q.RST_SENT = rdr["rst_sent"].ToString();
                    if (rdr["name"] != null) q.Name = rdr["name"].ToString();
                    if (rdr["country"] != null) q.Country = rdr["country"].ToString();
                    if (rdr["continent"] != null) q.Continent = rdr["continent"].ToString();
                    if (rdr["time"] != null) q.Time = rdr["time"].ToString();
                    if (rdr["date"] != null) q.Date = rdr["date"].ToString();
                    if (rdr["prop_mode"] != null) q.PROP_MODE = rdr["prop_mode"].ToString();
                    if (rdr["sat_name"] != null) q.SAT_NAME = rdr["sat_name"].ToString();
                    if (rdr["soapbox"] != null) q.SOAPBOX = rdr["soapbox"].ToString();
                    q.LotwStatus = 0;
                    list.Add(q);
                }
            }
            return list;
            }
        }

        // Number of QSOs still waiting to be uploaded to LoTW.
        public int GetPendingLotwCount()
        {
            lock (_dbLock)
            {
            if (con == null || con.State != ConnectionState.Open) return 0;
            using (SQLiteCommand cmd = new SQLiteCommand("SELECT count(Id) FROM qso WHERE lotw_status = 0", con))
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        // Updates the LoTW upload state of a single QSO (0 pending, 1 uploaded, 2 rejected).
        public void SetLotwStatus(int id, int status)
        {
            lock (_dbLock)
            {
            if (con == null || con.State != ConnectionState.Open) return;
            using (SQLiteCommand cmd = new SQLiteCommand("UPDATE qso SET lotw_status = @s WHERE Id = @id", con))
            {
                cmd.Parameters.Add(new SQLiteParameter("@s", status));
                cmd.Parameters.Add(new SQLiteParameter("@id", id));
                cmd.ExecuteNonQuery();
            }
            }
        }

        // Resets lotw_status to 0 (pending) for all QSOs on or after the given date string
        // (format "YYYY-MM-DD"). Returns the number of rows affected.
        public int ResetLotwStatusFromDate(string fromDate)
        {
            lock (_dbLock)
            {
            if (con == null || con.State != ConnectionState.Open) return 0;
            using (SQLiteCommand cmd = new SQLiteCommand("UPDATE qso SET lotw_status = 0 WHERE date >= @d", con))
            {
                cmd.Parameters.Add(new SQLiteParameter("@d", fromDate));
                return cmd.ExecuteNonQuery();
            }
            }
        }

        // Creates the eqsl_accounts table the first time. The table is managed entirely by hand in
        // Options -> eQSL Service; nothing is ever added automatically.
        private void EnsureEqslAccountsTable()
        {
            if (TableExists("eqsl_accounts")) return;

            string sql = @"
            CREATE TABLE [eqsl_accounts] (
                [Id] INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL
            , [callsign] nvarchar(100) NOT NULL UNIQUE COLLATE NOCASE
            , [username] nvarchar(100) NULL COLLATE NOCASE
            , [password] nvarchar(255) NULL
            );";
            using (var cmd = new SQLiteCommand(sql, con))
                cmd.ExecuteNonQuery();
        }

        private static EqslAccount ReadEqslAccount(SQLiteDataReader rdr)
        {
            return new EqslAccount
            {
                Id = rdr["Id"] == DBNull.Value ? 0 : Convert.ToInt32(rdr["Id"]),
                Callsign = rdr["callsign"] == DBNull.Value ? string.Empty : rdr["callsign"].ToString(),
                Username = rdr["username"] == DBNull.Value ? string.Empty : rdr["username"].ToString(),
                Password = rdr["password"] == DBNull.Value ? string.Empty : rdr["password"].ToString()
            };
        }

        // Returns all eQSL accounts (one row per station callsign), callsign ascending.
        public List<EqslAccount> GetEqslAccounts()
        {
            lock (_dbLock)
            {
            var list = new List<EqslAccount>();
            if (con == null || con.State != ConnectionState.Open) return list;
            using (var cmd = new SQLiteCommand("SELECT Id, callsign, username, password FROM eqsl_accounts ORDER BY callsign ASC", con))
            using (var rdr = cmd.ExecuteReader())
            {
                while (rdr.Read())
                    list.Add(ReadEqslAccount(rdr));
            }
            return list;
            }
        }

        // Returns the eQSL account for a station callsign, or null if there is no row for it.
        public EqslAccount GetEqslAccount(string callsign)
        {
            lock (_dbLock)
            {
            if (string.IsNullOrWhiteSpace(callsign) || con == null || con.State != ConnectionState.Open) return null;
            using (var cmd = new SQLiteCommand("SELECT Id, callsign, username, password FROM eqsl_accounts WHERE callsign = @c COLLATE NOCASE LIMIT 1", con))
            {
                cmd.Parameters.Add(new SQLiteParameter("@c", callsign.Trim()));
                using (var rdr = cmd.ExecuteReader())
                {
                    if (rdr.Read()) return ReadEqslAccount(rdr);
                }
            }
            return null;
            }
        }

        // Inserts (Id == 0) or updates (by Id) an eQSL account row. The row is keyed by its Id so the
        // callsign itself can be edited. Returns false (with an error message) if the callsign is
        // blank or already used by a different row. On a successful insert, account.Id is filled in.
        public bool SaveEqslAccount(EqslAccount account, out string error)
        {
            lock (_dbLock)
            {
            error = null;
            if (account == null) { error = "No account."; return false; }
            if (con == null || con.State != ConnectionState.Open) { error = "Database not available."; return false; }

            string callsign = (account.Callsign ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(callsign)) { error = "Station callsign cannot be empty."; return false; }

            // Reject a callsign already used by another row.
            using (var dup = new SQLiteCommand("SELECT count(*) FROM eqsl_accounts WHERE callsign = @c COLLATE NOCASE AND Id <> @id", con))
            {
                dup.Parameters.Add(new SQLiteParameter("@c", callsign));
                dup.Parameters.Add(new SQLiteParameter("@id", account.Id));
                if (Convert.ToInt32(dup.ExecuteScalar()) > 0)
                {
                    error = "The callsign " + callsign + " is already in the table.";
                    return false;
                }
            }

            if (account.Id == 0)
            {
                using (var cmd = new SQLiteCommand("INSERT INTO eqsl_accounts (callsign, username, password) VALUES (@c,@u,@p)", con))
                {
                    cmd.Parameters.Add(new SQLiteParameter("@c", callsign));
                    cmd.Parameters.Add(new SQLiteParameter("@u", (object)(account.Username ?? string.Empty)));
                    cmd.Parameters.Add(new SQLiteParameter("@p", (object)(account.Password ?? string.Empty)));
                    cmd.ExecuteNonQuery();
                }
                using (var idCmd = new SQLiteCommand("SELECT last_insert_rowid()", con))
                    account.Id = Convert.ToInt32(idCmd.ExecuteScalar());
            }
            else
            {
                using (var cmd = new SQLiteCommand("UPDATE eqsl_accounts SET callsign = @c, username = @u, password = @p WHERE Id = @id", con))
                {
                    cmd.Parameters.Add(new SQLiteParameter("@c", callsign));
                    cmd.Parameters.Add(new SQLiteParameter("@u", (object)(account.Username ?? string.Empty)));
                    cmd.Parameters.Add(new SQLiteParameter("@p", (object)(account.Password ?? string.Empty)));
                    cmd.Parameters.Add(new SQLiteParameter("@id", account.Id));
                    cmd.ExecuteNonQuery();
                }
            }
            account.Callsign = callsign;
            return true;
            }
        }

        // Removes an eQSL account row by its Id (used by the "Remove" button).
        public void DeleteEqslAccount(int id)
        {
            lock (_dbLock)
            {
            if (id <= 0 || con == null || con.State != ConnectionState.Open) return;
            using (var cmd = new SQLiteCommand("DELETE FROM eqsl_accounts WHERE Id = @id", con))
            {
                cmd.Parameters.Add(new SQLiteParameter("@id", id));
                cmd.ExecuteNonQuery();
            }
            }
        }

        // Function to check if a table exists in the database
        bool TableExists(string tableName)
        {
            using (var command = new SQLiteCommand($"SELECT name FROM sqlite_master WHERE type='table' AND name='{tableName}'", con))
            {
                using (var reader = command.ExecuteReader())
                {
                    return reader.HasRows;
                }
            }
        }

        private void UpdateSchema()
        {
            string createTable_qso = @"
            CREATE TABLE [qso] (
                [Id] INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL
            , [my_callsign] nvarchar(100) NOT NULL COLLATE NOCASE
            , [operator] nvarchar(100) NULL COLLATE NOCASE
            , [my_square] nvarchar(100) NULL COLLATE NOCASE
            , [my_locator] nvarchar(100) NULL COLLATE NOCASE
            , [dx_locator] nvarchar(100) NULL COLLATE NOCASE
            , [frequency] nvarchar(100) NULL COLLATE NOCASE
            , [band] nvarchar(100) NOT NULL COLLATE NOCASE
            , [dx_callsign] nvarchar(100) NOT NULL COLLATE NOCASE
            , [rst_rcvd] nvarchar(100) NULL COLLATE NOCASE
            , [rst_sent] nvarchar(100) NULL COLLATE NOCASE
            , [date] nvarchar(100) NOT NULL COLLATE NOCASE
            , [time] nvarchar(100) NOT NULL COLLATE NOCASE
            , [mode] nvarchar(100) NOT NULL COLLATE NOCASE
            , [submode] nvarchar(100) NULL COLLATE NOCASE
            , [exchange] nvarchar(100) NULL COLLATE NOCASE
            , [comment] nvarchar(500) NULL COLLATE NOCASE
            , [name] nvarchar(500) NULL COLLATE NOCASE
            , [country] nvarchar(100) NULL COLLATE NOCASE
            , [continent] nvarchar(100) NULL COLLATE NOCASE
            , [prop_mode] nvarchar(100) NULL COLLATE NOCASE
            , [sat_name] nvarchar(100) NULL COLLATE NOCASE
            , [soapbox] nvarchar(100) NULL COLLATE NOCASE
            , [eqsl_status] INTEGER NOT NULL DEFAULT 0
            , [qrz_status] INTEGER NOT NULL DEFAULT 0
            , [qrz_logid] nvarchar(50) NULL
            , [lotw_status] INTEGER NOT NULL DEFAULT 0
            );";

            string createTable_categories = @"
            DROP TABLE IF EXISTS[categories];
            CREATE TABLE [categories] (
                [Id] INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL
            , [name] nvarchar(100) NOT NULL COLLATE NOCASE
            , [description] nvarchar(100) NOT NULL COLLATE NOCASE
            , [event_id] bigint NOT NULL
            );
            INSERT INTO [categories] ([Id],[name],[description],[event_id]) VALUES (
            1,'','NONE',1);
            INSERT INTO [categories] ([Id],[name],[description],[event_id]) VALUES (
            2,'POR','Portable (1 Square)',1);
            INSERT INTO [categories] ([Id],[name],[description],[event_id]) VALUES (
            3,'M5','Mobile 5 (5 Squares)',1);
            INSERT INTO [categories] ([Id],[name],[description],[event_id]) VALUES (
            4,'M10','Mobile 10 (10 Squares)',1);
            INSERT INTO [categories] ([Id],[name],[description],[event_id]) VALUES (
            5,'YN','YN (Under 20 / License < 3 Years)',1);";

            string createTable_radio_events = @"
            DROP TABLE IF EXISTS[radio_events];
            CREATE TABLE [radio_events] (
                [Id] INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL
            , [name] nvarchar(100) NOT NULL COLLATE NOCASE
            , [description] nvarchar(100) NOT NULL COLLATE NOCASE
            , [is_categories] INTEGER NOT NULL COLLATE NOCASE
            );
            INSERT INTO [radio_events] ([Id],[name],[description],[is_categories]) VALUES (
            1,'holyland','Holyland Contest',1);
            INSERT INTO [radio_events] ([Id],[name],[description],[is_categories]) VALUES (
            2,'sukot','Sukot',1);
            INSERT INTO [radio_events] ([Id],[name],[description],[is_categories]) VALUES (
            3,'iarc','IARC Event',1);";

            string createTable_bands = @"DROP TABLE IF EXISTS[bands];
            CREATE TABLE [bands] (
                [Id] INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL
            , [name] nvarchar(100) NOT NULL COLLATE NOCASE
            , [description] nvarchar(100) NOT NULL COLLATE NOCASE
            , [event_id] bigint NOT NULL
            );
            INSERT INTO [bands] ([Id],[name],[description],[event_id]) VALUES (
            1,'ALL','ALL',1);
            INSERT INTO [bands] ([Id],[name],[description],[event_id]) VALUES (
            2,'10','10M',1);
            INSERT INTO [bands] ([Id],[name],[description],[event_id]) VALUES (
            3,'15','15M',1);
            INSERT INTO [bands] ([Id],[name],[description],[event_id]) VALUES (
            4,'20','20M',1);
            INSERT INTO [bands] ([Id],[name],[description],[event_id]) VALUES (
            5,'40','40M',1);
            INSERT INTO [bands] ([Id],[name],[description],[event_id]) VALUES (
            6,'80','80M',1);";

            string createTable_operators = @"DROP TABLE IF EXISTS[operators];
            CREATE TABLE [operators] (
                [Id] INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL
            , [name] nvarchar(100) NOT NULL COLLATE NOCASE
            , [description] nvarchar(100) NOT NULL COLLATE NOCASE
            , [event_id] bigint NOT NULL
            );
            INSERT INTO [operators] ([Id],[name],[description],[event_id]) VALUES (
            1,'SINGLE-OP','SINGLE-OP',1);
            INSERT INTO [operators] ([Id],[name],[description],[event_id]) VALUES (
            2,'MULTI-OP','MULTI-OP',1);
            INSERT INTO [operators] ([Id],[name],[description],[event_id]) VALUES (
            3,'CHECKLOG','CHECKLOG',1);
            INSERT INTO [operators] ([Id],[name],[description],[event_id]) VALUES (
            4,'SWL','SWL',1);";

            string createTable_power = @"DROP TABLE IF EXISTS[power];
            CREATE TABLE [power] (
                [Id] INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL
            , [name] nvarchar(100) NOT NULL COLLATE NOCASE
            , [description] nvarchar(100) NOT NULL COLLATE NOCASE
            , [event_id] bigint NOT NULL
            );
            INSERT INTO [power] ([Id],[name],[description],[event_id]) VALUES (
            1,'HIGH','High (>100W)',1);
            INSERT INTO [power] ([Id],[name],[description],[event_id]) VALUES (
            2,'LOW','Low (<100W)',1);
            INSERT INTO [power] ([Id],[name],[description],[event_id]) VALUES (
            3,'QRP','QRP(<10W)',1);";

            string createTable_modes = @"DROP TABLE IF EXISTS[modes];
            CREATE TABLE [modes] (
                [Id] INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL
            , [name] nvarchar(100) NOT NULL COLLATE NOCASE
            , [description] nvarchar(100) NOT NULL COLLATE NOCASE
            , [event_id] bigint NOT NULL
            );
            INSERT INTO [modes] ([Id],[name],[description],[event_id]) VALUES (
            1,'MIX','MIX',1);
            INSERT INTO [modes] ([Id],[name],[description],[event_id]) VALUES (
            2,'SSB','SSB',1);
            INSERT INTO [modes] ([Id],[name],[description],[event_id]) VALUES (
            3,'CW','CW',1);
            INSERT INTO [modes] ([Id],[name],[description],[event_id]) VALUES (
            4,'VHF/UHF','VHF/UHF',2);
            INSERT INTO [modes] ([Id],[name],[description],[event_id]) VALUES (
            5,'VHF','VHF',2);
            INSERT INTO [modes] ([Id],[name],[description],[event_id]) VALUES (
            6,'UHF','UHF',2);
            INSERT INTO [modes] ([Id],[name],[description],[event_id]) VALUES (
            7,'MIX','MIX',3);";

            if (!TableExists("qso"))
            {
                using (var command = new SQLiteCommand(createTable_qso, con))
                {
                    command.ExecuteNonQuery();
                }
            }
            else
            {
                AddColToTable("qso", "my_callsign", "nvarchar(100) NOT NULL");
                AddColToTable("qso", "operator", "nvarchar(100) NULL");
                AddColToTable("qso", "my_square", "nvarchar(100) NULL");
                AddColToTable("qso", "my_locator", "nvarchar(100) NULL");
                AddColToTable("qso", "dx_locator", "nvarchar(100) NULL");
                AddColToTable("qso", "dx_callsign", "nvarchar(100) NOT NULL");
                AddColToTable("qso", "prop_mode", "nvarchar(100) NULL");
                AddColToTable("qso", "sat_name", "nvarchar(100) NULL");
                AddColToTable("qso", "soapbox", "nvarchar(100) NULL");
            }
            AddEqslStatusColumn();
            AddQrzColumns();
            AddLotwColumns();
            EnsureEqslAccountsTable();
            EnsureEqslIndexes();
            EnsureQrzIndexes();
            EnsureLotwIndex();
            using (var command = new SQLiteCommand(createTable_categories, con))
            {
                command.ExecuteNonQuery();
            }
            using (var command = new SQLiteCommand(createTable_radio_events, con))
            {
                command.ExecuteNonQuery();
            }
            using (var command = new SQLiteCommand(createTable_bands, con))
            {
                command.ExecuteNonQuery();
            }
            using (var command = new SQLiteCommand(createTable_operators, con))
            {
                command.ExecuteNonQuery();
            }
            using (var command = new SQLiteCommand(createTable_power, con))
            {
                command.ExecuteNonQuery();
            }
            using (var command = new SQLiteCommand(createTable_modes, con))
            {
                command.ExecuteNonQuery();
            }
        }
    }

    // One eQSL account, keyed by the station callsign it is used for. The username is the eQSL
    // login (normally the callsign itself, but kept separate so it can differ).
    public class EqslAccount
    {
        public int Id { get; set; }   // 0 = not yet saved (a new row)
        public string Callsign { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
    }
}
