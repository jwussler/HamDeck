; HamDeck v2.0 Inno Setup Script
; Build the app first with: dotnet publish -c Release -r win-x64 --self-contained true
; Then compile this script with Inno Setup

#define MyAppName "HamDeck"
#define MyAppVersion "2.0.0"
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
OutputBaseFilename=HamDeck-v2.0-Setup
OutputDir=installer
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
PrivilegesRequired=lowest
SetupIconFile=Resources\hamdeck.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch HamDeck"; Flags: nowait postinstall skipifsilent
