; ---------------------------------------------------------------------------
; MusicDraft installer (Inno Setup 6+)
;
; What this installer CONTAINS:
;   - the published MusicDraft app (self-contained, so no .NET prerequisite)
;   - the pinned Python worker scripts (worker\**\*.py) copied into the publish output
;   - license notices
;
; What it deliberately does NOT contain:
;   - the ComfyUI runtime (~1.9 GB download / ~4.2 GB extracted)
;   - the YuE2 / SheetSage2 / Gemma model weights (multi-GB)
;   These are fetched on first run by the app's own setup dialog, because:
;     1. GitHub release assets are capped at 2 GiB per file, and a full bundle is ~12-13 GB;
;     2. the YuE2 weights are CC BY-NC 4.0 and explicitly forbid weight redistribution.
;   See planning/delivery/07-delivery.md ("Never ship a misleading self-contained claim").
;
; Build:  installer\build-installer.ps1 -Version 0.1.0
; ---------------------------------------------------------------------------

#define AppName "MusicDraft"
#define AppPublisher "MusicDraft"
#define AppExeName "MusicDraft.exe"

; Overridable from the command line: ISCC.exe /DAppVersion=1.2.3 /DSourceDir=..\publish
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\src\MusicDraft.App\bin\Release\net10.0-windows\win-x64\publish"
#endif

[Setup]
; A stable AppId keeps upgrades/uninstall working across versions. Do not change it.
AppId={{B7E4C2A1-9F3D-4E6B-8A21-5C7D9E0F1A2B}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}

; Per-user install: no admin prompt, and it matches the app's per-user data model.
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

OutputDir=..\artifacts
OutputBaseFilename=MusicDraftSetup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExeName}
LicenseFile=..\LICENSE
; SetupIconFile=app.ico   ; add an .ico and uncomment to brand the installer

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; The whole self-contained publish output (app + .NET runtime + worker scripts).
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

; NOTE: there is deliberately no [UninstallDelete] entry. %LOCALAPPDATA%\MusicDraft holds the
; downloaded runtime, model weights and the user's library (potentially many GB). Uninstalling the
; app must never silently delete that. Users can remove it manually from Settings or by deleting
; the folder.
