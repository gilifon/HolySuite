-- Script Date: 08/12/2020 22:03  - ErikEJ.SqlCeScripting version 3.5.2.86
DROP TABLE [qso];
CREATE TABLE [qso] (
  [Id] INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL
, [my_callsign] nvarchar(100) NOT NULL COLLATE NOCASE
, [operator] nvarchar(100) NULL COLLATE NOCASE
, [my_square] nvarchar(100) NULL COLLATE NOCASE
, [my_locator] nvarchar(100) NULL COLLATE NOCASE
, [dx_locator] nvarchar(100) NULL COLLATE NOCASE
, [frequency] nvarchar(100) NOT NULL COLLATE NOCASE
, [band] nvarchar(100) NOT NULL COLLATE NOCASE
, [dx_callsign] nvarchar(100) NOT NULL COLLATE NOCASE
, [rst_rcvd] nvarchar(100) NOT NULL COLLATE NOCASE
, [rst_sent] nvarchar(100) NOT NULL COLLATE NOCASE
, [date] nvarchar(100) NOT NULL COLLATE NOCASE
, [time] nvarchar(100) NOT NULL COLLATE NOCASE
, [mode] nvarchar(100) NOT NULL COLLATE NOCASE
, [exchange] nvarchar(100) NULL COLLATE NOCASE
, [comment] nvarchar(500) NULL COLLATE NOCASE
, [name] nvarchar(500) NULL COLLATE NOCASE
, [country] nvarchar(500) NULL COLLATE NOCASE
, [continent] nvarchar(500) NULL COLLATE NOCASE
, [prop_mode] nvarchar(100) NULL COLLATE NOCASE
, [sat_name] nvarchar(100) NULL COLLATE NOCASE
);

DROP TABLE [categories];
CREATE TABLE [categories] (
  [Id] INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL
, [name] nvarchar(100) NOT NULL COLLATE NOCASE
, [description] nvarchar(100) NOT NULL COLLATE NOCASE
, [event_id] INTEGER NOT NULL COLLATE NOCASE
);


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
2,'lighthouse','Lighthouse',0);
INSERT INTO [radio_events] ([Id],[name],[description],[is_categories]) VALUES (
3,'eurovision','Eurovision',0);
INSERT INTO [radio_events] ([Id],[name],[description],[is_categories]) VALUES (
4,'craters','Craters',0);
INSERT INTO [radio_events] ([Id],[name],[description],[is_categories]) VALUES (
5,'chanukah','Chanukah',0);
INSERT INTO [radio_events] ([Id],[name],[description],[is_categories]) VALUES (
6,'sukot','Sukot',1);

-- Script Date: 12/1/2021 9:48 PM  - ErikEJ.SqlCeScripting version 3.5.2.90
INSERT INTO [categories] ([Id],[name],[description],[event_id]) VALUES (
1,'CW','CW (Single OP, CW Only)',1);
INSERT INTO [categories] ([Id],[name],[description],[event_id]) VALUES (
2,'SSB','SSB (Single OP, SSB Only)',1);
INSERT INTO [categories] ([Id],[name],[description],[event_id]) VALUES (
3,'FT8','FT8 (Single OP, FT8 Only)',1);
INSERT INTO [categories] ([Id],[name],[description],[event_id]) VALUES (
4,'DIGI','DIGI (Single OP, RTTY/PSK Only)',1);
INSERT INTO [categories] ([Id],[name],[description],[event_id]) VALUES (
5,'QRP','QRP (Single OP, 10W Max)',1);
INSERT INTO [categories] ([Id],[name],[description],[event_id]) VALUES (
6,'SOB','SOB (Single OP, Single Band)',1);
INSERT INTO [categories] ([Id],[name],[description],[event_id]) VALUES (
7,'POR','POR (Single OP, Portable 1 Square)',1);
INSERT INTO [categories] ([Id],[name],[description],[event_id]) VALUES (
8,'M5','M5 (Single OP, Portable 5 Squares)',1);
INSERT INTO [categories] ([Id],[name],[description],[event_id]) VALUES (
9,'M10','M10 (Single OP, Portable 10 Squares)',1);
INSERT INTO [categories] ([Id],[name],[description],[event_id]) VALUES (
10,'MOP','MOP (Multi OP, Single TX)',1);
INSERT INTO [categories] ([Id],[name],[description],[event_id]) VALUES (
11,'MM','MM (Multi OP, Multi TX)',1);
INSERT INTO [categories] ([Id],[name],[description],[event_id]) VALUES (
12,'MMP','MMP (Multi OP, Single TX, Portable)',1);
INSERT INTO [categories] ([Id],[name],[description],[event_id]) VALUES (
13,'4Z9','4Z9 (4Z9 callsign)',1);
INSERT INTO [categories] ([Id],[name],[description],[event_id]) VALUES (
14,'SHA','SHA (Saturday Night)',1);
INSERT INTO [categories] ([Id],[name],[description],[event_id]) VALUES (
15,'SWL','SWL (Short Wave Listener)',1);
INSERT INTO [categories] ([Id],[name],[description],[event_id]) VALUES (
16,'NEW','NEW (License less than 2 years)',1);

