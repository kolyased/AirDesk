#ifndef PublishDir
  #error "Pass PublishDir from scripts/Package.ps1"
#endif
#ifndef OutputPath
  #error "Pass OutputPath from scripts/Package.ps1"
#endif
#ifndef AppVersion
  #error "Pass AppVersion from scripts/Package.ps1"
#endif

[Setup]
AppId={{A05B3464-9731-44CA-94A7-902E82617A47}
AppName=AirDesk
AppVersion={#AppVersion}
AppPublisher=AirDesk contributors
AppPublisherURL=https://github.com/kolyased/AirDesk
DefaultDirName={localappdata}\Programs\AirDesk
DefaultGroupName=AirDesk
UninstallDisplayIcon={app}\AirDesk.exe
SetupIconFile=..\src\AirDesk\airdesk.ico
LicenseFile=..\LICENSE
PrivilegesRequired=lowest
MinVersion=10.0.19045
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
AppMutex=Local\AirDesk,Local\AirDesk-0.1
SetupMutex=Local\AirDesk.Setup
CloseApplications=no
RestartApplications=no
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
OutputDir={#OutputPath}
OutputBaseFilename=AirDesk-{#AppVersion}-Setup-win-x64
VersionInfoVersion={#AppVersion}.0
VersionInfoDescription=AirDesk Setup (Windows x64)
#ifdef SignedRelease
SignTool=airdesk
SignedUninstaller=yes
#endif

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "autostart"; Description: "{cm:AutoStart}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\AirDesk"; Filename: "{app}\AirDesk.exe"; WorkingDir: "{app}"

[CustomMessages]
russian.AutoStart=Запускать AirDesk при входе в Windows (только мониторинг)
english.AutoStart=Start AirDesk at sign-in (monitoring only)
russian.Recovery=AirDesk сохранил отметку незавершённого управления. Проверьте BIOS/EC и подтвердите восстановление в AirDesk, прежде чем обновлять или удалять приложение.
english.Recovery=AirDesk recorded an unfinished cooling session. Check BIOS/EC and acknowledge recovery in AirDesk before updating or uninstalling.
russian.Running=Закройте AirDesk через «Выход» в трее и дождитесь возврата управления BIOS/EC.
english.Running=Exit AirDesk from the notification area and wait for BIOS/EC control to return.

[Code]
const
    RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
    RecoveryKey = 'SOFTWARE\AirDesk\Cooling';

var
    InitialTaskSelected: Boolean;

function RunCommand: String;
begin
    Result := '"' + ExpandConstant('{app}\AirDesk.exe') + '" --autostart';
end;

function OwnsStartup: Boolean;
var
    Current: String;
begin
    Result := RegQueryStringValue(HKCU, RunKey, 'AirDesk', Current) and
        (CompareText(Current, RunCommand) = 0);
end;

function BlockReason: String;
begin
    Result := '';
    if CheckForMutexes('Local\AirDesk,Local\AirDesk-0.1') then
        Result := CustomMessage('Running')
    else if RegValueExists(HKLM64, RecoveryKey, 'PendingCaptureV1') or
        RegValueExists(HKLM32, RecoveryKey, 'PendingCaptureV1') then
        Result := CustomMessage('Recovery');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
    Result := BlockReason;
end;

procedure CurPageChanged(Page: Integer);
begin
    if (Page <> wpSelectTasks) or WizardSilent or InitialTaskSelected then
        exit;
    InitialTaskSelected := True;
    if OwnsStartup then
        WizardSelectTasks('autostart')
    else
        WizardSelectTasks('!autostart');
end;

procedure CurStepChanged(Step: TSetupStep);
begin
    if Step <> ssPostInstall then
        exit;
    if WizardIsTaskSelected('autostart') then
        RegWriteStringValue(HKCU, RunKey, 'AirDesk', RunCommand)
    else if OwnsStartup then
        RegDeleteValue(HKCU, RunKey, 'AirDesk');
end;

function InitializeUninstall: Boolean;
var
    Reason: String;
begin
    Reason := BlockReason;
    Result := Reason = '';
    if not Result then
        SuppressibleMsgBox(Reason, mbError, MB_OK, IDOK);
end;

procedure CurUninstallStepChanged(Step: TUninstallStep);
begin
    if (Step = usUninstall) and OwnsStartup then
        RegDeleteValue(HKCU, RunKey, 'AirDesk');
end;
