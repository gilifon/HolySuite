CREATE TABLE [qso] (
  [Id] INTEGER NOT NULL
, [my_callsign] nvarchar(100) NOT NULL
, [my_square] nvarchar(100) NOT NULL
, [frequency] nvarchar(100) NOT NULL
, [band] nvarchar(100) NOT NULL
, [dx_callsign] nvarchar(100) NOT NULL
, [rst_rcvd] nvarchar(100) NOT NULL
, [rst_sent] nvarchar(100) NOT NULL
, [date] nvarchar(100) NOT NULL
, [time] nvarchar(100) NOT NULL
, [mode] nvarchar(100) NOT NULL
, [exchange] nvarchar(100) NULL
, [comment] nvarchar(500) NULL
, [name] nvarchar(500) NULL
, [country] nvarchar(500) NULL
, [prop_mode] nvarchar(100) NULL
, [sat_name] nvarchar(100) NULL
, CONSTRAINT [PK_qso] PRIMARY KEY ([Id])
);