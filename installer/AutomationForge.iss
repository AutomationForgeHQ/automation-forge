; Automation Forge — the Windows installer for the hub and the forge CLI.
;
; Built by tools/publish.ps1 (locally or in CI), which passes:
;   /DAppVersion=0.2.0            the version people see (may carry -nightly.yyyymmdd)
;   /DFileVersion=0.2.0.0         the numeric version Windows file properties need
;   /DSourceDir=<dist folder>     where hub\AutomationForgeHub.exe and cli\forge.exe are
;
; Per-user, no administrator prompt: it lands in %LOCALAPPDATA%\Programs. The hub
; asks for elevation itself, once, only when it writes into a Program Files
; engine. Silent updates run with /VERYSILENT /LAUNCH=1 — the hub starts this
; installer and exits; the installer relaunches the hub when it is done.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef FileVersion
  #define FileVersion "0.0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist"
#endif

#define AppName "Automation Forge"
#define AppPublisher "Blackcode SA"
#define AppURL "https://github.com/AutomationForgeHQ/automation-forge"
#define HubExe "AutomationForgeHub.exe"
; Must match HubUpdater.AppId in src/Forge.Hub/HubUpdater.cs.
#define AppGuid "4F0B9C7E-2A6D-4B1F-9E3C-7D5A1F0C8B21"

[Setup]
AppId={{{#AppGuid}}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases
VersionInfoVersion={#FileVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} setup
DefaultDirName={userpf}\Automation Forge
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableWelcomePage=no
PrivilegesRequired=lowest
OutputBaseFilename=AutomationForge-Setup
SetupIconFile=..\src\Forge.Hub\Assets\forge.ico
UninstallDisplayIcon={app}\{#HubExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
CloseApplications=yes
RestartApplications=no
ChangesEnvironment=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "autostart"; Description: "Start {#AppName} with Windows, in the tray, so it can watch for updates"; GroupDescription: "Background:"; Check: not IsUpgrade
Name: "addtopath"; Description: "Add the forge command line to PATH"; GroupDescription: "Command line:"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\hub\{#HubExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\cli\forge.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#HubExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#HubExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; Tasks: addtopath; Check: NeedsAddPath(ExpandConstant('{app}'))
; First install only: on an upgrade the hub's own setting is the truth and stays untouched.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "AutomationForge"; ValueData: """{app}\{#HubExe}"" --tray"; Tasks: autostart; Check: not IsUpgrade; Flags: uninsdeletevalue

[UninstallRun]
Filename: "{app}\{#HubExe}"; Parameters: "--uninstall-toasts"; RunOnceId: "toasts"; Flags: runhidden

[Run]
; Interactive install: the usual checkbox on the last page.
Filename: "{app}\{#HubExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
; Silent self-update: the hub passed /LAUNCH=1, so bring it back up.
Filename: "{app}\{#HubExe}"; Flags: nowait; Check: RelaunchRequested

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\AutomationForge\downloads"

[Code]
function NeedsAddPath(Param: string): boolean;
var
  OrigPath: string;
begin
  if not RegQueryStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', OrigPath) then
  begin
    Result := True;
    exit;
  end;
  Result := Pos(';' + Uppercase(Param) + ';', ';' + Uppercase(OrigPath) + ';') = 0;
end;

function RelaunchRequested: Boolean;
begin
  Result := ExpandConstant('{param:LAUNCH|0}') = '1';
end;

function IsUpgrade: Boolean;
begin
  Result := RegKeyExists(HKEY_CURRENT_USER,
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{' + '{#AppGuid}' + '}_is1');
end;

procedure RemoveFromPath(Dir: string);
var
  Path, Upper, UpperDir: string;
  P: Integer;
begin
  if not RegQueryStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', Path) then exit;
  Upper := ';' + Uppercase(Path) + ';';
  UpperDir := ';' + Uppercase(Dir) + ';';
  P := Pos(UpperDir, Upper);
  if P = 0 then exit;
  { P indexes the leading ';' of the match; drop the directory and one separator. }
  Delete(Path, P, Length(Dir) + 1);
  RegWriteExpandStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', Path);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RemoveFromPath(ExpandConstant('{app}'));
end;
