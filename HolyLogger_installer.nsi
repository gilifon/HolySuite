; HolyLogger Installer - NSIS Script
; Installs HolyLogger to Program Files and creates Start Menu + Desktop shortcuts

!define APPNAME "HolyLogger"
!define APPVERSION "8.1.2"
!define MANUFACTURER "4Z1KD"
!define INSTALL_DIR "$PROGRAMFILES\${MANUFACTURER}\${APPNAME}"
!define SOURCE_DIR "HolyLogger\bin\x86\Release"

Name "${APPNAME} ${APPVERSION}"
OutFile "HolyLogger_${APPVERSION}_Setup.exe"
InstallDir "${INSTALL_DIR}"
InstallDirRegKey HKLM "Software\${MANUFACTURER}\${APPNAME}" "InstallDir"
RequestExecutionLevel admin
SetCompressor /SOLID lzma

;--------------------------------
; Pages
Page directory
Page instfiles
UninstPage uninstConfirm
UninstPage instfiles

;--------------------------------
; Install
Section "Install"
  SetOutPath "$INSTDIR"

  ; Main files
  File "${SOURCE_DIR}\HolyLogger.exe"
  File "${SOURCE_DIR}\HolyLogger.exe.config"
  File "${SOURCE_DIR}\callsigns_merged_big.txt"
  File "${SOURCE_DIR}\DXCCManager.dll"
  File "${SOURCE_DIR}\HolyParser.dll"
  File "${SOURCE_DIR}\HolyParser.dll.config"
  File "${SOURCE_DIR}\ModeManager.dll"
  File "${SOURCE_DIR}\LiveCharts.dll"
  File "${SOURCE_DIR}\LiveCharts.Wpf.dll"
  File "${SOURCE_DIR}\MoreLinq.dll"
  File "${SOURCE_DIR}\Newtonsoft.Json.dll"
  File "${SOURCE_DIR}\StickyWindow.dll"
  File "${SOURCE_DIR}\System.Data.SQLite.dll"
  File "${SOURCE_DIR}\System.Net.Http.Formatting.dll"
  File "${SOURCE_DIR}\System.Runtime.InteropServices.RuntimeInformation.dll"
  File "${SOURCE_DIR}\System.ValueTuple.dll"
  File "${SOURCE_DIR}\WpfAnimatedGif.dll"
  File "${SOURCE_DIR}\WPFTextBoxAutoComplete.dll"
  File "${SOURCE_DIR}\Xceed.Wpf.AvalonDock.dll"
  File "${SOURCE_DIR}\Xceed.Wpf.AvalonDock.Themes.Aero.dll"
  File "${SOURCE_DIR}\Xceed.Wpf.AvalonDock.Themes.Metro.dll"
  File "${SOURCE_DIR}\Xceed.Wpf.AvalonDock.Themes.VS2010.dll"
  File "${SOURCE_DIR}\Xceed.Wpf.Toolkit.dll"

  ; SQLite native DLLs (x86 and x64 subfolders)
  SetOutPath "$INSTDIR\x86"
  File "${SOURCE_DIR}\x86\SQLite.Interop.dll"
  SetOutPath "$INSTDIR\x64"
  File "${SOURCE_DIR}\x64\SQLite.Interop.dll"

  ; Locale folders
  SetOutPath "$INSTDIR\cs-CZ"
  File /r "${SOURCE_DIR}\cs-CZ\*.*"
  SetOutPath "$INSTDIR\de"
  File /r "${SOURCE_DIR}\de\*.*"
  SetOutPath "$INSTDIR\es"
  File /r "${SOURCE_DIR}\es\*.*"
  SetOutPath "$INSTDIR\fr"
  File /r "${SOURCE_DIR}\fr\*.*"
  SetOutPath "$INSTDIR\hu"
  File /r "${SOURCE_DIR}\hu\*.*"
  SetOutPath "$INSTDIR\it"
  File /r "${SOURCE_DIR}\it\*.*"
  SetOutPath "$INSTDIR\ja-JP"
  File /r "${SOURCE_DIR}\ja-JP\*.*"
  SetOutPath "$INSTDIR\pt-BR"
  File /r "${SOURCE_DIR}\pt-BR\*.*"
  SetOutPath "$INSTDIR\ro"
  File /r "${SOURCE_DIR}\ro\*.*"
  SetOutPath "$INSTDIR\ru"
  File /r "${SOURCE_DIR}\ru\*.*"
  SetOutPath "$INSTDIR\sv"
  File /r "${SOURCE_DIR}\sv\*.*"
  SetOutPath "$INSTDIR\zh-Hans"
  File /r "${SOURCE_DIR}\zh-Hans\*.*"

  SetOutPath "$INSTDIR"

  ; Registry: install location + uninstall entry
  WriteRegStr HKLM "Software\${MANUFACTURER}\${APPNAME}" "InstallDir" "$INSTDIR"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "DisplayName" "${APPNAME} ${APPVERSION}"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "DisplayVersion" "${APPVERSION}"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "Publisher" "${MANUFACTURER}"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "InstallLocation" "$INSTDIR"
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "NoModify" 1
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "NoRepair" 1

  ; Shortcuts
  CreateDirectory "$SMPROGRAMS\${MANUFACTURER}"
  CreateShortcut "$SMPROGRAMS\${MANUFACTURER}\${APPNAME}.lnk" "$INSTDIR\HolyLogger.exe"
  CreateShortcut "$DESKTOP\${APPNAME}.lnk" "$INSTDIR\HolyLogger.exe"

  ; Uninstaller
  WriteUninstaller "$INSTDIR\Uninstall.exe"
SectionEnd

;--------------------------------
; Uninstall
Section "Uninstall"
  Delete "$INSTDIR\*.*"
  RMDir /r "$INSTDIR\x86"
  RMDir /r "$INSTDIR\x64"
  RMDir /r "$INSTDIR\cs-CZ"
  RMDir /r "$INSTDIR\de"
  RMDir /r "$INSTDIR\es"
  RMDir /r "$INSTDIR\fr"
  RMDir /r "$INSTDIR\hu"
  RMDir /r "$INSTDIR\it"
  RMDir /r "$INSTDIR\ja-JP"
  RMDir /r "$INSTDIR\pt-BR"
  RMDir /r "$INSTDIR\ro"
  RMDir /r "$INSTDIR\ru"
  RMDir /r "$INSTDIR\sv"
  RMDir /r "$INSTDIR\zh-Hans"
  RMDir "$INSTDIR"
  Delete "$SMPROGRAMS\${MANUFACTURER}\${APPNAME}.lnk"
  RMDir "$SMPROGRAMS\${MANUFACTURER}"
  Delete "$DESKTOP\${APPNAME}.lnk"
  DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}"
  DeleteRegKey HKLM "Software\${MANUFACTURER}\${APPNAME}"
SectionEnd
