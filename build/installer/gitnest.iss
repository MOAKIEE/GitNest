#ifndef AppVersion
  #define AppVersion "0.1.0-dev"
#endif

#ifndef NumericVersion
  #define NumericVersion "0.1.0.0"
#endif

#ifndef SourceDir
  #define SourceDir "..\..\artifacts\windows-x64"
#endif

#ifndef OutputDir
  #define OutputDir "..\..\artifacts\installer"
#endif

#ifndef OutputBaseFilename
  #define OutputBaseFilename "GitNest-Setup-" + AppVersion + "-win-x64"
#endif

[Setup]
AppId={{A7E9C4B2-4F81-4D26-9C1E-8B5F0D4A2E71}
AppName=GitNest
AppVersion={#AppVersion}
AppVerName=GitNest {#AppVersion}
AppPublisher=GitNest contributors
VersionInfoVersion={#NumericVersion}
VersionInfoProductName=GitNest
VersionInfoProductVersion={#NumericVersion}
VersionInfoTextVersion={#AppVersion}
VersionInfoDescription=GitNest Windows x64 Installer
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\GitNest
DefaultGroupName=GitNest
DisableProgramGroupPage=yes
DisableWelcomePage=no
LicenseFile=..\..\LICENSE
SetupIconFile=..\..\src\App.ico
UninstallDisplayIcon={app}\GitNest.exe
UninstallDisplayName=GitNest
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}
CloseApplications=yes
CloseApplicationsFilter=GitNest.exe
RestartApplications=no
ChangesEnvironment=no
AllowNoIcons=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,createdump.exe"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\GitNest"; Filename: "{app}\GitNest.exe"
Name: "{group}\Uninstall GitNest"; Filename: "{uninstallexe}"
Name: "{autodesktop}\GitNest"; Filename: "{app}\GitNest.exe"; Tasks: desktopicon
