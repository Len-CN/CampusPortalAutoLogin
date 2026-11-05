#define MyAppName "校园网自动登录"
#define MyAppExeName "CampusPortalAutoLogin.exe"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.5"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

[Setup]
AppId={{BFF47D16-8907-49BD-8FEA-F57608345EDA}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=CampusPortalAutoLogin
DefaultDirName={autopf}\CampusPortalAutoLogin
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=CampusPortalAutoLogin-{#MyAppVersion}-win-x64-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
CloseApplications=yes
CloseApplicationsFilter={#MyAppExeName}
RestartApplications=no
UninstallDisplayIcon={app}\{#MyAppExeName}
VersionInfoVersion={#MyAppVersion}
VersionInfoDescription={#MyAppName} 安装程序

[Languages]
Name: "chinesesimp"; MessagesFile: "ChineseSimplified.isl"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\docs\使用说明.md"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "..\docs\配置说明.md"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "..\docs\隐私说明.md"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "..\docs\已知限制.md"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "maintenance.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
var
  DeleteConfigSelected: Boolean;
  DeleteLogsSelected: Boolean;

function PowerShellQuote(const Value: String): String;
begin
  Result := Value;
  StringChangeEx(Result, '''', '''''', True);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  AppExe, Parameters: String;
  ResultCode: Integer;
begin
  Result := '';
  AppExe := ExpandConstant('{app}\{#MyAppExeName}');
  if not FileExists(AppExe) then
    Exit;

  Parameters := '-NoProfile -NonInteractive -Command ' +
    '"$p=Start-Process -FilePath ''' + PowerShellQuote(AppExe) +
    ''' -ArgumentList ''--shutdown'' -WindowStyle Hidden -PassThru; ' +
    'if(-not $p.WaitForExit(15000)){exit 124}; exit $p.ExitCode"';

  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Log('无法启动旧版本退出请求；将交由 Restart Manager 处理。')
  else if ResultCode = 124 then
    Log('旧版本在 15 秒内未响应退出请求；将交由 Restart Manager 提示并处理。')
  else if ResultCode <> 0 then
    Log(Format('旧版本退出请求返回代码 %d；将交由 Restart Manager 处理。', [ResultCode]));
end;

function RunMaintenance(const Mode, DataPath: String): Boolean;
var
  ScriptPath, AppExe, Parameters: String;
  ResultCode: Integer;
begin
  ScriptPath := ExpandConstant('{app}\maintenance.ps1');
  AppExe := ExpandConstant('{app}\{#MyAppExeName}');
  if not FileExists(ScriptPath) then
  begin
    Log('卸载维护脚本不存在：' + ScriptPath);
    Result := False;
    Exit;
  end;
  Parameters := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ScriptPath +
    '" -Mode ' + Mode + ' -AppPath "' + AppExe + '"';
  if DataPath <> '' then
    Parameters := Parameters + ' -DataPath "' + DataPath + '"';
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Log('无法启动卸载维护脚本，模式：' + Mode);
    Result := False;
  end
  else
  begin
    Result := ResultCode = 0;
    if not Result then
      Log(Format('卸载维护脚本失败，模式 %s，退出码 %d。', [Mode, ResultCode]));
  end;
end;

function InitializeUninstall(): Boolean;
var
  DataPath: String;
begin
  if not RunMaintenance('Shutdown', '') then
  begin
    MsgBox('无法安全关闭本安装目录中的驻留程序。请从托盘退出程序后重试；' +
      '为避免卸载后残留被占用文件，本次卸载已取消。', mbError, MB_OK);
    Result := False;
    Exit;
  end;
  DataPath := ExpandConstant('{localappdata}\CampusPortalAutoLogin');
  DeleteConfigSelected := SuppressibleMsgBox(
    '是否删除当前卸载账户的配置和加密凭据？' + #13#10 +
    DataPath + #13#10 +
    '选择“否”将保留 config.json 和 secrets.dat，其他 Windows 用户的数据不会处理。',
    mbConfirmation, MB_YESNO, IDNO) = IDYES;
  DeleteLogsSelected := SuppressibleMsgBox(
    '是否删除当前卸载账户的日志？' + #13#10 +
    DataPath + '\logs' + #13#10 +
    '选择“否”将保留 logs 目录，其他 Windows 用户的数据不会处理。',
    mbConfirmation, MB_YESNO, IDNO) = IDYES;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataPath: String;
begin
  if CurUninstallStep <> usUninstall then
    Exit;
  DataPath := ExpandConstant('{localappdata}\CampusPortalAutoLogin');
  if DeleteConfigSelected and not RunMaintenance('CleanupConfig', DataPath) then
    MsgBox('配置或加密凭据未能完整删除，请手动检查：' + #13#10 +
      DataPath, mbError, MB_OK);
  if DeleteLogsSelected and not RunMaintenance('CleanupLogs', DataPath) then
    MsgBox('日志未能完整删除，请手动检查：' + #13#10 +
      DataPath + '\logs', mbError, MB_OK);
  if not RunMaintenance('CleanupTasks', '') then
  begin
    MsgBox('无法确认计划任务已安全删除。为避免留下指向已删除程序的任务，' +
      '本次卸载已中止。', mbError, MB_OK);
    Abort;
  end;
end;
