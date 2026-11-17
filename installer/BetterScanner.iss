; Better Scanner installer (Inno Setup 6). Built by build.bat into publish\release\BetterScannerSetup.exe.
;
; One setup file for every office PC:
;   Windows 10/11 64-bit  → the self-contained build (publish\BetterScanner.exe)
;   anything older/32-bit → the .NET Framework 4.8 build (publish\Windows7\)
;
; Installs per user into %LocalAppData%\Programs\Better Scanner, so it needs no admin rights
; and the app's one-click "Update now" can keep swapping its own exe in place.

#define AppExe "BetterScanner.exe"
; "1.0.5.0" → "1.0.5"
#define AppVersion RemoveFileExt(GetVersionNumbersString("..\publish\" + AppExe))

[Setup]
; Must match UninstallKey in Updates\SelfUpdater.cs. Never change it, or installs stop upgrading each other.
AppId={{8F3C2A71-5B9E-4D6A-9C1F-2E7B4A6D3F10}
AppName=Better Scanner
AppVersion={#AppVersion}
AppVerName=Better Scanner {#AppVersion}
AppPublisher=Emil Paladin
AppPublisherURL=https://github.com/Lime1-hue/better-scanner-app
AppSupportURL=https://github.com/Lime1-hue/better-scanner-app
AppCopyright=© 2026 Emil Paladin
VersionInfoVersion={#AppVersion}
PrivilegesRequired=lowest
DefaultDirName={autopf}\Better Scanner
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
UsePreviousTasks=yes
MinVersion=6.1sp1
SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName=Better Scanner
WizardStyle=modern
OutputDir=..\publish\release
OutputBaseFilename=BetterScannerSetup
Compression=lzma2/max
SolidCompression=yes
; Closes a running copy (it frees its printer lock as it exits) and lets setup replace the exe.
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "startup"; Description: "Start Better Scanner when I sign in to Windows (it waits in the tray)"
Name: "desktopicon"; Description: "Put a shortcut on the desktop"

[Files]
Source: "..\publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion; Check: UseModernBuild
Source: "..\publish\Windows7\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion; Check: not UseModernBuild
Source: "..\publish\Windows7\{#AppExe}.config"; DestDir: "{app}"; Flags: ignoreversion; Check: not UseModernBuild

[InstallDelete]
; Switching to the modern build: the old .NET Framework config isn't needed.
Type: files; Name: "{app}\{#AppExe}.config"; Check: UseModernBuild
Type: files; Name: "{app}\{#AppExe}.old"
Type: files; Name: "{app}\{#AppExe}.new"

[Icons]
Name: "{autoprograms}\Better Scanner"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\Better Scanner"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Better Scanner"; ValueData: """{app}\{#AppExe}"" --tray"; Flags: uninsdeletevalue; Tasks: startup
; Unticked on a reinstall: take it back out.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "Better Scanner"; Flags: deletevalue dontcreatekey; Check: not WizardIsTaskSelected('startup')

[Run]
Filename: "{app}\{#AppExe}"; Description: "Open Better Scanner now"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Left behind by one-click updates.
Type: files; Name: "{app}\{#AppExe}.old"
Type: files; Name: "{app}\{#AppExe}.new"
Type: files; Name: "{app}\{#AppExe}.download"
Type: files; Name: "{app}\{#AppExe}.config.new"
Type: dirifempty; Name: "{app}"

[Code]
const
  AppMutex = 'BetterScanner.SingleInstance';
  NetFx48Release = 528040;

{ Windows 10/11 on 64-bit gets the self-contained build; everything else the .NET Framework 4.8 one. }
function UseModernBuild: Boolean;
begin
  Result := IsWin64 and (GetWindowsVersion >= $0A000000);
end;

function HasNetFx48: Boolean;
var
  Release: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release)
    and (Release >= NetFx48Release);
end;

function InitializeSetup: Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if (not UseModernBuild) and (not HasNetFx48) then
  begin
    if SuppressibleMsgBox('This PC needs Microsoft .NET Framework 4.8 to run Better Scanner.' + #13#10#13#10 +
      'Click OK to open the download page. Install it, restart the PC, then run this setup again.',
      mbInformation, MB_OKCANCEL, IDCANCEL) = IDOK then
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet-framework/net48', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    Result := False;
  end;
end;

{ The uninstaller can't close the tray app for you, so ask the user to exit it first. }
function InitializeUninstall: Boolean;
begin
  Result := True;
  while CheckForMutexes(AppMutex) do
  begin
    if SuppressibleMsgBox('Better Scanner is still running.' + #13#10#13#10 +
      'Right-click its icon near the clock (you may need to click ^ first) and choose Exit, then click Retry.',
      mbError, MB_RETRYCANCEL, IDCANCEL) <> IDRETRY then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  SettingsDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    SettingsDir := ExpandConstant('{userappdata}\BetterScanner');
    if DirExists(SettingsDir) and
      (SuppressibleMsgBox('Also delete your Better Scanner settings (printer, scan folder, options)?' + #13#10#13#10 +
        'Your scanned PDFs are not touched either way.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES) then
      DelTree(SettingsDir, True, True, True);
  end;
end;
