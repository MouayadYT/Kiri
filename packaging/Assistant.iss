; Inno Setup definition for the Assistant.
; Build-Installer.ps1 supplies these three preprocessor values:
;   AppVersion  - the version stamped into the package
;   PackageDir  - the completed Assistant-<version>-win-x64 package folder
;   OutputDir   - where the compiled Setup.exe is written

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PackageDir
  #define PackageDir "."
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif

[Setup]
AppId={{B9B8D7F7-0C2F-4C32-9A27-4A8A7A6D4F8C}
AppName=Assistant
AppVersion={#AppVersion}
AppVerName=Assistant {#AppVersion}
AppPublisher=Assistant
DefaultDirName={localappdata}\Programs\Assistant
DefaultGroupName=Assistant
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
Uninstallable=yes
UninstallDisplayName=Assistant
UninstallDisplayIcon={app}\Assistant.UI.exe
OutputDir={#OutputDir}
OutputBaseFilename=Assistant-{#AppVersion}-win-x64-Setup
SetupIconFile={#PackageDir}\app\Assistant.ico
WizardStyle=modern
WizardImageFile=branding\wizard-image.bmp
WizardSmallImageFile=branding\wizard-small.bmp
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
VersionInfoVersion={#AppVersion}
VersionInfoDescription=Assistant setup
VersionInfoProductName=Assistant
VersionInfoProductVersion={#AppVersion}
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "launch"; Description: "Launch Assistant when setup is complete"; Flags: unchecked
Name: "explorercontextmenu"; Description: "Add Ask Assistant to File Explorer's right-click menu"; Flags: unchecked

[Files]
; Extract the prerequisite on demand before copying the per-user application.
Source: "{#PackageDir}\app\tools\vc_redist.x64.exe"; Flags: dontcopy
; The package is carried inside the Inno installer and unpacked into its temporary working
; directory. Install-Assistant.ps1 then performs the verified per-user install into {app}.
Source: "{#PackageDir}\*"; DestDir: "{tmp}\AssistantPackage"; Flags: recursesubdirs createallsubdirs ignoreversion

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
var
  DeleteUserData: Boolean;

function Quote(const Value: string): string;
begin
  Result := '"' + Value + '"';
end;

function HasNativeRuntime(): Boolean;
var
  VersionMS, VersionLS: Cardinal;
begin
  Result := GetVersionNumbers(ExpandConstant('{sys}\msvcp140.dll'), VersionMS, VersionLS);
  if Result then
    Result := ((VersionMS shr 16) > 14) or (((VersionMS shr 16) = 14) and ((VersionMS and $FFFF) >= 44));
  Result := Result and FileExists(ExpandConstant('{sys}\vcruntime140.dll')) and FileExists(ExpandConstant('{sys}\vcruntime140_1.dll'));
end;

function PrepareToInstall(var NeedsRestart: Boolean): string;
var
  ResultCode: Integer;
begin
  Result := '';
  if HasNativeRuntime() then exit;
  ExtractTemporaryFile('vc_redist.x64.exe');
  WizardForm.StatusLabel.Caption := 'Preparing the native speech runtime...';
  if not ShellExec('runas', ExpandConstant('{tmp}\vc_redist.x64.exe'), '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := 'The Microsoft Visual C++ runtime could not be installed. Allow its Windows permission prompt, then run setup again.'
  else if (ResultCode <> 0) and (ResultCode <> 1638) and (ResultCode <> 3010) then
    Result := Format('The Microsoft Visual C++ runtime installation failed (code %d).', [ResultCode])
  else
    NeedsRestart := ResultCode = 3010;
end;

function HasSetupParam(const Name: string): Boolean;
var
  I: Integer;
  Value: string;
begin
  Result := False;
  for I := 1 to ParamCount do
  begin
    Value := UpperCase(ParamStr(I));
    if (Value = UpperCase(Name)) or (Value = UpperCase(Name) + '=1') then
    begin
      Result := True;
      exit;
    end;
  end;
end;

function InitializeUninstall(): Boolean;
begin
  DeleteUserData := HasSetupParam('/REMOVEUSERDATA');
  if not UninstallSilent then
    DeleteUserData := MsgBox(
      'Would you also like to delete all downloaded AI and voice models and your user configuration?' + #13#10 + #13#10 +
      'This includes conversation history, connected apps, cached downloads, and saved API keys.' + #13#10 + #13#10 +
      'Yes deletes this data. No keeps it for a future installation.',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
  Result := True;
end;

function GetUninstallCleanupParameters(): string;
begin
  Result := '-NoProfile -ExecutionPolicy Bypass -File ' + Quote(ExpandConstant('{app}\tools\Uninstall-Assistant.ps1')) +
    ' -InstallDirectory ' + Quote(ExpandConstant('{app}')) +
    ' -DataDirectory ' + Quote(ExpandConstant('{localappdata}\Assistant')) +
    ' -PreserveInstallDirectory -Quiet -Force';
  if DeleteUserData then
    Result := Result + ' -RemoveUserData';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep <> usUninstall then
    exit;
  { Build the cleanup arguments here, after the user answers. UninstallRun constants are saved at installation time.
    The script is started from the Windows folder and not from the install folder: a process holds its working folder, and a compiler it
    starts from the install folder finds the app's own .NET assemblies before Windows' own. }
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      GetUninstallCleanupParameters(), ExpandConstant('{sys}'), SW_HIDE,
      ewWaitUntilTerminated, ResultCode) then
    ResultCode := 1;
  { 3: the data folder is out of the way, and the few files that were still in use are deleted the next time the user signs in.
    4: everything was removed except the files of the data folder that another program still has open. }
  if (ResultCode = 3) and not UninstallSilent then
    MsgBox('A few of the Assistant''s files were still in use. They will be deleted the next time you sign in to Windows.',
      mbInformation, MB_OK)
  else if (ResultCode = 4) and not UninstallSilent then
    MsgBox('The Assistant was removed, but another program still has some of its data files open, so those could not be deleted. ' +
      'Close other programs and delete this folder to remove the rest:' + #13#10 + #13#10 +
      ExpandConstant('{localappdata}\Assistant'), mbInformation, MB_OK)
  else if (ResultCode <> 0) and (ResultCode <> 3) and (ResultCode <> 4) then
    MsgBox('Some integrations or user data could not be removed. Your remaining data is in ' +
      ExpandConstant('{localappdata}\Assistant') + '.' + #13#10 + #13#10 +
      'What went wrong is written in ' + ExpandConstant('{%TEMP}\Assistant-uninstall.log') + '.', mbError, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  Parameters: string;
  PowerShell: string;
  Script: string;
begin
  { After Finish: the Assistant, and with it the first-run setup, opens only once the installer's last page has been closed. It is never started
    while the installer is still on screen. (A silent install has no last page; it starts the Assistant here too, when that was asked for.) }
  if CurStep = ssDone then
  begin
    if WizardIsTaskSelected('launch') then
      Exec(ExpandConstant('{app}\Assistant.UI.exe'), '', ExpandConstant('{app}'), SW_SHOWNORMAL, ewNoWait, ResultCode);
    exit;
  end;

  if CurStep <> ssPostInstall then
    exit;

  PowerShell := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  Script := ExpandConstant('{tmp}\AssistantPackage\Install-Assistant.ps1');
  Parameters := '-NoProfile -ExecutionPolicy Bypass -File ' + Quote(Script) +
    ' -InstallDirectory ' + Quote(ExpandConstant('{app}'));
  if HasSetupParam('/SKIPINTEGRATIONS') then
    Parameters := Parameters + ' -SkipIntegrations';
  if WizardIsTaskSelected('explorercontextmenu') then
    Parameters := Parameters + ' -EnableExplorerContextMenu';
  if HasSetupParam('/SKIPSHORTCUT') then
    Parameters := Parameters + ' -SkipShortcut';
  Parameters := Parameters + ' -SkipUninstallEntry';

  if (not Exec(PowerShell, Parameters, ExpandConstant('{tmp}\AssistantPackage'), SW_SHOW,
      ewWaitUntilTerminated, ResultCode)) or (ResultCode <> 0) then
  begin
    MsgBox('Assistant installation failed. The package was not installed.', mbError, MB_OK);
    Abort;
  end;
end;
