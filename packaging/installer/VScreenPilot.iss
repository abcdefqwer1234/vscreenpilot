; VScreenPilot per-user installer (Inno Setup 6.3+).
; Build with: powershell -File scripts/build-installer.ps1 -Version 0.2.0
; The installer copies the self-contained exe and docs into
; %LOCALAPPDATA%\Programs\VScreenPilot and optionally adds that folder to the
; current user's PATH. It never requires administrator rights.

#ifndef MyAppVersion
  #define MyAppVersion "0.2.0"
#endif

#ifndef SourceDir
  #define SourceDir "..\..\artifacts\release\v0.2.0\server"
#endif

#ifndef RepoRoot
  #define RepoRoot "..\.."
#endif

#ifndef OutputDir
  #define OutputDir "..\..\artifacts\release"
#endif

#define MyAppName "VScreenPilot"
#define MyAppPublisher "VScreenPilot"
#define MyAppURL "https://github.com/abcdefqwer1234/vscreenpilot"
#define MyAppExeName "VScreenPilot.exe"

[Setup]
AppId={{8E2F1C64-3B7A-4E55-9E1B-2F0C6A9D4B71}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={localappdata}\Programs\VScreenPilot
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile={#RepoRoot}\LICENSE
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=VScreenPilot-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
ChangesEnvironment=yes
SetupLogging=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#MyAppVersion}
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Setup
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "modifypath"; Description: "Add VScreenPilot to the current user PATH"; GroupDescription: "Optional:"; Flags: checkedonce

[Files]
Source: "{#SourceDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoRoot}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoRoot}\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoRoot}\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Code]
const
  EnvironmentKey = 'Environment';
  WM_SETTINGCHANGE = $001A;
  SMTO_ABORTIFHUNG = $0002;

function SendMessageTimeout(
  hWnd: HWND; Msg: UINT; wParam: Longint; lParam: Longint;
  fuFlags: UINT; uTimeout: UINT; var lpdwResult: DWORD): Longint;
  external 'SendMessageTimeoutW@user32.dll stdcall';

procedure NotifyEnvironmentChanged;
var
  ResultCode: DWORD;
begin
  // lParam=0 表示设置变更；Explorer 会重新读取用户环境变量。
  SendMessageTimeout(
    HWND_BROADCAST, WM_SETTINGCHANGE, 0, 0,
    SMTO_ABORTIFHUNG, 5000, ResultCode);
end;

function PathContains(const Path, Dir: string): Boolean;
begin
  Result := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Path) + ';') > 0;
end;

procedure AddToPath(const Dir: string);
var
  Path: string;
begin
  if not RegQueryStringValue(HKEY_CURRENT_USER, EnvironmentKey, 'Path', Path) then
    Path := '';

  if PathContains(Path, Dir) then
    Exit;

  if Path = '' then
    Path := Dir
  else
    Path := Path + ';' + Dir;

  RegWriteExpandStringValue(HKEY_CURRENT_USER, EnvironmentKey, 'Path', Path);
  NotifyEnvironmentChanged;
end;

procedure RemoveFromPath(const Dir: string);
var
  Path, NewPath, Part: string;
  P: Integer;
begin
  if not RegQueryStringValue(HKEY_CURRENT_USER, EnvironmentKey, 'Path', Path) then
    Exit;

  NewPath := '';
  while Path <> '' do
  begin
    P := Pos(';', Path);
    if P = 0 then
    begin
      Part := Path;
      Path := '';
    end
    else
    begin
      Part := Copy(Path, 1, P - 1);
      Delete(Path, 1, P);
    end;

    if (Part <> '') and (CompareText(Part, Dir) <> 0) then
    begin
      if NewPath <> '' then
        NewPath := NewPath + ';';
      NewPath := NewPath + Part;
    end;
  end;

  RegWriteExpandStringValue(HKEY_CURRENT_USER, EnvironmentKey, 'Path', NewPath);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('modifypath') then
    AddToPath(ExpandConstant('{app}'));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RemoveFromPath(ExpandConstant('{app}'));
    NotifyEnvironmentChanged;
  end;
end;
