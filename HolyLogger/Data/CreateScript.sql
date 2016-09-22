-- Script Date: 22/09/2016 14:05  - ErikEJ.SqlCeScripting version 3.5.2.58
CREATE TABLE [qso] (
  [Id] INTEGER NOT NULL
, [my_callsign] nvarchar(100) NOT NULL
, [my_square] nvarchar(100) NOT NULL
, [frequancy] nvarchar(100) NOT NULL
, [dx_callsign] nvarchar(100) NOT NULL
, [rst_rcvd] nvarchar(100) NOT NULL
, [rst_sent] nvarchar(100) NOT NULL
, [timestamp] nvarchar(100) NOT NULL
, [exchnge] nvarchar(100) NOT NULL
, [comment] nvarchar(500) NOT NULL
, CONSTRAINT [PK_qso] PRIMARY KEY ([Id])
);
