-- Script Date: 08/12/2020 22:03  - ErikEJ.SqlCeScripting version 3.5.2.86
DROP TABLE IF EXISTS[qso];
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
);

DROP TABLE IF EXISTS[categories];
CREATE TABLE [categories] (
  [Id] INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL
, [name] nvarchar(100) NOT NULL COLLATE NOCASE
, [description] nvarchar(100) NOT NULL COLLATE NOCASE
, [mode] nvarchar(100) NOT NULL COLLATE NOCASE
, [operator] nvarchar(100) NOT NULL COLLATE NOCASE
, [power] nvarchar(100) NOT NULL COLLATE NOCASE
, [event_id] INTEGER NOT NULL COLLATE NOCASE
);

DROP TABLE IF EXISTS[radio_events];
CREATE TABLE [radio_events] (
  [Id] INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL
, [name] nvarchar(100) NOT NULL COLLATE NOCASE
, [description] nvarchar(100) NOT NULL COLLATE NOCASE
, [is_categories] INTEGER NOT NULL COLLATE NOCASE
);

-- Script Date: 12/1/2021 9:47 PM  - ErikEJ.SqlCeScripting version 3.5.2.90
INSERT INTO [radio_events] ([Id],[name],[description],[is_categories]) VALUES (
1,'holyland','Holyland Contest',1);
INSERT INTO [radio_events] ([Id],[name],[description],[is_categories]) VALUES (
2,'sukot','Sukot',1);
INSERT INTO [radio_events] ([Id],[name],[description],[is_categories]) VALUES (
3,'iarc','IARC Event',1);

-- Script Date: 12/1/2021 9:48 PM  - ErikEJ.SqlCeScripting version 3.5.2.90
INSERT INTO [categories] ([Id],[name],[description],[mode],[operator],[power],[event_id]) VALUES (
1,'CW','CW (Single OP, CW Only)','CW','SINGLE-OP','LOW',1);
INSERT INTO [categories] ([Id],[name],[description],[mode],[operator],[power],[event_id]) VALUES (
2,'SSB','SSB (Single OP, SSB Only)','SSB','SINGLE-OP','LOW',1);
INSERT INTO [categories] ([Id],[name],[description],[mode],[operator],[power],[event_id]) VALUES (
3,'MIX','MIX(Single OP, Mix Modes)','MIX','SINGLE-OP','LOW',1);
INSERT INTO [categories] ([Id],[name],[description],[mode],[operator],[power],[event_id]) VALUES (
4,'FT8','FT8 (Single OP, FT8 Only)','FT8','SINGLE-OP','LOW',1);
INSERT INTO [categories] ([Id],[name],[description],[mode],[operator],[power],[event_id]) VALUES (
5,'DIGI','DIGI (Single OP, RTTY/PSK Only)','DIGI','SINGLE-OP','LOW',1);
INSERT INTO [categories] ([Id],[name],[description],[mode],[operator],[power],[event_id]) VALUES (
6,'QRP','QRP (Single OP, 10W Max)','QRP','SINGLE-OP','QRP',1);
INSERT INTO [categories] ([Id],[name],[description],[mode],[operator],[power],[event_id]) VALUES (
7,'SOB','SOB (Single OP, Single Band)','SOB','SINGLE-OP','LOW',1);
INSERT INTO [categories] ([Id],[name],[description],[mode],[operator],[power],[event_id]) VALUES (
8,'POR','POR (Single OP, Portable 1 Square)','POR','SINGLE-OP','LOW',1);
INSERT INTO [categories] ([Id],[name],[description],[mode],[operator],[power],[event_id]) VALUES (
9,'M5','M5 (Single OP, Portable 5 Squares)','M5','SINGLE-OP','LOW',1);
INSERT INTO [categories] ([Id],[name],[description],[mode],[operator],[power],[event_id]) VALUES (
10,'M10','M10 (Single OP, Portable 10 Squares)','M10','SINGLE-OP','LOW',1);
INSERT INTO [categories] ([Id],[name],[description],[mode],[operator],[power],[event_id]) VALUES (
11,'MOP','MOP (Multi OP, Single TX)','MOP','MULTI-OP','LOW',1);
INSERT INTO [categories] ([Id],[name],[description],[mode],[operator],[power],[event_id]) VALUES (
12,'MM','MM (Multi OP, Multi TX)','MM','MULTI-OP','LOW',1);
INSERT INTO [categories] ([Id],[name],[description],[mode],[operator],[power],[event_id]) VALUES (
13,'MMP','MMP (Multi OP, Single TX, Portable)','MMP','MULTI-OP','LOW',1);
INSERT INTO [categories] ([Id],[name],[description],[mode],[operator],[power],[event_id]) VALUES (
14,'4Z9','4Z9 (4Z9 callsign)','4Z9','SINGLE-OP','LOW',1);
INSERT INTO [categories] ([Id],[name],[description],[mode],[operator],[power],[event_id]) VALUES (
15,'SHA','SHA (Saturday Night)','SHA','SINGLE-OP','LOW',1);
INSERT INTO [categories] ([Id],[name],[description],[mode],[operator],[power],[event_id]) VALUES (
16,'SWL','SWL (Short Wave Listener)','SWL','SINGLE-OP','LOW',1);
INSERT INTO [categories] ([Id],[name],[description],[mode],[operator],[power],[event_id]) VALUES (
17,'NEW','NEW (License less than 2 years)','NEW','SINGLE-OP','LOW',1);
INSERT INTO [categories] ([Id],[name],[description],[mode],[operator],[power],[event_id]) VALUES (
18,'VHF/UHF','VHF/UHF','VHF/UHF','SINGLE-OP','LOW',2);
INSERT INTO [categories] ([Id],[name],[description],[mode],[operator],[power],[event_id]) VALUES (
19,'VHF','VHF','VHF','SINGLE-OP','LOW',2);
INSERT INTO [categories] ([Id],[name],[description],[mode],[operator],[power],[event_id]) VALUES (
20,'UHF','UHF','UHF','SINGLE-OP','LOW',2);