; UEFormat模型查看器 · Alpha 定时过期版 安装脚本（Inno Setup 7）
; 与正式版完全独立：AppId / 安装目录 / 开始菜单项 / 输出文件名都不同，可与正式版同时安装。
; 该版本启动时通过 NTP 服务器校验网络时间，2026-09-24（北京时间）正式过期。
; 编译：ISCC.exe DeltaModelViewer_Alpha.iss
#define MyAppName "UEFormat模型查看器 Alpha测试版"
#define MyAppVersion "Alpha-20260924"
#define MyVersionInfoVersion "1.0.0.0"
#define MyAppPublisher "河豚潘PufferPan"
#define MyAppExeName "DeltaModelViewer.exe"
#define MyExpiryDate "2026年9月24日"

[Setup]
; Alpha 专用 AppId（与正式版 {{7E4A9C31-5D2F-4B8E-9A6D-C3F1B2E8A705}} 不同，互不覆盖）
AppId={{AD4CC5AC-9B5B-4E29-994A-1A1D002F8C02}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} ({#MyAppVersion})
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\UEFormatModelViewer_Alpha
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=.
OutputBaseFilename=UEFormatModelViewer_Alpha_Setup
LicenseFile=license_alpha.txt
SetupIconFile=app.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=arm64 x64compatible
ArchitecturesInstallIn64BitMode=arm64 x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
VersionInfoVersion={#MyVersionInfoVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=UEFormat模型查看器 Alpha测试版（2026-09-24 过期）

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Alpha 版应用程序（自包含 .NET 8 + WindowsAppSDK，由 dotnet publish -c Alpha 生成）
; 按系统架构选择安装（x64 / ARM64）
Source: "AlphaApp\x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: not IsArm64
Source: "AlphaApp\arm64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: IsArm64
; 模型与贴图数据包（安装后解压到安装目录 Data 文件夹）
Source: "E:\deltamodel\DeltaForce\Content\Models\model.zip"; DestDir: "{tmp}"; Flags: deleteafterinstall
Source: "F:\FModel-dec-2025\FModel\bin\Release\net8.0-windows\win-x64\Output\Exports\DeltaForce\Content\GenTexDirHD\Game\Models\texture.zip"; DestDir: "{tmp}"; Flags: deleteafterinstall
; 解压脚本
Source: "extract_data.ps1"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[Code]
// JSON 字符串转义：反斜杠加倍（Windows 路径保持反斜杠，JSON 语法正确）
function JsonEsc(const S: string): string;
var
  i: Integer;
begin
  Result := '';
  for i := 1 to Length(S) do
  begin
    if S[i] = '\' then
      Result := Result + '\' + '\'
    else
      Result := Result + S[i];
  end;
end;

// Alpha 版使用独立数据目录 %LOCALAPPDATA%\DeltaModelViewer-Alpha，与正式版互不干扰
function AlphaDataDir(): string;
begin
  Result := GetEnv('LOCALAPPDATA') + '\DeltaModelViewer-Alpha';
end;

// 从旧配置里取出主题选择（"System" / "Light" / "Dark"），取不到就返回空串
// 说明：Inno 7 的 LoadStringFromFile 出参是 AnsiString，所以这里按行读（LoadStringsFromFile）再找 Theme
function ExtractThemeFromFile(const CfgPath: string): string;
var
  Lines: TArrayOfString;
  i, p, q: Integer;
  s: string;
begin
  Result := '';
  if not LoadStringsFromFile(CfgPath, Lines) then Exit;
  for i := 0 to GetArrayLength(Lines) - 1 do
  begin
    s := Lines[i];
    p := Pos('"Theme"', s);
    if p = 0 then Continue;
    s := Copy(s, p + 7, Length(s));
    p := Pos(':', s);
    if p = 0 then Continue;
    s := Copy(s, p + 1, Length(s));
    p := Pos('"', s);
    if p = 0 then Continue;
    s := Copy(s, p + 1, Length(s));
    q := Pos('"', s);
    if q = 0 then Continue;
    Result := Copy(s, 1, q - 1);
    Exit;
  end;
end;

procedure WriteDataConfig();
var
  AppRoot, CfgDir, CfgPath, Json, Theme: string;
begin
  AppRoot := JsonEsc(ExpandConstant('{app}'));
  CfgDir := AlphaDataDir();
  CfgPath := CfgDir + '\config.json';
  ForceDirectories(CfgDir);
  // 重装时保留用户选的主题（浅色 / 深色 / 跟随系统），只改写数据目录
  Theme := ExtractThemeFromFile(CfgPath);
  if Theme = '' then
    Theme := 'System';
  // 备份旧配置后自动替换为安装目录下的数据路径
  if FileExists(CfgPath) then
    FileCopy(CfgPath, CfgPath + '.bak', False);
  Json := '{"ModelsRoot": "' + AppRoot + '\Data\Models\Props\' +
          '", "TexturesRoot": "' + AppRoot + '\Data\Textures\Props\' +
          '", "BackgroundPath": null, "Theme": "' + Theme + '"}';
  if not SaveStringToFile(CfgPath, Json, False) then
    MsgBox('配置文件写入失败: ' + CfgPath, mbError, MB_OK);
end;

procedure ExtractData();
var
  Tmp, Ps1, ModelZip, TextureZip, PsArgs: string;
  ResultCode: Integer;
begin
  Tmp := ExpandConstant('{tmp}');
  Ps1 := Tmp + '\extract_data.ps1';
  ModelZip := Tmp + '\model.zip';
  TextureZip := Tmp + '\texture.zip';
  PsArgs := '-NoProfile -ExecutionPolicy Bypass -File "' + Ps1 + '" -ModelZip "' + ModelZip + '" -TextureZip "' + TextureZip + '" -DataRoot "' + ExpandConstant('{app}') + '\Data"';
  WizardForm.StatusLabel.Caption := '正在解压模型与贴图数据到安装目录（可能需要几分钟）...';
  if Exec('powershell.exe', PsArgs, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    if ResultCode <> 0 then
      MsgBox('数据解压失败（错误码 ' + IntToStr(ResultCode) + '）。软件已安装，模型数据需手动解压。', mbError, MB_OK);
  end
  else
    MsgBox('无法启动 PowerShell 解压数据。软件已安装，模型数据需手动解压。', mbError, MB_OK);
end;

// 安装前告知限时 Alpha 的时间校验规则
function InitializeSetup(): Boolean;
var
  Answer: Integer;
begin
  Result := True;
  Answer := MsgBox('本安装包为 Alpha 测试版本，将于 {#MyExpiryDate} 正式过期。' + #13#10 + #13#10 +
    '启动时会通过公网 NTP 服务器获取网络时间来校验有效期：' + #13#10 +
    '· 系统时间与网络时间不一致（偏差超过 5 分钟）；' + #13#10 +
    '· 网络时间已超过过期日期；' + #13#10 +
    '· 无法连接任何 NTP 服务器完成校验。' + #13#10 +
    '出现以上任一情况，程序会提示「此版本为Alpha测试版本。于2026年9月24日正式过期，请等待正式版本发布」并退出。' + #13#10 + #13#10 +
    '本版本使用独立配置目录（%LOCALAPPDATA%\DeltaModelViewer-Alpha），可与正式版共存。' + #13#10 + #13#10 +
    '是否继续安装？', mbConfirmation, MB_YESNO);
  if Answer = IDNO then
    Result := False;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    ExtractData();
    WriteDataConfig();
  end;
end;

// ------------------------------------------------------------------
// 卸载时删除解压的 Data 文件夹与 Alpha 独立配置目录
// ------------------------------------------------------------------
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir, CfgDir: string;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{app}\Data');
    if DirExists(DataDir) then
    begin
      // 第三个参数设为 True 表示删除只读文件，第四个 True 表示删除子目录
      if not DelTree(DataDir, True, True, True) then
        MsgBox('无法完全删除数据文件夹 "' + DataDir + '"，请手动删除。', mbError, MB_OK);
    end;

    CfgDir := AlphaDataDir();
    if DirExists(CfgDir) then
      DelTree(CfgDir, True, True, True);
  end;
end;
