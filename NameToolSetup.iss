; NameTool 安装版打包脚本（Inno Setup）
; 版本号自动从发布产物 EXE 中读取，无需手动维护
; 先执行：dotnet publish "E:\Visual\NameTool\NameTool.csproj" -c Release
; 产物输出：E:\Visual\NameTool\publish\NameTool.exe

#define AppName "NameTool"
#define ExeName "NameTool.exe"
#define PublishDir "E:\Visual\NameTool\publish"
#define OutputDir "E:\build\NameTool\installer"
#define AppIcon "E:\Visual\NameTool\Images\logo.ico"
#define AppVersion GetStringFileInfo(PublishDir + "\" + ExeName, "ProductVersion")
#define SetupName "NameTool_v" + AppVersion + "_Setup"

[Setup]
AppId={{7F7E2C5C-8D65-4F44-BE3E-6D7AF9D2B4D1}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=EngDel
DefaultDirName=D:\Program Files\{#AppName}
DefaultGroupName={#AppName}
OutputDir={#OutputDir}
OutputBaseFilename={#SetupName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile={#AppIcon}
PrivilegesRequired=lowest
Uninstallable=yes
CreateUninstallRegKey=yes
UninstallDisplayIcon={app}\{#ExeName}

[UninstallDelete]
; ⚠️ 这里**故意不写** `Type: filesandordirs; Name: "{app}"`。
;
; 原因（这正是「升级后要重新登录 115」的根因，v1.9.0 修掉）：
;   Inno Setup 在安装同 AppId 的新版本时，会**先静默运行旧版的卸载程序**。
;   而 `filesandordirs; Name: "{app}"` 会把整个安装目录连 data\ 一起删干净 ——
;   data\ 里正是 115 的登录凭据（115-account.json）、序号预设、列宽记录。
;   于是每次覆盖升级都等于「卸载干净再重装」，登录态必然丢失。
;
;   Setup 装过的文件（NameTool.exe 等）Inno 卸载时会自动清理，本来就不需要写在这里；
;   本段只用于「Setup 没装过、但卸载时也该一并清掉」的东西。
;   data\ 要不要删，交给下面的代码段按用户的选择处理（静默卸载一律保留）。
;
; 顺带提醒：本文件的注释里**不要单独占一行**写出下面那些方括号段名
; （按段落切分的脚本会被注释里的假段名带偏，v1.9.0 踩过一次）。

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"

[Files]
; ⚠️ 必须排除 data\ —— 那是**用户数据目录**，运行过之后里面会有 115 网盘的登录凭据
; （data\115-account.json，含 UID/CID/SEID/KID，等同账号会话）。
; 一旦打进安装包上传到公网更新服务器，任何人都能下载并提取出账号凭据。
; 程序首次运行会自己 Directory.CreateDirectory 建这个目录（MainViewModel 构造里
; 的 MigrateLegacyData 就会做），所以安装包里**根本不需要**它。
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "Images\*,data\*"

[InstallDelete]
Type: files; Name: "{app}\NameTool_v*.exe"
Type: filesandordirs; Name: "{app}\Images"
; 旧版（v1.3.x，那时还没开 IncludeNativeLibrariesForSelfExtract）散落在安装目录的
; 5 个 WPF 原生库。单文件版已经把它们打进 exe 里了，这几个残留文件不但白占约 8MB，
; 还可能被 .NET 的原生库搜索优先命中（版本比内置的旧）⇒ 一并清掉，
; 让安装目录回到「只有 NameTool.exe + data\」的单文件形态。
Type: files; Name: "{app}\D3DCompiler_47_cor3.dll"
Type: files; Name: "{app}\Penimc_cor3.dll"
Type: files; Name: "{app}\PresentationNative_cor3.dll"
Type: files; Name: "{app}\vcruntime140_cor3.dll"
Type: files; Name: "{app}\wpfgfx_cor3.dll"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#ExeName}"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#ExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#ExeName}"; Description: "启动 {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
var
  ShouldDeleteData: Boolean;

  /// 升级保护用的备份目录
  UpgradeBackupDir: String;

  /// 是否已经从某个旧位置成功捞到数据（捞到了就不必再试第二次）
  BackupSourceFound: Boolean;

const
  /// 数据目录名（放在 exe 同级）
  DataFolder = 'data';

  /// 备份目录的目录名（拼在临时目录下）
  UpgradeBackupLeaf = 'NameTool-upgrade-backup';

  /// 本程序的卸载注册表项。Inno 默认用 HKCU 或 HKLM 存安装位置，
  /// 取决于安装时是否管理员权限；本脚本 PrivilegesRequired=lowest ⇒ 通常是 HKCU。
  /// 注意：脚本语言里的字符串字面量不会被自动展开，所以这里的 { } 就是字面量本身，
  /// 不需要写成 {{ }}（只有传给 ExpandConstant 的才用 {{ }} 表示字面量）。
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{7F7E2C5C-8D65-4F44-BE3E-6D7AF9D2B4D1}_is1';

/// ---------------------------------------------------------------------------
/// 升级保护：把旧版的 data\ 先备份出来，装完再放回。
///
/// 为什么必须有这一段：
///   Inno 在安装同 AppId 的新版本时，会**先静默运行旧版的卸载程序**。而 v1.8.4 及更早的
///   卸载脚本里有 `[UninstallDelete] filesandordirs; Name: "{app}"`，会把整个安装目录
///   连 data\ 一起删掉 —— data\ 里正是 115 的登录凭据（115-account.json）。
///   那一刻新版的代码还没开始跑，所以**只能**由新版安装器抢在它前面把数据捞走。
///
/// ⚠️ 铁律：本段里凡是在 PrepareToInstall 之前被调用的函数，**一律不许用 {app}**。
///   {app} 要等用户在向导里选完目录才初始化，在 InitializeSetup 阶段展开它会直接抛
///     An attempt was made to expand the "app" constant before it was initialized.
///   并**中止整个安装**（v1.9.0 就是这么翻的车）。所以早期阶段只能靠注册表定位，
///   到了 PrepareToInstall 才允许用 {app}。
///
/// 三个阶段各管一段：
///   1) InitializeSetup  —— 最早，只能查注册表；专治「已注册的旧版本会被静默卸载」。
///   2) PrepareToInstall —— 此时 {app} 可用；兜住「没注册过（直接拷 exe 的绿色用法）」。
///   3) ssPostInstall    —— 文件装完，把备份放回。
///
/// 三层之外还有一层兜底：程序自己会把凭据镜像到 %LOCALAPPDATA%\NameTool，
/// 所以即使这里全都没捞到，只要旧版程序跑过一次，登录态仍能找回来。
/// ---------------------------------------------------------------------------

/// 备份目录。**不能**用 {app}（见上面铁律）。
/// 首选环境变量 TEMP —— 它不涉及任何常量展开，是 InitializeSetup 阶段最安全的取法
/// （{app} 在那个阶段会直接抛错并中止安装；{tmp} 虽然一般可用，但没必要冒这个险）。
/// 全程 try/except：取不到就返回空字符串让调用方跳过备份，绝不抛出去。
function ResolveBackupDir(): String;
var
  Base: String;
begin
  Base := GetEnv('TEMP');
  if Base = '' then Base := GetEnv('TMP');

  if Base = '' then
  begin
    try
      Base := ExpandConstant('{tmp}');
    except
      Base := '';
    end;
  end;

  if Base = '' then
    Result := ''
  else
    Result := AddBackslash(Base) + UpgradeBackupLeaf;
end;

/// 从注册表找旧版安装位置下的 data\。**只查注册表，不碰 {app}。**
/// 没注册过（或注册表被清理过）就返回空字符串 —— 那种情况下 Inno 也不会去跑旧卸载器，
/// 老目录里的 data\ 自然原样留着，由 PrepareToInstall 那一阶段接手。
function RegisteredInstallDataDir(): String;
var
  Loc: String;
  Candidate: String;
begin
  Result := '';

  if Result = '' then
  begin
    Loc := '';
    if RegQueryStringValue(HKCU, UninstallKey, 'InstallLocation', Loc) and (Loc <> '') then
    begin
      Candidate := AddBackslash(Loc) + DataFolder;
      if DirExists(Candidate) then Result := Candidate;
    end;
  end;

  if Result = '' then
  begin
    Loc := '';
    if RegQueryStringValue(HKLM, UninstallKey, 'InstallLocation', Loc) and (Loc <> '') then
    begin
      Candidate := AddBackslash(Loc) + DataFolder;
      if DirExists(Candidate) then Result := Candidate;
    end;
  end;
end;

/// 把 Src 目录下的**文件**复制到备份目录（data\ 下没有子目录）。
/// 返回复制成功的个数；0 表示没捞到东西（无源目录 / 建不了备份目录）。
function BackupDataFrom(Src: String): Integer;
var
  FindRec: TFindRec;
begin
  Result := 0;
  if Src = '' then exit;
  if not DirExists(Src) then exit;

  if UpgradeBackupDir = '' then UpgradeBackupDir := ResolveBackupDir();
  if UpgradeBackupDir = '' then exit;

  if DirExists(UpgradeBackupDir) then
    DelTree(UpgradeBackupDir, True, True, True);
  if not CreateDir(UpgradeBackupDir) then
  begin
    UpgradeBackupDir := '';
    exit;
  end;

  if FindFirst(AddBackslash(Src) + '*', FindRec) then
  begin
    repeat
      // 16 = FILE_ATTRIBUTE_DIRECTORY；只复制文件
      if (FindRec.Attributes and 16) = 0 then
      begin
        if CopyFile(AddBackslash(Src) + FindRec.Name,
                    AddBackslash(UpgradeBackupDir) + FindRec.Name, False) then
          Result := Result + 1;
      end;
    until not FindNext(FindRec);
    FindClose(FindRec);
  end;

  if Result > 0 then
    BackupSourceFound := True;
end;

/// 把备份里的文件放回新版 data\。**只在目标缺失时**放回，不覆盖新版自己写的任何东西。
function RestoreExistingData(): Integer;
var
  Dst: String;
  FindRec: TFindRec;
begin
  Result := 0;
  if UpgradeBackupDir = '' then exit;
  if not DirExists(UpgradeBackupDir) then exit;

  Dst := AddBackslash(ExpandConstant('{app}')) + DataFolder;
  if not DirExists(Dst) then
  begin
    if not CreateDir(Dst) then exit;
  end;

  if FindFirst(AddBackslash(UpgradeBackupDir) + '*', FindRec) then
  begin
    repeat
      if (FindRec.Attributes and 16) = 0 then
      begin
        if not FileExists(AddBackslash(Dst) + FindRec.Name) then
        begin
          if CopyFile(AddBackslash(UpgradeBackupDir) + FindRec.Name,
                      AddBackslash(Dst) + FindRec.Name, False) then
            Result := Result + 1;
        end;
      end;
    until not FindNext(FindRec);
    FindClose(FindRec);
  end;
end;

/// 阶段 1：最早的一步，此时旧版卸载程序还没跑、{app} 还没初始化。
function InitializeSetup(): Boolean;
var
  n: Integer;
begin
  // 无论如何都要放行 —— 备份是「保护数据」，不能反过来把安装卡住。
  Result := True;

  try
    UpgradeBackupDir := ResolveBackupDir();
    n := BackupDataFrom(RegisteredInstallDataDir());
    if n > 0 then
      Log(Format('升级保护：已备份旧版 %d 个数据文件', [n]))
    else
      Log('升级保护：注册表里没有旧版安装位置（直接拷贝的绿色用法），改由 PrepareToInstall 兜底');
  except
    // InitializeSetup 阶段能用的 API 有限，任何意外都只记日志、不阻断安装
    Log('升级保护：备份旧数据时出错，已跳过（不影响安装）');
  end;
end;

/// 阶段 2：向导已完成、{app} 一定可用。兜住「没注册过」的情况
/// （注册表无记录时 Inno 不会跑旧卸载器，所以老目录里的 data\ 这时候还在）。
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  n: Integer;
begin
  Result := '';   // 返回空字符串 = 继续安装

  try
    if not BackupSourceFound then
    begin
      n := BackupDataFrom(AddBackslash(ExpandConstant('{app}')) + DataFolder);
      if n > 0 then
        Log(Format('升级保护：已从 %s 备份 %d 个数据文件', [ExpandConstant('{app}'), n]));
    end;
  except
    Log('升级保护：第二次备份尝试出错，已跳过（不影响安装）');
  end;
end;

/// 阶段 3：文件装完，把备份放回。
procedure CurStepChanged(CurStep: TSetupStep);
var
  n: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    try
      n := RestoreExistingData();
      if n > 0 then
        Log(Format('升级保护：已恢复 %d 个数据文件 ⇒ 升级后无需重新登录 115', [n]));
    except
      Log('升级保护：恢复数据时出错，备份仍在 ' + UpgradeBackupDir);
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
  Cmd: String;
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    DataDir := AddBackslash(ExpandConstant('{app}')) + DataFolder;
    ShouldDeleteData := False;

    // 只有在**交互式**卸载、且用户明确选了「删除数据」时才真删；默认一律保留。
    // 关键点：静默卸载（/SILENT，以及**升级时 Inno 自动触发的旧版卸载**）不会弹窗，
    // 于是直接走「保留」这条默认路径 —— 这也是「升级不影响登录状态」的保证之一。
    // 顺带把默认按钮放在「否」（MB_DEFBUTTON2），避免回车误删。
    if DirExists(DataDir) and (not UninstallSilent) then
    begin
      if MsgBox('是否删除程序数据？' + #13#10 + #13#10 +
        '数据目录：' + DataDir + #13#10 +
        '包含 115 网盘登录凭据、序号预设、列宽记录。' + #13#10 +
        '如果将来可能重新安装，建议选择"否"。',
        mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      begin
        ShouldDeleteData := True;
      end;
    end;
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    if ShouldDeleteData then
    begin
      // 只有用户明确要删时才收尾清理剩下的目录。此时卸载程序自身还在 {app} 里运行，
      // 直接删会撞上「文件被占用」，所以交给一个延迟命令去做。
      // ⚠️ 别把这句挪到 else 分支：那样「选择保留数据」反而会把 data\ 删掉。
      Cmd := '/C ping 127.0.0.1 -n 2 > nul & rd /s /q "' + ExpandConstant('{app}') + '"';
      Exec(ExpandConstant('{cmd}'), Cmd, '', SW_HIDE, ewNoWait, ResultCode);
    end;
  end;
end;
