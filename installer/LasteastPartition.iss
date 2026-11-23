; Lasteast Partition — Inno Setup script. Built by CI from the self-contained publish folder.
;   iscc /DAppVersion=1.0.0 /DSourceDir=..\artifacts\app installer\LasteastPartition.iss
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\app"
#endif

[Setup]
AppId={{6F3A1C2E-8B9D-4E57-9A41-3C2B8E7D5F10}
AppName=Lasteast Partition
AppVersion={#AppVersion}
AppPublisher=Lasteast
AppPublisherURL=https://github.com/lasteast-collab/Partition
DefaultDirName={autopf}\Lasteast Partition
DefaultGroupName=Lasteast Partition
DisableProgramGroupPage=yes
OutputDir=..\artifacts
OutputBaseFilename=LasteastPartition-{#AppVersion}-setup-x64
SetupIconFile=..\src\Lasteast.App\Assets\app.ico
UninstallDisplayIcon={app}\LasteastPartition.exe
WizardImageFile=wizard-large.bmp
WizardSmallImageFile=wizard-small.bmp
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
PrivilegesRequired=admin
ShowLanguageDialog=auto
LicenseFile=..\LICENSE.txt

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "he"; MessagesFile: "compiler:Languages\Hebrew.isl"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
#if FileExists(CompilerPath + "Languages\Arabic.isl")
Name: "ar"; MessagesFile: "compiler:Languages\Arabic.isl"
#endif

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Lasteast Partition"; Filename: "{app}\LasteastPartition.exe"
Name: "{group}\{cm:UninstallProgram,Lasteast Partition}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Lasteast Partition"; Filename: "{app}\LasteastPartition.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\LasteastPartition.exe"; Description: "{cm:LaunchProgram,Lasteast Partition}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; settings, logs and partition-table backups in %LOCALAPPDATA% are kept on purpose (they may be needed for a restore)
Type: filesandordirs; Name: "{app}\rescue"
