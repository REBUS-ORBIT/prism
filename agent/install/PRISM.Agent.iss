; -----------------------------------------------------------------------------
; PRISM Agent -- Inno Setup script
;
; Compiled by .github/workflows/agent.yml on windows-latest with Inno Setup 6
; (installed via Chocolatey).  Pass two compile-time defines:
;
;   /DPayloadDir=<absolute path to the dotnet publish/staging folder>
;   /DAgentVersion=<x.y.z>
;
; The wizard:
;   1. Welcome page
;   2. Install dir picker (defaults to Program Files\PRISM.Agent)
;   3. Connection settings (PRISM URL, Node name, Slots)
;   4. Install -> copies payload, then runs install.ps1 with the wizard inputs
;      (install.ps1 writes agent-config.json + registers the scheduled task)
;   5. Finish -> optional "Launch agent" + "Open Web UI" checkboxes
;
; AppId MUST stay constant across releases for upgrades to be detected.  Do
; not regenerate this GUID.
; -----------------------------------------------------------------------------

#define AppName       "PRISM Agent"
#define AppPublisher  "REBUS-ORBIT"
#define AppExeName    "PRISM.Agent.exe"
#define AppId         "{{8F3D9A12-7E5C-4B11-A0F2-9D1E3C7B5142}"

#ifndef AgentVersion
  #define AgentVersion "0.0.0"
#endif

#ifndef PayloadDir
  #error PayloadDir is required.  Pass /DPayloadDir=<absolute path> to ISCC.
#endif

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AgentVersion}
AppVerName={#AppName} {#AgentVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/REBUS-ORBIT/prism
AppSupportURL=https://github.com/REBUS-ORBIT/prism/issues
AppUpdatesURL=https://github.com/REBUS-ORBIT/prism-agent/releases/latest
DefaultDirName={autopf64}\PRISM.Agent
DefaultGroupName=PRISM Agent
DisableProgramGroupPage=yes
DisableDirPage=auto
AllowNoIcons=no
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64
ArchitecturesAllowed=x64
OutputDir=.
OutputBaseFilename=PRISM.Agent-Setup-v{#AgentVersion}
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
UninstallDisplayName={#AppName}
; Apps & Features entry icon -- pulled from the resource directory of the
; running EXE, which now carries the PRISM logo via <ApplicationIcon> in
; PRISM.Agent.csproj.
UninstallDisplayIcon={app}\{#AppExeName}
; v0.1.35: brand the installer wizard window and the install.exe itself with
; the PRISM logo. Path is relative to this .iss file (agent/install/).
SetupIconFile=..\src\PRISM.Agent\Assets\PRISM.Agent.ico
CloseApplications=force
RestartApplications=no
VersionInfoVersion={#AgentVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription=PRISM conversion agent installer
VersionInfoProductName={#AppName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; \
  GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Start Menu group (name = AppName).  "PRISM Agent" launches the tray app
; directly, useful when the operator stopped it via Stop Agent and wants
; to bring it back without rebooting.  "Web UI" opens the browser page;
; "Uninstall" removes the package.
;
; v0.1.35: every shortcut now points IconFilename at the side-by-side
; PRISM.Agent.ico so the brand icon shows in Explorer / Start menu /
; taskbar even if Windows hasn't refreshed the cached EXE icon yet.
; The Web UI shortcut targets an http:// URL, so the IconFilename is
; mandatory there -- Windows would otherwise render the default browser
; icon, which gives no visual indication that this opens the agent.
Name: "{group}\PRISM Agent";         Filename: "{app}\{#AppExeName}"; \
  WorkingDir: "{app}"; Comment: "Launch the PRISM Agent tray app"; \
  IconFilename: "{app}\Assets\PRISM.Agent.ico"
Name: "{group}\PRISM Agent Web UI";  Filename: "http://localhost:7421/"; \
  Comment: "Open the local agent configuration page"; \
  IconFilename: "{app}\Assets\PRISM.Agent.ico"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"

; Optional desktop shortcut (off by default; toggled via [Tasks] checkbox)
Name: "{autodesktop}\PRISM Agent";   Filename: "{app}\{#AppExeName}"; \
  WorkingDir: "{app}"; Tasks: desktopicon; \
  Comment: "Launch the PRISM Agent tray app"; \
  IconFilename: "{app}\Assets\PRISM.Agent.ico"

[Run]
; Run install.ps1 with the wizard inputs.  install.ps1 detects "in-place" mode
; (its $scriptRoot equals $InstallDir) and skips the redundant payload copy.
; -ForceConfig is intentionally NOT passed so an existing agent-config.json on
; an upgrade is preserved.
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\install.ps1"" -PrismUrl ""{code:GetPrismUrl}"" -NodeName ""{code:GetNodeName}"" -Slots {code:GetSlots} -InstallDir ""{app}"" -LaunchNow"; \
  StatusMsg: "Configuring agent and registering scheduled task..."; \
  Flags: runhidden waituntilterminated

; Optional finish-page checkboxes (silent installs skip these via skipifsilent).
Filename: "http://localhost:7421/"; \
  Description: "Open agent web UI"; \
  Flags: shellexec postinstall skipifsilent unchecked

[UninstallRun]
; uninstall.ps1 stops the running agent and removes the scheduled task.
; -NoFileCleanup leaves the on-disk payload to Inno's [UninstallDelete],
; which avoids the script self-deleting its own parent directory mid-run.
; -KeepData preserves C:\ProgramData\PRISM.Agent\logs\ across uninstalls.
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\uninstall.ps1"" -InstallDir ""{app}"" -KeepData -NoFileCleanup"; \
  Flags: runhidden waituntilterminated; \
  RunOnceId: "RunUninstallScript"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

; -----------------------------------------------------------------------------
[Code]
var
  ConnectionPage: TInputQueryWizardPage;

procedure InitializeWizard;
begin
  ConnectionPage := CreateInputQueryPage(wpSelectDir,
    'PRISM connection settings',
    'Configure how this workstation connects to the PRISM server.',
    'You can change these later from the agent web UI ' +
    '(http://localhost:7421/) or by editing agent-config.json directly.');

  ConnectionPage.Add('PRISM server URL:', False);
  ConnectionPage.Add('Node name:', False);
  ConnectionPage.Add('Slots (1-8):', False);

  ConnectionPage.Values[0] := 'wss://prism.rebus.industries/ws/agent';
  ConnectionPage.Values[1] := GetEnv('COMPUTERNAME');
  if ConnectionPage.Values[1] = '' then
    ConnectionPage.Values[1] := 'rhino-workstation';
  ConnectionPage.Values[2] := '2';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  SlotsStr: String;
  SlotsInt: Integer;
begin
  Result := True;
  if CurPageID = ConnectionPage.ID then
  begin
    if Trim(ConnectionPage.Values[0]) = '' then
    begin
      MsgBox('PRISM server URL is required.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    if Trim(ConnectionPage.Values[1]) = '' then
    begin
      MsgBox('Node name is required.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    SlotsStr := Trim(ConnectionPage.Values[2]);
    SlotsInt := StrToIntDef(SlotsStr, -1);
    if (SlotsInt < 1) or (SlotsInt > 8) then
    begin
      MsgBox('Slots must be an integer between 1 and 8.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
  end;
end;

function GetPrismUrl(Param: String): String;
begin
  Result := Trim(ConnectionPage.Values[0]);
end;

function GetNodeName(Param: String): String;
begin
  Result := Trim(ConnectionPage.Values[1]);
end;

function GetSlots(Param: String): String;
begin
  Result := Trim(ConnectionPage.Values[2]);
  if Result = '' then Result := '2';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  // Stop a running agent so we can replace its files cleanly.  Errors here
  // are non-fatal -- the agent simply might not be running yet.
  Exec(ExpandConstant('{cmd}'), '/c taskkill /IM PRISM.Agent.exe /F /T',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{cmd}'), '/c schtasks /End /TN PRISM.Agent',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  // Brief pause so handles drop before we start writing into {app}.
  Sleep(500);
  Result := '';
end;
