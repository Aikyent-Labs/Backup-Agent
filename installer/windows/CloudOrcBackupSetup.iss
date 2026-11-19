; CloudOrc Backup Agent - Windows installer (Inno Setup 6).
;
; Build (CI or scripts/package-windows.ps1):
;   ISCC /DMyAppVersion=1.2.3 /DSourceDir=..\..\dist\win-x64\cloudorc-backup /DTraySourceDir=..\..\dist\win-x64\tray
;        [/DDefaultApiBase=https://api.cloudorc.example/ /DDefaultPortalUrl=https://portal.cloudorc.example/]
;        installer\windows\CloudOrcBackupSetup.iss
;
; Install - GUI: double-click CloudOrcBackupSetup.exe, type the activation code from the portal.
; Install - command line / RMM / GPO (elevated):
;   CloudOrcBackupSetup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CODE=ABCD-1234 [/API-BASE=https://...]
;   CloudOrcBackupSetup.exe /VERYSILENT /TOKEN=ENR-...        (CloudOrc VPS zero-touch, used by the Server Agent)
;   CloudOrcBackupSetup.exe /VERYSILENT /COMPONENTS="agent"   (servers: no status icon)
; Exit codes: 0 ok, 2 bad --api-base, 10/11 a service did not start, 20 activation failed.
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
  // GUI flow: one page that asks for the activation code shown in the CloudOrc portal.
  CodePage := CreateInputQueryPage(wpSelectComponents,
    'Activate this computer',
    'Enter the activation code from the CloudOrc portal (Devices > Add device).',
    'You can leave this empty and activate later from the status icon or with "cloudorc-backup activate <CODE>".');
  CodePage.Add('Activation code:', False);
  CodePage.Values[0] := GetNamedParam('code');
end;

function ActivationCode(): String;
begin
  if WizardSilent() or (CodePage = nil) then
    Result := Trim(GetNamedParam('code'))
  else
    Result := Trim(CodePage.Values[0]);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  agentExe, watchdogExe, token, code, apiBase, portalUrl, args: String;
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

    apiBase := GetNamedParam('api-base');
    if apiBase = '' then
      apiBase := '{#DefaultApiBase}';
    portalUrl := GetNamedParam('portal-url');
    if portalUrl = '' then
      portalUrl := '{#DefaultPortalUrl}';
    if apiBase <> '' then
    begin
      args := 'set-activation --api-base "' + apiBase + '"';
      if portalUrl <> '' then
        args := args + ' --portal-url "' + portalUrl + '"';
      if not RunAgent(agentExe, args) then
        FailInstall('The CloudOrc address (--api-base) was rejected; it must be an https:// address.', 2);
    end;

    // Activate BEFORE the service starts: a failed activation never leaves a half-configured agent.
    token := GetNamedParam('token');
    code := ActivationCode();
    if token <> '' then
    begin
      if not RunAgent(agentExe, 'enroll --token "' + token + '"') then
        FailInstall('Enrollment failed - the token is invalid or expired. See C:\ProgramData\CloudOrc\Backup\logs.', 20);
    end
    else if code <> '' then
    begin
      if not RunAgent(agentExe, 'activate "' + code + '" --no-restart') then
        FailInstall('Activation failed - the code may be wrong, expired or already used. Create a new code in the CloudOrc portal.', 20);
    end;

    InstallOrUpdateService('{#AgentServiceName}', 'CloudOrc Backup Agent',
      'Encrypted backup of this computer to CloudOrc. Managed from the CloudOrc portal.', agentExe);
    if not StartServiceAndVerify('{#AgentServiceName}') then
      FailInstall('The CloudOrc Backup Agent service did not start. See C:\ProgramData\CloudOrc\Backup\logs.', 10);

    InstallOrUpdateService('{#WatchdogServiceName}', 'CloudOrc Backup Watchdog',
      'Keeps the CloudOrc Backup Agent healthy.', watchdogExe);
    if not StartServiceAndVerify('{#WatchdogServiceName}') then
      FailInstall('The CloudOrc Backup Watchdog service did not start. See C:\ProgramData\CloudOrc\Backup\watchdog\logs.', 11);
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
