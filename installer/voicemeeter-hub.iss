; Inno Setup script for voicemeeter-hub.
; Per-user install (no administrator rights) into %LOCALAPPDATA%\voicemeeter-hub, which is exactly
; the path the Stream Dock plugin already probes, so no environment variable is needed.
;
; Build:  iscc /DAppVersion=0.1.0 /DSourceDir="..\dist\hub" installer\voicemeeter-hub.iss
; SourceDir must contain the published VoicemeeterHub.exe and log4net.config.

#define AppName "Voicemeeter Hub"

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#ifndef SourceDir
  #define SourceDir "..\dist\hub"
#endif

[Setup]
AppId={{B3F5B7A2-6C4D-4E9A-9F21-7A1E2C3D4E5F}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Project contributors
DefaultDirName={localappdata}\voicemeeter-hub
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=Output
OutputBaseFilename=voicemeeter-hub-{#AppVersion}-setup
UninstallDisplayName={#AppName} {#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Files]
Source: "{#SourceDir}\VoicemeeterHub.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\log4net.config"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\Voicemeeter Hub"; Filename: "{app}\VoicemeeterHub.exe"

[Tasks]
Name: "startup"; Description: "Start Voicemeeter Hub automatically when I sign in to Windows"; GroupDescription: "Additional tasks:"; Flags: unchecked

[Registry]
; Optional autostart at logon (only when the task above is selected). The hub exits on its own when
; idle and clients relaunch it on demand, so autostart is off by default.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "VoicemeeterHub"; ValueData: """{app}\VoicemeeterHub.exe"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\VoicemeeterHub.exe"; Description: "Launch Voicemeeter Hub now"; Flags: nowait postinstall skipifsilent
