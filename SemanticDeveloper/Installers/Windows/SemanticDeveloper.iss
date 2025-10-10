; Inno Setup script to package SemanticDeveloper
#define AppName "SemanticDeveloper"
#define AppVersion "1.0.5"
#define Publisher "Stainless Designer LLC"
#define URL "https://github.com/stainless-design/semantic-developer"
#ifndef RID
#define RID "win-x64"
#endif
#ifndef OutDir
#define OutDir "out\\publish"
#endif
#ifndef ArtifactsDir
#define ArtifactsDir "artifacts"
#endif

[Setup]
AppId={{C21A3B89-8F5C-4E9F-9F5D-7B5C6A5B3A99}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#Publisher}
AppPublisherURL={#URL}
DefaultDirName={autopf64}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\SemanticDeveloper.exe
OutputBaseFilename=SemanticDeveloperSetup-{#RID}
OutputDir={#ArtifactsDir}
Compression=lzma2
SolidCompression=yes
DisableDirPage=auto
DisableProgramGroupPage=auto
ArchitecturesInstallIn64BitMode=x64 arm64
SetupIconFile=..\\..\\SemanticDeveloper\\Images\\SemanticDeveloperLogo.ico

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Files]
Source: "{#OutDir}/*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\SemanticDeveloper.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\SemanticDeveloper.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\SemanticDeveloper.exe"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
