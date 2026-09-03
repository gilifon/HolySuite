; Inno Setup script for HolyLogger
; Builds a single setup.exe from the Release (x86) build output.
; Compile with: "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" HolyLogger.iss

#define MyAppName "HolyLogger"
#define MyAppVersion "8.9.6"
#define MyAppPublisher "HolyLogger"
#define MyAppExeName "HolyLogger.exe"
#define SrcDir "..\HolyLogger\bin\x86\Release"

[Setup]
; A unique AppId keeps upgrades/uninstall consistent across versions. Do not change.
AppId={{8B5C9E42-3F7A-4D21-9C6E-1A2B3C4D5E6F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
; NOT the program own crown. Pointing this at HolyLogger icon gave the SETUP file the same icon as
; the program itself, so on a desktop the installer and the program looked like one and the same
; thing. This is the classic setup icon, kept beside the script rather than read out of the Inno
; installation folder, so the installer builds the same on anybody machine.
SetupIconFile=setup.ico
; Relative to this script, so whoever builds the installer gets it next to the source instead of
; needing one particular user's folder to exist on their machine.
OutputDir=Output
OutputBaseFilename=HolyLogger-{#MyAppVersion}-setup
Compression=lzma2
; NOT solid. Solid compression packs every file into one stream that has to be decompressed in
; order, so the progress bar cannot move until a file finishes - and 11 of this app's 25 MB are a
; SINGLE file (Data\callsigns_merged_big.txt). The bar therefore sat still for most of the install
; and then jumped to full at the end, which looks like a hung installer.
; Non-solid costs a couple of MB of download and gives a bar that actually tracks the work.
SolidCompression=no
WizardStyle=modern
; x86 app -> install under Program Files (x86) and run the wizard in 32-bit mode.
PrivilegesRequired=admin

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; *.xml excluded as well as *.pdb: those are the libraries' IntelliSense documentation (LiveCharts,
; MoreLinq, Newtonsoft.Json, SQLite...), 3.1 MB of the 25 MB payload, and nothing reads them at
; runtime. Only .config files are needed beside the DLLs, and those are kept.
Source: "{#SrcDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion; Excludes: "*.pdb,*.xml"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
function IsDotNet48Installed(): Boolean;
var
  Release: Cardinal;
begin
  Result := False;
  // .NET Framework 4.8 = Release >= 528040 (528049 on Win10 2019-05+).
  if RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) then
    Result := (Release >= 528040);
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if not IsDotNet48Installed() then
  begin
    if MsgBox('HolyLogger requires the Microsoft .NET Framework 4.8, which does not appear to be installed.' + #13#10 + #13#10 +
              'You can install it from:' + #13#10 +
              'https://dotnet.microsoft.com/download/dotnet-framework/net48' + #13#10 + #13#10 +
              'Continue with the installation anyway?', mbConfirmation, MB_YESNO) = IDNO then
      Result := False;
  end;
end;
