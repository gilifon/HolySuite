-- Script Date: 08/12/2020 22:03  - ErikEJ.SqlCeScripting version 3.5.2.86
DROP TABLE [qso];
CREATE TABLE [qso] (
  [Id] INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL
, [my_callsign] nvarchar(100) NOT NULL COLLATE NOCASE
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
, [submode] nvarchar(100) NULL COLLATE NOCASE
, [exchange] nvarchar(100) NULL COLLATE NOCASE
, [comment] nvarchar(500) NULL COLLATE NOCASE
, [name] nvarchar(500) NULL COLLATE NOCASE
, [country] nvarchar(500) NULL COLLATE NOCASE
, [prop_mode] nvarchar(100) NULL COLLATE NOCASE
, [sat_name] nvarchar(100) NULL COLLATE NOCASE
);
