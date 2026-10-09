; Beam installer (Inno Setup 6). Build with build.ps1 -Installer, or:
;   iscc /DArch=x64 installer\Beam.iss
; Installs per user by default (no administrator rights). Choosing "Install for all users"
; elevates and additionally adds a Windows Firewall rule so Beam is reachable on private networks
; without the first-run firewall prompt.

#ifndef Arch
  #define Arch "x64"
#endif
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#define AppName "Beam"
#define AppExe "Beam.exe"
#define AppId "Beam.Desktop"

[Setup]
AppId={{6C1E4F0B-8B57-4C43-9E0B-3E7A1B9D4F21}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Beam
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\artifacts\installer
OutputBaseFilename=BeamSetup-{#AppVersion}-{#Arch}
SetupIconFile=..\src\Beam.App\Assets\beam.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
MinVersion=10.0.17763
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startup"; Description: "Start Beam when I sign in, so other computers can send me files"; GroupDescription: "Other:"
Name: "contextmenu"; Description: "Add ""Send with Beam"" to the right-click menu of files and folders"; GroupDescription: "Other:"

[Files]
Source: "..\artifacts\publish\win-{#Arch}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; The AppUserModelID must match WindowsPlatformServices.AppUserModelId so toasts show Beam's name and icon.
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; AppUserModelID: "{#AppId}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; AppUserModelID: "{#AppId}"; Tasks: desktopicon

[Registry]
; Same value Beam writes itself when "Start Beam when I sign in" is switched on in Settings.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExe}"" --minimized"; Tasks: startup; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\AppUserModelId\{#AppId}"; Flags: uninsdeletekey dontcreatekey
; "Send with Beam" on files and folders (Windows 11: under "Show more options"). Each selected item runs
; Beam.exe --send <item>; the running Beam collects them into one send list (single-instance pipe).
; HKA = this user, or all users when installed for everyone.
Root: HKA; Subkey: "Software\Classes\*\shell\BeamSend"; ValueType: string; ValueName: ""; ValueData: "Send with Beam"; Tasks: contextmenu; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\*\shell\BeamSend"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#AppExe}"",0"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\*\shell\BeamSend"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Player"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\*\shell\BeamSend\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" --send ""%1"""; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\Directory\shell\BeamSend"; ValueType: string; ValueName: ""; ValueData: "Send with Beam"; Tasks: contextmenu; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Directory\shell\BeamSend"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#AppExe}"",0"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\Directory\shell\BeamSend"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Player"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\Directory\shell\BeamSend\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" --send ""%1"""; Tasks: contextmenu

[Run]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#AppName}"" dir=in action=allow program=""{app}\{#AppExe}"" enable=yes profile=private,domain"; Flags: runhidden; Check: IsAdminInstallMode; StatusMsg: "Allowing Beam through Windows Firewall..."
; Direct connections (Wi-Fi Direct): Windows treats that network as public, so allow Beam there too, but only
; from the addresses Windows hands out on it (192.168.137.x).
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#AppName} (direct connection)"" dir=in action=allow program=""{app}\{#AppExe}"" enable=yes profile=public remoteip=192.168.137.0/24"; Flags: runhidden; Check: IsAdminInstallMode; StatusMsg: "Allowing Beam through Windows Firewall..."
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#AppName}"" program=""{app}\{#AppExe}"""; Flags: runhidden; Check: IsAdminInstallMode; RunOnceId: "RemoveFirewallRule"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#AppName} (direct connection)"" program=""{app}\{#AppExe}"""; Flags: runhidden; Check: IsAdminInstallMode; RunOnceId: "RemoveDirectFirewallRule"

; Settings, history and the device identity in %LOCALAPPDATA%\Beam are kept on uninstall so a
; reinstall keeps the same name and trusted devices. Received files are never touched.
