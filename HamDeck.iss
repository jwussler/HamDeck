; HamDeck v3.1 Inno Setup Script
; Build the app first with: dotnet publish -c Release -r win-x64 --self-contained true
; Then compile this script with Inno Setup

#define MyAppName "HamDeck"
#define MyAppVersion "3.3.0"
#define MyAppPublisher "WA0O"
#define MyAppURL "https://wa0o.com"
#define MyAppExeName "HamDeck.exe"

; Point this to your publish output folder
#define PublishDir "bin\Release\net8.0-windows\win-x64\publish"

[Setup]
AppId={{8F2B3A4E-5C6D-7E8F-9A0B-1C2D3E4F5A6B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputBaseFilename=HamDeck-v3.3-Setup
OutputDir=installer
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
PrivilegesRequired=admin
SetupIconFile=Resources\hamdeck.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "firewall"; Description: "Add Windows Firewall rules (required for remote access)"; GroupDescription: "Network Configuration:"
Name: "urlacl"; Description: "Register HTTP ports (required for web dashboard and audio stream)"; GroupDescription: "Network Configuration:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; URL ACL reservations so HamDeck can bind HTTP ports without running as admin
Filename: "netsh"; Parameters: "http add urlacl url=http://+:5001/ user=Everyone"; StatusMsg: "Registering API port..."; Flags: runhidden waituntilterminated; Tasks: urlacl
Filename: "netsh"; Parameters: "http add urlacl url=http://+:5002/ user=Everyone"; StatusMsg: "Registering dashboard port..."; Flags: runhidden waituntilterminated; Tasks: urlacl

; Windows Firewall rules for remote/LAN access
Filename: "netsh"; Parameters: "advfirewall firewall add rule name=""HamDeck API"" dir=in action=allow protocol=TCP localport=5001"; StatusMsg: "Adding firewall rule for API..."; Flags: runhidden waituntilterminated; Tasks: firewall
Filename: "netsh"; Parameters: "advfirewall firewall add rule name=""HamDeck Dashboard"" dir=in action=allow protocol=TCP localport=5002"; StatusMsg: "Adding firewall rule for dashboard..."; Flags: runhidden waituntilterminated; Tasks: firewall

; Launch after install
Filename: "{app}\{#MyAppExeName}"; Description: "Launch HamDeck"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Clean up URL ACL reservations
Filename: "netsh"; Parameters: "http delete urlacl url=http://+:5001/"; Flags: runhidden waituntilterminated
Filename: "netsh"; Parameters: "http delete urlacl url=http://+:5002/"; Flags: runhidden waituntilterminated

; Clean up firewall rules
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""HamDeck API"""; Flags: runhidden waituntilterminated
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""HamDeck Dashboard"""; Flags: runhidden waituntilterminated
