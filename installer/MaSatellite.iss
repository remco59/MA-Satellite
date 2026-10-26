; Inno Setup script for the ma-satellite installer (.exe).
; Build with scripts/Build-Installer.ps1, which publishes the unpackaged app first.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\win-x64"
#endif
#define AppExe "MaSatellite.App.exe"

[Setup]
; Never change AppId: it is how upgrades and uninstall find this installation.
AppId={{ECC1EC11-34E5-469E-BBF2-CC855C95F790}
AppName=ma-satellite
AppVersion={#AppVersion}
AppPublisher=Remco
DefaultDirName={autopf}\ma-satellite
DefaultGroupName=ma-satellite
DisableProgramGroupPage=yes
; Installs for the current user without admin rights by default; "install for all users" and the firewall rule need elevation.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\artifacts\dist
OutputBaseFilename=ma-satellite-setup-{#AppVersion}
SetupIconFile=..\src\MaSatellite.App\Assets\tray.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; The app lives in the tray without a window, so Restart Manager cannot ask it to close; PrepareToInstall stops it instead.
CloseApplications=no

[Tasks]
Name: "firewall"; Description: "Allow Music Assistant to connect in (TCP 8928, private networks only)"; Check: IsAdminInstallMode

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\ma-satellite"; Filename: "{app}\{#AppExe}"

[Registry]
; The app writes this value itself (Start with Windows); remove it again on uninstall.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "MaSatellite"; Flags: dontcreatekey uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"; ValueName: "MaSatellite"; Flags: dontcreatekey uninsdeletevalue

[Run]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ma-satellite"""; Flags: runhidden; Tasks: firewall
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""ma-satellite"" dir=in action=allow program=""{app}\{#AppExe}"" protocol=TCP localport=8928 profile=private"; Flags: runhidden; Tasks: firewall
Filename: "{app}\{#AppExe}"; Description: "Start ma-satellite"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ma-satellite"""; Flags: runhidden; RunOnceId: "RemoveFirewallRule"

[Code]
procedure StopRunningApp;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningApp;
  Result := '';
end;

function InitializeUninstall: Boolean;
begin
  StopRunningApp;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if (CurUninstallStep = usPostUninstall) and (not UninstallSilent) then
  begin
    DataDir := ExpandConstant('{localappdata}\MaSatellite');
    if DirExists(DataDir) and
       (MsgBox('Also delete ma-satellite''s settings, saved pairing keys and logs?'#13#10 + DataDir, mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
      DelTree(DataDir, True, True, True);
  end;
end;
