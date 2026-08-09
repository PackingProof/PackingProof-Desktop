#ifndef MyAppVersion
  #error MyAppVersion is required
#endif
#ifndef MyAppVersion4
  #error MyAppVersion4 is required
#endif
#ifndef SourceDir
  #error SourceDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif
#ifndef InstallerCompression
  #define InstallerCompression "lzma2/ultra64"
#endif

#define MyAppName "快递打包监控"
#define MyAppExeName "ExpressPackingMonitoring.exe"
#define MyAppId "{{99E9FCE3-C8FE-4D7A-9FA4-BC9CB9186B05}"
#define MyAppUserModelId "PackingProof.ExpressPackingMonitoring"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} v{#MyAppVersion}
AppPublisher=m-RNA
AppPublisherURL=https://github.com/PackingProof/PackingProof-Desktop
AppSupportURL=https://github.com/PackingProof/PackingProof-Desktop/issues
AppUpdatesURL=https://github.com/PackingProof/PackingProof-Desktop/releases
DefaultDirName={localappdata}\Programs\ExpressPackingMonitoring
DefaultGroupName={#MyAppName}
DisableDirPage=no
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=PackingProof_Setup_v{#MyAppVersion}
SetupIconFile=..\ExpressPackingMonitoring\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
LicenseFile=..\LICENSE
Compression={#InstallerCompression}
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter=ExpressPackingMonitoring.exe
RestartApplications=no
VersionInfoVersion={#MyAppVersion4}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoDescription={#MyAppName} 安装程序
VersionInfoProductName={#MyAppName}
#ifdef SignToolName
SignTool={#SignToolName}
SignedUninstaller=yes
#else
SignedUninstaller=no
#endif

[Languages]
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式："

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"; AppUserModelID: "{#MyAppUserModelId}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"; Parameters: "/SILENT /EPMUNINSTALLOPTIONS"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"; AppUserModelID: "{#MyAppUserModelId}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Description: "立即启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[CustomMessages]
chinesesimplified.UninstallOptionsTitle=卸载快递打包监控
chinesesimplified.UninstallOptionsHeading=卸载前，请选择要保留的内容
chinesesimplified.UninstallOptionsDescription=默认不勾选，重新安装后仍可继续使用原来的设置和录像
chinesesimplified.UninstallDeleteSettings=删除设置和临时文件
chinesesimplified.UninstallDeleteSettingsHelp=清除设置、日志和缓存；不会删除录像、录像记录和恢复备份
chinesesimplified.UninstallDeleteRecordings=删除录像和录像记录
chinesesimplified.UninstallDeleteRecordingsHelp=先删除由程序管理的录像，全部成功后再删除录像数据库；删除后无法恢复
chinesesimplified.UninstallStart=开始卸载
chinesesimplified.UninstallCancel=取消
chinesesimplified.UninstallCleanupFailed=部分选中的内容未能安全删除，其他数据已保留%n详情见：%1
english.UninstallOptionsTitle=Uninstall PackingProof
english.UninstallOptionsHeading=Choose what to keep before uninstalling
english.UninstallOptionsDescription=Nothing is selected by default, so reinstalling can restore your settings and recordings
english.UninstallDeleteSettings=Delete settings and temporary files
english.UninstallDeleteSettingsHelp=Removes settings, logs, and cache without deleting recordings, history, or recovery backups
english.UninstallDeleteRecordings=Delete recordings and recording history
english.UninstallDeleteRecordingsHelp=Deletes managed recordings first, then removes the recording database only after all files are removed
english.UninstallStart=Uninstall
english.UninstallCancel=Cancel
english.UninstallCleanupFailed=Some selected content could not be safely removed, so the remaining data was kept%nDetails: %1
chinesesimplified.DirRequiresAdmin=所选文件夹不支持安装。请不要选择 Program Files、Windows、ProgramData 或磁盘根目录这类系统文件夹，换一个普通文件夹（例如 D:\PackingProof 或“文档”文件夹）即可。安装位置必须允许当前用户直接写入，否则以后无法自动更新。
english.DirRequiresAdmin=The selected folder is not supported for installation. Please avoid system folders such as Program Files, Windows, ProgramData, or a drive root, and choose a normal user-writable folder (for example D:\PackingProof or your Documents folder). The install location must remain writable by the current user, otherwise automatic updates will fail.
chinesesimplified.UpgradeDirPrompt=检测到本机已安装旧版本。%n%n是否先删除旧版本的程序文件和启动器，再重新安装？%n你的设置、数据库和录像都会保留（它们保存在系统用户目录中），不会受影响。%n%n选择“是”会先卸载旧版本，再继续安装；选择“否”则直接覆盖安装，旧文件夹中可能残留不再使用的文件。
english.UpgradeDirPrompt=An existing installation was detected.%n%nDo you want to remove the old version's program files and launcher before reinstalling?%nYour settings, database, and recordings will be kept (they are stored in your user profile folder) and will not be affected.%n%nChoose Yes to uninstall the old version before continuing, or No to install directly over the old version (stale files may remain in the old folder).
chinesesimplified.AppRunningBeforeRemove=快递打包监控正在运行。请先关闭正在运行的快递打包监控，然后再继续安装。
english.AppRunningBeforeRemove=PackingProof is currently running. Please close PackingProof before continuing the installation.
chinesesimplified.OldVersionRunningAtPrepare=快递打包监控仍在运行，无法删除旧版本。%n请先关闭快递打包监控，然后重新运行安装程序。%n%n旧版本位置：%1
english.OldVersionRunningAtPrepare=PackingProof is still running, so the previous version cannot be removed.%nPlease close PackingProof and run the installer again.%n%nOld version location: %1
chinesesimplified.OldVersionRemovalFailed=删除旧版本失败，本次安装已中止。%n请关闭正在运行的快递打包监控（如有），然后重新运行安装程序。%n%n旧版本位置：%1
english.OldVersionRemovalFailed=Removing the previous version failed, so this installation has been cancelled.%nPlease close PackingProof if it is running, and run the installer again.%n%nOld version location: %1

[Code]
var
  DeleteLocalData: Boolean;
  DeleteRecordings: Boolean;
  CleanupFailed: Boolean;
  CleanupPlanPath: String;
  CleanupLogPath: String;
  RemoveOldVersion: Boolean;
  UpgradeDirPromptShown: Boolean;

function Quote(const Value: String): String;
begin
  Result := '"' + Value + '"';
end;

function IsSilentUninstall: Boolean;
var
  Index: Integer;
  Argument: String;
begin
  Result := False;
  for Index := 1 to ParamCount do
  begin
    Argument := Uppercase(ParamStr(Index));
    if (Argument = '/SILENT') or (Argument = '/VERYSILENT') then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

function HasCommandLineArgument(const ExpectedArgument: String): Boolean;
var
  Index: Integer;
begin
  Result := False;
  for Index := 1 to ParamCount do
  begin
    if CompareText(ParamStr(Index), ExpectedArgument) = 0 then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

function StartsWithFolder(const Dir, Prefix: String): Boolean;
begin
  Result := (Pos(Prefix, Dir) = 1) and
    ((Length(Dir) = Length(Prefix)) or (Dir[Length(Prefix) + 1] = '\'));
end;

function IsProtectedInstallRoot(const Dir: String): Boolean;
var
  UpperDir: String;
begin
  UpperDir := Uppercase(Dir);
  Result :=
    StartsWithFolder(UpperDir, Uppercase(ExpandConstant('{pf}'))) or
    StartsWithFolder(UpperDir, Uppercase(ExpandConstant('{pf32}'))) or
    StartsWithFolder(UpperDir, Uppercase(ExpandConstant('{win}'))) or
    StartsWithFolder(UpperDir, Uppercase(ExpandConstant('{commonappdata}'))) or
    ((Length(Dir) = 3) and (Dir[2] = ':') and (Dir[3] = '\'));
end;

function IsDirectoryWritable(const Dir: String): Boolean;
var
  TestFile: String;
begin
  Result := False;
  if not DirExists(Dir) then
  begin
    if not ForceDirectories(Dir) then
      Exit;
  end;

  TestFile := AddBackslash(Dir) + 'PackingProofSetupWriteTest.tmp';
  if SaveStringToFile(TestFile, 'x', False) then
  begin
    DeleteFile(TestFile);
    Result := True;
  end;
end;

function GetPreviousInstallDir: String;
var
  InstallDir: String;
begin
  Result := '';
  if RegQueryStringValue(HKCU64,
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{99E9FCE3-C8FE-4D7A-9FA4-BC9CB9186B05}_is1',
    'InstallLocation', InstallDir) then
  begin
    while (Length(InstallDir) > 3) and (InstallDir[Length(InstallDir)] = '\') do
      Delete(InstallDir, Length(InstallDir), 1);
    Result := InstallDir;
  end;
end;

function DirHasInstalledApp(const Dir: String): Boolean;
begin
  Result :=
    FileExists(AddBackslash(Dir) + 'app\ExpressPackingMonitoring.dll') or
    FileExists(AddBackslash(Dir) + 'app\ExpressPackingMonitoring.exe') or
    FileExists(AddBackslash(Dir) + 'ExpressPackingMonitoring.exe');
end;

procedure RemoveRuntimeLeftovers(const Dir: String);
var
  LeftoverDir: String;
begin
  LeftoverDir := AddBackslash(Dir) + 'app\winrt-disabled';
  if DirExists(LeftoverDir) then
  begin
    if DelTree(LeftoverDir, True, True, True) then
      Log('Removed runtime leftover: ' + LeftoverDir)
    else
      Log('Failed to remove runtime leftover: ' + LeftoverDir);
  end;
end;

function RemoveOldInstall(const Dir: String): String;
var
  OldUninstaller: String;
  AppDir: String;
  ResultCode: Integer;
begin
  Result := '';
  if not DirExists(Dir) then
    Exit;
  if (Length(Dir) = 3) and (Dir[2] = ':') and (Dir[3] = '\') then
    Exit;
  if not (DirHasInstalledApp(Dir) or FileExists(AddBackslash(Dir) + 'unins000.exe')) then
    Exit;

  OldUninstaller := AddBackslash(Dir) + 'unins000.exe';
  if FileExists(OldUninstaller) then
  begin
    Log('Removing old install via uninstaller: ' + OldUninstaller);
    if not Exec(OldUninstaller, '/SILENT', Dir, SW_SHOWNORMAL,
        ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
    begin
      Log('Old uninstaller failed: resultCode=' + IntToStr(ResultCode));
      Result := FmtMessage(CustomMessage('OldVersionRemovalFailed'), [Dir]);
    end
    else
    begin
      Log('Old install removed via uninstaller: ' + Dir);
      RemoveRuntimeLeftovers(Dir);
    end;
    Exit;
  end;

  Log('Removing old install files directly: ' + Dir);
  AppDir := AddBackslash(Dir) + 'app';
  if DirExists(AppDir) and not DelTree(AppDir, True, True, True) then
  begin
    Result := FmtMessage(CustomMessage('OldVersionRemovalFailed'), [Dir]);
    Exit;
  end;

  if FileExists(AddBackslash(Dir) + 'ExpressPackingMonitoring.exe') and
     not DeleteFile(AddBackslash(Dir) + 'ExpressPackingMonitoring.exe') then
  begin
    Result := FmtMessage(CustomMessage('OldVersionRemovalFailed'), [Dir]);
    Exit;
  end;

  DeleteFile(AddBackslash(Dir) + 'LICENSE.txt');
  DeleteFile(AddBackslash(Dir) + 'unins000.exe');
  DeleteFile(AddBackslash(Dir) + 'unins000.dat');

  if CompareText(Dir, WizardDirValue) <> 0 then
    RemoveDir(Dir);
  Log('Old install files removed directly: ' + Dir);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  SelectedDir: String;
  PreviousDir: String;
begin
  Result := True;
  if CurPageID <> wpSelectDir then
    Exit;

  SelectedDir := WizardDirValue;
  if IsProtectedInstallRoot(SelectedDir) or (not IsDirectoryWritable(SelectedDir)) then
  begin
    MsgBox(CustomMessage('DirRequiresAdmin'), mbInformation, MB_OK);
    Result := False;
    Exit;
  end;

  if (not WizardSilent) then
  begin
    PreviousDir := GetPreviousInstallDir;
    if (PreviousDir <> '') or DirHasInstalledApp(SelectedDir) then
    begin
      if not UpgradeDirPromptShown then
      begin
        UpgradeDirPromptShown := True;
        if MsgBox(CustomMessage('UpgradeDirPrompt'), mbConfirmation, MB_YESNO) = IDYES then
        begin
          if CheckForMutexes('Local\ExpressPackingMonitoring.Mutex') then
          begin
            UpgradeDirPromptShown := False;
            MsgBox(CustomMessage('AppRunningBeforeRemove'), mbInformation, MB_OK);
            Result := False;
            Exit;
          end;

          RemoveOldVersion := True;
        end
        else
          RemoveOldVersion := False;
      end;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  PreviousDir: String;
begin
  Result := '';
  NeedsRestart := False;
  if not RemoveOldVersion then
    Exit;

  PreviousDir := GetPreviousInstallDir;
  if (PreviousDir <> '') and (CompareText(PreviousDir, WizardDirValue) <> 0) then
  begin
    if CheckForMutexes('Local\ExpressPackingMonitoring.Mutex') then
    begin
      Result := FmtMessage(CustomMessage('OldVersionRunningAtPrepare'), [PreviousDir]);
      Exit;
    end;

    Result := RemoveOldInstall(PreviousDir);
    if Result <> '' then
      Exit;
  end;

  if DirHasInstalledApp(WizardDirValue) then
  begin
    if CheckForMutexes('Local\ExpressPackingMonitoring.Mutex') then
    begin
      Result := FmtMessage(CustomMessage('OldVersionRunningAtPrepare'), [WizardDirValue]);
      Exit;
    end;

    Result := RemoveOldInstall(WizardDirValue);
    if Result <> '' then
      Exit;
  end;
end;

function RunCleanupCommand(const OptionName, PlanPath: String): Boolean;
var
  ResultCode: Integer;
  AppExe: String;
  Parameters: String;
begin
  AppExe := ExpandConstant('{app}\app\ExpressPackingMonitoring.exe');
  Parameters := OptionName;
  if PlanPath <> '' then
    Parameters := Parameters + ' ' + Quote(PlanPath);
  Parameters := Parameters + ' --uninstall-log ' + Quote(CleanupLogPath);
  Result :=
    FileExists(AppExe) and
    Exec(AppExe, Parameters, ExpandConstant('{app}\app'), SW_HIDE,
      ewWaitUntilTerminated, ResultCode) and
    (ResultCode = 0);
end;

function ShowUninstallOptions: Boolean;
var
  OptionsForm: TSetupForm;
  HeadingLabel: TNewStaticText;
  DescriptionLabel: TNewStaticText;
  SettingsCheckBox: TNewCheckBox;
  SettingsHelpLabel: TNewStaticText;
  RecordingsCheckBox: TNewCheckBox;
  RecordingsHelpLabel: TNewStaticText;
  Separator: TBevel;
  StartButton: TNewButton;
  CancelButton: TNewButton;
begin
  OptionsForm := CreateCustomForm(ScaleX(520), ScaleY(300), True, True);
  try
    OptionsForm.Caption := CustomMessage('UninstallOptionsTitle');
    OptionsForm.Position := poScreenCenter;
    OptionsForm.BorderStyle := bsDialog;

    HeadingLabel := TNewStaticText.Create(OptionsForm);
    HeadingLabel.Parent := OptionsForm;
    HeadingLabel.Left := ScaleX(24);
    HeadingLabel.Top := ScaleY(22);
    HeadingLabel.Width := ScaleX(472);
    HeadingLabel.Height := ScaleY(26);
    HeadingLabel.AutoSize := False;
    HeadingLabel.Caption := CustomMessage('UninstallOptionsHeading');
    HeadingLabel.Font.Size := 13;
    HeadingLabel.Font.Style := [fsBold];

    DescriptionLabel := TNewStaticText.Create(OptionsForm);
    DescriptionLabel.Parent := OptionsForm;
    DescriptionLabel.Left := ScaleX(24);
    DescriptionLabel.Top := ScaleY(54);
    DescriptionLabel.Width := ScaleX(472);
    DescriptionLabel.AutoSize := False;
    DescriptionLabel.WordWrap := True;
    DescriptionLabel.Caption := CustomMessage('UninstallOptionsDescription');
    DescriptionLabel.Font.Color := clGray;

    SettingsCheckBox := TNewCheckBox.Create(OptionsForm);
    SettingsCheckBox.Parent := OptionsForm;
    SettingsCheckBox.Left := ScaleX(24);
    SettingsCheckBox.Top := ScaleY(92);
    SettingsCheckBox.Width := ScaleX(472);
    SettingsCheckBox.Caption := CustomMessage('UninstallDeleteSettings');
    SettingsCheckBox.Checked := False;
    SettingsCheckBox.Font.Style := [fsBold];

    SettingsHelpLabel := TNewStaticText.Create(OptionsForm);
    SettingsHelpLabel.Parent := OptionsForm;
    SettingsHelpLabel.Left := ScaleX(48);
    SettingsHelpLabel.Top := ScaleY(116);
    SettingsHelpLabel.Width := ScaleX(448);
    SettingsHelpLabel.Height := ScaleY(34);
    SettingsHelpLabel.AutoSize := False;
    SettingsHelpLabel.WordWrap := True;
    SettingsHelpLabel.Caption := CustomMessage('UninstallDeleteSettingsHelp');
    SettingsHelpLabel.Font.Color := clGray;

    RecordingsCheckBox := TNewCheckBox.Create(OptionsForm);
    RecordingsCheckBox.Parent := OptionsForm;
    RecordingsCheckBox.Left := ScaleX(24);
    RecordingsCheckBox.Top := ScaleY(158);
    RecordingsCheckBox.Width := ScaleX(472);
    RecordingsCheckBox.Caption := CustomMessage('UninstallDeleteRecordings');
    RecordingsCheckBox.Checked := False;
    RecordingsCheckBox.Font.Style := [fsBold];

    RecordingsHelpLabel := TNewStaticText.Create(OptionsForm);
    RecordingsHelpLabel.Parent := OptionsForm;
    RecordingsHelpLabel.Left := ScaleX(48);
    RecordingsHelpLabel.Top := ScaleY(182);
    RecordingsHelpLabel.Width := ScaleX(448);
    RecordingsHelpLabel.Height := ScaleY(36);
    RecordingsHelpLabel.AutoSize := False;
    RecordingsHelpLabel.WordWrap := True;
    RecordingsHelpLabel.Caption := CustomMessage('UninstallDeleteRecordingsHelp');
    RecordingsHelpLabel.Font.Color := clGray;

    Separator := TBevel.Create(OptionsForm);
    Separator.Parent := OptionsForm;
    Separator.Left := 0;
    Separator.Top := ScaleY(238);
    Separator.Width := OptionsForm.ClientWidth;
    Separator.Height := ScaleY(1);
    Separator.Shape := bsTopLine;

    StartButton := TNewButton.Create(OptionsForm);
    StartButton.Parent := OptionsForm;
    StartButton.Width := ScaleX(112);
    StartButton.Height := ScaleY(32);
    StartButton.Left := OptionsForm.ClientWidth - ScaleX(248);
    StartButton.Top := ScaleY(254);
    StartButton.Caption := CustomMessage('UninstallStart');
    StartButton.Default := True;
    StartButton.ModalResult := mrOk;

    CancelButton := TNewButton.Create(OptionsForm);
    CancelButton.Parent := OptionsForm;
    CancelButton.Width := ScaleX(112);
    CancelButton.Height := ScaleY(32);
    CancelButton.Left := OptionsForm.ClientWidth - ScaleX(128);
    CancelButton.Top := ScaleY(254);
    CancelButton.Caption := CustomMessage('UninstallCancel');
    CancelButton.Cancel := True;
    CancelButton.ModalResult := mrCancel;

    Result := OptionsForm.ShowModal = mrOk;
    if Result then
    begin
      DeleteLocalData := SettingsCheckBox.Checked;
      DeleteRecordings := RecordingsCheckBox.Checked;
    end;
  finally
    OptionsForm.Free;
  end;
end;

function InitializeUninstall: Boolean;
begin
  DeleteLocalData := False;
  DeleteRecordings := False;
  CleanupFailed := False;

  if IsSilentUninstall and not HasCommandLineArgument('/EPMUNINSTALLOPTIONS') then
    Result := True
  else
    Result := ShowUninstallOptions;
end;

procedure PrepareRecordingCleanup;
begin
  if not DeleteRecordings then
    Exit;

  if not RunCleanupCommand('--uninstall-plan-recordings', CleanupPlanPath) then
  begin
    CleanupFailed := True;
    Exit;
  end;

  if not RunCleanupCommand('--uninstall-delete-recordings', CleanupPlanPath) then
    CleanupFailed := True;
end;

procedure PrepareLocalDataCleanup;
begin
  if not DeleteLocalData or CleanupFailed then
    Exit;
  if not RunCleanupCommand('--uninstall-delete-local-data', '') then
    CleanupFailed := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  UninstallSubkey: String;
  UninstallCommand: String;
begin
  if CurStep <> ssPostInstall then
    Exit;

  UninstallSubkey := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{99E9FCE3-C8FE-4D7A-9FA4-BC9CB9186B05}_is1';
  UninstallCommand := Quote(ExpandConstant('{uninstallexe}')) + ' /SILENT /EPMUNINSTALLOPTIONS';
  RegWriteStringValue(HKCU64, UninstallSubkey, 'UninstallString', UninstallCommand);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    CleanupPlanPath := ExpandConstant('{tmp}\ExpressPackingMonitoring-uninstall-recordings.json');
    CleanupLogPath := ExpandConstant('{tmp}\ExpressPackingMonitoring-Uninstall.log');
    DeleteFile(CleanupPlanPath);
    CleanupFailed := False;
    PrepareRecordingCleanup;
    PrepareLocalDataCleanup;
    if CleanupFailed then
      MsgBox(
        FmtMessage(CustomMessage('UninstallCleanupFailed'), [CleanupLogPath]),
        mbError, MB_OK);
  end
  else if CurUninstallStep = usPostUninstall then
    DeleteFile(CleanupPlanPath);
end;
