﻿using HolyParser;
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

                con = new SQLiteConnection(@"DataSource = " + dbPath + @";Version=3");
                con.Open();

                UpdateSchema();
                
                con.Close();
                con.Dispose();
                
                con = new SQLiteConnection(@"DataSource = " + dbPath + @";Version=3");
                con.Open();

            }
            catch (Exception e)
            {
                throw new Exception("Failed to connect to DB: " + e.Message);
            }
            
        }

        // Public static method to get the single instance of the class.
        public static DataAccess GetInstance()
        {
            // If instance is null, create a new instance.
            if (instance == null)
            {
                instance = new DataAccess();
            }
            return instance;
        }

        public void Close()
        {
            con.Close();
            con.Dispose();
        }

        public QSO Insert(QSO qso)
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
        public bool Insert(IEnumerable<QSO> qsos)
        {
            if (con != null && con.State == System.Data.ConnectionState.Open)
            {
                SQLiteTransaction T = con.BeginTransaction();
                foreach (var qso in qsos)
                {
                    SQLiteCommand insertSQL = new SQLiteCommand("INSERT INTO qso (my_callsign,operator,my_square,my_locator,dx_locator,frequency,band,dx_callsign,rst_rcvd,rst_sent,date,time,mode,submode,exchange,comment,name,country,continent,prop_mode,sat_name,soapbox) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)", con);
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
        public void Update(QSO qso)
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
        public void DeleteAll()
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
        public ObservableCollection<QSO> GetAllQSOs()
        {
            CultureInfo enUS = new CultureInfo("en-US");
            ObservableCollection<QSO> qso_list = new ObservableCollection<QSO>();
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
                        q.StandartizeQSO();
                        qso_list.Add(q);
                    }
                }
            }
            return qso_list;
        }
        public ObservableCollection<QSO> GetTopQSOs(int i)
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
                        q.StandartizeQSO();
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
        public int GetGridCount()
        {
            string stm = "SELECT count(distinct exchange) FROM qso where dx_callsign like '4X%' or dx_callsign like '4Z%'";
            using (SQLiteCommand cmd = new SQLiteCommand(stm, con))
            {
                cmd.CommandType = CommandType.Text;
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }
        public int GetDXCCCount()
        {
            string stm = "SELECT count(distinct country) FROM qso";
            using (SQLiteCommand cmd = new SQLiteCommand(stm, con))
            {
                cmd.CommandType = CommandType.Text;
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        public ObservableCollection<RadioEvent> GetRadioEvents()
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

        public ObservableCollection<GenericItem> GetTableData(string tableName, int eventId=1)
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
}
