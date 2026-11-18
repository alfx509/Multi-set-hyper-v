; MultiSeat all-in-one installer (Inno Setup 6).
; Build: .\build.ps1 -Installer   (or the GitHub "build" workflow)
; Contains: MultiSeat app, player agent, Interception keyboard/mouse driver, Windows setup script.
; The .NET runtime is built in (self-contained), so nothing else needs to be installed.

#ifndef AppVersion
  #define AppVersion "0.3.0"
#endif
#define Root ".."

[Setup]
AppId={{6F1D2C1B-6B6E-4C55-9C1A-6D2B4E0B7A31}
AppName=MultiSeat
AppVersion={#AppVersion}
AppVerName=MultiSeat {#AppVersion}
AppPublisher=MultiSeat contributors
AppPublisherURL=https://github.com/alfx509/Multi-set-hyper-v
DefaultDirName={autopf}\MultiSeat
DisableDirPage=yes
DefaultGroupName=MultiSeat
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#Root}\out\installer
OutputBaseFilename=MultiSeat-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
LicenseFile={#Root}\LICENSE
InfoBeforeFile=info-before.txt
UninstallDisplayIcon={app}\MultiSeat.exe
UninstallDisplayName=MultiSeat
CloseApplications=force
; The keyboard/mouse driver becomes active after a restart.
AlwaysRestart=yes

[Tasks]
Name: desktopicon; Description: "Create a desktop icon"; GroupDescription: "Shortcuts:"
Name: sessionhost; Description: "Allow 4 to 8 players (installs Windows Server's Remote Desktop Session Host role; free for 120 days, then needs RDS licenses)"; GroupDescription: "Windows Server:"; Flags: unchecked; Check: IsServer

[Files]
Source: "{#Root}\out\MultiSeat\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#Root}\out\SeatAgent\*"; DestDir: "{app}\Agent"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#Root}\third_party\Interception\*"; DestDir: "{app}\interception"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#Root}\third_party\Interception\library\x64\interception.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Root}\setup\Configure-MultiSeat.ps1"; DestDir: "{app}\setup"; Flags: ignoreversion
Source: "{#Root}\docs\*"; DestDir: "{app}\docs"; Flags: ignoreversion recursesubdirs
Source: "{#Root}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Root}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\MultiSeat"; Filename: "{app}\MultiSeat.exe"
Name: "{autoprograms}\MultiSeat guide"; Filename: "{app}\docs\guide.md"
Name: "{autodesktop}\MultiSeat"; Filename: "{app}\MultiSeat.exe"; Tasks: desktopicon

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\setup\Configure-MultiSeat.ps1"" -AppDir ""{app}"" -Uninstall"; \
  Flags: runhidden waituntilterminated; RunOnceId: "MultiSeatConfigureUninstall"

[Code]
function IsServer: Boolean;
var
  Version: TWindowsVersion;
begin
  GetWindowsVersionEx(Version);
  Result := Version.ProductType <> VER_NT_WORKSTATION;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  { Close MultiSeat and the agent so their files can be replaced. }
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM MultiSeat.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM SeatAgent.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Params: String;
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    WizardForm.StatusLabel.Caption := 'Setting up Windows for MultiSeat (driver, graphics, sound)...';
    Params := '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\setup\Configure-MultiSeat.ps1') +
              '" -AppDir "' + ExpandConstant('{app}') + '"';
    if WizardIsTaskSelected('sessionhost') then
      Params := Params + ' -InstallSessionHost';

    if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Params, '', SW_HIDE,
                ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
      MsgBox('MultiSeat was installed, but setting up Windows did not finish.' + #13#10 +
             'Details: C:\ProgramData\MultiSeat\setup.log' + #13#10#13#10 +
             'You can run this setup again to retry.', mbError, MB_OK);
  end;
end;
