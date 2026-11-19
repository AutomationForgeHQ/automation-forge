; Kovati Hub (beta): the Windows installer for Kovati Hub v2 and the forge CLI.
;
; Built by tools/publish.ps1 (locally or in CI), which passes:
;   /DAppVersion=0.5.0-beta.1     the version people see (may carry -nightly.yyyymmdd)
;   /DFileVersion=0.5.0.0         the numeric version Windows file properties need
;   /DSourceDir=<dist folder>     where hub\KovatiHub.exe and cli\forge.exe are
;
; Per-user, no administrator prompt: it lands in %LOCALAPPDATA%\Programs\Kovati Hub. The hub
; asks for elevation itself, once, only when it writes into a Program Files
; engine. Silent updates run with /VERYSILENT /LAUNCH=1 — the hub starts this
; installer and exits; the installer relaunches the hub when it is done.
;
; The beta installs BESIDE the Automation Forge Hub 0.4.x and never touches it. Everything Windows
; tells installed programs apart by is its own: the AppId (0.4.x: 4F0B9C7E-...), the folder
; (0.4.x: {userpf}\Automation Forge), the program file (0.4.x: AutomationForgeHub.exe, which is the
; only image name 0.4.x's installer closes), the Run value (0.4.x: AutomationForge), the Start menu
; entry and the setup file name. It closes only KovatiHub.exe, does not delete 0.4.x's shortcuts,
; does not start with Windows and does not add forge to PATH unless those boxes are ticked.
; Both Hubs share %LOCALAPPDATA%\AutomationForge (receipts, sign-in, settings, caches).

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef FileVersion
  #define FileVersion "0.0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist"
#endif

#define AppName "Kovati Hub (beta)"
#define DirName "Kovati Hub"
#define AppPublisher "Bojan Andrejek / MetaWorx LLC"
#define AppURL "https://github.com/AutomationForgeHQ/automation-forge"
#define HubExe "KovatiHub.exe"
; Must match HubIdentity.AppId in src/Forge.Core/HubIdentity.cs (read by HubUpdater.AppId).
#define AppGuid "E13BC642-3BBA-4D78-88D9-8D3767887CAE"

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
DefaultDirName={userpf}\{#DirName}
DefaultGroupName={#AppName}
UsePreviousGroup=no
DisableProgramGroupPage=yes
DisableWelcomePage=no
PrivilegesRequired=lowest
OutputBaseFilename=KovatiHub-Setup
SetupIconFile=..\src\Forge.Hub\Assets\forge.ico
UninstallDisplayIcon={app}\{#HubExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
; A hub in the tray has no window to close, so the Restart Manager cannot ask it;
; PrepareToInstall below asks it ourselves (--quit) and stops it if it will not.
CloseApplications=no
RestartApplications=no
ChangesEnvironment=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; Both off by default: two Hubs watching from the tray, or two forge.exe on PATH, only when somebody asks for it.
Name: "autostart"; Description: "Start {#AppName} with Windows, in the tray, so it can watch for updates"; GroupDescription: "Background:"; Flags: unchecked; Check: not IsUpgrade
Name: "addtopath"; Description: "Add this forge command line to PATH (the Automation Forge Hub may already have put one there)"; GroupDescription: "Command line:"; Flags: unchecked
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\hub\{#HubExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\cli\forge.exe"; DestDir: "{app}"; Flags: ignoreversion
; The catalogue and the installer's set of stills (spec 12.4), read by the Hub and by forge install --track.
Source: "{#SourceDir}\hub\catalogue\*"; DestDir: "{app}\catalogue"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; The catalogue is replaced whole, so a picture the new one no longer names does not linger.
; (Nothing of the Automation Forge Hub's is deleted: its shortcuts and folder stay as they are.)
Type: filesandordirs; Name: "{app}\catalogue"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#HubExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#HubExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; Tasks: addtopath; Check: NeedsAddPath(ExpandConstant('{app}'))
; First install only: on an upgrade the hub's own setting is the truth and stays untouched.
; Its own value (HubIdentity.AutostartValue); 0.4.x's is "AutomationForge".
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "KovatiHub"; ValueData: """{app}\{#HubExe}"" --tray"; Tasks: autostart; Check: not IsUpgrade; Flags: uninsdeletevalue

[UninstallRun]
Filename: "{app}\{#HubExe}"; Parameters: "--uninstall-toasts"; RunOnceId: "toasts"; Flags: runhidden

[Run]
; Interactive install: the usual checkbox on the last page.
Filename: "{app}\{#HubExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
; Silent self-update: the hub passed /LAUNCH=1, so bring it back up.
Filename: "{app}\{#HubExe}"; Flags: nowait; Check: RelaunchRequested

[UninstallDelete]
; Only this Hub's downloaded installers: the download folder is shared with the Automation Forge Hub.
Type: files; Name: "{localappdata}\AutomationForge\downloads\KovatiHub-Setup-*.exe"

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

{ Only KovatiHub.exe. The Automation Forge Hub runs as AutomationForgeHub.exe and is never asked or stopped. }
function IsHubRunning: Boolean;
var
  ResultCode: Integer;
begin
  { find exits 0 when tasklist listed the image. }
  Result := Exec('cmd.exe', '/c tasklist /FI "IMAGENAME eq {#HubExe}" | find /I "{#HubExe}" >nul',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

{ The running hub may be in the tray with no window; ask it to quit, then insist. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Exe: string;
  ResultCode, I: Integer;
begin
  Result := '';
  if not IsHubRunning then exit;
  { This install's own KovatiHub.exe, which signals Local\KovatiHub.Quit: only the beta Hub listens to it. }
  Exe := ExpandConstant('{app}\{#HubExe}');
  if FileExists(Exe) then
    Exec(Exe, '--quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  for I := 1 to 20 do
  begin
    if not IsHubRunning then break;
    Sleep(500);
  end;
  if IsHubRunning then
  begin
    Exec('taskkill.exe', '/F /IM {#HubExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(1000);
  end;
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
