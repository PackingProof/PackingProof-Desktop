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
AppPublisherURL=https://github.com/m-RNA/ExpressPackingMonitoring
AppSupportURL=https://github.com/m-RNA/ExpressPackingMonitoring/issues
AppUpdatesURL=https://github.com/m-RNA/ExpressPackingMonitoring/releases
DefaultDirName={localappdata}\Programs\ExpressPackingMonitoring
DefaultGroupName={#MyAppName}
DisableDirPage=yes
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

[Code]
var
  DeleteLocalData: Boolean;
  DeleteRecordings: Boolean;
  CleanupFailed: Boolean;
  CleanupPlanPath: String;
  CleanupLogPath: String;

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
