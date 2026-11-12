; KTC Monitoring Portal - Windows installer (NSIS 3, Unicode)
; Build: installer/build-installer.sh   (needs publish output in installer/publish)
Unicode true
!include "MUI2.nsh"
!include "nsDialogs.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!include "StrFunc.nsh"
${StrRep}
${UnStrRep}

!ifndef VERSION
  !define VERSION "1.0.0"
!endif
!define APP "KTC Monitoring Portal"
!define SVC "KtcPortal"
!define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\KtcPortal"

Name "${APP}"
OutFile "KtcMonitoringPortal-Setup-${VERSION}.exe"
InstallDir "$PROGRAMFILES64\KtcPortal"
InstallDirRegKey HKLM "Software\KtcPortal" "InstallDir"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
BrandingText "${APP} ${VERSION}"
VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "${APP}"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "FileDescription" "${APP} installer"

Var Dlg
Var hKtcUrl
Var hDb
Var hPort
Var hAdmin
Var hSvcUser
Var hSvcPass
Var hSmtp
Var hMock
Var KtcUrl
Var DbConn
Var Port
Var Admins
Var SvcUser
Var SvcPass
Var Smtp
Var Mock
Var CfgDir
Var Upgrading

!define MUI_ABORTWARNING
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
Page custom SettingsPage SettingsLeave
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "http://localhost:$Port"
!define MUI_FINISHPAGE_RUN_TEXT "Open the portal in the browser"
!define MUI_FINISHPAGE_RUN_FUNCTION OpenPortal
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Function OpenPortal
  ExecShell "open" "http://localhost:$Port"
FunctionEnd

Function .onInit
  SetRegView 64
  StrCpy $CfgDir "$COMMONFILES64\..\..\ProgramData\KtcPortal"
  ReadEnvStr $0 "ProgramData"
  ${If} $0 == ""
    StrCpy $0 "C:\ProgramData"
  ${EndIf}
  StrCpy $CfgDir "$0\KtcPortal"
  StrCpy $KtcUrl "https://ktc-server.example.local/"
  StrCpy $DbConn "Server=.;Database=KtcMonitoringPortal;Trusted_Connection=True;TrustServerCertificate=True"
  StrCpy $Port "5080"
  StrCpy $Admins ""
  StrCpy $SvcUser ""
  StrCpy $SvcPass ""
  StrCpy $Smtp ""
  StrCpy $Mock "0"
  StrCpy $Upgrading "0"
  IfFileExists "$CfgDir\appsettings.Production.json" 0 +2
    StrCpy $Upgrading "1"
  ; silent installs: Setup.exe /S /KTCURL=... /DBCONN=... /PORT=8080 /ADMIN=user1;user2 /SVCUSER=... /SVCPASS=... /SMTP=host /MOCK=1 /D=C:\path
  ${GetParameters} $R0
  ${GetOptions} $R0 "/KTCURL=" $R1
  ${If} $R1 != ""
    StrCpy $KtcUrl $R1
  ${EndIf}
  ${GetOptions} $R0 "/DBCONN=" $R1
  ${If} $R1 != ""
    StrCpy $DbConn $R1
  ${EndIf}
  ${GetOptions} $R0 "/PORT=" $R1
  ${If} $R1 != ""
    StrCpy $Port $R1
  ${EndIf}
  ${GetOptions} $R0 "/ADMIN=" $R1
  ${If} $R1 != ""
    StrCpy $Admins $R1
  ${EndIf}
  ${GetOptions} $R0 "/SVCUSER=" $R1
  ${If} $R1 != ""
    StrCpy $SvcUser $R1
  ${EndIf}
  ${GetOptions} $R0 "/SVCPASS=" $R1
  ${If} $R1 != ""
    StrCpy $SvcPass $R1
  ${EndIf}
  ${GetOptions} $R0 "/SMTP=" $R1
  ${If} $R1 != ""
    StrCpy $Smtp $R1
  ${EndIf}
  ${GetOptions} $R0 "/MOCK=" $R1
  ${If} $R1 != ""
    StrCpy $Mock $R1
  ${EndIf}
FunctionEnd

Function SettingsPage
  ${If} $Upgrading == "1"
    Abort   ; upgrade: keep the existing configuration untouched
  ${EndIf}
  !insertmacro MUI_HEADER_TEXT "Portal settings" "These are saved to $CfgDir\appsettings.Production.json and can be edited later."
  nsDialogs::Create 1018
  Pop $Dlg
  ${NSD_CreateLabel} 0 0 100% 10u "KTC REST API base URL (e.g. https://ktc-restapi.corp.local/)"
  ${NSD_CreateText} 0 11u 100% 12u $KtcUrl
  Pop $hKtcUrl
  ${NSD_CreateLabel} 0 27u 100% 10u "SQL Server connection string (database is created/migrated automatically)"
  ${NSD_CreateText} 0 38u 100% 12u $DbConn
  Pop $hDb
  ${NSD_CreateLabel} 0 54u 30% 10u "HTTP port"
  ${NSD_CreateText} 0 65u 30% 12u $Port
  Pop $hPort
  ${NSD_CreateLabel} 34% 54u 66% 10u "Portal administrators (usernames, separated by ;)"
  ${NSD_CreateText} 34% 65u 66% 12u $Admins
  Pop $hAdmin
  ${NSD_CreateLabel} 0 81u 50% 10u "KTC service account - username (alerts / background)"
  ${NSD_CreateText} 0 92u 48% 12u $SvcUser
  Pop $hSvcUser
  ${NSD_CreateLabel} 52% 81u 48% 10u "KTC service account - password"
  ${NSD_CreatePassword} 52% 92u 48% 12u $SvcPass
  Pop $hSvcPass
  ${NSD_CreateLabel} 0 108u 100% 10u "SMTP host for alert/report e-mail (optional)"
  ${NSD_CreateText} 0 119u 60% 12u $Smtp
  Pop $hSmtp
  ${NSD_CreateCheckbox} 0 138u 100% 12u "Demo mode (built-in mock KTC + in-memory DB; NOT for production)"
  Pop $hMock
  ${If} $Mock == "1"
    ${NSD_Check} $hMock
  ${EndIf}
  nsDialogs::Show
FunctionEnd

Function SettingsLeave
  ${NSD_GetText} $hKtcUrl $KtcUrl
  ${NSD_GetText} $hDb $DbConn
  ${NSD_GetText} $hPort $Port
  ${NSD_GetText} $hAdmin $Admins
  ${NSD_GetText} $hSvcUser $SvcUser
  ${NSD_GetText} $hSvcPass $SvcPass
  ${NSD_GetText} $hSmtp $Smtp
  ${NSD_GetState} $hMock $0
  ${If} $0 == ${BST_CHECKED}
    StrCpy $Mock "1"
  ${Else}
    StrCpy $Mock "0"
  ${EndIf}
  ${If} $Mock != "1"
  ${AndIf} $KtcUrl == ""
    MessageBox MB_ICONEXCLAMATION "The KTC base URL is required."
    Abort
  ${EndIf}
  IntOp $0 $Port + 0
  ${If} $0 < 1
  ${OrIf} $0 > 65535
    MessageBox MB_ICONEXCLAMATION "Enter a valid port (1-65535)."
    Abort
  ${EndIf}
FunctionEnd

; JSON-escape $R9 in place
!macro JsonEsc
  ${StrRep} $R9 $R9 "\" "\\"
  ${StrRep} $R9 $R9 '"' '\"'
!macroend

Function WriteConfig
  CreateDirectory "$CfgDir"
  CreateDirectory "$CfgDir\logs"
  CreateDirectory "$CfgDir\keys"
  FileOpen $0 "$CfgDir\appsettings.Production.json" w
  FileWrite $0 '{$\r$\n'
  FileWrite $0 '  "Urls": "http://*:$Port",$\r$\n'
  StrCpy $R9 "$CfgDir\logs\ktc-portal-.log"
  !insertmacro JsonEsc
  FileWrite $0 '  "Serilog": { "WriteTo": [ { "Name": "Console" }, { "Name": "File", "Args": { "path": "$R9", "rollingInterval": "Day", "retainedFileCountLimit": 30 } } ] },$\r$\n'
  StrCpy $R9 "$CfgDir\keys"
  !insertmacro JsonEsc
  FileWrite $0 '  "DataProtection": { "KeysPath": "$R9" },$\r$\n'
  StrCpy $R9 $DbConn
  !insertmacro JsonEsc
  FileWrite $0 '  "ConnectionStrings": { "KtcPortal": "$R9" },$\r$\n'
  ${If} $Mock == "1"
    FileWrite $0 '  "Database": { "Provider": "InMemory" },$\r$\n'
  ${Else}
    FileWrite $0 '  "Database": { "Provider": "SqlServer" },$\r$\n'
  ${EndIf}
  StrCpy $R9 $KtcUrl
  !insertmacro JsonEsc
  FileWrite $0 '  "Ktc": { "BaseUrl": "$R9", "UseMock": '
  ${If} $Mock == "1"
    FileWrite $0 'true'
  ${Else}
    FileWrite $0 'false'
  ${EndIf}
  ${If} $SvcUser != ""
    StrCpy $R9 $SvcUser
    !insertmacro JsonEsc
    FileWrite $0 ', "ServiceAccount": { "Username": "$R9", '
    StrCpy $R9 $SvcPass
    !insertmacro JsonEsc
    FileWrite $0 '"Password": "$R9", "AuthMode": "DOMAIN" }'
  ${EndIf}
  FileWrite $0 ' },$\r$\n'
  ; admins: "a;b" -> ["a","b"]
  StrCpy $R9 $Admins
  !insertmacro JsonEsc
  ${StrRep} $R9 $R9 ";" '","'
  ${If} $Admins != ""
    FileWrite $0 '  "Portal": { "AdminUsers": ["$R9"], "RequireHttps": false },$\r$\n'
  ${Else}
    FileWrite $0 '  "Portal": { "RequireHttps": false },$\r$\n'
  ${EndIf}
  StrCpy $R9 $Smtp
  !insertmacro JsonEsc
  FileWrite $0 '  "Notifications": { "Smtp": { "Host": "$R9" } }$\r$\n'
  FileWrite $0 '}$\r$\n'
  FileClose $0
  ; secrets live in this file: restrict to SYSTEM + Administrators
  nsExec::Exec 'icacls "$CfgDir" /inheritance:r /grant:r "SYSTEM:(OI)(CI)F" "Administrators:(OI)(CI)F"'
  Pop $0
FunctionEnd

Section "Install"
  SetRegView 64
  SetOutPath "$INSTDIR"
  ; stop an existing service before replacing files
  nsExec::Exec 'sc stop ${SVC}'
  Pop $0
  Sleep 2500
  File /r "publish\*.*"

  ${If} $Upgrading == "0"
    Call WriteConfig
  ${Else}
    DetailPrint "Existing configuration kept: $CfgDir\appsettings.Production.json"
  ${EndIf}

  ; service
  nsExec::Exec 'sc query ${SVC}'
  Pop $0
  ${If} $0 != 0
    nsExec::ExecToLog 'sc create ${SVC} binPath= "\"$INSTDIR\Ktc.Web.exe\"" start= delayed-auto DisplayName= "${APP}"'
    Pop $0
  ${Else}
    nsExec::ExecToLog 'sc config ${SVC} binPath= "\"$INSTDIR\Ktc.Web.exe\"" start= delayed-auto'
    Pop $0
  ${EndIf}
  nsExec::Exec 'sc description ${SVC} "KTC ATM/ticket/incident/transaction monitoring portal"'
  Pop $0
  nsExec::Exec 'sc failure ${SVC} reset= 86400 actions= restart/5000/restart/15000/restart/60000'
  Pop $0

  ; firewall (inbound TCP port, only added on fresh install)
  ${If} $Upgrading == "0"
    nsExec::Exec 'netsh advfirewall firewall delete rule name="${APP}"'
    Pop $0
    nsExec::Exec 'netsh advfirewall firewall add rule name="${APP}" dir=in action=allow protocol=TCP localport=$Port'
    Pop $0
  ${EndIf}

  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKLM "Software\KtcPortal" "InstallDir" "$INSTDIR"
  WriteRegStr HKLM "${UNINST_KEY}" "DisplayName" "${APP}"
  WriteRegStr HKLM "${UNINST_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKLM "${UNINST_KEY}" "Publisher" "KTC Monitoring"
  WriteRegStr HKLM "${UNINST_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKLM "${UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKLM "${UNINST_KEY}" "NoModify" 1
  CreateDirectory "$SMPROGRAMS\${APP}"
  CreateShortcut "$SMPROGRAMS\${APP}\Open Portal.lnk" "http://localhost:$Port"
  CreateShortcut "$SMPROGRAMS\${APP}\Configuration folder.lnk" "$CfgDir"
  CreateShortcut "$SMPROGRAMS\${APP}\Uninstall.lnk" "$INSTDIR\Uninstall.exe"

  nsExec::ExecToLog 'sc start ${SVC}'
  Pop $0
SectionEnd

Section "Uninstall"
  SetRegView 64
  nsExec::Exec 'sc stop ${SVC}'
  Pop $0
  Sleep 3000
  nsExec::Exec 'sc delete ${SVC}'
  Pop $0
  nsExec::Exec 'netsh advfirewall firewall delete rule name="${APP}"'
  Pop $0
  RMDir /r "$SMPROGRAMS\${APP}"
  RMDir /r "$INSTDIR"
  DeleteRegKey HKLM "${UNINST_KEY}"
  DeleteRegKey HKLM "Software\KtcPortal"
  ; configuration, logs and Data Protection keys in %ProgramData%\KtcPortal are deliberately kept
SectionEnd
