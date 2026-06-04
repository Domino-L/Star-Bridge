#define MyAppName "Star Bridge"
#define MyAppNameCn "æ˜Ÿæµ·èˆ°æ¡¥"
#define MyAppVersion "0.3.1"
#define MyAppPublisher "Domino-L"
#define MyAppExeName "Star Bridge.exe"
#define MyRelayExeName "Star Bridge Relay Server.exe"
#define MySourceDir "..\StarBridge.Desktop\bin\Release\net8.0-windows\win-x64\publish"
#define MyRelaySourceDir "..\StarBridge.Server\bin\Release\net8.0\win-x64\publish"
#define MyIconPath "..\StarBridge.Desktop\Assets\Brand\StarBridge_AppIcon.ico"

[Setup]
AppId={{8F0E3D89-0DC1-4C51-8B6C-1BC7BA90378F}
AppName={#MyAppNameCn}
AppVerName={#MyAppNameCn} {#MyAppVersion}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\Star Bridge
DefaultGroupName={#MyAppNameCn}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=StarBridge-{#MyAppVersion}-win-x64-setup
SetupIconFile={#MyIconPath}
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#MySourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#MyRelaySourceDir}\*"; DestDir: "{app}\RelayServer"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\scripts\Start Star Bridge Relay Server.cmd"; DestDir: "{app}\Tools"; Flags: ignoreversion

[InstallDelete]
Type: filesandordirs; Name: "{app}\config"

[Icons]
Name: "{group}\{#MyAppNameCn}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{group}\Star Bridge Relay Server"; Filename: "{app}\Tools\Start Star Bridge Relay Server.cmd"; WorkingDir: "{app}\Tools"; IconFilename: "{app}\RelayServer\{#MyRelayExeName}"
Name: "{autodesktop}\{#MyAppNameCn}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppNameCn, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
