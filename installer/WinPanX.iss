#define MyAppName "WinPan X"
#define MyAppExeName "WinPanX.Agent.exe"
#define MyAppPublisher "WinPan X"
#define MyAppURL ""
#define MyAppId "{{41EEC61A-8FE1-475A-BF3D-C949AB4F9E28}"

#ifndef BuildOutput
  #define BuildOutput "..\artifacts\publish\win-x64"
#endif
#ifndef AppVersion
  #define AppVersion "0.50.0"
#endif
#ifndef InstallerSuffix
  #define InstallerSuffix ""
#endif

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppVerName={#MyAppName} {#AppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={localappdata}\Programs\WinPanX
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=WinPanX-Setup{#InstallerSuffix}
SetupIconFile=..\src\WinPanX.Agent\Assets\WinPanX.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Installer

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Run WinPan X on sign-in"; GroupDescription: "Additional options:"; Flags: unchecked
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional options:"; Flags: unchecked

[Files]
Source: "{#BuildOutput}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "winpanx.json"
Source: "..\src\WinPanX.Agent\winpanx.json"; DestDir: "{localappdata}\WinPanX"; DestName: "winpanx.json"; Flags: onlyifdoesntexist uninsneveruninstall

[Icons]
Name: "{autoprograms}\WinPan X"; Filename: "{app}\{#MyAppExeName}"; Parameters: """{localappdata}\WinPanX\winpanx.json"""; WorkingDir: "{app}"
Name: "{autodesktop}\WinPan X"; Filename: "{app}\{#MyAppExeName}"; Parameters: """{localappdata}\WinPanX\winpanx.json"""; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "WinPanX"; ValueData: """{app}\{#MyAppExeName}"" ""{localappdata}\WinPanX\winpanx.json"""; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Parameters: """{localappdata}\WinPanX\winpanx.json"""; Description: "Launch WinPan X"; Flags: nowait postinstall skipifsilent unchecked

