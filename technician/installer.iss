#define MyAppName "InnAware Support Technician"
#define MyAppVersion "0.6.0"
#define MyAppPublisher "InnAware"
#define MyAppExeName "InnAware-Support-Technician.exe"

[Setup]
AppId={{B4D45A1B-557C-4BCE-A97B-06768B28A4DF}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\InnAware\Support Technician
DefaultGroupName=InnAware
DisableProgramGroupPage=yes
OutputDir=..\dist\installer
OutputBaseFilename=InnAware-Support-Technician-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayName={#MyAppName}
SetupLogging=yes

[Files]
Source: "..\dist\technician\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\InnAware Support Technician"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\InnAware Support Technician"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch InnAware Support Technician"; Flags: nowait postinstall skipifsilent
