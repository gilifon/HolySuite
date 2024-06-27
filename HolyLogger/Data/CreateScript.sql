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
, [event_id] bigint NOT NULL
);

DROP TABLE IF EXISTS[radio_events];
CREATE TABLE [radio_events] (
  [Id] INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL
, [name] nvarchar(100) NOT NULL COLLATE NOCASE
, [description] nvarchar(100) NOT NULL COLLATE NOCASE
, [is_categories] INTEGER NOT NULL COLLATE NOCASE
);

DROP TABLE [bands];
CREATE TABLE [bands] (
  [Id] INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL
, [name] nvarchar(100) NOT NULL COLLATE NOCASE
, [description] nvarchar(100) NOT NULL COLLATE NOCASE
, [event_id] bigint NOT NULL
);

DROP TABLE [operators];
CREATE TABLE [operators] (
  [Id] INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL
, [name] nvarchar(100) NOT NULL COLLATE NOCASE
, [description] nvarchar(100) NOT NULL COLLATE NOCASE
, [event_id] bigint NOT NULL
);

DROP TABLE [power];
CREATE TABLE [power] (
  [Id] INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL
, [name] nvarchar(100) NOT NULL COLLATE NOCASE
, [description] nvarchar(100) NOT NULL COLLATE NOCASE
, [event_id] bigint NOT NULL
);

DROP TABLE IF EXISTS [modes];
CREATE TABLE [modes] (
  [Id] INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL
, [name] nvarchar(100) NOT NULL COLLATE NOCASE
, [description] nvarchar(100) NOT NULL COLLATE NOCASE
, [event_id] INTEGER NOT NULL COLLATE NOCASE
);

-- Script Date: 12/1/2021 9:47 PM  - ErikEJ.SqlCeScripting version 3.5.2.90
INSERT INTO [radio_events] ([Id],[name],[description],[is_categories]) VALUES (
1,'holyland','Holyland Contest',1);
INSERT INTO [radio_events] ([Id],[name],[description],[is_categories]) VALUES (
2,'sukot','Sukot',1);
INSERT INTO [radio_events] ([Id],[name],[description],[is_categories]) VALUES (
3,'iarc','IARC Event',1);

-- Script Date: 12/1/2021 9:48 PM  - ErikEJ.SqlCeScripting version 3.5.2.90
INSERT INTO [categories] ([Id],[name],[description],[event_id]) VALUES (
1,'NONE','NONE',1);
INSERT INTO [categories] ([Id],[name],[description],[event_id]) VALUES (
2,'POR','Portable (1 Square)',1);
INSERT INTO [categories] ([Id],[name],[description],[event_id]) VALUES (
3,'M5','Mobile 5 (5 Squares)',1);
INSERT INTO [categories] ([Id],[name],[description],[event_id]) VALUES (
4,'M10','Mobile 10 (10 Squares)',1);
INSERT INTO [categories] ([Id],[name],[description],[event_id]) VALUES (
5,'YN','YN (Under 20 / License < 3 Yers)',1);

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
7,'MIX','MIX',3);

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
6,'80','80M',1);

INSERT INTO [operators] ([Id],[name],[description],[event_id]) VALUES (
1,'SINGLE-OP','SINGLE-OP',1);
INSERT INTO [operators] ([Id],[name],[description],[event_id]) VALUES (
2,'MULTI-OP','MULTI-OP',1);
INSERT INTO [operators] ([Id],[name],[description],[event_id]) VALUES (
3,'CHECKLOG','CHECKLOG',1);
INSERT INTO [operators] ([Id],[name],[description],[event_id]) VALUES (
4,'SWL','SWL',1);

INSERT INTO [power] ([Id],[name],[description],[event_id]) VALUES (
1,'HIGH','High (>100W)',1);
INSERT INTO [power] ([Id],[name],[description],[event_id]) VALUES (
2,'LOW','Low (<100W)',1);
INSERT INTO [power] ([Id],[name],[description],[event_id]) VALUES (
3,'QRP','QRP(<10W)',1);