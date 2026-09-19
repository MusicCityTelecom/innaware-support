#ifndef PublishDir
  #define PublishDir "..\dist\technician"
#endif

#ifndef OutputDir
  #define OutputDir "..\dist\installer"
#endif

#define MyAppName "InnAware Support Technician"
#define MyAppVersion "0.6.0"
#define MyAppPublisher "InnAware"
#define MyAppExeName "InnAwareSupportTechnician.exe"

[Setup]
AppId={{F6D48E10-1836-4F33-9A54-88C8935C247A}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\InnAware\Support Technician
DefaultGroupName=InnAware
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=InnAware-Support-Technician-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\InnAware Support Technician"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\InnAware Support Technician"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch InnAware Support Technician"; Flags: nowait postinstall skipifsilent
