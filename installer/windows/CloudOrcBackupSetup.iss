; CloudOrc Backup Agent - Windows installer (Inno Setup 6).
;
; Build (CI or scripts/package-windows.ps1):
;   ISCC /DMyAppVersion=1.2.3 /DSourceDir=..\..\dist\win-x64\cloudorc-backup /DTraySourceDir=..\..\dist\win-x64\tray
;        [/DDefaultApiBase=https://api.cloudorc.example/ /DDefaultPortalUrl=https://portal.cloudorc.example/]
;        installer\windows\CloudOrcBackupSetup.iss
;
; Install - GUI: double-click CloudOrcBackupSetup.exe, paste the enrollment token from the portal.
; Upgrade: run a newer setup on a computer that is already connected - the token page says so; leave it
;          empty to only replace the program (the device identity stays in ProgramData), or paste a new
;          token to connect the computer again (e.g. after it was removed in the portal).
; Install - command line / RMM / GPO (elevated):
;   CloudOrcBackupSetup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TOKEN=CLB1.xxxx   (enrollment token)
;   CloudOrcBackupSetup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART   (upgrade of a connected computer)
;   CloudOrcBackupSetup.exe /VERYSILENT /TOKEN=ENR-...        (CloudOrc VPS zero-touch, used by the Server Agent)
;   CloudOrcBackupSetup.exe /VERYSILENT /COMPONENTS="agent"   (servers: no status icon)
; Exit codes: 0 ok, 10/11 a service did not start, 21 installed and running but NOT activated
;             (wrong/expired/used token - activate later with "cloudorc-backup activate <TOKEN>").
;
; Installs two Windows services (LocalSystem, delayed automatic start so the boot is never slowed):
;   CloudOrcBackupAgent     the Backup Agent
;   CloudOrcBackupWatchdog  keeps the agent healthy
; Uninstall keeps C:\ProgramData\CloudOrc\Backup (identity + local state) - backups in CloudOrc are never touched.

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0-dev"
#endif
; Windows file version must be numeric (X.Y.Z): "1.2.0-beta" -> "1.2.0".
#ifndef NumericVersion
  #define NumericVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\dist\win-x64\cloudorc-backup"
#endif
#ifndef TraySourceDir
  #define TraySourceDir "..\..\dist\win-x64\tray"
#endif
#ifndef DefaultApiBase
  #define DefaultApiBase ""
#endif
#ifndef DefaultPortalUrl
  #define DefaultPortalUrl ""
#endif

#define MyAppName "CloudOrc Backup Agent"
#define MyAppPublisher "CloudOrc"
#define AgentServiceName "CloudOrcBackupAgent"
#define WatchdogServiceName "CloudOrcBackupWatchdog"

[Setup]
; Fixed product id - never change it, upgrades and uninstall depend on it. Different from the
; CloudOrc Server Agent's id: the two products install, upgrade and uninstall independently.
AppId={{7C1B4E2A-3D5F-4A8B-9E61-2F0C8D7B5A93}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#NumericVersion}
VersionInfoProductName={#MyAppName}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=CloudOrc Backup Agent Setup
DefaultDirName={autopf}\CloudOrc\Backup
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.14393
Compression=lzma2/max
SolidCompression=yes
OutputDir=Output
OutputBaseFilename=CloudOrcBackupSetup
UninstallDisplayIcon={app}\bin\cloudorc-backup.exe
UninstallDisplayName={#MyAppName}
WizardStyle=modern
CloseApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Types]
Name: "full"; Description: "Backup Agent with status icon (desktops, laptops)"
Name: "server"; Description: "Backup Agent only (servers)"
Name: "custom"; Description: "Custom"; Flags: iscustom

[Components]
Name: "agent"; Description: "CloudOrc Backup Agent + Watchdog"; Types: full server custom; Flags: fixed
Name: "tray"; Description: "Status icon for signed-in users"; Types: full

[Files]
Source: "{#SourceDir}\bin\*"; DestDir: "{app}\bin"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: agent
Source: "{#SourceDir}\engines\*"; DestDir: "{app}\engines"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: agent
Source: "{#SourceDir}\VERSION"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist; Components: agent
Source: "{#TraySourceDir}\*"; DestDir: "{app}\tray"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist; Components: tray

[Registry]
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "CloudOrcBackupTray"; ValueData: """{app}\tray\CloudOrcBackupTray.exe"""; Flags: uninsdeletevalue; Components: tray
; cloudorc-backup://activate?code=... links from the portal (the tray always asks before activating).
Root: HKLM; Subkey: "SOFTWARE\Classes\cloudorc-backup"; ValueType: string; ValueName: ""; ValueData: "URL:CloudOrc Backup activation"; Flags: uninsdeletekey; Components: tray
Root: HKLM; Subkey: "SOFTWARE\Classes\cloudorc-backup"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""; Components: tray
Root: HKLM; Subkey: "SOFTWARE\Classes\cloudorc-backup\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\tray\CloudOrcBackupTray.exe"" ""%1"""; Components: tray

[Code]
var
  CodePage: TInputQueryWizardPage;
  ActivationProblem: String;

procedure ExitProcess(uExitCode: UINT);
external 'ExitProcess@kernel32.dll stdcall';

procedure FailInstall(Message: String; ExitCode: Integer);
begin
  if not WizardSilent() then
    MsgBox(Message, mbCriticalError, MB_OK);
  Log(Message);
  ExitProcess(ExitCode);
end;

// /CODE=x, /code x, --code=x, --code x
function GetNamedParam(Name: String): String;
var
  i: Integer;
  p, upperP, upperName: String;
begin
  Result := '';
  upperName := Uppercase(Name);
  for i := 1 to ParamCount do
  begin
    p := ParamStr(i);
    upperP := Uppercase(p);
    if ((upperP = '--' + upperName) or (upperP = '/' + upperName)) and (i < ParamCount) then
    begin
      Result := ParamStr(i + 1);
      Exit;
    end;
    if Copy(upperP, 1, Length(upperName) + 3) = '--' + upperName + '=' then
    begin
      Result := Copy(p, Length(upperName) + 4, MaxInt);
      Exit;
    end;
    if Copy(upperP, 1, Length(upperName) + 2) = '/' + upperName + '=' then
    begin
      Result := Copy(p, Length(upperName) + 3, MaxInt);
      Exit;
    end;
  end;
end;

function RunSc(Args: String): Integer;
var
  ResultCode: Integer;
begin
  if not Exec(ExpandConstant('{sys}\sc.exe'), Args, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    ResultCode := -1;
  Result := ResultCode;
end;

function ServiceExists(ServiceName: String): Boolean;
begin
  Result := RunSc('query "' + ServiceName + '"') = 0;
end;

// RUNNING, STOP_PENDING or START_PENDING (i.e. not yet fully stopped).
function IsServiceActive(ServiceName: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(ExpandConstant('{cmd}'), '/c sc.exe query "' + ServiceName + '" | find "STOPPED" >nul', '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode <> 0);
end;

procedure StopServiceIfPresent(ServiceName: String);
var
  i: Integer;
begin
  if ServiceExists(ServiceName) then
  begin
    RunSc('stop "' + ServiceName + '"');
    // give it time to release its files
    for i := 1 to 60 do
    begin
      if not IsServiceActive(ServiceName) then
        Break;
      Sleep(1000);
    end;
  end;
end;

procedure DeleteServiceIfPresent(ServiceName: String);
begin
  if ServiceExists(ServiceName) then
    RunSc('delete "' + ServiceName + '"');
end;

procedure InstallOrUpdateService(ServiceName, DisplayName, Description, ExePath: String);
begin
  if not ServiceExists(ServiceName) then
    RunSc('create "' + ServiceName + '" binPath= "\"' + ExePath + '\"" DisplayName= "' + DisplayName + '" start= delayed-auto')
  else
    RunSc('config "' + ServiceName + '" binPath= "\"' + ExePath + '\"" start= delayed-auto');
  RunSc('description "' + ServiceName + '" "' + Description + '"');
  // OS-level restart on crash; the watchdog additionally catches a hung (running but unhealthy) agent.
  RunSc('failure "' + ServiceName + '" reset= 86400 actions= restart/30000/restart/60000/restart/120000');
  RunSc('failureflag "' + ServiceName + '" 1');
end;

function IsServiceRunning(ServiceName: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(ExpandConstant('{cmd}'), '/c sc.exe query "' + ServiceName + '" | find "RUNNING" >nul', '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

function StartServiceAndVerify(ServiceName: String): Boolean;
var
  i: Integer;
begin
  RunSc('start "' + ServiceName + '"');
  Result := False;
  for i := 1 to 30 do
  begin
    if IsServiceRunning(ServiceName) then
    begin
      Result := True;
      Exit;
    end;
    Sleep(1000);
  end;
end;

// Already connected to CloudOrc (enrolled before): an upgrade only replaces the program files.
// The device identity lives in ProgramData and is never touched, so it never asks for a token again.
function IsAlreadyActivated(): Boolean;
begin
  Result := FileExists(ExpandConstant('{commonappdata}\CloudOrc\Backup\enrollment.dat'));
end;

function IsEnrollmentToken(Value: String): Boolean;
begin
  Result := Copy(Uppercase(Trim(Value)), 1, 5) = 'CLB1.';
end;

function RunAgent(AgentExe, Args: String): Boolean;
var
  ResultCode: Integer;
begin
  Log('Running: cloudorc-backup ' + Args);
  Result := Exec(AgentExe, Args, ExtractFileDir(AgentExe), SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
  if not Result then
    Log('Exit code: ' + IntToStr(ResultCode));
end;

procedure InitializeWizard();
begin
  // GUI flow: ONE field - the enrollment token from the portal's "Add device" page.
  // The token carries the CloudOrc address, so nothing else is asked. The page is ALWAYS shown:
  // an already connected computer can leave it empty (update only) or connect again with a new token
  // (e.g. after it was removed in the portal or its earlier activation did not finish).
  if IsAlreadyActivated() then
    CodePage := CreateInputQueryPage(wpSelectComponents,
      'This computer is already connected',
      'This computer is already connected to CloudOrc. Leave the token empty to only update the Backup Agent.',
      'To connect it again (for example after it was removed in the portal), paste a new enrollment token ' +
      'from the CloudOrc portal (Backup > Computers > Add device). The token starts with CLB1.')
  else
    CodePage := CreateInputQueryPage(wpSelectComponents,
      'Activate this computer',
      'Paste the enrollment token from the CloudOrc portal (Backup > Computers > Add device).',
      'The token starts with CLB1. You can also leave it empty and activate later from the status icon ' +
      'or with "cloudorc-backup activate <TOKEN>".');
  CodePage.Add('Enrollment token:', False);
  CodePage.Values[0] := GetNamedParam('token');
end;

// The enrollment token (CLB1....) from the page or the command line (/TOKEN=).
function ActivationCode(): String;
begin
  if WizardSilent() or (CodePage = nil) then
    Result := Trim(GetNamedParam('token'))
  else
    Result := Trim(CodePage.Values[0]);
  if not IsEnrollmentToken(Result) then
    Result := '';
end;

// Upgrade of an already connected computer: skip the token page.
function ShouldSkipPage(PageID: Integer): Boolean;
begin
  // Never skipped: a connected computer may need a new token (see InitializeWizard).
  Result := False;
end;

// CloudOrc address kept for "activate later" (the token brings its own address).
function ApiAddress(): String;
begin
  Result := Trim(GetNamedParam('api-base'));
  if Result = '' then
    Result := '{#DefaultApiBase}';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CodePage <> nil) and (CurPageID = CodePage.ID) and (Trim(CodePage.Values[0]) <> '') and not IsEnrollmentToken(CodePage.Values[0]) then
  begin
    MsgBox('This is not an enrollment token. Copy the token from the CloudOrc portal (Backup > Computers > Add device); it starts with CLB1. Or leave the field empty to activate later.', mbError, MB_OK);
    Result := False;
  end;
end;

// Silent installs report "installed but not activated" with exit code 21 (RMM / scripts check it).
function GetCustomSetupExitCode(): Integer;
begin
  if ActivationProblem <> '' then
    Result := 21
  else
    Result := 0;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  agentExe, watchdogExe, token, code, apiBase, portalUrl, args: String;
  alreadyActivated: Boolean;
begin
  if CurStep = ssInstall then
  begin
    // Release the files before they are replaced (upgrade).
    StopServiceIfPresent('{#WatchdogServiceName}');
    StopServiceIfPresent('{#AgentServiceName}');
  end;

  if CurStep = ssPostInstall then
  begin
    agentExe := ExpandConstant('{app}\bin\cloudorc-backup.exe');
    watchdogExe := ExpandConstant('{app}\bin\cloudorc-backup-watchdog.exe');

    apiBase := ApiAddress();
    portalUrl := GetNamedParam('portal-url');
    if portalUrl = '' then
      portalUrl := '{#DefaultPortalUrl}';
    alreadyActivated := IsAlreadyActivated();
    if alreadyActivated then
      Log('Already connected to CloudOrc: upgrading the program only (no new activation).');
    // An already connected computer keeps its CloudOrc address; an enrollment token carries its own.
    if (apiBase <> '') and not alreadyActivated and not IsEnrollmentToken(ActivationCode()) then
    begin
      args := 'set-activation --api-base "' + apiBase + '"';
      if portalUrl <> '' then
        args := args + ' --portal-url "' + portalUrl + '"';
      if not RunAgent(agentExe, args) then
        ActivationProblem := 'The CloudOrc address was rejected (it must start with https://).';
    end;

    // Activate before the service starts. A failed activation no longer stops the installation:
    // the agent is installed and running and can be activated later.
    token := GetNamedParam('token');
    if IsEnrollmentToken(token) then
      token := '';  // CLB1 tokens are redeemed by "activate" below; ENR-... tokens by "enroll".
    code := ActivationCode();
    // Already connected: no token = update only; a new enrollment token connects it again.
    if alreadyActivated then
      token := '';
    if (ActivationProblem = '') and (token <> '') then
    begin
      if not RunAgent(agentExe, 'enroll --token "' + token + '"') then
        ActivationProblem := 'Enrollment failed - the token is invalid or expired.';
    end
    else if (ActivationProblem = '') and (code <> '') then
    begin
      if not RunAgent(agentExe, 'activate "' + code + '" --no-restart') then
        ActivationProblem := 'Activation failed - the enrollment token may be wrong, expired or already used, or CloudOrc could not be reached.';
    end;

    InstallOrUpdateService('{#AgentServiceName}', 'CloudOrc Backup Agent',
      'Encrypted backup of this computer to CloudOrc. Managed from the CloudOrc portal.', agentExe);
    if not StartServiceAndVerify('{#AgentServiceName}') then
      FailInstall('The CloudOrc Backup Agent service did not start. See C:\ProgramData\CloudOrc\Backup\logs.', 10);

    InstallOrUpdateService('{#WatchdogServiceName}', 'CloudOrc Backup Watchdog',
      'Keeps the CloudOrc Backup Agent healthy.', watchdogExe);
    if not StartServiceAndVerify('{#WatchdogServiceName}') then
      FailInstall('The CloudOrc Backup Watchdog service did not start. See C:\ProgramData\CloudOrc\Backup\watchdog\logs.', 11);

    if ActivationProblem <> '' then
    begin
      Log('Installed but not activated: ' + ActivationProblem);
      if not WizardSilent() then
        MsgBox('The CloudOrc Backup Agent is installed and running, but this computer is not activated yet.' + #13#10#13#10 +
          ActivationProblem + #13#10#13#10 +
          'Create a new enrollment token in the CloudOrc portal (Backup > Computers > Add device) and run, as Administrator:' + #13#10 +
          '"C:\Program Files\CloudOrc\Backup\bin\cloudorc-backup.exe" activate <TOKEN>', mbInformation, MB_OK);
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    StopServiceIfPresent('{#WatchdogServiceName}');
    StopServiceIfPresent('{#AgentServiceName}');
    DeleteServiceIfPresent('{#WatchdogServiceName}');
    DeleteServiceIfPresent('{#AgentServiceName}');
  end;
end;
