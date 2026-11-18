; RO2RW PC installer (Inno Setup). Installs to C:\RO2RW with its own folder;
; Projects, tools and settings live inside the program folder (writable by users).
; Upgrades replace the EXE in place and keep the Projects folder.
#ifndef AppVer
  #define AppVer "0.0.0"
#endif
#ifndef SrcExe
  #define SrcExe "RO2RW-PC.exe"
#endif
#define MyName "RO2RW PC"

[Setup]
AppId={{A3F2B1C4-5D6E-47F8-9A0B-1C2D3E4F5A6B}
AppName={#MyName}
AppVersion={#AppVer}
AppPublisher=shmuel-sinay
AppSupportURL=https://github.com/shmuel-sinay/ro2rw
DefaultDirName={sd}\RO2RW
DisableDirPage=no
DefaultGroupName={#MyName}
DisableProgramGroupPage=yes
OutputBaseFilename=RO2RW-PC-Setup
OutputDir=.
Compression=lzma2/max
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64
WizardStyle=modern
UninstallDisplayIcon={app}\RO2RW-PC.exe
UninstallDisplayName={#MyName} {#AppVer}

[Files]
Source: "{#SrcExe}"; DestDir: "{app}"; Flags: ignoreversion

[Dirs]
; grant standard users write access so Projects/tools/settings work without admin
Name: "{app}"; Permissions: users-modify
Name: "{app}\Projects"; Permissions: users-modify
Name: "{app}\tools"; Permissions: users-modify

[Icons]
Name: "{group}\{#MyName}"; Filename: "{app}\RO2RW-PC.exe"
Name: "{group}\Projects"; Filename: "{app}\Projects"
Name: "{group}\Uninstall {#MyName}"; Filename: "{uninstallexe}"
Name: "{commondesktop}\{#MyName}"; Filename: "{app}\RO2RW-PC.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Run]
Filename: "{app}\RO2RW-PC.exe"; Description: "Launch RO2RW PC"; Flags: nowait postinstall skipifsilent

; Note: no [UninstallDelete] for Projects - user data is kept on uninstall.
