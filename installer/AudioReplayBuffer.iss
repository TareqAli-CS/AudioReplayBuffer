; Inno Setup script for Audio Replay Buffer
; Build: ISCC.exe AudioReplayBuffer.iss   (expects the app published to ..\publish)

#define MyAppName "Audio Replay Buffer"
#define MyAppVersion "1.1.0"
#define MyAppPublisher "TareqAli-CS"
#define MyAppURL "https://github.com/TareqAli-CS/AudioReplayBuffer"
#define MyAppExeName "AudioReplayBuffer.exe"

[Setup]
AppId={{7A3F9C41-5E82-4B1D-9F60-2C8A41D7B3E9}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
; Per-user install: no admin prompt, lands in %LocalAppData%\Programs
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\AudioReplayBuffer
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=AudioReplayBuffer-Setup-{#MyAppVersion}
SetupIconFile=..\AudioReplayBuffer\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{userprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Make sure the app isn't running while files are removed
Filename: "{cmd}"; Parameters: "/C taskkill /im {#MyAppExeName} /f"; Flags: runhidden; RunOnceId: "KillApp"
